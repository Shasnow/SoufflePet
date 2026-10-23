// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Services.Plugins;

/// <summary>
/// 每个插件独占一个可卸载的 <see cref="AssemblyLoadContext"/>：禁用时 Unload 即可尽力卸载程序集，
/// 插件之间、插件与主程序之间的类型与静态状态互不影响。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDirectory;
    private readonly AssemblyDependencyResolver? _resolver;

    /// <param name="pluginId">插件 id，仅用于日志与调试时区分上下文。</param>
    /// <param name="mainAssemblyPath">插件入口程序集的绝对路径。</param>
    public PluginLoadContext(string pluginId, string mainAssemblyPath)
        : base($"plugin:{pluginId}", isCollectible: true)
    {
        _pluginDirectory = Path.GetDirectoryName(mainAssemblyPath) ?? string.Empty;

        // deps.json 存在时用它解析插件私有依赖；插件包通常只带 dll，缺失则退回目录探测
        var depsPath = Path.ChangeExtension(mainAssemblyPath, ".deps.json");
        if (File.Exists(depsPath)) _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 契约程序集永远用宿主这一份，保证 IPlugin / AIFunction 的类型身份一致
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(IPlugin).Assembly.GetName()))
            return typeof(IPlugin).Assembly;

        // 宿主已加载的程序集（M.E.AI、BCL 等）优先共享，避免插件自带同名副本造成类型分裂
        foreach (var loaded in Default.Assemblies)
        {
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, loaded.GetName())) return loaded;
        }

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is not null && !File.Exists(path)) path = null;

        if (path is null && !string.IsNullOrEmpty(assemblyName.Name))
        {
            var candidate = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(candidate)) path = candidate;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
