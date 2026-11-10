// 製品のメインウィンドウを、一時フォルダ・偽の時計・偽の OS の情報で組み立て、画面の外(x=-20000)に非アクティブで出す(INV-18・AC-21)。
// 使い方: using var app = TestApp.Start(); app.Ui(() => ...) で UI スレッドの上で触り、Uia.Root(app.Hwnd) から UIA で探す。Restart() は同じ一時フォルダで起動し直す。キャンバスは app.CanvasView・app.CanvasTransform(UI スレッドの上で触る)。
// GPU は既定で作らない(Gpu を WARP にすると RenderService を作る)。Dispose でウィンドウを閉じ、一時フォルダを消す。
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlintLoop.App.CanvasLayers;
using GlintLoop.App.Startup;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;
using GlintLoop.Render;

namespace GlintLoop.Tests.Ui;

public sealed class TestApp : IDisposable
{
    private readonly bool _ownsRoot;
    private readonly Func<AppShell, ICrashHandler>? _crashFactory;
    private readonly PerfRecorder? _perf;

    private TestApp(string root, bool ownsRoot, FakeClock clock, FakeOsInfo os, GpuAdapterRequest? gpu, bool show, FakeFileSystem fileSystem, IDeviceFactory deviceFactory, Func<AppShell, ICrashHandler>? crashFactory = null,
        PerfRecorder? perf = null)
    {
        _perf = perf;
        Root = root;
        _ownsRoot = ownsRoot;
        _crashFactory = crashFactory;
        Paths = AppPaths.UnderOneRoot(root);
        Clock = clock;
        Os = os;
        Gpu = gpu;
        FileSystem = fileSystem;
        DeviceFactory = deviceFactory;
        var services = new ShellServices(Paths, Clock, Clock, Os, Modifiers, FileDialogs, Clipboard, gpu, "0.0.0-test")
        {
            Offscreen = true,
            FileSystem = fileSystem,
            DragRunner = DragRunner,
            DeviceFactory = deviceFactory,
            CrashDialog = CrashDialog,
            ProcessExit = ProcessExit,
            CrashFactory = crashFactory,
            Perf = perf,
        };
        Shell = UiHost.Invoke(() =>
        {
            var s = AppShell.Create(Application.Current, services);
            if (show) s.Show();
            return s;
        });
        if (show)
        {
            UiHost.Idle();
            Hwnd = UiHost.Invoke(() => new WindowInteropHelper(Shell.Window).Handle);
        }
    }

    /// <summary>
    /// 起動する。prepare は起動の前に一時フォルダへ何か置くとき(例 settings.json)に使う。os・clock・fileSystem・deviceFactory を渡すと、その偽物を使う。
    /// fileSystem の既定は素通しの FakeFileSystem、deviceFactory の既定は本物の作り方(gpu を WARP にする)。perf は測定の記録(AC-59。既定は記録しない)。
    /// </summary>
    public static TestApp Start(Action<AppPaths>? prepare = null, FakeOsInfo? os = null, FakeClock? clock = null, GpuAdapterRequest? gpu = null, bool show = true,
        FakeFileSystem? fileSystem = null, IDeviceFactory? deviceFactory = null, Func<AppShell, ICrashHandler>? crashFactory = null, PerfRecorder? perf = null)
    {
        var root = Directory.CreateTempSubdirectory("glintloop-ui-").FullName;
        prepare?.Invoke(AppPaths.UnderOneRoot(root));
        return new TestApp(root, true, clock ?? new FakeClock(), os ?? new FakeOsInfo(), gpu, show, fileSystem ?? new FakeFileSystem(), deviceFactory ?? DefaultDeviceFactory.Instance, crashFactory, perf);
    }

    /// <summary>今のアプリを閉じ、同じ一時フォルダ・同じ偽物で起動し直す(設定の保存と復元の確かめ)。古い方の一時フォルダは新しい方が消す。</summary>
    public TestApp Restart(bool show = true)
    {
        CloseShell();
        return new TestApp(Root, true, Clock, Os, Gpu, show, FileSystem, DeviceFactory, _crashFactory, _perf);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public FakeClock Clock { get; }

    public FakeOsInfo Os { get; }

    public GpuAdapterRequest? Gpu { get; }

    public FakeModifierKeys Modifiers { get; } = new();

    public FakeFileDialogs FileDialogs { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakeFileSystem FileSystem { get; }

    public FakeDragRunner DragRunner { get; } = new();

    public IDeviceFactory DeviceFactory { get; }

    /// <summary>クラッシュのダイアログの偽物(製品のダイアログを出さない)。</summary>
    public FakeCrashDialog CrashDialog { get; } = new();

    /// <summary>プロセスの終了の偽物(テストのプロセスを終わらせない)。</summary>
    public FakeProcessExit ProcessExit { get; } = new();

    public AppShell Shell { get; }

    public IntPtr Hwnd { get; }

    /// <summary>キャンバスの領域の中身(ImageLayer・OverlayLayer)。UI スレッドの上(app.Ui の中)で触る。</summary>
    public CanvasView CanvasView => Shell.CanvasView;

    /// <summary>キャンバスの座標の変換。UI スレッドの上(app.Ui の中)で触る。</summary>
    public CanvasTransform CanvasTransform => Shell.CanvasTransform;

    /// <summary>ウィンドウの DPI の倍率(画面の画素 ÷ DIP)。</summary>
    public double DpiScale => UiHost.Invoke(() => VisualTreeHelper.GetDpi(Shell.Window).DpiScaleX);

    public void Ui(Action action) => UiHost.Invoke(action);

    public T Ui<T>(Func<T> func) => UiHost.Invoke(func);

    private bool _closed;

    private void CloseShell()
    {
        if (_closed) return;
        _closed = true;
        UiHost.Invoke(() =>
        {
            Shell.Window.Close();
            Shell.Dispose();
        });
    }

    public void Dispose()
    {
        CloseShell();
        if (!_ownsRoot) return;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
