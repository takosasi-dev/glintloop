// 起動の処理(Main の中身。FR-92・FR-104): 最初に多重起動の判定 → 負けたらパイプで連絡して終了コード 0(片付け・ログ・設定に触れない)→ 勝ったら片付けと起動 → 未処理例外の登録 → パイプのサーバーを始める。
// ウィンドウと GPU を作る部分・前面に出す部分は口の越しに使う。製品の実装は ProductionStartup、テストは偽物と実行ごとの GUID の名前を渡す。
using System.IO;
using System.Windows.Threading;
using GlintLoop.App.Platform;
using GlintLoop.App.Screens;
using GlintLoop.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace GlintLoop.App.Startup;

/// <summary>判定に勝った起動で動いているアプリ。</summary>
public interface IRunningApp : IDisposable
{
    ICrashHandler Crash { get; }

    IUnhandledExceptionSource Exceptions { get; }

    /// <summary>2 つ目の起動からの連絡(パイプのスレッドで呼ぶ)。path は検査を通ったパスか null。</summary>
    void ReceiveFromSecondInstance(string? path);

    /// <summary>メッセージのループを回し、終了コードを返す。</summary>
    int Run();
}

/// <summary>起動の処理が外から受け取るもの。</summary>
public sealed record StartupPorts(string Sid, Func<IReadOnlyList<string>, IRunningApp> StartApp)
{
    /// <summary>2 つ目の起動が、接続した直後に行う処理(前面に出す許可を渡す)。</summary>
    public Action<SafePipeHandle>? OnConnectedToFirstInstance { get; init; }

    /// <summary>相対パスの基準(2 つ目の起動の作業フォルダ)。</summary>
    public Func<string> WorkingDirectory { get; init; } = () => Environment.CurrentDirectory;

    /// <summary>サーバーが内容を読むのを待つ上限(null は既定。テストが短くする)。</summary>
    public TimeSpan? PipeReadTimeout { get; init; }

    /// <summary>測定の記録を始める(§6.8)。判定に勝った起動だけが、StartApp の直前に 1 回呼ぶ。記録しないときは null を返す。</summary>
    public Func<PerfRecorder?>? StartPerf { get; init; }
}

public static class AppStartup
{
    public static int Run(IReadOnlyList<string> args, StartupPorts ports)
    {
        var names = SingleInstanceNames.ForSid(ports.Sid);
        using var gate = SingleInstanceMutex.TryAcquire(names.Mutex);
        if (gate is null)
        {
            // 判定に負けた起動: 連絡のほかには何もしない(FR-92)。応答が無くても何もせずに終わる(E-46)
            SingleInstanceClient.Send(names.Pipe, FirstPath(args, ports.WorkingDirectory), ports.OnConnectedToFirstInstance);
            return 0;
        }
        // 測定の記録は、判定に通って動き続けるインスタンスだけが、判定の直後に始める(§6.8)
        using var perf = ports.StartPerf?.Invoke();
        using var app = ports.StartApp(args);
        app.Exceptions.UiThreadException += e => app.Crash.Handle(e, CrashSource.UiThread);
        app.Exceptions.AppDomainException += e => app.Crash.Handle(e, CrashSource.AppDomain);
        using var server = new SingleInstancePipeServer(names.Pipe, app.ReceiveFromSecondInstance, ports.PipeReadTimeout);
        server.Start();
        return app.Run();
    }

    // 最初の引数を、2 つ目の起動の作業フォルダを基準に絶対パスにする。パスとして読めない引数は、パスなしとして扱う
    private static string? FirstPath(IReadOnlyList<string> args, Func<string> workingDirectory)
    {
        if (args.Count == 0) return null;
        try
        {
            return Path.GetFullPath(args[0], workingDirectory());
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>2 つ目の起動からの連絡を UI スレッドへ渡す: 前面に出し、パスがあれば取り込みの入口を呼ぶ。</summary>
public sealed class SecondInstanceReceiver(Dispatcher ui, IWindowForeground foreground, Func<string, Task> import)
{
    /// <summary>製品の組み立て: 取り込みの入口は ScreensModule.ImportStartupArgs。</summary>
    public static SecondInstanceReceiver ForShell(AppShell shell, IWindowForeground foreground) =>
        new(shell.Window.Dispatcher, foreground, path =>
            shell.Modules.OfType<ScreensModule>().FirstOrDefault() is { } screens ? screens.ImportStartupArgs([path]) : Task.CompletedTask);

    /// <summary>パイプのスレッドから呼ぶ。UI スレッドの処理を待たない(UI スレッドが止まっていても 0x06 を返すため)。</summary>
    public void Receive(string? path) => ui.BeginInvoke(() =>
    {
        foreground.BringToFront();
        if (path is not null) _ = import(path);
    });
}
