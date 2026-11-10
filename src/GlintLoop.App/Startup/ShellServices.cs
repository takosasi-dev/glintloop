// 起動の組み立てに外から渡すもの(§11 Phase 3 の前書きの (c)(d)・Phase 5 の前書き・INV-18): データの置き場・時計・タイマー・OS の情報・修飾キー・ファイルダイアログ・クリップボード・GPU のアダプターの要求・ファイルシステム・ドラッグの実行・デバイスの作り方。
// 製品は Production(%APPDATA%・%LOCALAPPDATA% の GlintLoop、実時計、実 OS、高性能 GPU)。テストは一時フォルダ・偽の時計・偽の OS・WARP・偽のファイルシステムを渡す。
// Offscreen は、テストのためにウィンドウを画面の外(x=-20000)に非アクティブで出す指定。
using System.Windows;
using System.Windows.Threading;
using GlintLoop.App.Platform;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;
using GlintLoop.Render;

namespace GlintLoop.App.Startup;

public sealed record ShellServices(
    AppPaths Paths,
    IClock Clock,
    ITimerFactory Timers,
    IOsInfo Os,
    IModifierKeys Modifiers,
    IFileDialogs FileDialogs,
    IClipboardService Clipboard,
    GpuAdapterRequest? Gpu,
    string Version)
{
    /// <summary>テスト用: ウィンドウを画面の外に、非アクティブで、タスクバーに出さずに表示する。</summary>
    public bool Offscreen { get; init; }

    /// <summary>起動時の片付け(.tmp・退避・古いログの削除)を行うか。多重起動の判定に負けた起動では false(FR-92)。</summary>
    public bool RunStartupMaintenance { get; init; } = true;

    /// <summary>ファイルの読み書きの口(Phase 5。テストは指定した操作で例外を投げられる偽物)。</summary>
    public IFileSystem FileSystem { get; init; } = RealFileSystem.Instance;

    /// <summary>ドラッグの実行の口(FR-82。テストは偽物)。</summary>
    public IDragRunner DragRunner { get; init; } = WpfDragRunner.Instance;

    /// <summary>GPU のデバイスの作り方(FR-93・FR-94。テストは偽物)。</summary>
    public IDeviceFactory DeviceFactory { get; init; } = DefaultDeviceFactory.Instance;

    /// <summary>処理されない例外のダイアログ(FR-104。テストは偽物)。</summary>
    public ICrashDialog CrashDialog { get; init; } = new WpfCrashDialog(() => Theme.ThemeVariant.Dark);

    /// <summary>プロセスの終了の依頼(FR-104。テストは偽物)。</summary>
    public IProcessExit ProcessExit { get; init; } = EnvironmentProcessExit.Instance;

    /// <summary>処理されない例外の入口の作り方(null は CrashHandler.ForShell。テストは呼び出しを数える包みを渡せる)。</summary>
    public Func<AppShell, ICrashHandler>? CrashFactory { get; init; }

    /// <summary>測定の記録(§6.8。GLINT_PERF が 1 のときだけ。始めるのは起動の処理で、ここでは受け取るだけ)。null は記録しない。</summary>
    public PerfRecorder? Perf { get; init; }

    /// <summary>製品の既定。</summary>
    public static ShellServices Production(Dispatcher dispatcher, Func<Window?> owner) => new(
        AppPaths.Production(),
        SystemClock.Instance,
        new DispatcherTimerFactory(dispatcher),
        new Win32OsInfo(),
        WpfModifierKeys.Instance,
        new Win32FileDialogs(owner),
        new WpfClipboard(),
        GpuAdapterRequest.HighPerformance,
        AppVersion.Current);
}

/// <summary>アプリの版の文字列(Directory.Build.props の Version。情報の版に commit の印を付けない設定にしてある)。</summary>
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
