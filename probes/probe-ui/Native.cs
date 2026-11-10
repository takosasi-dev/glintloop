// probe-ui が呼ぶ Win32(DWM・DPI・モニター・電源・画面の読み取り)の宣言と定数。
// 定数と構造体は Microsoft Learn の DWMWINDOWATTRIBUTE・DWM_SYSTEMBACKDROP_TYPE・SYSTEM_POWER_STATUS・GetSystemMetrics(2026-10-27 に確認)の値。
// 製品のコードではない(測定用)。
using System.Runtime.InteropServices;

namespace GlintLoop.Probes.Ui;

static class Native
{
    // DWMWINDOWATTRIBUTE: DWMWA_NCRENDERING_ENABLED=1 から順に数え、EXTENDED_FRAME_BOUNDS=9、USE_IMMERSIVE_DARK_MODE=20(明記)、
    // WINDOW_CORNER_PREFERENCE=33(明記)、BORDER_COLOR=34、CAPTION_COLOR=35、TEXT_COLOR=36、
    // VISIBLE_FRAME_BORDER_THICKNESS=37、SYSTEMBACKDROP_TYPE=38(Windows 11 Build 22621 以降。Get でも Set でも使える)。
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;    // RECT(画面の座標)。Get だけ
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // BOOL。Windows 11 Build 22000 以降
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;     // DWM_SYSTEMBACKDROP_TYPE

    // DWM_SYSTEMBACKDROP_TYPE
    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;      // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Desktop Acrylic
    public const int DWMSBT_TABBEDWINDOW = 4;    // Mica Alt

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] public static extern int DwmIsCompositionEnabled(out bool enabled);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 は ((DPI_AWARENESS_CONTEXT)-4)
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    // MONITOR_DPI_TYPE: MDT_EFFECTIVE_DPI = 0
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // 窓の状態
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public const int SM_REMOTESESSION = 0x1000; // 0 以外ならリモート デスクトップの接続先のセッション
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    // 電源。SystemStatusFlag: 0=バッテリー節約オフ、1=オン(Windows 10 以降)。ACLineStatus: 0=電池、1=電源、255=不明
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // 画面(DWM が合成したあとの絵)を読む。GetDC(NULL) は画面全体の DC
    public const uint SRCCOPY = 0x00CC0020;
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
}
