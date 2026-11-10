// キャンバス上のレイヤーの枠と 4 隅のハンドル(FR-50・FR-51・§9.3): 重ね描きの層(OverlayLayer)に、描くだけの要素(LayerFrameVisual)と、
// ポインターを受ける全面の透明な要素(LayerCanvasInput。層の一番下に置き、ほかの担当のハンドルの邪魔をしない)を置く。
// 枠の内側のドラッグで位置、隅のハンドルで一様な拡縮(D-13)、クリックで選択。ドラッグは BeginContinuous〜EndContinuous で Undo の 1 件。計算は LayerFrameMath。
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.CanvasLayers;
using GlintLoop.App.Theme;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Layers;

/// <summary>枠の操作の状態と、セッションへの反映(座標はキャンバスの DIP で受ける。テストはこのメソッドを直接呼ぶ)。</summary>
public sealed class LayerFrameController
{
    private enum Mode { None, ClickOnly, MovePending, Move, Scale }

    private readonly EditorSession _session;
    private readonly CanvasTransform _transform;
    private readonly Func<bool> _handlesShown;
    private Mode _mode;
    private Point _press;
    private FrameHit _pressHit;
    private int _layer;
    private Placement _startPlace = Placement.Identity;
    private Rect _startExact;
    private SizeD _composite;
    private bool _continuous;

    public LayerFrameController(EditorSession session, CanvasTransform transform, Func<bool> handlesShown)
    {
        _session = session;
        _transform = transform;
        _handlesShown = handlesShown;
    }

    /// <summary>ハンドルの当たり判定の一辺(DIP。16)。</summary>
    public double HandleHit { get; init; } = (double)AppResources.LayoutTokens["LayerFrameHandleHit"];

    /// <summary>各レイヤーの枠(ネイティブ寸法の座標。番号の順)。文書が無ければ空。</summary>
    public IReadOnlyList<Rect> Frames { get; private set; } = [];

    /// <summary>枠を出すレイヤーの番号(出さないときは null)。</summary>
    public int? ShownLayer { get; private set; }

    /// <summary>出している枠のキャンバスの DIP の矩形(出さないときは Rect.Empty)。</summary>
    public Rect FrameDip => ShownLayer is { } i && i < Frames.Count ? _transform.ToCanvas(Frames[i]) : Rect.Empty;

    /// <summary>ドラッグの途中か。</summary>
    public bool IsDragging => _mode is Mode.Move or Mode.Scale;

    /// <summary>枠と表示の条件を文書から読み直す(文書の変更・スタックの行の選択の変更のたびに呼ぶ)。</summary>
    public void Refresh()
    {
        var doc = _session.Document;
        if (doc is null)
        {
            Frames = [];
            ShownLayer = null;
            return;
        }
        var layout = _session.Layout(CompositeGeometry.ProxyLongSide(_session.CompositeNative()), int.MaxValue);
        var frames = new Rect[doc.Layers.Count];
        for (var i = 0; i < frames.Length; i++) frames[i] = LayerFrameMath.FrameNative(layout, i);
        Frames = frames;
        var sel = doc.Selected;
        ShownLayer = !sel.IsAll && sel.LayerIndex > 0 && doc.Layers[sel.LayerIndex].Visible && !_handlesShown() ? sel.LayerIndex : null;
    }

    /// <summary>点(DIP)の当たり判定。</summary>
    public FrameHit HitAt(Point dip) => ShownLayer is null ? FrameHit.None : LayerFrameMath.HitTest(FrameDip, dip, HandleHit);

    /// <summary>点(DIP)のカーソルの種類。</summary>
    public FrameCursor CursorAt(Point dip) => LayerFrameMath.CursorFor(HitAt(dip));

    public void PointerDown(Point dip)
    {
        EndGesture();
        if (!_session.HasDocument) return;
        Refresh();
        _press = dip;
        _pressHit = HitAt(dip);
        _mode = Mode.ClickOnly;
        if (ShownLayer is not { } i || _pressHit == FrameHit.None) return;
        var doc = _session.Document!;
        _layer = i;
        _startPlace = doc.Layers[i].Place;
        _composite = _session.CompositeNative();
        var native = CompositeGeometry.LayerNative(doc.Layers[i], _session.SourceSize);
        _startExact = LayerFrameMath.FrameExact(_composite, native, _startPlace);
        if (_pressHit == FrameHit.Inside)
        {
            _mode = Mode.MovePending;
            return;
        }
        _mode = Mode.Scale;
        Begin();
    }

    public void PointerMove(Point dip)
    {
        switch (_mode)
        {
            case Mode.MovePending:
                if (LayerFrameMath.IsClick(_press, dip)) return;
                _mode = Mode.Move;
                Begin();
                Apply(dip);
                break;
            case Mode.Move:
            case Mode.Scale:
                Apply(dip);
                break;
        }
    }

    public void PointerUp(Point dip)
    {
        var mode = _mode;
        if (mode is Mode.Move or Mode.Scale) Apply(dip);
        if (mode is (Mode.ClickOnly or Mode.MovePending) && _pressHit is (FrameHit.None or FrameHit.Inside) && LayerFrameMath.IsClick(_press, dip))
            ClickSelect(dip);
        EndGesture();
    }

    /// <summary>ドラッグをやめる(ポインターの捕捉が外れたとき)。途中までの変更は 1 件として残す。</summary>
    public void Cancel() => EndGesture();

    private void ClickSelect(Point dip)
    {
        var doc = _session.Document;
        if (doc is null || Frames.Count != doc.Layers.Count) return;
        var p = _transform.ToDocument(dip);
        var visible = doc.Layers.Select(l => l.Visible).ToArray();
        if (LayerFrameMath.PickLayer(Frames, visible, doc.Selected, p) is { } pick) _session.Select(Target.OfLayer(pick));
    }

    private void Apply(Point dip)
    {
        var doc = _session.Document;
        if (doc is null || _layer >= doc.Layers.Count) return;
        var place = _mode == Mode.Scale
            ? LayerFrameMath.Scale(_composite, _startExact, _startPlace, _pressHit, _transform.ToDocument(dip))
            : LayerFrameMath.Move(_startPlace, _press, dip, _transform.Scale);
        _session.SetLayerPlacement(_layer, place.X, place.Y, place.Scale);
    }

    private void Begin()
    {
        if (_continuous) return;
        _continuous = true;
        _session.BeginContinuous();
    }

    private void EndGesture()
    {
        _mode = Mode.None;
        if (!_continuous) return;
        _continuous = false;
        _session.EndContinuous();
    }
}

/// <summary>枠と 4 隅のハンドルを描く要素(当たり判定を持たない。層の全体に置き、変換を見て自分で描く)。</summary>
public sealed class LayerFrameVisual : FrameworkElement
{
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(LayerFrameVisual), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HandleFillProperty = DependencyProperty.Register(
        nameof(HandleFill), typeof(Brush), typeof(LayerFrameVisual), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly LayerFrameController _controller;

    public LayerFrameVisual(LayerFrameController controller)
    {
        _controller = controller;
        IsHitTestVisible = false;
        SetResourceReference(LineBrushProperty, "AccentBrush");
        SetResourceReference(HandleFillProperty, "OnAccentBrush");
    }

    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public Brush? HandleFill { get => (Brush?)GetValue(HandleFillProperty); set => SetValue(HandleFillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var r = _controller.FrameDip;
        if (r.IsEmpty || LineBrush is null) return;
        var t = AppResources.LayoutTokens;
        var line = (double)t["StrokeEmphasis"];
        var thin = (double)t["Stroke"];
        var handle = (double)t["LayerFrameHandle"];
        // 外側の 1 DIP の縁(on-accent)で、accent に近い色の画像の上でも線を見分けられるようにする
        var halo = r;
        halo.Inflate(line / 2 + thin / 2, line / 2 + thin / 2);
        dc.DrawRectangle(null, new Pen(HandleFill, thin), halo);
        dc.DrawRectangle(null, new Pen(LineBrush, line), r);
        var inner = handle - line;
        foreach (var c in new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft })
            dc.DrawRectangle(HandleFill, new Pen(LineBrush, line), new Rect(c.X - inner / 2, c.Y - inner / 2, inner, inner));
    }
}

/// <summary>
/// キャンバスのポインターを受ける全面の透明な要素(重ね描きの層の一番下)。枠とハンドルに当たった押下だけを自分で受けて捕捉し、
/// それ以外の左ドラッグはキャンバスのパン(FR-16。担当 canvas)に任せる。当たっていない場所のクリック(FR-51)は、キャンバスの全体で見張って選ぶ。
/// </summary>
public sealed class LayerCanvasInput : FrameworkElement
{
    private readonly LayerFrameController _controller;
    private Point? _clickPress;

    public LayerCanvasInput(LayerFrameController controller)
    {
        _controller = controller;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    /// <summary>
    /// 枠とハンドルに当たっていない場所のクリックを見張る(canvas はキャンバスの領域。処理済みの押下も受ける)。
    /// 押してから離すまでの移動が 4 DIP 未満なら、クリックとして選ぶ(FR-51)。
    /// </summary>
    public void WatchClicks(UIElement canvas)
    {
        canvas.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            // 上に重なったほかの担当の要素(スタックのハンドルなど)が受けた押下は、クリックとして扱わない
            var p = e.GetPosition(this);
            _clickPress = ReferenceEquals(e.OriginalSource, this) && _controller.HitAt(p) == FrameHit.None ? p : null;
        }), handledEventsToo: true);
        canvas.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (_clickPress is not { } press) return;
            _clickPress = null;
            _controller.PointerDown(press);
            _controller.PointerUp(e.GetPosition(this));
        }), handledEventsToo: true);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (_controller.HitAt(p) == FrameHit.None) return;
        _controller.PointerDown(p);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (IsMouseCaptured) _controller.PointerMove(p);
        Cursor = (_controller.IsDragging ? CursorOfDrag() : _controller.CursorAt(p)) switch
        {
            FrameCursor.Move => Cursors.SizeAll,
            FrameCursor.SizeNwse => Cursors.SizeNWSE,
            FrameCursor.SizeNesw => Cursors.SizeNESW,
            _ => null,
        };
    }

    private FrameCursor CursorOfDrag() => Cursor == Cursors.SizeNWSE ? FrameCursor.SizeNwse : Cursor == Cursors.SizeNESW ? FrameCursor.SizeNesw : FrameCursor.Move;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        _controller.PointerUp(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _controller.Cancel();
    }
}
