// 起動の組み立て(composition root): 渡された ShellServices から、ログ・設定・テーマ・EditorSession・ショートカット・メインウィンドウ・描画を作ってつなぐ。出力設定と lastExport(§9.11 の 2)、設定の保存の失敗と通知バー(E-43)もここでつなぐ。
// 製品の App とテストの UI ホストが同じ Create を呼ぶ(テストは偽の services を渡す)。後の担当は Shell のプロパティ(Session・Router・CanvasView など)から各部品を受け取り、Router に動作を登録する。
// ログは app_start(描画のデバイスができたとき)と app_exit(Dispose)を書く。WM_SETTINGCHANGE を受けたら OS の情報を読み直す(FR-95)。
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlintLoop.App.CanvasLayers;
using GlintLoop.App.Input;
using GlintLoop.App.Params;
using GlintLoop.App.Platform;
using GlintLoop.App.Playback;
using GlintLoop.App.Shell;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Text;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Render.Effects;

namespace GlintLoop.App.Startup;

public sealed class AppShell : IDisposable
{
    private readonly Application _app;
    private bool _disposed;
    private readonly List<IShellModule> _modules = [];
    private OutputSettings _newDocumentOutput = OutputSettings.Default;

    private AppShell(Application app, ShellServices services)
    {
        _app = app;
        Services = services;
        if (services.RunStartupMaintenance)
        {
            StartupMaintenance.Run(services.Paths, services.FileSystem);
            JsonlLogWriter.CleanUp(services.Paths.LogsDir, services.Clock, services.FileSystem);
        }
        Log = new JsonlLogWriter(services.Paths.LogsDir, services.Clock, services.Version, fileSystem: services.FileSystem);
        SettingsStore = new SettingsStore(services.Paths, services.Timers, Log, services.FileSystem);
        SettingsStore.Load();

        AppResources.Apply(app);
        Theme = new ThemeManager(app, services.Os) { Setting = Settings.Theme };

        Catalog = BuiltInEffects.Load(Log);
        Session = new EditorSession(Catalog, log: Log);
        // 取り込みは UI スレッドの外で文書を作るので、設定の JSON を読まず、UI スレッドで直した値を渡す
        _newDocumentOutput = Settings.NewDocumentOutput();
        Session.NewDocumentOutput = () => Volatile.Read(ref _newDocumentOutput);
        ImportTarget = new SessionImportTarget(Session);
        Router = new ShortcutRouter(services.Modifiers, () => Session.HasDocument);

        NoticeBar = new NoticeBar(services.Timers);
        SettingsStore.SaveFailed += OnSettingsSaveFailed;
        Dialogs = new DialogHost();
        CommandBar = new CommandBarView();
        CanvasTransform = new CanvasTransform();
        CanvasView = new CanvasView(CanvasTransform);
        Window = new MainWindow(NoticeBar, Dialogs, CommandBar, CanvasView);
        Theme.Attach(Window);
        UpdateMotion();
        Settings.Changed += OnSettingChanged;
        services.Os.Changed += OnOsChanged;

        Router.Attach(Window);
        Router.Escape.Register(EscapeTarget.Dialog, () => Dialogs.IsOpen, Dialogs.CancelTop);
        foreach (var a in new[] { ShortcutAction.PaneBoundaryLeft, ShortcutAction.PaneBoundaryRight, ShortcutAction.PaneBoundaryUp, ShortcutAction.PaneBoundaryDown })
            Router.Register(a, inv => inv.Source is PaneSplitter s && Window.MoveSplitterByKey(s, inv.Action, inv.Chord.Shift));
        Router.Register(ShortcutAction.ShowShortcutList, inv =>
        {
            if (Dialogs.Top is ShortcutListView) return true;
            Dialogs.Show(new ShortcutListView(), "DialogShortcutsWidth", "DialogShortcutsHeight", MotionCause.Keyboard);
            return true;
        });
        CommandBar.Clicked += c =>
        {
            if (c.Action is { } a) Router.Invoke(a, CommandBar.Button(c.Icon));
        };
        Session.Changed += OnSessionChanged;
        if (services.Perf is { } perf)
        {
            // 測定の記録(§6.8): 最初の描画の完了(startup)と、つまみ・枠を押している間(drag_frame)
            Window.ContentRendered += (_, _) => perf.ContentRendered();
            Session.ContinuousChanged += perf.SetDragging;
        }
        RefreshWelcome();
        PlaceWindow();
        Window.SourceInitialized += (_, _) =>
            (PresentationSource.FromVisual(Window) as HwndSource)?.AddHook(WndProc);
        Window.Closing += (_, _) => SaveWindowState();

        if (services.Gpu is { } gpu)
        {
            Recovery = new DeviceRecovery(services.Clock, services.Timers, Log, a => _app.Dispatcher.BeginInvoke(a));
            Recovery.SwitchedToSoftware += () => NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.GpuSoftware));
            RenderReady = StartRender(gpu);
            // 失敗は RaiseFatal で渡し済み。観測されない例外として 2 回目の入口に行かないようにする
            RenderReady.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            // テキストのビットマップの受け口は作るときに入れる(後から入れると、最初の描画で文字が出ないことがある)
            RendererReady = RenderReady.ContinueWith(t => new DocumentRenderer(t.Result, SourceImages(ImportTarget), catalog: Catalog) { TextRasterizer = TextRasterizer },
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        Crash = (services.CrashFactory ?? CrashHandler.ForShell)(this);
        // 作り直しの最終的な失敗は、処理されない例外として扱う(FR-93・FR-94)
        if (Recovery is not null) Recovery.FatalHandler = e => Crash.Handle(e, CrashSource.UiThread);
        AttachModules();
    }

    // 取り込んだ画像の画素(SessionImportTarget.Images)を、描画の受け口に包む
    private static FuncSourceImages SourceImages(SessionImportTarget target) =>
        new(sha => target.Images.TryGetValue(sha, out var im) ? new SourceImage(im.Width, im.Height, im.Pixels) : null);

    private void AttachModules()
    {
        var types = typeof(AppShell).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IShellModule).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IShellModule)Activator.CreateInstance(t)!)
            .OrderBy(m => m.Order).ThenBy(m => m.GetType().FullName, StringComparer.Ordinal);
        foreach (var m in types)
        {
            m.Attach(this);
            _modules.Add(m);
        }
    }

    /// <summary>組み立てる(UI スレッドで呼ぶ)。ウィンドウはまだ出さない(Show で出す)。</summary>
    public static AppShell Create(Application app, ShellServices services) => new(app, services);

    public ShellServices Services { get; }

    public JsonlLogWriter Log { get; }

    /// <summary>処理されない例外の入口(FR-104)。モジュールより先に作る。</summary>
    public ICrashHandler Crash { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings => SettingsStore.Settings;

    public ThemeManager Theme { get; }

    public IEffectCatalog Catalog { get; }

    public EditorSession Session { get; }

    public ShortcutRouter Router { get; }

    public MainWindow Window { get; }

    public NoticeBar NoticeBar { get; }

    public DialogHost Dialogs { get; }

    public CommandBarView CommandBar { get; }

    /// <summary>キャンバスの座標の変換(文書の画素 ↔ キャンバスの DIP)。CanvasView.Transform と同じもの。</summary>
    public CanvasTransform CanvasTransform { get; }

    /// <summary>キャンバスの領域の中身。ImageLayer(画像)と OverlayLayer(ハンドル・枠)に要素を足す。</summary>
    public CanvasView CanvasView { get; }

    /// <summary>描画の準備(GPU のデバイス)。services.Gpu が null のときは null(テストで描画を使わないとき)。</summary>
    public Task<RenderService>? RenderReady { get; }

    /// <summary>
    /// デバイスの消失からの復帰(FR-93)。services.Gpu が null のときは null。RenderService の参照は復帰のあとも同じものを使える(中身のデバイスだけが替わる)。
    /// FatalHandler に処理されない例外の入口(FR-104)をつなぐ。Recovered で作り直しを受け、RegisterBeforeRecreate で作り直しの前の後始末を足す。
    /// </summary>
    public DeviceRecovery? Recovery { get; }

    /// <summary>取り込みの行き先(文書と画素)。貼り付け・ドロップ・開くの ImageImporter はこれを渡して作る。画素は Images で読める。</summary>
    public SessionImportTarget ImportTarget { get; }

    /// <summary>文書の描画(プロキシ・サムネイル)。描画のデバイスができたら完了する。全担当で 1 つを共有する。GPU を渡さないテストでは null。</summary>
    public Task<DocumentRenderer>? RendererReady { get; }

    /// <summary>取り込んだ画像の画素の受け口(文書の描画と同じ中身)。</summary>
    public ISourceImages Sources => SourceImages(ImportTarget);

    /// <summary>テキストのビットマップを描く受け口(共有の DocumentRenderer に作るときに入れたもの。ハンドルの位置を求めるときにも使う)。</summary>
    public ITextRasterizer TextRasterizer { get; } = new SkiaTextRasterizer();

    /// <summary>layer.render_shrunk を文書ごとに 1 回にする記録(表示と書き出しの両方の経路で同じものを使う。E-19)。UI スレッドで使う。</summary>
    public RenderShrinkNotices ShrinkNotices { get; } = new();

    /// <summary>パラメータ欄の型ごとの入力部品の登録(text・font は担当 text が登録する)。</summary>
    public ParamEditorRegistry ParamEditors { get; } = new();

    /// <summary>
    /// いま表示しているフレームの位置(k・N・再生中か。Phase 4)。N は文書の変更のたびにここで入れ直す。k と再生中は担当 timeline が入れる。UI スレッドで使う。
    /// </summary>
    public FrameCursor Frames { get; } = new();

    /// <summary>つないだ画面の部品(IShellModule)。テストで型を指定して取り出すときに使う。</summary>
    public IReadOnlyList<IShellModule> Modules => _modules;

    /// <summary>起動時に決めたウィンドウの矩形(FR-106。Offscreen のときもこの値は計算する)。</summary>
    public Rect InitialPlacement { get; private set; }

    public void Show() => Window.Show();

    /// <summary>文書の有無に合わせてウェルカム画面の無効表示を切り替える(Session.Changed の Document で呼ぶ)。</summary>
    public void RefreshWelcome() => Window.SetWelcome(!Session.HasDocument);

    /// <summary>
    /// セッションの変更(取り込みは UI スレッドの外で上がる)。文書の有無が変わったらウェルカム画面を切り替え、フレームの位置を先頭に戻す。
    /// どの変更でも、フレーム数 N(Frames)をいまの文書から入れ直す。モジュールより先に登録しているので、各モジュールの受け手より先に済む。
    /// </summary>
    private void OnSessionChanged(SessionChange what)
    {
        if (_app.Dispatcher.CheckAccess()) ApplySessionChange(what);
        else _app.Dispatcher.BeginInvoke(() => ApplySessionChange(what));
    }

    private void ApplySessionChange(SessionChange what)
    {
        if (what.HasFlag(SessionChange.Document))
        {
            RefreshWelcome();
            Frames.Reset();
            ShrinkNotices.Reset();
        }
        // 出力設定が変わるたびに lastExport へ書く(§9.11 の 2)。新しい文書を始めただけでは書かない(.glint を開いても lastExport は変わらない)
        else if (what.HasFlag(SessionChange.Output) && Session.Document is { } changed)
        {
            Settings.LastExport = OutputSettingsJson.ToJsonObject(changed.Output);
        }
        Frames.SetFrameCount(Session.Document is { } doc ? MotionInfo.FrameCount(doc, Catalog) : 1);
    }

    private void OnSettingChanged(string key)
    {
        switch (key)
        {
            case "theme":
                Theme.Setting = Settings.Theme;
                break;
            case "reduceMotion":
                UpdateMotion();
                break;
            case "lastExport" or "defaultFormat":
                Volatile.Write(ref _newDocumentOutput, Settings.NewDocumentOutput());
                break;
        }
    }

    // E-43: 設定画面を開いているときは、設定画面の上端の帯にも出る(SettingsView が受ける)
    private void OnSettingsSaveFailed(Exception _)
    {
        void Show() => NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.SettingsSaveFailed));
        if (_app.Dispatcher.CheckAccess()) Show();
        else _app.Dispatcher.BeginInvoke(Show);
    }

    private void OnOsChanged()
    {
        if (_app.Dispatcher.CheckAccess()) UpdateMotion();
        else _app.Dispatcher.BeginInvoke(UpdateMotion);
    }

    /// <summary>アニメーションの縮小(FR-97)をウィンドウに置く(子へ継承する)。</summary>
    private void UpdateMotion() => Motion.SetReduced(Window, Motion.IsReduced(Settings.ReduceMotion, Services.Os.AnimationsEnabled));

    private void PlaceWindow()
    {
        var r = AppResources.LayoutTokens;
        var size = new Size((double)r["WindowDefaultWidth"], (double)r["WindowDefaultHeight"]);
        InitialPlacement = WindowPlacement.Restore(Settings.Window, Services.Os.Monitors, size);
        Window.WindowStartupLocation = WindowStartupLocation.Manual;
        Window.RequestClientSize(InitialPlacement.Size);
        if (Services.Offscreen)
        {
            Window.Left = OffscreenLeft;
            Window.Top = 0;
            Window.ShowActivated = false;
            Window.ShowInTaskbar = false;
        }
        else
        {
            Window.Left = InitialPlacement.X;
            Window.Top = InitialPlacement.Y;
            if (Settings.Window.Maximized == true) Window.WindowState = WindowState.Maximized;
        }
        var w = Settings.Window;
        Window.SetPaneWidths(w.LeftPane ?? Window.LeftPaneWidth, w.RightPane ?? Window.RightPaneWidth, PaneSide.Right);
    }

    /// <summary>
    /// 画面の描画かサムネイルの失敗を知らせる(E-41。UI スレッド): ログ render_fail と通知バーの render.failed。
    /// デバイスの消失(作り直しが描き直す)・取り消し・閉じたあとの失敗は数えない。
    /// </summary>
    public void ReportRenderFailure(LogRenderTarget target, Exception e)
    {
        if (!GpuErrors.IsRenderFailure(e)) return;
        Log.Write(LogEvents.RenderFail(target, GpuErrors.IsOutOfMemory(e) ? LogRenderFailReason.Oom : LogRenderFailReason.GpuError));
        NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.RenderFailed));
    }

    /// <summary>テストで出すウィンドウの左端(画面の外)。</summary>
    public const double OffscreenLeft = -20000;

    /// <summary>
    /// ウィンドウの位置(左上)・大きさ(クライアント領域。§9.3)・左右ペインの幅を設定に入れる(保存は 500 ms 後)。
    /// 画面の外に出しているときは、実際の位置の代わりに起動時に決めた位置を使う(テストが実際の画面の位置を変えないため)。
    /// </summary>
    public void SaveWindowState()
    {
        var client = Window.ClientSize;
        var pos = Services.Offscreen ? InitialPlacement.TopLeft
            : Window.WindowState == WindowState.Normal ? new Point(Window.Left, Window.Top) : Window.RestoreBounds.TopLeft;
        Settings.Window = new WindowSettings(pos.X, pos.Y, client.Width, client.Height,
            Window.WindowState == WindowState.Maximized, Window.LeftPaneWidth, Window.RightPaneWidth);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Native.WM_SETTINGCHANGE or Native.WM_THEMECHANGED) Services.Os.Refresh();
        return IntPtr.Zero;
    }

    private async Task<RenderService> StartRender(GpuAdapterRequest gpu)
    {
        RenderService render;
        try
        {
            render = await RenderService.CreateAsync(gpu, Log, Services.DeviceFactory);
        }
        catch (Exception e)
        {
            // WARP のデバイスも作れない(FR-94): 処理されない例外として扱う
            Recovery!.RaiseFatal(e);
            throw;
        }
        render.Perf = Services.Perf;
        Recovery!.Attach(render);
        if (render.FallbackReason is { } why)
            Log.Write(LogEvents.WarpFallback(why == GpuFallbackReason.FloatRtUnsupported ? LogWarpReason.FloatRtUnsupported : LogWarpReason.NoAdapter));
        var dpi = (int)Math.Round(VisualTreeHelper.GetDpi(Window).PixelsPerDip * 100);
        Log.Write(LogEvents.AppStart(Services.Os.BuildNumber, render.IsWarp ? LogAdapter.Warp : LogAdapter.Hardware, render.AdapterName, dpi));
        return render;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = _modules.Count - 1; i >= 0; i--) (_modules[i] as IDisposable)?.Dispose();
        Recovery?.Dispose();
        Settings.Changed -= OnSettingChanged;
        SettingsStore.SaveFailed -= OnSettingsSaveFailed;
        Session.Changed -= OnSessionChanged;
        Services.Os.Changed -= OnOsChanged;
        SettingsStore.Flush();
        SettingsStore.Dispose();
        Theme.Dispose();
        Log.Write(LogEvents.AppExit(0));
        // 文書の描画を先に放し、そのあとで描画のデバイスを放す
        var rendererGone = RendererReady?.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default) ?? Task.CompletedTask;
        RenderReady?.ContinueWith(t => rendererGone.ContinueWith(_ => t.Result.Dispose(), TaskScheduler.Default),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }
}
