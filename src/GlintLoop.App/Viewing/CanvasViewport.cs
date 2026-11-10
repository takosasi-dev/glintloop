// キャンバスの倍率と表示位置の状態(FR-15・FR-16)。倍率(出力の画素の基準)を持ち、合成の寸法・出力の長辺・DPI・キャンバスの大きさから CanvasTransform に値を入れる。
// フィットの状態(IsFit)の間は、合成やキャンバスの大きさが変わるたびにフィットし直す。拡大縮小・パンで外れ、Ctrl+0 か文書を始めたときに戻る(仮の決め)。
// 画面に依存しない(UI スレッドで使うが、Dispatcher は要らない)。座標の計算は ViewMath の関数で行う。
using System.Windows;
using GlintLoop.App.CanvasLayers;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Viewing;

public sealed class CanvasViewport
{
    private readonly CanvasTransform _t;
    private SizeD _native;
    private double _outScale = 1;
    private double _dpi = 1;
    private Size _view;
    private Point _dragStart;
    private Vector _dragOffset;

    public CanvasViewport(CanvasTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _t = transform;
    }

    public CanvasTransform Transform => _t;

    /// <summary>倍率(1 が 100%。出力の画素が基準)。</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>フィットの状態(大きさが変わるとフィットし直す)。</summary>
    public bool IsFit { get; private set; }

    /// <summary>合成の寸法が入っているか(文書があるか)。</summary>
    public bool HasGeometry { get; private set; }

    public SizeD Native => _native;

    /// <summary>Lout÷max(Wc, Hc)。</summary>
    public double OutScale => _outScale;

    public double Dpi => _dpi;

    public Size ViewSize => _view;

    public bool IsDragging { get; private set; }

    /// <summary>倍率が変わったとき(表示位置だけの変化では上がらない)。</summary>
    public event Action? ZoomChanged;

    private double ScaleOf(double zoom) => ViewMath.DipPerNative(zoom, _outScale, _dpi);

    private Size ImageAt(double scale) => new(_native.W * scale, _native.H * scale);

    private bool ViewReady => _view.Width > 0 && _view.Height > 0;

    /// <summary>文書を始めたとき(FR-15): 寸法を入れてフィットにする。キャンバスの大きさがまだ無ければ、大きさが来たときにフィットする。</summary>
    public void Start(SizeD native, double outScale)
    {
        _native = native;
        _outScale = outScale;
        HasGeometry = true;
        Fit();
    }

    /// <summary>文書が無くなったとき。</summary>
    public void Clear()
    {
        HasGeometry = false;
        IsFit = false;
        IsDragging = false;
    }

    /// <summary>合成の寸法か出力の長辺が変わったとき。フィットの状態ならフィットし直し、そうでなければ倍率を保ち、中心の位置を割合で保つ。</summary>
    public void UpdateGeometry(SizeD native, double outScale)
    {
        if (!HasGeometry)
        {
            Start(native, outScale);
            return;
        }
        if (native == _native && outScale == _outScale) return;
        var oldImage = ImageAt(_t.Scale);
        _native = native;
        _outScale = outScale;
        Relayout(oldImage, _view);
    }

    /// <summary>DPI の倍率(画面の画素÷DIP)が変わったとき。</summary>
    public void SetDpi(double dpi)
    {
        if (!(dpi > 0) || dpi == _dpi) return;
        var oldImage = ImageAt(_t.Scale);
        _dpi = dpi;
        if (HasGeometry) Relayout(oldImage, _view);
    }

    /// <summary>キャンバスの大きさが変わったとき。</summary>
    public void SetViewSize(Size view)
    {
        if (view == _view) return;
        var oldView = _view;
        _view = view;
        if (HasGeometry) Relayout(ImageAt(_t.Scale), oldView);
    }

    private void Relayout(Size oldImage, Size oldView)
    {
        if (!ViewReady) return;
        if (IsFit || oldView.Width <= 0 || oldView.Height <= 0)
        {
            Fit();
            return;
        }
        var scale = ScaleOf(Zoom);
        var image = ImageAt(scale);
        var off = ViewMath.KeepRelativeCenter(_t.Offset, oldImage, oldView, image, _view);
        _t.Set(scale, ViewMath.Constrain(off, image, _view));
    }

    /// <summary>フィット(Ctrl+0)。</summary>
    public void Fit()
    {
        IsFit = true;
        if (!HasGeometry || !ViewReady) return;
        SetZoomCore(ViewMath.FitZoom(_view, _native, _outScale, _dpi));
        var scale = ScaleOf(Zoom);
        var image = ImageAt(scale);
        _t.Set(scale, ViewMath.Constrain(default, image, _view));
    }

    private void SetZoomCore(double zoom)
    {
        if (zoom == Zoom) return;
        Zoom = zoom;
        ZoomChanged?.Invoke();
    }

    private Point ViewCenter => new(_view.Width / 2, _view.Height / 2);

    /// <summary>倍率を zoom(範囲の端で止める)にする。anchor はキャンバスの点(無ければキャンバスの中心)で、その下の文書の点を動かさない。</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        if (!HasGeometry) return;
        var z = ViewMath.ClampZoom(zoom);
        IsFit = false;
        SetZoomCore(z);
        if (!ViewReady) return;
        var scale = ScaleOf(z);
        var off = ViewMath.ZoomAbout(anchor ?? ViewCenter, _t.Offset, _t.Scale, scale);
        _t.Set(scale, ViewMath.Constrain(off, ImageAt(scale), _view));
    }

    /// <summary>steps 回の拡大(正)・縮小(負)。</summary>
    public void StepZoom(int steps, Point? anchor = null)
    {
        if (steps == 0) return;
        SetZoom(ViewMath.StepZoom(Zoom, steps), anchor);
    }

    /// <summary>表示位置を delta(DIP)だけ動かす。画像がキャンバスより小さい軸は中央のまま。</summary>
    public void PanBy(Vector delta)
    {
        if (!HasGeometry || !ViewReady) return;
        SetOffset(_t.Offset + delta);
    }

    private void SetOffset(Vector offset)
    {
        var next = ViewMath.Constrain(offset, ImageAt(_t.Scale), _view);
        if (next == _t.Offset) return;
        IsFit = false;
        _t.Set(_t.Scale, next);
    }

    /// <summary>ドラッグのパンを始める(キャンバスの点)。</summary>
    public void BeginDrag(Point at)
    {
        if (!HasGeometry) return;
        IsDragging = true;
        _dragStart = at;
        _dragOffset = _t.Offset;
    }

    /// <summary>ドラッグの途中。押した点からの移動量だけ表示位置を動かす。</summary>
    public void DragTo(Point at)
    {
        if (!IsDragging || !ViewReady) return;
        SetOffset(_dragOffset + (at - _dragStart));
    }

    public void EndDrag() => IsDragging = false;
}
