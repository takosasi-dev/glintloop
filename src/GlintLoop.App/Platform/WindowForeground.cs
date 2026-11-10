// 前面に出す処理(FR-92)。前面を変える Win32 と WPF の呼び出しは製品でこのファイルだけに置く(AC-21 の (1) の 2026-11-05 の注記)。テストはこのファイルを呼ばず、IWindowForeground の偽物を使う。
// 2 つ目の起動がパイプのサーバーのプロセスに前面に出る許可を渡し、1 つ目の起動が最小化を戻して前面に出る。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace GlintLoop.App.Platform;

public sealed class WindowForeground(Window window) : IWindowForeground
{
    // ShowWindow の nCmdShow: SW_RESTORE = 9(最小化の前の状態に戻す。最大化していたら最大化に戻る)
    private const int SW_RESTORE = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    /// <summary>2 つ目の起動の側: 接続したパイプのサーバーのプロセスに、前面に出る許可を渡す。できなくても連絡は続ける。</summary>
    public static void AllowPipeServerProcess(SafePipeHandle pipe)
    {
        if (GetNamedPipeServerProcessId(pipe, out var processId)) AllowSetForegroundWindow(processId);
    }

    /// <summary>1 つ目の起動の側(UI スレッド): 最小化を戻して前面に出す。</summary>
    public void BringToFront()
    {
        if (window.WindowState == WindowState.Minimized) ShowWindow(new WindowInteropHelper(window).Handle, SW_RESTORE);
        window.Activate();
    }
}
