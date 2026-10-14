using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;
using ShufuleiPet.Services;

namespace ShufuleiPet.Utils;

/// <summary>
/// NVIDIA App 游戏库读取器：从 NVIDIA App 的应用库文件里提取它识别为游戏的应用，转换为 game 类目的进程检测条目。
/// 仅供启动时合并使用，不写回用户配置文件。
/// </summary>
internal static class NvidiaGameLibrary
{
    private static readonly ILogger Logger = Log.ForContext(typeof(NvidiaGameLibrary));

    /// <summary>NVIDIA App 应用库文件（每用户一份；缺失或解析失败时返回空列表，不影响功能）。</summary>
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NVIDIA Corporation", "NVIDIA App", "NvBackend", "ApplicationStorage.json");

    /// <summary>
    /// 读取并过滤出游戏条目：排除创意类应用（IsCreativeApplication=true），且要求支持"优化设置"（IsOpsSupported=true）；
    /// 进程名取 ImageFiles 里的 .exe 文件名（与 ProcessDetector 的 ProcessName 口径一致），列表为空时回退 DetectedFiles。
    /// </summary>
    public static List<AppEntry> Load()
    {
        if (!File.Exists(StoragePath)) return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(StoragePath));
            if (!doc.RootElement.TryGetProperty("Applications", out var apps)
                || apps.ValueKind != JsonValueKind.Array) return [];

            var result = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 跨条目去重（同一游戏可能有多个安装目录）
            foreach (var item in apps.EnumerateArray())
            {
                if (!item.TryGetProperty("Application", out var app)) continue;
                if (ReadBool(app, "IsCreativeApplication")) continue;
                if (!ReadBool(app, "IsOpsSupported")) continue;

                foreach (var name in ReadProcessNames(app))
                {
                    if (seen.Add(name)) result.Add(new AppEntry { Process = name, Category = "game" });
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            // NVIDIA App 升级可能改动文件结构，读取失败时静默跳过（回退内置默认条目）
            Logger.Warning(ex, "read nvidia application storage failed, skipped");
            return [];
        }
    }

    /// <summary>读取布尔字段：字段缺失或非 true 一律按 false 处理。</summary>
    private static bool ReadBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>取主程序 exe 名（ImageFiles）；为空时回退全量特征文件（DetectedFiles）。</summary>
    private static List<string> ReadProcessNames(JsonElement app)
    {
        var names = new List<string>();
        CollectExeNames(app, "ImageFiles", names);
        if (names.Count == 0) CollectExeNames(app, "DetectedFiles", names);
        return names;
    }

    /// <summary>收集指定数组字段里的 .exe 文件名（去扩展名）；dll/js/ico 等特征文件与启动器无关路径一并过滤。</summary>
    private static void CollectExeNames(JsonElement app, string property, List<string> names)
    {
        if (!app.TryGetProperty(property, out var files) || files.ValueKind != JsonValueKind.Array) return;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.String) continue;
            var path = file.GetString();
            if (path == null) continue;
            var name = Path.GetFileName(path);
            if (name.Length == 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(Path.GetFileNameWithoutExtension(name));
        }
    }
}
