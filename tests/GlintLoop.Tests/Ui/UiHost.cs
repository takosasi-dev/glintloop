// テストの UI スレッド(1 プロセスに 1 つの Application)。ランナーのスレッドとは別の STA スレッドで Application.Run を回し、最初に使ったときに作る。
// 使い方: UiHost.Invoke(() => ...) で UI スレッドの上で実行する。UIA のクライアントの呼び出しは、必ずテストのスレッド(Invoke の外)から行う(中から呼ぶと止まる)。
// UI スレッドで処理されなかった例外は捕まえて、次の Invoke でテスト側に投げ直す。WaitUntil は条件が真になるまで UI スレッドを回して待つ。
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using GlintLoop.App.Theme;

namespace GlintLoop.Tests.Ui;

public static class UiHost
{
    private static readonly Lazy<Dispatcher> DispatcherLazy = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static Exception? _pending;

    public static Dispatcher Dispatcher => DispatcherLazy.Value;

    public static Application App => Invoke(() => Application.Current);

    private static Dispatcher Start()
    {
        Dispatcher? d = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AppResources.Apply(app);
                PopupGuard.Install();
                app.DispatcherUnhandledException += (_, e) =>
                {
                    _pending ??= e.Exception;
                    e.Handled = true;
                };
                d = Dispatcher.CurrentDispatcher;
                ready.Set();
                app.Run();
            }
            catch (Exception e)
            {
                failure = e;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "GlintLoop.Tests UI",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        return d!;
    }

    public static void Invoke(Action action)
    {
        Dispatcher.Invoke(action);
        ThrowPending();
    }

    public static T Invoke<T>(Func<T> func)
    {
        var r = Dispatcher.Invoke(func);
        ThrowPending();
        return r;
    }

    /// <summary>UI スレッドの待ちの仕事(レイアウト・描画の準備)が済むまで待つ。</summary>
    public static void Idle()
    {
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        ThrowPending();
    }

    /// <summary>condition(UI スレッドで評価)が真になるまで待つ。timeout を過ぎたら false。</summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            Idle();
            if (Invoke(condition)) return true;
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(15);
        }
    }

    private static void ThrowPending()
    {
        var e = Interlocked.Exchange(ref _pending, null);
        if (e is not null) ExceptionDispatchInfo.Throw(e);
    }
}
