// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// 对话模型管理窗口的视图模型（与 Live2D 模型管理区分）：左侧提供商列表 + 右侧提供商配置与模型清单。
/// 提供商就是配置单位（名称 / 接口地址 / API 密钥），模型清单可从接口获取或手动添加，
/// 勾选的模型在点「保存配置」后写回 <see cref="SouffleSettings.ChatProviders" />，
/// 随后重建 <see cref="SouffleSettings.ChatModels" />，对话窗口的模型选择列表随之更新。
/// </summary>
public partial class LlmModelsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<LlmModelsViewModel>();

    private readonly SouffleSettings _settings;
    private readonly ILlmModelCatalog _catalog;

    /// <summary>在途的「获取模型列表」请求；切换提供商时取消，null 表示当前没有请求。</summary>
    private CancellationTokenSource? _fetchCts;

    public LlmModelsViewModel(SouffleSettings settings, ILlmModelCatalog catalog)
    {
        _settings = settings;
        _catalog = catalog;
        SelectedProvider = Providers.FirstOrDefault();
    }

    /// <summary>提供商列表：直接绑定设置里的集合，新建 / 移除即写回配置。</summary>
    public ObservableCollection<ChatProviderConfig> Providers => _settings.ChatProviders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ChatProviderConfig? SelectedProvider { get; set; }

    partial void OnSelectedProviderChanged(ChatProviderConfig? value)
    {
        // 切走时先取消在途请求：旧结果既不能进新提供商的工作清单，也不能改新面板的状态
        CancelFetch();
        ResultMessage = string.Empty;
        FetchStatus = string.Empty;
        NewModelId = string.Empty;
        IsConfirmingRemove = false;
        LoadEditor();
    }

    public bool HasSelection => SelectedProvider is not null;

    /// <summary>选中提供商的模型清单（工作副本，勾选状态在保存时才写回配置）。</summary>
    public ObservableCollection<LlmProviderModelItem> Models { get; } = [];

    public bool HasModels => Models.Count > 0;

    // ===== 编辑表单：字段和模型勾选一样是工作副本，点「保存配置」才写回提供商配置 =====
    // 这样未保存 / 半截的输入既不会被别的 Persist() 顺带写进配置，也不会进入对话窗口的模型列表。

    [ObservableProperty]
    public partial string EditorName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorApiKey { get; set; } = string.Empty;

    public string WindowTitle => $"{_settings.PetName} · 对话模型";

    /// <summary>标题栏下方的说明文案。</summary>
    public string ToolHint => "按提供商配置接口与密钥，勾选要启用的模型";

    /// <summary>列表上方的统计文案：提供商数量与已启用模型数量。</summary>
    public string Status => Providers.Count == 0
        ? "还没有提供商，点「新建」添加"
        : $"共 {Providers.Count} 个提供商 · 已启用 {_settings.ChatModels.Count} 个模型";

    /// <summary>保存 / 移除的结果提示（空串表示不显示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultMessage))]
    public partial string ResultMessage { get; set; } = string.Empty;

    public bool HasResultMessage => ResultMessage.Length > 0;

    /// <summary>是否正在请求模型列表（请求期间禁用「获取模型列表」）。</summary>
    [ObservableProperty]
    public partial bool IsFetching { get; set; }

    /// <summary>获取模型列表的状态文案（成功与失败都用这一行）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFetchStatus))]
    public partial string FetchStatus { get; set; } = string.Empty;

    public bool HasFetchStatus => FetchStatus.Length > 0;

    /// <summary>手动添加模型标识的输入框：接口没实现 /models 时也能配置。</summary>
    [ObservableProperty]
    public partial string NewModelId { get; set; } = string.Empty;

    /// <summary>新增一个空提供商：接口信息留空，填好后点「获取模型列表」。</summary>
    [RelayCommand]
    private void AddProvider()
    {
        var provider = new ChatProviderConfig { Name = UniqueProviderName("新提供商") };
        Providers.Add(provider);
        SelectedProvider = provider;
        ResultMessage = "填好接口地址与密钥后点「获取模型列表」";
        Persist();
    }

    /// <summary>移除二次确认是否显示：删掉的提供商连同模型清单与密钥一起消失。</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; set; }

    /// <summary>二次确认里的提示文案：说明会连带删掉什么。</summary>
    public string RemovePrompt => SelectedProvider is { } provider
        ? $"「{provider.Name}」以及它的 {provider.Models.Count} 个模型、接口地址与 API 密钥都会从配置里删除。"
        : string.Empty;

    /// <summary>点击「移除」：先弹二次确认（与 Live2D 模型管理一致），确认后才真正删除。</summary>
    [RelayCommand]
    private void RequestRemoveProvider()
    {
        if (SelectedProvider is null) return;

        ResultMessage = string.Empty;
        OnPropertyChanged(nameof(RemovePrompt));
        IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemoveProvider() => IsConfirmingRemove = false;

    /// <summary>确认移除：取消它的在途请求，删掉提供商，并重建对话窗口的模型选择列表。</summary>
    [RelayCommand]
    private void ConfirmRemoveProvider()
    {
        IsConfirmingRemove = false;
        if (SelectedProvider is not { } provider) return;

        CancelFetch(); // 在途请求属于这个提供商，跟着一起作废
        Providers.Remove(provider);
        SelectedProvider = Providers.FirstOrDefault();
        ResultMessage = $"已移除「{provider.Name}」";
        Persist();
    }

    /// <summary>
    /// 请求提供商的模型列表：合并进清单（已有的保留勾选状态），不自动启用新模型。
    /// 请求最长 30 秒，期间用户可能切到别的提供商：结果只回填给发起它的那个提供商，
    /// 切换时也会取消在途请求，避免 A 的模型落进 B 的工作清单、进而被「保存配置」写进 B 的配置。
    /// </summary>
    /// <remarks>
    /// 允许并发执行：切到别的提供商后可以立刻发起新请求，旧请求自身会被取消并作废（见 <see cref="IsCurrentFetch" />）；
    /// 同一提供商的重复点击仍由 <see cref="IsFetching" /> 挡住。
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FetchModelsAsync()
    {
        if (SelectedProvider is not { } provider || IsFetching) return;

        var baseUrl = EditorBaseUrl.Trim();
        if (!IsHttpUrl(baseUrl))
        {
            FetchStatus = "请先填写合法的接口地址（http/https）";
            return;
        }

        CancelFetch(); // 同一提供商不该叠加两个请求
        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        IsFetching = true;
        FetchStatus = "正在获取模型列表…";
        try
        {
            var ids = await _catalog.ListModelsAsync(baseUrl, EditorApiKey, cts.Token);

            // 请求期间切走了（或又发起了新请求）：这份结果属于上一个面板，直接丢弃
            if (!IsCurrentFetch(cts, provider)) return;

            // 同一个提供商也可能在请求期间改了接口地址：结果是旧地址的，别拿来填新地址的清单
            if (!string.Equals(EditorBaseUrl.Trim(), baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                FetchStatus = "接口地址已修改，这次结果作废，请重新获取";
                return;
            }

            var added = MergeModels(ids);
            FetchStatus = ids.Count == 0
                ? "接口没有返回任何模型，可以手动填写模型标识"
                : $"获取到 {ids.Count} 个模型（新增 {added} 个），勾选后点「保存配置」生效";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentFetch(cts, provider)) FetchStatus = "获取超时或已取消";
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "list models of provider {Provider} failed", provider.Name);
            if (IsCurrentFetch(cts, provider)) FetchStatus = $"获取失败：{ex.Message}";
        }
        finally
        {
            // 只有仍是当前这次请求时才复位状态：切走时 CancelFetch 已置空，别覆盖新提供商的请求
            if (ReferenceEquals(_fetchCts, cts))
            {
                _fetchCts = null;
                IsFetching = false;
            }

            cts.Dispose();
        }
    }

    /// <summary>这次请求是否仍然对应界面上的当前提供商（未被取消、也没有切走）。</summary>
    private bool IsCurrentFetch(CancellationTokenSource cts, ChatProviderConfig provider) =>
        ReferenceEquals(_fetchCts, cts) && ReferenceEquals(SelectedProvider, provider);

    /// <summary>
    /// 取消在途的「获取模型列表」请求（切换提供商 / 还原 / 移除 / 窗口隐藏时调用）。
    /// CTS 由请求自身在 finally 里释放；这里同时把「获取中」状态复位——否则取消后按钮会一直灰着，
    /// 直到那个已经没人关心的请求自己超时（最长 30 秒）才恢复。
    /// </summary>
    public void CancelFetch()
    {
        var cts = _fetchCts;
        if (cts is null) return;

        _fetchCts = null;
        IsFetching = false;
        FetchStatus = string.Empty;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 请求刚好结束并释放了，无需处理
        }
    }

    /// <summary>手动添加一个模型标识（默认勾选，省一步点击）。</summary>
    [RelayCommand]
    private void AddModel()
    {
        if (SelectedProvider is null) return;

        var modelId = NewModelId.Trim();
        if (modelId.Length == 0) return;

        if (Models.Any(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            FetchStatus = $"「{modelId}」已在模型列表里";
            return;
        }

        Models.Add(new LlmProviderModelItem(new ChatProviderModel { ModelId = modelId, IsActive = true }, this));
        NewModelId = string.Empty;
        FetchStatus = string.Empty;
        OnPropertyChanged(nameof(HasModels));
    }

    /// <summary>从清单里删掉一行（保存后才会真正从配置里消失）。</summary>
    public void RemoveModel(LlmProviderModelItem item)
    {
        Models.Remove(item);
        OnPropertyChanged(nameof(HasModels));
    }

    /// <summary>保存选中提供商的配置与模型清单，并重建对话窗口的模型选择列表。</summary>
    [RelayCommand]
    private void Save()
    {
        if (SelectedProvider is not { } provider) return;

        var name = EditorName.Trim();
        if (name.Length == 0)
        {
            ResultMessage = "请填写提供商名称";
            return;
        }

        if (Providers.Any(p => p != provider
                && string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            ResultMessage = "已存在同名提供商";
            return;
        }

        var baseUrl = EditorBaseUrl.Trim();
        if (!IsHttpUrl(baseUrl))
        {
            ResultMessage = "接口地址需为合法的 http/https 地址";
            return;
        }

        var previousName = provider.Name;
        provider.Name = name;
        provider.BaseUrl = baseUrl;
        provider.ApiKey = EditorApiKey.Trim();
        provider.Models = [.. Models.Select(model => model.ToConfig())];

        // 键里带提供商名：改名后同步改写当前选中项的键，否则重建列表后按旧键找不到模型，选中项会被清空
        _settings.ChatModelKey = ChatModelConfig.RenameProvider(_settings.ChatModelKey, previousName, name);

        Persist();
        LoadEditor(); // 规范化后的值（Trim / 改名）回填界面，工作副本与配置保持一致
        var active = provider.Models.Count(model => model.IsActive);
        ResultMessage = active > 0
            ? $"已保存「{name}」：{active} 个模型已加入模型选择列表"
            : $"已保存「{name}」：勾选模型后才会出现在对话窗口的模型选择列表";
    }

    /// <summary>丢弃未保存的改动：把编辑字段与模型勾选都还原成配置里的值。</summary>
    [RelayCommand]
    private void Revert()
    {
        if (SelectedProvider is null) return;

        // 在途请求是拿未保存的地址发的，还原后它已经没有意义了
        CancelFetch();
        ResultMessage = string.Empty;
        FetchStatus = string.Empty;
        NewModelId = string.Empty;
        LoadEditor();
    }

    /// <summary>把选中提供商的配置载入编辑表单（含模型清单）；未选中时清空。</summary>
    private void LoadEditor()
    {
        EditorName = SelectedProvider?.Name ?? string.Empty;
        EditorBaseUrl = SelectedProvider?.BaseUrl ?? string.Empty;
        EditorApiKey = SelectedProvider?.ApiKey ?? string.Empty;
        ReloadModels();
    }

    /// <summary>按配置重建选中提供商的模型清单（切换提供商、保存后回到界面都用它）。</summary>
    private void ReloadModels()
    {
        Models.Clear();
        if (SelectedProvider is { } provider)
        {
            foreach (var model in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(model.ModelId)) continue;
                Models.Add(new LlmProviderModelItem(model, this));
            }
        }

        OnPropertyChanged(nameof(HasModels));
    }

    /// <summary>把接口返回的模型标识合并进清单：已有的保留原勾选状态与显示名，新出现的追加在末尾。</summary>
    private int MergeModels(IReadOnlyList<string> modelIds)
    {
        var added = 0;
        foreach (var modelId in modelIds)
        {
            if (Models.Any(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase))) continue;

            Models.Add(new LlmProviderModelItem(new ChatProviderModel { ModelId = modelId }, this));
            added++;
        }

        if (added > 0) OnPropertyChanged(nameof(HasModels));
        return added;
    }

    /// <summary>
    /// 落盘并让对话窗口的模型选择列表跟上：集合与模型清单的变化不会触发 PropertyChanged，
    /// 所以这里显式重建 + 保存（与 Live2D 模型管理面板一致）。
    /// </summary>
    private void Persist()
    {
        _settings.RebuildChatModels();
        _settings.Save();
        OnPropertyChanged(nameof(Status));
    }

    private string UniqueProviderName(string baseName)
    {
        var name = baseName;
        for (var i = 2; Providers.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName}-{i}";
        return name;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
