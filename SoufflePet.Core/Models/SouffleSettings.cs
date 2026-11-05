// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace SoufflePet.Models;

/// <summary>
/// 应用设置（SoufflePet.json）：唯一事实源。属性变化触发 PropertyChanged，
/// 持久化与运行时副作用由订阅者统一处理（接线见 App 组合根），写入方（托盘菜单/
/// 设置界面等）只需给属性赋值，不自行 Save、不直接操作服务或窗口。
/// </summary>
public partial class SouffleSettings : ObservableObject
{
    private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "SoufflePet.json");
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 保存串行化：App 的 PropertyChanged 与设置 API 可能从不同线程同时落盘，
    /// 且设置文件是进程级唯一资源（不同实例也会写同一路径），因此锁必须是静态的。
    /// </summary>
    private static readonly object SaveGate = new();

    [ObservableProperty]
    [Description("是否启用自动表情（检测到游戏/音乐应用自动切换表情，进程白名单见 autoexpression.json）")]
    public partial bool AutoExpressionEnabled { get; set; } = true;

    /// <summary>自动表情轮询间隔（秒）</summary>
    public float AutoExpressionPollSeconds { get; init; } = 3f;

    /// <summary>当前模型在游戏状态下使用的表情名：跟随 Live2DModels 里当前模型的配置，无模型时为空。</summary>
    [JsonIgnore]
    public string AutoExpressionGameExpression => ActiveLive2DModel?.GameExpression ?? string.Empty;

    /// <summary>当前模型在音乐状态下使用的表情名：跟随 Live2DModels 里当前模型的配置，无模型时为空。</summary>
    [JsonIgnore]
    public string AutoExpressionMusicExpression => ActiveLive2DModel?.MusicExpression ?? string.Empty;

    [Description("自动表情应用列表")]
    public List<AppEntry> AutoExpressionApps { get; init; } = AppEntry.BuildDefaults();

    [ObservableProperty]
    [Description("是否启用口型律动（自动表情处于 music 状态时，系统播放声音驱动嘴部开合）")]
    public partial bool LipSyncEnabled { get; set; } = true;

    [ObservableProperty]
    [Description("是否启用目光跟随（视线跟随鼠标移动）")]
    public partial bool GazeFollowEnabled { get; set; } = true;

    [ObservableProperty]
    [Description("是否启用游戏输入联动")]
    public partial bool GamepadEnabled { get; set; } = true;

    [ObservableProperty]
    [Description("是否启用常显手柄（不等待游戏检测，直接显示手柄并驱动）")]
    public partial bool GamepadManual { get; set; }

    [ObservableProperty]
    [Description("是否启用常显音乐模式（不等待音乐检测，直接显示音乐表情并驱动口型律动）")]
    public partial bool MusicManual { get; set; }

    [ObservableProperty]
    [Description("是否显示帧率")]
    public partial bool IsShowFps { get; set; }

    [ObservableProperty]
    [Description("是否开机自启动（写入当前用户 HKCU/.../Run 注册表项）")]
    public partial bool StartWithWindows { get; set; }

    [Description("模型根目录（应用目录下 wwwroot 内的相对路径，默认 models）")]
    public string ModelsDirectory { get; init; } = "models";

    [Description("Live2D 模型列表：启动时加载上次选中的模型（默认第一个），运行中可从桌宠右键菜单切换")]
    public ObservableCollection<Live2DModelConfig> Live2DModels { get; set; } = [];

    /// <summary>当前加载的模型下标：切换模型时更新并随设置落盘，重启后沿用上次选中的模型。</summary>
    [ObservableProperty]
    [Description("当前加载的 Live2D 模型下标：右键菜单切换时写回，重启后沿用")]
    public partial int ActiveLive2DModelIndex { get; set; }

    /// <summary>当前选中的模型：列表为空时返回 null，下标越界时按第一个处理。</summary>
    [JsonIgnore]
    public Live2DModelConfig? ActiveLive2DModel =>
        Live2DModels.Count == 0
            ? null
            : Live2DModels[Math.Clamp(ActiveLive2DModelIndex, 0, Live2DModels.Count - 1)];

    /// <summary>
    /// 值没变也触发一次「当前模型下标」的变更通知：若只靠改写属性值，
    /// 移除当前模型后夹紧到的下标可能仍是原值，重载订阅就收不到通知（桌宠不会换模型）。
    /// 通知只发一次，因此不会像「先抖到 -1 再写回」那样产生两次落盘与两次重载。
    /// </summary>
    public void NotifyActiveLive2DModelIndexChanged() =>
        OnPropertyChanged(nameof(ActiveLive2DModelIndex));

    /// <summary>默认称呼：首次启动写入设置文件，之后可直接改 SoufflePet.json 调整。</summary>
    public const string DefaultPetName = "舒芙蕾";

    [ObservableProperty]
    [Description("桌面宠物的称呼（聊天窗口标题、输入提示、记忆自称与默认人设；修改后重启生效）")]
    public partial string PetName { get; set; } = DefaultPetName;

    [Description("大语言模型提供商列表（对话模型管理面板按提供商配置接口与密钥，并勾选可用模型）")]
    public ObservableCollection<ChatProviderConfig> ChatProviders { get; set; } = [];

    /// <summary>
    /// 对话窗口的模型选择列表：由 <see cref="ChatProviders" /> 里已启用的模型展开而来，
    /// 不单独落盘（唯一事实源是提供商配置），提供商配置变化后由 <see cref="RebuildChatModels" /> 重建。
    /// </summary>
    [JsonIgnore]
    [Description("对话窗口的模型选择列表（由 ChatProviders 里已启用的模型生成，不写回配置文件）")]
    public ObservableCollection<ChatModelConfig> ChatModels { get; } = [];

    [ObservableProperty]
    [Description("联网搜索用的百度千帆 API Key（web_search 工具）；留空时该工具会提示未配置")]
    public partial string WebSearchApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [Description("当前选择的对话模型：提供商/模型标识，为空表示未选择")]
    public partial string ChatModelKey { get; set; } = string.Empty;

    /// <summary>默认人设：首次启动写入设置文件，之后可直接改 SoufflePet.json 调整。</summary>
    public static readonly string DefaultChatSystemPrompt = BuildDefaultChatSystemPrompt(DefaultPetName);

    private static string BuildDefaultChatSystemPrompt(string petName) =>
        $"你是「{petName}」，一个性格活泼、温柔、有点黏人的女孩，住在用户的电脑桌面上。" +
        "请用简短、口语化的中文回复，一般不超过三句话，不要使用 Markdown 标题或代码块。";

    [ObservableProperty]
    [Description("对话系统提示词（人设），为空表示不注入系统消息")]
    public partial string ChatSystemPrompt { get; set; } = DefaultChatSystemPrompt;

    [ObservableProperty]
    [Description("是否启用长期记忆（对话落盘、档案注入与关键词检索）")]
    public partial bool ChatMemoryEnabled { get; set; } = true;

    [ObservableProperty]
    [Description("是否启用时间感知（每轮把当前本地时间附加到用户消息末尾；关闭后模型看不到任何时间信息）")]
    public partial bool TimeAwarenessEnabled { get; set; } = true;

    [ObservableProperty]
    [Description("命令执行审批规则（Manual 每次询问，FullAccess 直接执行）")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public partial ToolApprovalMode ToolApproval { get; set; } = ToolApprovalMode.Manual;
    
    [Description("最大工具调用轮数")]
    public int MaxToolRounds { get; init; } = 10;

    [ObservableProperty]
    [Description("对话历史窗口高水位（用户提问轮数）：超过后一次性裁到 HistoryKeepTurns；越小越省 token，但上下文越短")]
    public partial int HistoryWindowTurns { get; set; } = 50;

    [ObservableProperty]
    [Description("对话历史窗口低水位（用户提问轮数）：与高水位差得越多，裁剪越少、前缀缓存越稳定，但上下文压缩得越狠")]
    public partial int HistoryKeepTurns { get; set; } = 30;

    [ObservableProperty]
    [Description("已停用的技能名称（未列出的技能默认启用；技能自动从当前工作区与家目录下的 .agents/skills、.souffle/skills 读取）")]
    public partial List<string> DisabledSkills { get; set; } = [];

    [ObservableProperty]
    [Description("已停用的插件 id（未列出的插件默认启用；插件从应用目录 plugins 与家目录 .souffle/plugins 读取）")]
    public partial List<string> DisabledPlugins { get; set; } = [];

    [ObservableProperty]
    [Description("历史工作区路径列表（对话窗口底部的工作区切换记录，可删除）")]
    public partial List<string> WorkspacePaths { get; set; } = [];

    [ObservableProperty]
    [Description("当前激活的工作区路径；为空表示应用所在目录（工具相对路径、命令执行与 AGENTS.md 均以此为准）")]
    public partial string ActiveWorkspacePath { get; set; } = string.Empty;

    // ===== React UI =====

    [ObservableProperty]
    [Description("React UI 的强调色（#RRGGBB；为空时使用默认琥珀色，跟随系统开启时以系统强调色为准）")]
    public partial string UiAccentColor { get; set; } = string.Empty;

    [ObservableProperty]
    [Description("React UI 是否跟随 Windows 系统强调色（开启后忽略 UiAccentColor）")]
    public partial bool UiAccentFollowSystem { get; set; }

    [ObservableProperty]
    [Description("React UI 侧栏是否手动收起（跨断点时页面自行决定抽屉/常驻）")]
    public partial bool UiSidebarCollapsed { get; set; }

    [ObservableProperty]
    [Description("React UI 上次打开的会话 id（M4 起用于启动恢复；为空时使用会话索引里的 activeId）")]
    public partial string UiLastSessionId { get; set; } = string.Empty;

    [Description("定时任务列表（触发时把任务内容作为提示词交给桌宠发起一轮对话）")]
    public ObservableCollection<ScheduledTask> ScheduledTasks { get; set; } = [];

    public static SouffleSettings Load()
    {
        if (!File.Exists(SettingsFilePath)) return FallBackLoad();

        try
        {
            var text = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<SouffleSettings>(text) ?? new SouffleSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception e)
        {
            Log.ForContext<SouffleSettings>().Warning(e, "Failed to load settings");
            return FallBackLoad();
        }
    }

    /// <summary>修正旧配置：称呼为空时回退默认值；仍是默认人设时让其中的称呼跟随 PetName。</summary>
    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(PetName)) PetName = DefaultPetName;
        if (ChatSystemPrompt == BuildDefaultChatSystemPrompt(DefaultPetName) && PetName != DefaultPetName)
            ChatSystemPrompt = BuildDefaultChatSystemPrompt(PetName);
        // 列表被改短/清空后旧下标可能越界：回到第一个，避免启动时加载不存在的模型
        if (ActiveLive2DModelIndex < 0 || ActiveLive2DModelIndex >= Live2DModels.Count)
            ActiveLive2DModelIndex = 0;
        // 历史窗口：至少要保留 1 轮，且低水位必须小于高水位，否则裁剪逻辑会退化成每轮都切
        if (HistoryWindowTurns < 2) HistoryWindowTurns = 2;
        if (HistoryKeepTurns < 1) HistoryKeepTurns = 1;
        if (HistoryKeepTurns >= HistoryWindowTurns) HistoryKeepTurns = HistoryWindowTurns - 1;
        // 对话模型：按提供商配置展开成对话窗口用的列表
        RebuildChatModels();
    }

    /// <summary>
    /// 按「提供商 → 已启用模型」展开对话窗口的模型选择列表。
    /// 会原地合并：键相同的条目复用原实例并改写字段，避免对话窗口里正在用的选中项被换掉。
    /// </summary>
    public void RebuildChatModels()
    {
        var desired = new List<ChatModelConfig>();
        foreach (var provider in ChatProviders)
        {
            foreach (var model in provider.Models)
            {
                if (!model.IsActive || string.IsNullOrWhiteSpace(model.ModelId)) continue;

                var key = ChatModelConfig.KeyOf(provider.Name, model.ModelId);
                if (desired.Any(m => m.Key == key)) continue;

                desired.Add(new ChatModelConfig
                {
                    Provider = provider.Name,
                    Name = DisplayNameOf(model),
                    ModelId = model.ModelId,
                    BaseUrl = provider.BaseUrl,
                    ApiKey = provider.ApiKey
                });
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var target = desired[i];
            var existing = ChatModels.FirstOrDefault(m => m.Key == target.Key);
            if (existing is null)
            {
                ChatModels.Insert(i, target);
                continue;
            }

            var index = ChatModels.IndexOf(existing);
            if (index != i) ChatModels.Move(index, i);
            existing.Provider = target.Provider;
            existing.Name = target.Name;
            existing.BaseUrl = target.BaseUrl;
            existing.ApiKey = target.ApiKey;
        }

        while (ChatModels.Count > desired.Count) ChatModels.RemoveAt(ChatModels.Count - 1);
    }

    /// <summary>模型在对话窗口里的显示名：优先取自定义名称，留空时用模型标识。</summary>
    public static string DisplayNameOf(ChatProviderModel model) =>
        string.IsNullOrWhiteSpace(model.Name) ? model.ModelId.Trim() : model.Name.Trim();

    public void Save()
    {
        lock (SaveGate)
        {
            var json = JsonSerializer.Serialize(this, JsonSerializerOptions);
            var path = SettingsFilePath;
            // 临时文件名带进程/线程标记，避免多个实例或杀软扫描时争用同一个 .tmp
            var temp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
            try
            {
                File.WriteAllText(temp, json);
                RetryMove(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 清理失败不掩盖原异常 */ }
                throw;
            }
        }
    }

    /// <summary>
    /// 原子替换配置文件。目标文件偶尔被杀软/索引器短暂占用，
    /// 重试几次而不是把异常抛给调用方（设置保存失败不应打断用户操作）。
    /// </summary>
    private static void RetryMove(string source, string destination)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Thread.Sleep(20 * attempt);
            }
            catch (IOException) when (attempt >= attempts)
            {
                // 仍失败时退化为覆盖写：最坏情况是短暂读到半份文件，但不会丢配置
                File.Copy(source, destination, overwrite: true);
                File.Delete(source);
                return;
            }
        }
    }

    private static SouffleSettings FallBackLoad()
    {
        // 首次运行（或配置损坏）：沿用原来的默认模型，用户自行在 SoufflePet.json 里配模型列表
        var defaultSettings = new SouffleSettings();
        defaultSettings.Live2DModels.Add(new Live2DModelConfig { Name = "舒芙蕾", WatermarkPartId = "watermark" });
        defaultSettings.Save();
        return defaultSettings;
    }
}
