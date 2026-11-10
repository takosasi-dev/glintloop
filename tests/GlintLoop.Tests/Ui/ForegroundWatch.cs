// 前面の見張り(AC-21 の (1)): 前面のウィンドウを別のスレッドから短い間隔(約 1 ms)で見て、このプロセス(GlintLoop を組み立てたテストのプロセス)のウィンドウが前面になった回数を数える。
// テストの外の操作で前面が別のアプリに替わるのは数えない(AC-21 の (1) の 2026-10-28 の注記)。ShellAc21Tests から切り出し、ほかの画面のテストでも使えるようにした。
using System.Runtime.InteropServices;

namespace GlintLoop.Tests.Ui;

public sealed class ForegroundWatch : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly uint _pid = (uint)Environment.ProcessId;
    private volatile int _samples;
    private volatile int _ownHits;

    public ForegroundWatch()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AC-21 の前面の見張り" };
        _thread.Start();
        while (Samples == 0) Thread.Yield();
    }

    /// <summary>見た回数。</summary>
    public int Samples => _samples;

    /// <summary>このプロセスのウィンドウが前面だった回数。</summary>
    public int OwnHits => _ownHits;

    private void Run()
    {
        while (!_stop)
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && GetWindowThreadProcessId(fg, out var pid) != 0 && pid == _pid) _ownHits++;
            _samples++;
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
