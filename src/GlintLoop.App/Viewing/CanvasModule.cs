// キャンバスの担当の部品(IShellModule、Order 100): 文書の表示(D-38)・拡大縮小とパン(FR-15・FR-16)・元画像(FR-17)・分割比較(FR-18)・背景(FR-20)・コマンドバーの状態。
// Session.Changed を UI スレッドに回して受け、共有の DocumentRenderer でプロキシを描き(最新だけを出す)、CanvasView の画像の層に置く。倍率と表示位置は CanvasViewport が CanvasTransform に入れる。
// Router には Undo・Redo・元画像・拡大縮小・フィット・100%・パン・分割比較の縦線を登録する。表示するフレームは shell.Frames の k・N(Phase 4)。実出力(FR-19)は口だけで、中身は担当 band が ShowActualOutput で渡す。
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.CanvasLayers;
using GlintLoop.App.Input;
using GlintLoop.App.Shell;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Viewing;

/// <summary>キャンバスの表示の状態(§9.3 の画面をまたぐ規則 (2) の優先: 元画像、実出力、分割比較、通常)。</summary>
public enum CanvasMode { None, Normal, Split, Original, ActualOutput }

public sealed class CanvasModule : IShellModule, IDisposable
{
    /// <summary>描き直しのきっかけになる変更(時間は N と t を変える。出力設定はプロキシの絵を変えないので入れない)。</summary>
    public const SessionChange RenderTriggers = SessionChange.Document | SessionChange.Layers | SessionChange.Placement | SessionChange.Stack |
        SessionChange.Params | SessionChange.CompositeSize | SessionChange.Other | SessionChange.Time;

    /// <summary>実出力の表示からプロキシの表示へ自動で戻す変更(編集の操作。FR-19)。</summary>
    public const SessionChange ActualOutputExitTriggers = RenderTriggers | SessionChange.Output;

    private AppShell _shell = null!;
    private EditorSession _session = null!;
    private CanvasView _view = null!;
    private CanvasTransform _t = null!;
    private ButtonBase _originalButton = null!;
    private ToggleButton _splitButton = null!;
    private DependencyPropertyDescriptor? _pressedDescriptor;
    private SessionDocument? _originalFor;
    private string? _originalWantedKey;
    private string? _originalRequestedKey;
    private string? _originalShownKey;
    private SizeD _pictureNative;
    private SizeD _originalNative;
    private double _originalOut = 1;
    private bool _keyHeld;
    private bool _buttonHeld;
    private int _wheelRemainder;
    private bool _lineDragging;
    private MouseButton? _panButton;
    private bool _disposed;
    private readonly List<TaskCompletionSource> _displayWaiters = new();

    public int Order => 100;

    public CanvasViewport Viewport { get; private set; } = null!;

    /// <summary>加工後の合成の描画(最新だけを出す)。GPU の無いテストでも作るが、描画は依頼しない。</summary>
    public LatestRender Processed { get; private set; } = null!;

    /// <summary>加工前の合成(元画像・分割比較の左側)の描画。</summary>
    public LatestRender Original { get; private set; } = null!;

    /// <summary>加工後の合成の表示。</summary>
    public DocumentPicture Picture { get; } = new();

    /// <summary>加工前の合成の表示。</summary>
    public DocumentPicture OriginalPicture { get; } = new();

    public CanvasSurface Surface { get; } = new();

    public SplitLine Line { get; } = new();

    public BackgroundPicker Picker { get; private set; } = null!;

    /// <summary>元画像を押している間(Backslash かボタン)。</summary>
    public bool IsOriginalHeld => _keyHeld || _buttonHeld;

    /// <summary>分割比較が入っているか。</summary>
    public bool IsSplit { get; private set; }

    /// <summary>分割比較の縦線の位置(合成の幅に対する比。初期は 0.5)。</summary>
    public double SplitPosition { get; private set; } = 0.5;

    /// <summary>加工前の合成を置いている矩形(今の合成の文書の画素)。</summary>
    public Rect OriginalRect { get; private set; }

    public CanvasMode Mode { get; private set; }

    /// <summary>実出力の中身を置く枠(画像の層。合成の矩形に引き伸ばす)。中身は ShowActualOutput で入れる。</summary>
    public ContentControl ActualOutputHost { get; } = new() { Focusable = false, IsTabStop = false, IsHitTestVisible = false };

    /// <summary>実出力を表示しているか(元画像を押している間も true のまま。Mode は元画像が優先)。</summary>
    public bool IsActualOutput { get; private set; }

    private bool _actualOutputAvailable;

    /// <summary>
    /// 実出力を出せるか(完成したキャッシュのファイルがある。担当 band が入れる)。コマンドバーの「実出力」ボタンの有効・無効になる。
    /// false にすると、表示中の実出力も閉じる。
    /// </summary>
    public bool IsActualOutputAvailable
    {
        get => _actualOutputAvailable;
        set
        {
            if (_actualOutputAvailable == value) return;
            _actualOutputAvailable = value;
            if (!value) HideActualOutput();
            ApplyCommandStates();
        }
    }

    /// <summary>実出力の表示に入ったとき・出たとき(UI スレッド)。</summary>
    public event Action? ActualOutputChanged;

    /// <summary>表示を出し直したとき(テストの待ち合わせ用)。</summary>
    public event Action? Presented;

    /// <summary>
    /// いまの文書の加工後の表示が済むのを待つ(取り込みの最初の表示。担当 screens の ImportCoordinator.WaitForDisplay に統合がつなぐ)。UI スレッドで呼ぶ。
    /// 描画の結果を画面に載せたとき、描画が失敗したとき、文書が閉じたときに完了する。描画が無い(GPU を渡さない)ときはすぐ完了する。
    /// </summary>
    public Task WaitForDisplay()
    {
        if (!CanRender || _disposed) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _displayWaiters.Add(tcs);
        ReleaseDisplayWaiters(force: false);
        return tcs.Task;
    }

    // いまの文書を出し終えたら(force なら無条件に)待っているものを完了させる
    private void ReleaseDisplayWaiters(bool force)
    {
        if (_displayWaiters.Count == 0) return;
        if (!force && _session.Document is { } doc && !ReferenceEquals(Processed.PresentedDocument, doc)) return;
        foreach (var w in _displayWaiters) w.TrySetResult();
        _displayWaiters.Clear();
    }

    public void Attach(AppShell shell)
    {
        _shell = shell;
        _session = shell.Session;
        _view = shell.CanvasView;
        _t = shell.CanvasTransform;
        Viewport = new CanvasViewport(_t);
        Processed = new LatestRender(RenderAsync);
        Original = new LatestRender(RenderAsync);
        if (shell.Services.Perf is { } perf) Processed.Replaced = Original.Replaced = perf.CountRenderCall;
        Processed.Presented += OnProcessedPresented;
        Original.Presented += OnOriginalPresented;
        Processed.Failed += OnRenderFailed;
        Original.Failed += e =>
        {
            _originalRequestedKey = null;
            OnRenderFailed(e);
        };

        Surface.Visibility = Visibility.Collapsed;
        Picture.Visibility = Visibility.Collapsed;
        OriginalPicture.Visibility = Visibility.Collapsed;
        Line.Visibility = Visibility.Collapsed;
        _view.ImageLayer.Add(Surface);
        _view.ImageLayer.Add(OriginalPicture);
        _view.ImageLayer.Add(Picture);
        ActualOutputHost.Visibility = Visibility.Collapsed;
        _view.ImageLayer.Add(ActualOutputHost);
        ApplyBackground();

        _originalButton = shell.CommandBar.Button(Icons.Original);
        _splitButton = (ToggleButton)shell.CommandBar.Button(Icons.SplitCompare);
        // クリックでも UIA の Toggle でも入・切が変わるので、Checked と Unchecked で受ける
        _splitButton.Checked += OnSplitToggled;
        _splitButton.Unchecked += OnSplitToggled;
        _pressedDescriptor = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));
        _pressedDescriptor.AddValueChanged(_originalButton, OnOriginalButtonPressed);
        Picker = new BackgroundPicker(shell.Settings, shell.CommandBar.Button(Icons.CanvasBackground));
        shell.CommandBar.Clicked += OnCommandClicked;
        shell.Settings.Changed += OnSettingChanged;

        RegisterShortcuts(shell.Router);
        shell.Router.Escape.Register(EscapeTarget.ActualOutput, () => IsActualOutput, HideActualOutput);

        _session.Changed += OnSessionChanged;
        shell.Frames.Changed += OnFramesChanged;
        if (shell.Recovery is { } recovery) recovery.Recovered += OnDeviceRecovered;
        _t.Changed += UpdateClips;
        _view.SizeChanged += OnViewSizeChanged;
        _view.Loaded += OnViewLoaded;
        shell.Window.DpiChanged += OnDpiChanged;
        _view.PreviewMouseWheel += OnWheel;
        _view.PreviewMouseDown += OnViewMouseDown;
        Surface.MouseLeftButtonDown += OnSurfaceLeftDown;
        Surface.MouseMove += OnSurfaceMove;
        Surface.MouseUp += OnSurfaceUp;
        Surface.LostMouseCapture += (_, _) => EndPan();
        Line.MouseLeftButtonDown += OnLineDown;
        Line.MouseMove += OnLineMove;
        Line.MouseLeftButtonUp += OnLineUp;
        Line.LostMouseCapture += (_, _) => _lineDragging = false;

        Viewport.SetDpi(VisualTreeHelper.GetDpi(shell.Window).DpiScaleX);
        Viewport.SetViewSize(_view.RenderSize);
        if (_session.HasDocument) Refresh(SessionChange.Document | SessionChange.AllState);
        else ApplyCommandStates();
    }

    // ================================================================ 描画

    private async Task<DocumentImage> RenderAsync(RenderRequest r)
    {
        var renderer = await (_shell.RendererReady ?? throw new InvalidOperationException("描画が無い"));
        return await renderer.RenderProxyAsync(r.Doc, DocumentPixelFormat.Pbgra32, r.K, r.N);
    }

    private bool CanRender => _shell.RendererReady is not null;

    /// <summary>
    /// 文書 doc で表示するフレーム (k, N)。N はこの文書から求め(モーションが無ければ 1)、k は shell.Frames の k を N−1 までに切る。
    /// </summary>
    public (int K, int N) DisplayFrame(SessionDocument doc)
    {
        var n = MotionInfo.FrameCount(doc, _shell.Catalog);
        return (Math.Clamp(_shell.Frames.K, 0, n - 1), n);
    }

    private void OnFramesChanged()
    {
        if (_disposed || !CanRender || _session.Document is not { } doc) return;
        var (k, n) = DisplayFrame(doc);
        Processed.Request(doc, k, n);
    }

    private void OnSessionChanged(SessionChange what)
    {
        var d = _shell.Window.Dispatcher;
        if (d.CheckAccess()) Refresh(what);
        else d.BeginInvoke(() => Refresh(what));
    }

    private void Refresh(SessionChange what)
    {
        if (_disposed) return;
        if (!_session.HasDocument)
        {
            ClearDocument();
            ApplyCommandStates();
            return;
        }
        var doc = _session.Document!;
        var (native, outScale) = Geometry(doc);
        if ((what & SessionChange.Document) != 0)
        {
            ResetOriginal();
            Viewport.Start(native, outScale);
        }
        else
        {
            Viewport.UpdateGeometry(native, outScale);
        }
        Surface.Visibility = Visibility.Visible;
        // 実出力の表示中に編集が入ったら、プロキシの表示に自動で戻る(FR-19)
        if ((what & ActualOutputExitTriggers) != 0) HideActualOutput();
        if ((what & RenderTriggers) != 0) RequestRenders(doc);
        if ((what & (SessionChange.Document | SessionChange.History)) != 0) ApplyCommandStates();
        UpdatePresentation();
    }

    /// <summary>文書の合成のネイティブ寸法と、Lout÷max(Wc, Hc)。</summary>
    private (SizeD Native, double OutScale) Geometry(SessionDocument doc)
    {
        var native = CompositeGeometry.CompositeNative(doc, _session.SourceSize);
        var lout = OutputSizing.LongSide(doc, _session.SourceSize);
        return (native, OutputSizing.Scale(lout, native));
    }

    private bool NeedsOriginal => IsOriginalHeld || IsSplit;

    private void RequestRenders(SessionDocument doc)
    {
        if (!CanRender) return;
        var (k, n) = DisplayFrame(doc);
        Processed.Request(doc, k, n);
        if (NeedsOriginal) RequestOriginal(doc);
    }

    /// <summary>
    /// 加工前の合成の描画を頼む。加工前の見た目はスタックの外の項目(配置・表示など)だけで決まるので、加工前の文書の JSON を鍵にし、
    /// 鍵が出しているものと同じなら描き直さない。鍵が違う間(描き終えるまで)は古い加工前を出さない(UpdatePresentation)。
    /// </summary>
    private void RequestOriginal(SessionDocument doc)
    {
        if (!CanRender || ReferenceEquals(_originalFor, doc)) return;
        _originalFor = doc;
        var view = OriginalView.Of(doc);
        var key = DocumentJson.Write(view);
        _originalWantedKey = key;
        if (key == _originalRequestedKey) return;
        _originalRequestedKey = key;
        Original.Request(view);
    }

    private void ResetOriginal()
    {
        Original.Clear();
        OriginalPicture.ClearImage();
        _originalFor = null;
        _originalWantedKey = null;
        _originalRequestedKey = null;
        _originalShownKey = null;
    }

    private void OnProcessedPresented(SessionDocument doc, DocumentImage image)
    {
        if (!_session.HasDocument) return;
        Picture.SetImage(image);
        _shell.Services.Perf?.DisplayUpdated();
        _pictureNative = image.Layout.Native;
        CanvasLayer.SetDocumentRect(Picture, new Rect(0, 0, _pictureNative.W, _pictureNative.H));
        foreach (var n in _shell.ShrinkNotices.Observe(image)) _shell.NoticeBar.Show(n);
        UpdatePresentation();
        ReleaseDisplayWaiters(force: false);
    }

    private void OnOriginalPresented(SessionDocument doc, DocumentImage image)
    {
        if (!_session.HasDocument) return;
        OriginalPicture.SetImage(image);
        _originalShownKey = _originalRequestedKey;
        _originalNative = image.Layout.Native;
        _originalOut = OutputSizing.Scale(OutputSizing.LongSide(doc, _session.SourceSize), _originalNative);
        UpdatePresentation();
    }

    private void OnRenderFailed(Exception e)
    {
        // 最後に成功した表示を残し、通知バーにエラー(§9.3 の編集画面)。次の編集で再び試す(E-41)
        _shell.ReportRenderFailure(LogRenderTarget.Display, e);
        ReleaseDisplayWaiters(force: true);
    }

    // デバイスを作り直した: EditorSession から表示を描き直す(FR-93)。作り直しの間に描いていなかったときも、新しいデバイスの絵に揃える
    private void OnDeviceRecovered()
    {
        if (_disposed || _session.Document is not { } doc) return;
        Processed.Redo();
        _originalFor = null;
        _originalRequestedKey = null;
        if (NeedsOriginal) RequestOriginal(doc);
    }

    private void ClearDocument()
    {
        Processed.Clear();
        ResetOriginal();
        Picture.ClearImage();
        Viewport.Clear();
        Surface.Visibility = Visibility.Collapsed;
        if (Surface.IsKeyboardFocusWithin || Line.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        _keyHeld = false;
        SetSplit(false);
        HideActualOutput();
        UpdatePresentation();
        ReleaseDisplayWaiters(force: true);
    }

    // ================================================================ 実出力(FR-19。中身は担当 band)

    /// <summary>
    /// 実出力の表示に入る。content(担当 band が作る、キャッシュのファイルを出す要素)を合成の矩形に引き伸ばして置く。
    /// 文書が無いとき・IsActualOutputAvailable が false のときは入らず false。元画像を押している間は元画像が優先(§9.3 の画面をまたぐ規則 (2))。
    /// </summary>
    public bool ShowActualOutput(UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!_session.HasDocument || !IsActualOutputAvailable) return false;
        ActualOutputHost.Content = content;
        var changed = !IsActualOutput;
        IsActualOutput = true;
        UpdatePresentation();
        if (changed) ActualOutputChanged?.Invoke();
        return true;
    }

    /// <summary>実出力の表示から出て、プロキシの表示に戻る(中身を外す)。表示していなければ何もしない。</summary>
    public void HideActualOutput()
    {
        if (!IsActualOutput) return;
        IsActualOutput = false;
        ActualOutputHost.Content = null;
        UpdatePresentation();
        ActualOutputChanged?.Invoke();
    }

    // ================================================================ 表示の切り替え

    private void UpdatePresentation()
    {
        var hasDoc = _session.HasDocument && Picture.Bitmap is not null;
        var originalReady = OriginalPicture.Bitmap is not null && _originalShownKey is not null && _originalShownKey == _originalWantedKey;
        Mode = !_session.HasDocument ? CanvasMode.None
            : IsOriginalHeld && originalReady ? CanvasMode.Original
            : IsActualOutput ? CanvasMode.ActualOutput
            : IsSplit ? CanvasMode.Split
            : CanvasMode.Normal;
        Picture.Visibility = hasDoc && Mode is not (CanvasMode.Original or CanvasMode.ActualOutput) ? Visibility.Visible : Visibility.Collapsed;
        ActualOutputHost.Visibility = Mode == CanvasMode.ActualOutput ? Visibility.Visible : Visibility.Collapsed;
        if (_pictureNative.W > 0) CanvasLayer.SetDocumentRect(ActualOutputHost, new Rect(0, 0, _pictureNative.W, _pictureNative.H));
        var showOriginal = originalReady && (Mode == CanvasMode.Original || Mode == CanvasMode.Split);
        OriginalPicture.Visibility = showOriginal ? Visibility.Visible : Visibility.Collapsed;
        if (originalReady)
        {
            OriginalRect = ViewMath.OriginalRect(_pictureNative, Viewport.OutScale, _originalNative, _originalOut);
            CanvasLayer.SetDocumentRect(OriginalPicture, OriginalRect);
        }
        // 元画像と実出力の間は、加工後の合成に合わせたハンドル・枠を隠す(仮の決め)
        _view.OverlayLayer.Visibility = Mode is CanvasMode.Original or CanvasMode.ActualOutput ? Visibility.Hidden : Visibility.Visible;
        ShowLine(Mode == CanvasMode.Split && hasDoc);
        UpdateClips();
        Presented?.Invoke();
    }

    // 縦線は分割比較の間だけ重ね描きの層に置く(切のときは UIA と Tab の巡回に出さない)
    private void ShowLine(bool show)
    {
        Line.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var inLayer = ReferenceEquals(Line.Parent, _view.OverlayLayer);
        if (show && !inLayer) _view.OverlayLayer.Add(Line);
        else if (!show && inLayer)
        {
            if (Line.IsKeyboardFocusWithin && Surface.IsVisible) Surface.Focus();
            _view.OverlayLayer.Remove(Line);
        }
    }

    /// <summary>縦線のキャンバスの x(DIP)。</summary>
    public double SplitCanvasX => _t.ToCanvas(new Point(SplitPosition * _pictureNative.W, 0)).X;

    private void UpdateClips()
    {
        if (Mode != CanvasMode.Split)
        {
            Picture.Clip = null;
            OriginalPicture.Clip = null;
            return;
        }
        var x = SplitCanvasX;
        var pr = _t.ToCanvas(new Rect(0, 0, _pictureNative.W, _pictureNative.H));
        var lx = Math.Clamp(x - pr.X, 0, pr.Width);
        Picture.Clip = new RectangleGeometry(new Rect(lx, 0, pr.Width - lx, pr.Height));
        var or = _t.ToCanvas(OriginalRect);
        var ox = Math.Clamp(x - or.X, 0, or.Width);
        OriginalPicture.Clip = new RectangleGeometry(new Rect(0, 0, ox, or.Height));
        var hit = (double)_view.FindResource("HitMin");
        var half = _t.ToDocumentLength(hit / 2);
        CanvasLayer.SetDocumentRect(Line, new Rect(SplitPosition * _pictureNative.W - half, 0, half * 2, _pictureNative.H));
    }

    // ================================================================ 元画像と分割比較

    private void OnOriginalButtonPressed(object? sender, EventArgs e) => SetHeld(ref _buttonHeld, _originalButton.IsPressed);

    private void SetHeld(ref bool flag, bool value)
    {
        if (flag == value) return;
        var before = IsOriginalHeld;
        flag = value;
        if (IsOriginalHeld && !before && _session.Document is { } doc) RequestOriginal(doc);
        UpdatePresentation();
    }

    /// <summary>Backslash の押下と離し(テストからも呼べる)。</summary>
    public void SetOriginalKeyHeld(bool held) => SetHeld(ref _keyHeld, held);

    /// <summary>分割比較の入・切。文書が無いときは入らない。</summary>
    public void SetSplit(bool on)
    {
        var next = on && _session.HasDocument;
        if (_splitButton.IsChecked != next) _splitButton.IsChecked = next;
        if (IsSplit == next) return;
        IsSplit = next;
        if (!next && Line.IsKeyboardFocusWithin && Surface.IsVisible) Surface.Focus();
        if (next && _session.Document is { } doc) RequestOriginal(doc);
        UpdatePresentation();
    }

    /// <summary>縦線の位置(0〜1)を入れる。</summary>
    public void SetSplitPosition(double f)
    {
        var v = ViewMath.ClampSplit(f);
        if (v == SplitPosition) return;
        SplitPosition = v;
        UpdateClips();
    }

    /// <summary>縦線をキャンバスの x(DIP)へ動かす(ドラッグの関数)。</summary>
    public void SetSplitFromCanvasX(double canvasX)
    {
        if (_pictureNative.W <= 0) return;
        SetSplitPosition(_t.ToDocument(new Point(canvasX, 0)).X / _pictureNative.W);
    }

    private void OnLineDown(object sender, MouseButtonEventArgs e)
    {
        if (Line.CaptureMouse()) _lineDragging = true;
        e.Handled = true;
    }

    private void OnLineMove(object sender, MouseEventArgs e)
    {
        if (_lineDragging) SetSplitFromCanvasX(e.GetPosition(_view).X);
    }

    private void OnLineUp(object sender, MouseButtonEventArgs e)
    {
        _lineDragging = false;
        Line.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ================================================================ コマンドバー

    private void OnSplitToggled(object sender, RoutedEventArgs e) => SetSplit(_splitButton.IsChecked == true);

    private void OnCommandClicked(CommandDef c)
    {
        if (c.Icon == Icons.CanvasBackground) Picker.Open();
    }

    /// <summary>
    /// ボタンの有効・無効(§9.3)。Undo・Redo は履歴の端で無効。実出力は文書があって IsActualOutputAvailable のとき(担当 band が入れる)だけ有効。
    /// ウェルカム画面の無効表示は AppShell が先に入れる(文書の有無が変わるたびに全部を入れ直すので、そのあとに入れ直す)。
    /// </summary>
    public void ApplyCommandStates()
    {
        if (_shell is null) return;
        var cb = _shell.CommandBar;
        if (_session.HasDocument)
        {
            cb.SetEnabled(Icons.Undo, _session.CanUndo);
            cb.SetEnabled(Icons.Redo, _session.CanRedo);
        }
        cb.SetEnabled(Icons.ActualOutput, _session.HasDocument && IsActualOutputAvailable);
    }

    // ================================================================ ショートカット

    private void RegisterShortcuts(ShortcutRouter r)
    {
        r.Register(ShortcutAction.Undo, _ =>
        {
            if (!_session.HasDocument) return false;
            _session.Undo();
            return true;
        });
        r.Register(ShortcutAction.Redo, _ =>
        {
            if (!_session.HasDocument) return false;
            _session.Redo();
            return true;
        });
        r.Register(ShortcutAction.ShowOriginal, inv =>
        {
            if (!_session.HasDocument) return false;
            // ボタンの Click から来た呼び出し(押している間はボタンの IsPressed で扱う)
            if (ReferenceEquals(inv.Source, _originalButton) && inv.Chord == default) return true;
            SetOriginalKeyHeld(!inv.IsRelease);
            return true;
        });
        r.Register(ShortcutAction.ZoomIn, _ => WithDoc(() => Viewport.StepZoom(1)));
        r.Register(ShortcutAction.ZoomOut, _ => WithDoc(() => Viewport.StepZoom(-1)));
        r.Register(ShortcutAction.ZoomFit, _ => WithDoc(Viewport.Fit));
        r.Register(ShortcutAction.Zoom100, _ => WithDoc(() => Viewport.SetZoom(1)));
        r.Register(ShortcutAction.PanUp, inv => WithDoc(() => Viewport.PanBy(ViewMath.PanDelta(0, -1, inv.Chord.Shift))));
        r.Register(ShortcutAction.PanDown, inv => WithDoc(() => Viewport.PanBy(ViewMath.PanDelta(0, 1, inv.Chord.Shift))));
        r.Register(ShortcutAction.PanLeft, inv => WithDoc(() => Viewport.PanBy(ViewMath.PanDelta(-1, 0, inv.Chord.Shift))));
        r.Register(ShortcutAction.PanRight, inv => WithDoc(() => Viewport.PanBy(ViewMath.PanDelta(1, 0, inv.Chord.Shift))));
        r.Register(ShortcutAction.SplitLineLeft, _ => IsSplit && WithDoc(() => SetSplitPosition(SplitPosition - ViewMath.SplitStep)));
        r.Register(ShortcutAction.SplitLineRight, _ => IsSplit && WithDoc(() => SetSplitPosition(SplitPosition + ViewMath.SplitStep)));
    }

    private bool WithDoc(Action action)
    {
        if (!_session.HasDocument) return false;
        action();
        return true;
    }

    // ================================================================ ポインター

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_session.HasDocument || (_shell.Services.Modifiers.Current & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        Wheel(e.Delta, e.GetPosition(_view));
    }

    /// <summary>Ctrl+ホイール(1 ノッチ 120 で 1 回。上向きが拡大)。anchor の下の点を動かさない。</summary>
    public void Wheel(int delta, Point anchor)
    {
        var total = _wheelRemainder + delta;
        var steps = total / ViewMath.WheelNotch;
        _wheelRemainder = total - steps * ViewMath.WheelNotch;
        if (steps != 0) Viewport.StepZoom(steps, anchor);
    }

    private void OnViewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_session.HasDocument || Mode == CanvasMode.None) return;
        BeginPan(MouseButton.Middle, e.GetPosition(_view));
        e.Handled = true;
    }

    private void OnSurfaceLeftDown(object sender, MouseButtonEventArgs e)
    {
        Surface.Focus();
        BeginPan(MouseButton.Left, e.GetPosition(_view));
        e.Handled = true;
    }

    private void BeginPan(MouseButton button, Point at)
    {
        if (_panButton is not null) return;
        if (!Surface.CaptureMouse()) return;
        _panButton = button;
        Viewport.BeginDrag(at);
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (_panButton is not null) Viewport.DragTo(e.GetPosition(_view));
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_panButton != e.ChangedButton) return;
        Surface.ReleaseMouseCapture();
        EndPan();
        e.Handled = true;
    }

    private void EndPan()
    {
        _panButton = null;
        Viewport.EndDrag();
    }

    // ================================================================ 大きさ・DPI・設定

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e) => Viewport.SetViewSize(e.NewSize);

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        Viewport.SetDpi(VisualTreeHelper.GetDpi(_view).DpiScaleX);
        Viewport.SetViewSize(_view.RenderSize);
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => Viewport.SetDpi(e.NewDpi.DpiScaleX);

    private void OnSettingChanged(string key)
    {
        if (key == "bgPreview") ApplyBackground();
    }

    private void ApplyBackground()
    {
        var bg = _shell.Settings.BgPreview;
        CanvasBackground.Apply(Picture.Backdrop, bg);
        CanvasBackground.Apply(OriginalPicture.Backdrop, bg);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDisplayWaiters(force: true);
        _session.Changed -= OnSessionChanged;
        _shell.Frames.Changed -= OnFramesChanged;
        if (_shell.Recovery is { } recovery) recovery.Recovered -= OnDeviceRecovered;
        _t.Changed -= UpdateClips;
        _shell.CommandBar.Clicked -= OnCommandClicked;
        _shell.Settings.Changed -= OnSettingChanged;
        _pressedDescriptor?.RemoveValueChanged(_originalButton, OnOriginalButtonPressed);
    }
}
