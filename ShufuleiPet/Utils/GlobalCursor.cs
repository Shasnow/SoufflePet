using System;
using System.Runtime.InteropServices;

namespace ShufuleiPet.Utils;

/// <summary>
/// 全局光标位置查询：GetCursorPos + ClientToScreen 换算成指定窗口客户区内的坐标（物理像素）。
/// Avalonia 的 PointerMoved 只在光标位于窗口内时触发，视线跟随与逐像素命中检测
/// 都需要在窗口外也拿到光标位置，故统一走这里轮询（各调用方自行决定换算方式）。
/// </summary>
public static class GlobalCursor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint lpPoint);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    /// <summary>
    /// 获取光标在指定窗口客户区内的位置（物理像素，原点为窗口客户区左上角）。
    /// 光标在窗口外时坐标为负或超出客户区，由调用方自行裁剪判断。句柄无效或调用失败返回 false。
    /// </summary>
    public static bool TryGetClientPos(IntPtr hwnd, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (hwnd == IntPtr.Zero) return false;
        if (!GetCursorPos(out var screen)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) return false;
        x = screen.X - origin.X;
        y = screen.Y - origin.Y;
        return true;
    }
}
