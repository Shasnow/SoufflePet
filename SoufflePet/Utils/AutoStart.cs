using System;
using Microsoft.Win32;

namespace SoufflePet.Utils;

/// <summary>
/// 开机自启管理：写入/读取 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表项。
/// 用 HKCU（当前用户）而非 HKLM：对当前用户生效且不需要管理员权限。
/// exe 移动位置后注册表里的旧路径会失效，重新切换一次开关即可按当前路径重建。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoufflePet";

    /// <summary>当前是否已注册开机自启（读注册表实时状态，不依赖内存标记）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false; // 非 Windows 平台不支持开机自启
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false; // 读取失败按未启用处理，不抛异常打断启动流程
        }
    }

    /// <summary>写入或移除开机自启注册表项；异常向上抛，由调用方决定如何处理。</summary>
    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Auto start is only supported on Windows.");
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("open HKCU Run key failed");
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("cannot resolve current exe path");
            // 注册表数据是命令行字符串，路径含空格时必须加引号
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, false); // 不存在时不报错（可能被外部清理过）
        }
    }
}
