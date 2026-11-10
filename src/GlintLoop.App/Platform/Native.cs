// 製品が呼ぶ Win32(RegGetValueW は読むだけ・モニターの一覧・DWM の属性の読み取り・WM_SETTINGCHANGE)の宣言と定数。
// 定数は Microsoft Learn の RegGetValueW(RRF_RT_REG_DWORD)・MONITORINFO・DWMWINDOWATTRIBUTE の値(probe-ui の Native.cs と同じ)。
using System.Runtime.InteropServices;

namespace GlintLoop.App.Platform;

internal static class Native
{
    // HKEY_CURRENT_USER は ((HKEY)(ULONG_PTR)((LONG)0x80000001))。64 ビットでは符号拡張した値になる。
    public static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    public const uint RRF_RT_REG_DWORD = 0x00000010;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegGetValueW(IntPtr hkey, string subKey, string value, uint flags, out uint type, out uint data, ref uint dataSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITORINFOF_PRIMARY = 1;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    // MONITOR_DPI_TYPE: MDT_EFFECTIVE_DPI = 0
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_THEMECHANGED = 0x031A;

    // DWMWINDOWATTRIBUTE: SYSTEMBACKDROP_TYPE = 38(Windows 11 Build 22621 以降)。DWM_SYSTEMBACKDROP_TYPE の MAINWINDOW = 2(Mica)
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_MAINWINDOW = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
}
