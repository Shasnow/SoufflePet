// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using Microsoft.Extensions.AI;
using SoufflePet.Core.Chat;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>工具气泡标题的生成规则：内置工具、插件模板与未知工具回退。</summary>
public sealed class ToolDisplayTests
{
    [Theory]
    [InlineData("run_shell_command", "{\"command\":\"npm test\"}", "运行命令 · npm test")]
    [InlineData("read_file", "{\"path\":\"C:\\\\tmp\\\\a.txt\"}", "读取 · a.txt")]
    [InlineData("read_file", "{\"path\":\"@D:/work/b.cs\"}", "读取 · b.cs")]
    [InlineData("web_search", "{\"query\":\"Avalonia 12\"}", "搜索 · Avalonia 12")]
    [InlineData("set_expression", "{\"name\":\"开心\"}", "切换表情 · 开心")]
    [InlineData("set_expression", "{\"name\":\"\"}", "恢复默认表情")]
    [InlineData("ask_user", "{\"questions\":[{\"question\":\"选哪个\"}]}", "提问 · 选哪个")]
    [InlineData("browser_open", "{\"url\":\"example.com\"}", "打开网页 · example.com")]
    [InlineData("take_screenshot", "{}", "截图 · 当前显示器")]
    [InlineData("write_file", "{\"path\":\"D:\\\\work\\\\a.txt\"}", "写入 · a.txt")]
    [InlineData("edit_file", "{\"path\":\"D:/work/b.cs\"}", "编辑 · b.cs")]
    [InlineData("search_files", "{\"query\":\"SearchText\"}", "查找 · SearchText")]
    [InlineData("find_files", "{\"pattern\":\"*.cs\"}", "查找文件 · *.cs")]
    [InlineData("list_plugins", "{}", "列出插件")]
    [InlineData("set_plugin_enabled", "{\"pluginId\":\"browser-use\",\"enabled\":true}", "启用插件 · browser-use")]
    [InlineData("set_plugin_enabled", "{\"pluginId\":\"browser-use\",\"enabled\":false}", "停用插件 · browser-use")]
    [InlineData("reload_plugins", "{}", "重新扫描插件")]
    public void DescribesBuiltInTools(string name, string arguments, string expected) =>
        Assert.Equal(expected, ToolDisplay.Describe(name, arguments));

    [Fact]
    public void UnknownToolReturnsNull() =>
        Assert.Null(ToolDisplay.Describe("mcp__something", "{}"));

    [Fact]
    public void MalformedArgumentsKeepVerb() =>
        Assert.Equal("运行命令", ToolDisplay.Describe("run_shell_command", "{not json"));

    [Fact]
    public void LongSubjectIsTruncated()
    {
        var command = new string('x', 100);
        var label = ToolDisplay.Describe("run_shell_command", $"{{\"command\":\"{command}\"}}");
        Assert.NotNull(label);
        Assert.EndsWith("…", label);
        Assert.True(label!.Length < command.Length);
    }

    [Fact]
    public void ExpandsPluginTemplate()
    {
        Assert.Equal("翻译 · hello world",
            ToolDisplay.Expand("翻译 · {text}", "{\"text\":\"hello world\"}"));
    }

    [Fact]
    public void MissingTemplateValueBecomesEmpty()
    {
        Assert.Equal("翻译", ToolDisplay.Expand("翻译 · {text}", "{}"));
        Assert.Null(ToolDisplay.Expand("", "{}"));
    }

    [Fact]
    public void PluginTemplateOverridesBuiltInRule()
    {
        var tool = AIFunctionFactory.Create(
            (string text) => text,
            new AIFunctionFactoryOptions
            {
                Name = "read_file",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    [ToolDisplay.DisplayPropertyKey] = "翻译 · {text}",
                },
            });

        Assert.Equal("翻译 · 你好", ToolDisplay.Describe(tool, "read_file", "{\"text\":\"你好\"}"));
    }

    [Fact]
    public void UnknownFunctionFallsBackToNull()
    {
        var tool = AIFunctionFactory.Create(
            (string text) => text,
            new AIFunctionFactoryOptions { Name = "mcp_like" });

        Assert.Null(ToolDisplay.Describe(tool, "mcp_like", "{\"text\":\"你好\"}"));
        Assert.Equal("读取 · a.txt", ToolDisplay.Describe(tool, "read_file", "{\"path\":\"a.txt\"}"));
    }
}