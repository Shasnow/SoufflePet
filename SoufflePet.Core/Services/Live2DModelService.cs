// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>模型导入/移除/修改的结果：Message 可直接展示给用户，Model 为导入后的配置（失败时为 null）。</summary>
public readonly record struct Live2DModelResult(bool Success, string Message, Live2DModelConfig? Model = null);

/// <summary> Live2D 模型文件管理：导入压缩包、删除模型目录、校验模型配置。配置列表本身由设置持有。</summary>
public interface ILive2DModelService
{
    /// <summary>模型根目录：应用目录下的 <c>wwwroot/&lt;ModelsDirectory&gt;</c>。</summary>
    string ModelsRoot { get; }

    /// <summary>
    /// 从压缩包导入模型：解压到模型根目录，并把包内的 exp3 / motion3 登记进 model3.json
    /// （表情写入 Expressions，动作按文件分组建 Motion），返回可直接展示的结果。
    /// </summary>
    Live2DModelResult Import(string archivePath);

    /// <summary>移除模型：删除该模型所在目录（只处理模型根目录内的子目录）。</summary>
    Live2DModelResult Remove(Live2DModelConfig model);

    /// <summary>修改后的配置校验：名字非空且对应的 model3.json 存在才算有效。</summary>
    Live2DModelResult Update(Live2DModelConfig model);
}

public sealed class Live2DModelService(SouffleSettings settings) : ILive2DModelService
{
    private static readonly ILogger Logger = Log.ForContext<Live2DModelService>();

    /// <summary>改 model3.json 时保留中文原文、缩进格式（默认编码器会把中文转成 \uXXXX）。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ModelsRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot", settings.ModelsDirectory);

    public Live2DModelResult Import(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return new Live2DModelResult(false, "找不到压缩包");

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && !IsJunk(e.FullName))
                .ToList();

            // 取层级最浅的 model3.json 作为模型入口，它所在的目录就是模型目录
            var modelEntry = entries
                .Where(e => e.Name.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c == '/'))
                .FirstOrDefault();
            if (modelEntry is null) return new Live2DModelResult(false, "压缩包里没有找到 model3.json");

            var prefix = modelEntry.FullName[..^modelEntry.Name.Length]; // 形如 "DeepSeek/"，根目录时为空串
            var folder = Sanitize(prefix.Length > 0
                ? prefix.TrimEnd('/').Split('/')[^1]
                : Path.GetFileNameWithoutExtension(archivePath));
            if (folder.Length == 0) folder = "model";

            var target = Path.Combine(ModelsRoot, folder);
            var existed = Directory.Exists(target);
            Directory.CreateDirectory(target);

            var targetRoot = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            foreach (var item in entries)
            {
                if (!item.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = item.FullName[prefix.Length..];
                if (relative.Length == 0) continue;

                var destination = Path.GetFullPath(
                    Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));

                // 防目录穿越：解压结果必须仍落在模型目录内
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                item.ExtractToFile(destination, overwrite: true);
            }

            var stem = ModelStem(modelEntry.Name);
            var modelJson = Path.Combine(target, modelEntry.Name);
            var (expressions, motions) = RegisterReferences(modelJson, target);

            var config = new Live2DModelConfig
            {
                Name = string.Equals(folder, stem, StringComparison.OrdinalIgnoreCase) ? folder : $"{folder}/{stem}",
                WatermarkPartId = DetectWatermark(target, stem),
            };

            Logger.Information(
                "live2d model imported {Model} expressions={Expressions} motions={Motions}",
                config.Name, expressions, motions);

            var message = existed ? $"已更新模型「{config.Name}」" : $"已导入模型「{config.Name}」";
            if (expressions > 0 || motions > 0)
                message += $"，登记表情 {expressions} 个、动作 {motions} 个";
            return new Live2DModelResult(true, message, config);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "import live2d model from {Archive} failed", archivePath);
            return new Live2DModelResult(false, $"导入失败：{ex.Message}");
        }
    }

    public Live2DModelResult Remove(Live2DModelConfig model)
    {
        try
        {
            var folder = Path.GetFullPath(Path.Combine(ModelsRoot, model.FolderName));
            if (!IsUnder(folder, Path.GetFullPath(ModelsRoot)))
                return new Live2DModelResult(false, "模型目录不在可管理范围内，未删除");

            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);

            Logger.Information("live2d model removed {Model} ({Path})", model.Name, folder);
            return new Live2DModelResult(true, $"已移除模型「{model.Name}」");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "remove live2d model {Model} failed", model.Name);
            return new Live2DModelResult(false, $"移除失败：{ex.Message}");
        }
    }

    public Live2DModelResult Update(Live2DModelConfig model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            return new Live2DModelResult(false, "模型名不能为空");

        var modelJson = Path.Combine(ModelsRoot, model.FolderName, $"{model.FileStem}.model3.json");
        return File.Exists(modelJson)
            ? new Live2DModelResult(true, $"已保存「{model.Name}」的配置", model)
            : new Live2DModelResult(false, $"找不到模型文件：{modelJson}");
    }

    /// <summary>
    /// 把目录内的 exp3 / motion3 登记进 model3.json：表情写 Expressions（名字取文件名），
    /// 动作写 Motions（文件名含 idle/待机 归入 Idle 组，其余每个文件自成一组）。
    /// 已登记过的文件跳过，重复导入不会产生重复项。
    /// </summary>
    private static (int Expressions, int Motions) RegisterReferences(string modelJsonPath, string modelDir)
    {
        var root = JsonNode.Parse(File.ReadAllText(modelJsonPath)) as JsonObject;
        if (root is null || root["FileReferences"] is not JsonObject references) return (0, 0);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expressionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (references["Expressions"] is JsonArray existingExpressions)
        {
            foreach (var item in existingExpressions)
            {
                if (item?["File"]?.GetValue<string>() is { } file) referenced.Add(Normalize(file));
                if (item?["Name"]?.GetValue<string>() is { } name) expressionNames.Add(name);
            }
        }

        if (references["Motions"] is JsonObject existingMotions)
        {
            foreach (var group in existingMotions)
            {
                if (group.Value is not JsonArray motions) continue;
                foreach (var item in motions)
                {
                    if (item?["File"]?.GetValue<string>() is { } file) referenced.Add(Normalize(file));
                }
            }
        }
        else
        {
            existingMotions = new JsonObject();
            references["Motions"] = existingMotions;
        }

        var expressions = references["Expressions"] as JsonArray ?? new JsonArray();
        var addedExpressions = 0;
        foreach (var file in Directory.EnumerateFiles(modelDir, "*.exp3.json", SearchOption.AllDirectories))
        {
            var relative = RelativePath(modelDir, file);
            var name = Stem(file, ".exp3.json");
            if (!referenced.Add(relative) || !expressionNames.Add(name)) continue;

            expressions.Add(new JsonObject { ["Name"] = name, ["File"] = relative });
            addedExpressions++;
        }
        if (expressions.Count > 0) references["Expressions"] = expressions;

        var addedMotions = 0;
        foreach (var file in Directory.EnumerateFiles(modelDir, "*.motion3.json", SearchOption.AllDirectories))
        {
            var relative = RelativePath(modelDir, file);
            if (!referenced.Add(relative)) continue;

            var stem = Stem(file, ".motion3.json");
            var groupName = stem.Contains("idle", StringComparison.OrdinalIgnoreCase) || stem.Contains("待机")
                ? "Idle"
                : stem;

            if (existingMotions[groupName] is not JsonArray group)
            {
                group = new JsonArray();
                existingMotions[groupName] = group;
            }
            group.Add(new JsonObject { ["File"] = relative });
            addedMotions++;
        }

        File.WriteAllText(modelJsonPath, root.ToJsonString(WriteOptions));
        return (addedExpressions, addedMotions);
    }

    /// <summary>从 cdi3.json 里找显示名含「水印」的部件，作为默认水印部件 Id；找不到返回空串。</summary>
    private static string DetectWatermark(string modelDir, string stem)
    {
        var cdi = Path.Combine(modelDir, $"{stem}.cdi3.json");
        if (!File.Exists(cdi)) return string.Empty;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(cdi)) is not JsonObject root
                || root["Parts"] is not JsonArray parts)
            {
                return string.Empty;
            }

            foreach (var part in parts)
            {
                var name = part?["Name"]?.GetValue<string>();
                var id = part?["Id"]?.GetValue<string>();
                if (id is { Length: > 0 } && name is not null
                    && (name.Contains("水印") || name.Contains("watermark", StringComparison.OrdinalIgnoreCase)))
                {
                    return id;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "read cdi3 {File} failed", cdi);
        }

        return string.Empty;
    }

    /// <summary>去掉形如 .model3.json / .exp3.json / .motion3.json 的后缀，得到主文件名。</summary>
    private static string Stem(string path, string suffix) =>
        Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(path)[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(path);

    private static string ModelStem(string fileName) => Stem(fileName, ".model3.json");

    private static string RelativePath(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>压缩包里的系统垃圾（__MACOSX、.DS_Store 等）不参与导入。</summary>
    private static bool IsJunk(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
        || path.Split('/').Any(part => part.StartsWith('.'));

    /// <summary>换成合法的目录名：不允许的字符换成连字符。</summary>
    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Trim().Length);
        foreach (var ch in name.Trim())
            builder.Append(invalid.Contains(ch) ? '-' : ch);

        var result = builder.ToString().Trim(' ', '.', '-');
        return result is "" or "." or ".." ? string.Empty : result;
    }

    /// <summary>path 是否位于 root 目录内（不包含 root 自身）；比较忽略大小写并兼容跨盘符。</summary>
    private static bool IsUnder(string path, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}