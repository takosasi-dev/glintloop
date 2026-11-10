// 起動の処理に渡す製品の実装: Application を作り、製品の既定の ShellServices で Shell を組み立ててメインウィンドウを出す(片付けは AppShell の中)。
// 2 つ目の起動の前面の許可と、1 つ目の起動の前面に出す動作は WindowForeground に任せる。テストはここを呼ばず、StartupPorts に偽物を渡す。
using System.Windows;
using System.Windows.Threading;
using GlintLoop.App.Platform;
using GlintLoop.App.Theme;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;

namespace GlintLoop.App.Startup;

public static class ProductionStartup
{
    public static StartupPorts Ports()
    {
        PerfRecorder? perf = null;
        return new(SingleInstanceNames.CurrentUserSid(), args => new RunningApp(args, perf))
        {
            OnConnectedToFirstInstance = WindowForeground.AllowPipeServerProcess,
            StartPerf = () => perf = StartPerf(),
        };
    }

    // 1 秒ごとの行と ui_stall のタイマーは、どちらも UI スレッドの DispatcherTimer(優先度 Normal)。このスレッドがあとで Application を回す
    private static PerfRecorder? StartPerf()
    {
        var timers = new DispatcherTimerFactory(Dispatcher.CurrentDispatcher);
        return PerfRecorder.TryStart(Environment.GetEnvironmentVariable(PerfRecorder.EnvironmentVariable), AppPaths.Production().PerfDir,
            SystemClock.Instance, RealFileSystem.Instance, timers, timers, CurrentProcessStats.Instance);
    }

    private sealed class RunningApp : IRunningApp
    {
        private readonly Application _app;
        private readonly AppShell _shell;
        private readonly SecondInstanceReceiver _receiver;

        public RunningApp(IReadOnlyList<string> args, PerfRecorder? perf)
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            AppShell? shell = null;
            var services = ShellServices.Production(_app.Dispatcher, () => _app.MainWindow) with
            {
                CrashDialog = new WpfCrashDialog(() => shell?.Theme.Current ?? ThemeVariant.Dark),
                Perf = perf,
            };
            shell = AppShell.Create(_app, services);
            _shell = shell;
            _app.MainWindow = _shell.Window;
            _shell.Show();
            Exceptions = new WpfUnhandledExceptionSource(_app.Dispatcher);
            _receiver = SecondInstanceReceiver.ForShell(_shell, new WindowForeground(_shell.Window));
            // 起動引数の取り込み(FR-7)。Dispatcher が回り始めてから始める(取り込みの続きを UI スレッドに戻すため)
            if (_shell.Modules.OfType<Screens.ScreensModule>().FirstOrDefault() is { } screens)
                _app.Dispatcher.BeginInvoke(() => screens.ImportStartupArgs(args));
        }

        public ICrashHandler Crash => _shell.Crash;

        public IUnhandledExceptionSource Exceptions { get; }

        public void ReceiveFromSecondInstance(string? path) => _receiver.Receive(path);

        public int Run() => _app.Run();

        public void Dispose() => _shell.Dispose();
    }
}
