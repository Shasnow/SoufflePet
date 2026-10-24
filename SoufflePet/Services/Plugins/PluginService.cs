// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Plugin.Sdk;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;

namespace SoufflePet.Services.Plugins;

/// <summary>插件生命周期管理：发现、加载、启用/禁用、卸载、导入，以及向对话提供插件工具。</summary>
public interface IPluginService : IDisposable
{
    /// <summary>已发现插件的快照（按发现顺序，内置优先）；界面只读。</summary>
    IReadOnlyList<PluginDescriptor> Plugins { get; }

    /// <summary>插件扫描目录：应用目录 plugins 与家目录 .souffle/plugins。</summary>
    IReadOnlyList<string> SearchDirectories { get; }

    /// <summary>用户导入插件的安装目录（~/.souffle/plugins）。</summary>
    string InstallDirectory { get; }

    /// <summary>插件状态或启停变化（可能来自后台线程，界面需切回 UI 线程刷新）。</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 获取已启用插件的工具（懒加载）。<paramref name="reservedNames"/> 是主程序本地工具名，
    /// 插件工具重名时自动加「插件id__」前缀。
    /// </summary>
    Task<IReadOnlyList<AITool>> GetToolsAsync(
        IReadOnlyCollection<string>? reservedNames = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为本轮回复创建正文处理管线：创建时询问已加载插件是否参与（按模型 id），
    /// 参与插件的异常由管线兜底，原样放行正文。
    /// </summary>
    ResponsePipeline CreateResponsePipeline(string modelId);

    /// <summary>启用或禁用插件：启用立即加载，禁用依次 Shutdown → Unload。</summary>
    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>保存插件配置：写回 plugin.json 的 config 段，并让运行中的实例按新配置重新加载。</summary>
    Task SaveConfigAsync(string pluginId, JsonElement config, CancellationToken cancellationToken = default);

    /// <summary>重新扫描插件目录（保留已加载实例的状态）。</summary>
    void Discover();

    /// <summary>从 zip 包导入插件到用户目录；成功后自动启用。</summary>
    PluginInstallResult Install(string archivePath);
}

/// <summary>
/// 插件服务实现：磁盘扫描只读清单，程序集在首次需要工具时才加载；
/// 单插件加载/初始化/调用失败只影响它自己，主程序与其它插件继续工作。
/// </summary>
public sealed class PluginService : IPluginService
{
    private static readonly ILogger Logger = Log.ForContext<PluginService>();

    /// <summary>多数模型接口对工具名长度的限制是 64 个字符。</summary>
    private const int MaxToolNameLength = 64;

    private const string ManifestFileName = "plugin.json";

    /// <summary>写回 plugin.json 时保持缩进与中文原样，方便用户直接查看。</summary>
    private static readonly JsonSerializerOptions ManifestWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SouffleSettings _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly List<PluginDescriptor> _descriptors = [];
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PluginService(SouffleSettings settings, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
        BuiltInDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
        InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".souffle", "plugins");
        Discover();
        _ = Task.Run(PreloadEnabledAsync);
    }

    /// <summary>启动后在后台加载已启用插件，插件窗口无需先禁用再启用即可看到工具列表。</summary>
    private async Task PreloadEnabledAsync()
    {
        try
        {
            await GetToolsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "preload enabled plugins failed");
        }
    }

    public string InstallDirectory { get; }

    /// <summary>随主程序发布的插件目录（应用目录下 plugins）。</summary>
    public string BuiltInDirectory { get; }

    public IReadOnlyList<string> SearchDirectories => [BuiltInDirectory, InstallDirectory];

    public IReadOnlyList<PluginDescriptor> Plugins
    {
        get { lock (_sync) return [.. _descriptors]; }
    }

    public event EventHandler? Changed;

    public void Discover()
    {
        var found = ScanDirectories();

        lock (_sync)
        {
            var existing = _descriptors.ToDictionary(d => d.Manifest.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in found)
            {
                if (!existing.TryGetValue(descriptor.Manifest.Id, out var old)) continue;
                // 同一插件重扫：清单以磁盘为准，运行期状态沿用
                descriptor.Enabled = old.Enabled;
                descriptor.State = old.State;
                descriptor.Error = old.Error;
                descriptor.ToolNames = old.ToolNames;
                descriptor.FailureCount = old.FailureCount;
            }

            _descriptors.Clear();
            _descriptors.AddRange(found);
        }

        RaiseChanged();
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(
        IReadOnlyCollection<string>? reservedNames = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var usedNames = new HashSet<string>(reservedNames ?? [], StringComparer.OrdinalIgnoreCase);
            var tools = new List<AITool>();

            foreach (var descriptor in Snapshot())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!descriptor.Enabled) continue;

                if (!_loaded.ContainsKey(descriptor.Manifest.Id))
                {
                    await LoadAsync(descriptor, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }

                if (!_loaded.TryGetValue(descriptor.Manifest.Id, out var loaded)) continue;

                var timeout = TimeSpan.FromSeconds(Math.Clamp(descriptor.Manifest.TimeoutSeconds, 1, 300));
                foreach (var tool in loaded.Tools)
                {
                    var name = UniqueToolName(descriptor.Manifest.Id, tool.Name, usedNames);
                    tools.Add(new PluginToolGuard(tool, name, this, descriptor.Manifest.Id, timeout));
                }
            }

            return tools;
        }
        finally
        {
            _gate.Release();
            if (changed) RaiseChanged();
        }
    }

    public ResponsePipeline CreateResponsePipeline(string modelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 先取快照再调用插件代码，避免持有锁执行外部逻辑
        List<(string Id, PluginBase Plugin)> plugins = [];
        lock (_sync)
        {
            foreach (var descriptor in _descriptors)
            {
                if (!descriptor.Enabled) continue;
                if (!_loaded.TryGetValue(descriptor.Manifest.Id, out var loaded)) continue;
                plugins.Add((descriptor.Manifest.Id, loaded.Instance));
            }
        }

        var participants = new List<ResponsePipeline.Participant>();
        foreach (var (id, plugin) in plugins)
        {
            try
            {
                if (plugin.BeginResponse(modelId))
                    participants.Add(new ResponsePipeline.Participant(this, id, plugin));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "plugin {Plugin} begin response failed", id);
                RecordFailure(id, $"响应开始失败：{ex.Message}");
            }
        }

        return new ResponsePipeline(participants);
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var descriptor = Snapshot()
                .FirstOrDefault(d => string.Equals(d.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null) return;

            if (descriptor.Enabled != enabled)
            {
                descriptor.Enabled = enabled;
                UpdateDisabledSetting(pluginId, disabled: !enabled);
                changed = true;
            }

            if (enabled)
            {
                if (!_loaded.ContainsKey(descriptor.Manifest.Id))
                {
                    descriptor.State = PluginState.Discovered;
                    descriptor.Error = null;
                    descriptor.FailureCount = 0;
                    await LoadAsync(descriptor, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }
            }
            else if (_loaded.ContainsKey(descriptor.Manifest.Id))
            {
                await UnloadAsync(descriptor).ConfigureAwait(false);
                changed = true;
            }
            else if (descriptor.State is not PluginState.Disabled)
            {
                // 未加载过的插件直接停用：清掉「等待加载」状态
                descriptor.State = PluginState.Disabled;
                descriptor.ToolNames = [];
                descriptor.Error = null;
                changed = true;
            }
        }
        finally
        {
            _gate.Release();
            if (changed) RaiseChanged();
        }
    }

    public async Task SaveConfigAsync(string pluginId, JsonElement config, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var descriptor = Snapshot()
                .FirstOrDefault(d => string.Equals(d.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"找不到插件「{pluginId}」");

            // 只替换 config 段，保留清单里的其它字段；JSON 注释会在重写时丢失
            var manifestPath = Path.Combine(descriptor.Directory, ManifestFileName);
            var root = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? throw new InvalidDataException("plugin.json 根节点不是对象");

            var incoming = JsonNode.Parse(config.GetRawText());
            JsonNode? mergedConfig;
            if (root["config"] is JsonObject existing && incoming is JsonObject patch)
            {
                // 只覆盖表单提交的键，保留未在 configSchema 中声明的配置
                foreach (var (key, value) in patch)
                    existing[key] = value?.DeepClone();
                mergedConfig = existing;
            }
            else
            {
                mergedConfig = incoming;
                root["config"] = mergedConfig;
            }

            await File.WriteAllTextAsync(
                manifestPath,
                root.ToJsonString(ManifestWriteOptions),
                cancellationToken).ConfigureAwait(false);

            descriptor.Manifest.Config = mergedConfig is null
                ? null
                : JsonSerializer.SerializeToElement(mergedConfig);

            // 已加载的实例立即按新配置重建；启用中的插件顺带完成首次加载
            if (_loaded.ContainsKey(descriptor.Manifest.Id))
                await UnloadAsync(descriptor).ConfigureAwait(false);

            if (descriptor.Enabled)
                await LoadAsync(descriptor, cancellationToken).ConfigureAwait(false);

            changed = true;
            Logger.Information("plugin {Plugin} config saved", descriptor.Manifest.Id);
        }
        finally
        {
            _gate.Release();
            if (changed) RaiseChanged();
        }
    }

    public PluginInstallResult Install(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return new PluginInstallResult(false, "找不到插件包");

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && !IsJunk(e.FullName))
                .ToList();

            // 取层级最浅的 plugin.json 作为清单入口，它所在的目录就是插件根目录
            var manifestEntry = entries
                .Where(e => string.Equals(e.Name, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c == '/'))
                .FirstOrDefault();
            if (manifestEntry is null) return new PluginInstallResult(false, "插件包里没有找到 plugin.json");

            PluginManifest manifest;
            using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            {
                manifest = PluginManifest.Parse(reader.ReadToEnd());
            }

            if (manifest.Validate() is { } error) return new PluginInstallResult(false, error);

            var prefix = manifestEntry.FullName[..^manifestEntry.Name.Length]; // 形如 "my-plugin/"，根目录时为空串
            var target = Path.Combine(InstallDirectory, manifest.Id);
            var existed = Directory.Exists(target);
            Directory.CreateDirectory(target);

            var targetRoot = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            foreach (var entry in entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = entry.FullName[prefix.Length..];
                if (relative.Length == 0) continue;

                var destination = Path.GetFullPath(
                    Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));

                // 防目录穿越：解压结果必须仍落在插件目录内
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            var assemblyPath = Path.GetFullPath(Path.Combine(target, manifest.Assembly));
            if (!assemblyPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
            {
                return new PluginInstallResult(
                    false, $"插件包里缺少程序集 {manifest.Assembly}");
            }

            // 导入后默认启用；已在运行的旧版本要到重启后才换成新程序集
            UpdateDisabledSetting(manifest.Id, disabled: false);
            var running = IsLoaded(manifest.Id);
            Discover();
            
            Logger.Information("plugin {Plugin} installed to {Target}", manifest.Id, target);
            return new PluginInstallResult(
                true,
                running
                    ? $"已更新插件「{manifest.DisplayName}」，重启后生效"
                    : existed ? $"已更新插件「{manifest.DisplayName}」" : $"已安装插件「{manifest.DisplayName}」",
                manifest.Id);
        }
        catch (InvalidDataException ex)
        {
            Logger.Warning(ex, "parse plugin manifest from {Archive} failed", archivePath);
            return new PluginInstallResult(false, $"plugin.json 无法解析：{ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "install plugin from {Archive} failed", archivePath);
            return new PluginInstallResult(false, $"安装失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<LoadedPlugin> loaded;
        lock (_sync)
        {
            loaded = [.. _loaded.Values];
            _loaded.Clear();
        }

        foreach (var plugin in loaded)
        {
            try
            {
                plugin.Instance.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "shutdown plugin on dispose failed");
            }

            UnloadContext(plugin.Context);
        }

        _gate.Dispose();
    }

    /// <summary>扫描两个插件目录：目录内每个含 plugin.json 的子目录视为一个插件（同 id 先到先得）。</summary>
    private List<PluginDescriptor> ScanDirectories()
    {
        var found = new List<PluginDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, isBuiltIn) in new[] { (BuiltInDirectory, true), (InstallDirectory, false) })
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var manifestPath = Path.Combine(directory, ManifestFileName);
                    if (!File.Exists(manifestPath)) continue;

                    PluginManifest manifest;
                    try
                    {
                        manifest = PluginManifest.Load(manifestPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "parse plugin manifest {Path} failed", manifestPath);
                        continue;
                    }

                    if (manifest.Validate() is { } error)
                    {
                        Logger.Warning("plugin manifest {Path} invalid: {Error}", manifestPath, error);
                        continue;
                    }

                    if (!seen.Add(manifest.Id))
                    {
                        Logger.Debug("duplicate plugin {Plugin} ignored ({Directory})", manifest.Id, directory);
                        continue;
                    }

                    var enabled = !_settings.DisabledPlugins
                        .Any(id => string.Equals(id, manifest.Id, StringComparison.OrdinalIgnoreCase));

                    found.Add(new PluginDescriptor
                    {
                        Manifest = manifest,
                        Directory = Path.GetFullPath(directory),
                        IsBuiltIn = isBuiltIn,
                        Enabled = enabled,
                        State = enabled ? PluginState.Discovered : PluginState.Disabled,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "scan plugin directory {Directory} failed", root);
            }
        }

        return found;
    }

    /// <summary>加载并初始化单个插件；失败只标记该插件（调用方须持有 <see cref="_gate"/>）。</summary>
    private async Task LoadAsync(PluginDescriptor descriptor, CancellationToken cancellationToken)
    {
        var id = descriptor.Manifest.Id;
        descriptor.State = PluginState.Loading;
        descriptor.Error = null;

        PluginLoadContext? context = null;
        try
        {
            var assemblyPath = Path.GetFullPath(Path.Combine(descriptor.Directory, descriptor.Manifest.Assembly));
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException($"找不到插件程序集 {descriptor.Manifest.Assembly}", assemblyPath);

            context = new PluginLoadContext(id, assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = ResolveEntryType(assembly, descriptor.Manifest.EntryType);
            if (Activator.CreateInstance(type) is not PluginBase instance)
                throw new InvalidOperationException($"入口类型 {type.FullName} 不是有效的插件");

            var pluginContext = new PluginContext(
                descriptor.Manifest.Id,
                descriptor.Directory,
                descriptor.Manifest.Config,
                _loggerFactory.CreateLogger($"Plugin.{id}"));
            await instance.InitializeAsync(pluginContext, cancellationToken).ConfigureAwait(false);

            // 工具由插件在钩子里自行添加；只做后处理、不提供工具的插件同样允许
            List<AIFunction> builtTools = [];
            instance.BuildTools(builtTools);

            _loaded[id] = new LoadedPlugin(context, instance, builtTools);
            context = null; // 所有权移交，禁用时再卸载

            descriptor.ToolNames = [.. builtTools.Select(t => t.Name)];
            descriptor.State = PluginState.Enabled;
            Logger.Information("plugin {Plugin} loaded with {Count} tools", id, descriptor.ToolNames.Count);
        }
        catch (Exception ex)
        {
            context?.Unload();
            descriptor.State = PluginState.Failed;
            descriptor.Error = Unwrap(ex).Message;
            descriptor.ToolNames = [];
            Logger.Error(ex, "load plugin {Plugin} failed", id);
        }
    }

    /// <summary>停用插件：先让插件清理，再卸载加载上下文（调用方须持有 <see cref="_gate"/>）。</summary>
    private async Task UnloadAsync(PluginDescriptor descriptor)
    {
        var id = descriptor.Manifest.Id;
        if (_loaded.Remove(id, out var loaded))
        {
            try
            {
                await loaded.Instance.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "plugin {Plugin} shutdown failed", id);
            }

            UnloadContext(loaded.Context);
        }

        descriptor.ToolNames = [];
        descriptor.Error = null;
        descriptor.State = PluginState.Disabled;
        Logger.Information("plugin {Plugin} disabled", id);
    }

    /// <summary>工具超时或抛异常时由守卫回调：累计次数并通知界面。</summary>
    internal void RecordFailure(string pluginId, string message)
    {
        lock (_sync)
        {
            var descriptor = _descriptors
                .FirstOrDefault(d => string.Equals(d.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null) return;
            descriptor.FailureCount++;
        }

        Logger.Warning("plugin {Plugin} tool failure: {Message}", pluginId, message);
        RaiseChanged();
    }

    private void UpdateDisabledSetting(string pluginId, bool disabled)
    {
        var list = new List<string>(_settings.DisabledPlugins);
        var index = list.FindIndex(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (disabled)
        {
            if (index >= 0) return;
            list.Add(pluginId);
        }
        else
        {
            if (index < 0) return;
            list.RemoveAt(index);
        }

        // 赋值触发 PropertyChanged，持久化由 App 组合根统一处理
        _settings.DisabledPlugins = list;
    }

    private bool IsLoaded(string pluginId)
    {
        lock (_sync) return _loaded.ContainsKey(pluginId);
    }

    private List<PluginDescriptor> Snapshot()
    {
        lock (_sync) return [.. _descriptors];
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>卸载加载上下文并尽力回收（JIT 局部变量可能短暂持有引用，属正常现象）。</summary>
    private static void UnloadContext(PluginLoadContext? context)
    {
        context?.Unload();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static Type ResolveEntryType(Assembly assembly, string? entryType)
    {
        if (!string.IsNullOrWhiteSpace(entryType))
        {
            var type = assembly.GetType(entryType.Trim(), throwOnError: false)
                ?? throw new InvalidOperationException($"找不到入口类型 {entryType}");
            return EnsurePluginType(type);
        }

        var candidates = assembly.GetTypes()
            .Where(t => typeof(PluginBase).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("程序集里没有 PluginBase 子类"),
            _ => throw new InvalidOperationException("程序集里有多个 PluginBase 子类，请在 plugin.json 里指定 entryType"),
        };
    }

    private static Type EnsurePluginType(Type type) =>
        typeof(PluginBase).IsAssignableFrom(type) && !type.IsAbstract
            ? type
            : throw new InvalidOperationException($"入口类型 {type.FullName} 没有继承 PluginBase");

    /// <summary>插件工具与已有工具重名时加「插件id__工具名」前缀并做 ASCII 净化。</summary>
    private static string UniqueToolName(string pluginId, string toolName, HashSet<string> usedNames)
    {
        if (usedNames.Add(toolName)) return toolName;

        var raw = $"{SanitizeName(pluginId, "plugin")}__{SanitizeName(toolName, "tool")}";
        if (raw.Length > MaxToolNameLength) raw = raw[..MaxToolNameLength];

        var name = raw;
        for (var i = 2; !usedNames.Add(name); i++)
        {
            var suffix = $"_{i}";
            name = raw.Length + suffix.Length <= MaxToolNameLength
                ? raw + suffix
                : raw[..(MaxToolNameLength - suffix.Length)] + suffix;
        }

        return name;
    }

    private static string SanitizeName(string value, string fallback)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');

        var name = builder.ToString().Trim('_');
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>压缩包里的系统垃圾（__MACOSX、.DS_Store 等）不参与安装。</summary>
    private static bool IsJunk(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
        || path.Split('/').Any(part => part.StartsWith('.'));

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;

    /// <summary>已加载插件：保持加载上下文、实例与原始工具列表（包装在每次取工具时生成）。</summary>
    private sealed class LoadedPlugin(PluginLoadContext context, PluginBase instance, IReadOnlyList<AIFunction> tools)
    {
        public PluginLoadContext Context { get; } = context;

        public PluginBase Instance { get; } = instance;

        public IReadOnlyList<AIFunction> Tools { get; } = tools;
    }
}

/// <summary>
/// 插件工具守卫：透传工具名与参数 schema，只负责超时与异常兜底——
/// 插件工具挂起或抛异常时返回错误文本给模型，并把失败计数记到插件上，不影响主程序。
/// </summary>
internal sealed class PluginToolGuard(
    AIFunction inner,
    string name,
    PluginService owner,
    string pluginId,
    TimeSpan timeout)
    : DelegatingAIFunction(inner)
{
    /// <summary>实际暴露给模型的工具名（冲突时带插件前缀）。</summary>
    public override string Name => name;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var task = base.InvokeCoreAsync(arguments, cancellationToken).AsTask();

        using var delayCts = new CancellationTokenSource();
        var finished = await Task.WhenAny(task, Task.Delay(timeout, delayCts.Token)).ConfigureAwait(false);
        await delayCts.CancelAsync();

        if (finished != task)
        {
            // 超时后不再等待结果；观察后续可能出现的异常，避免未观察的任务异常
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            cancellationToken.ThrowIfCancellationRequested();
            owner.RecordFailure(pluginId, $"{name} 执行超时");
            return $"工具 {name} 执行超时（{timeout.TotalSeconds:0} 秒），已放弃等待。";
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            owner.RecordFailure(pluginId, $"{name} 执行失败：{ex.Message}");
            return $"插件 {pluginId} 的工具 {name} 执行失败：{ex.Message}";
        }
    }
}
