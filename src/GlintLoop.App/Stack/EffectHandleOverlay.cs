// キャンバスのエフェクトのハンドル(FR-21)を描き、ドラッグで値を変える重ね描きの部品。CanvasView.OverlayLayer に 1 つ置き、変換が変わるたびに描き直す。
// ハンドルを持つ行を選んでいるときだけ出す。当たるのはハンドルの 16 DIP 四方だけで、ほかの場所のクリックは下へ通す。押してから離すまでのドラッグは Undo の 1 件。
// 位置・当たり判定・値の計算は HandleMath(座標を引数に取る関数)。見た目はレイヤーの枠のハンドルと同じ(8×8、塗り on-accent、枠 accent の 2 DIP)。
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

public sealed class EffectHandleOverlay : FrameworkElement
{
    private readonly StackModule _module;
    private IReadOnlyList<PlacedHandle> _handles = [];
    private Drag? _drag;

    private sealed record Drag(PlacedHandle Handle, Target Target, long Salt, StackEntry Start, TargetGeometry Geometry, double Scale, Point StartPointer, ParamDef? Param);

    public EffectHandleOverlay(StackModule module)
    {
        _module = module;
        module.Shell.CanvasTransform.Changed += InvalidateVisual;
    }

    /// <summary>いま出しているハンドル(キャンバスの DIP)。</summary>
    public IReadOnlyList<PlacedHandle> Current => Compute();

    /// <summary>ハンドルの当たり判定の大きさの半分(DIP)。</summary>
    public double HitHalf => (double)FindResource("LayerFrameHandleHit") / 2;

    public bool IsDragging => _drag is not null;

    /// <summary>選んでいる行や値が変わったとき。</summary>
    public void Refresh() => InvalidateVisual();

    private (EffectDef Def, StackEntry Entry, StackRowSelection Sel)? Active()
    {
        var doc = _module.Shell.Session.Document;
        if (doc is null || _module.SelectedRow is not { } sel || sel.Target != doc.Selected) return null;
        var stack = doc.StackOf(sel.Target);
        if (sel.Row >= stack.Count) return null;
        var entry = stack[sel.Row];
        if (_module.DefOf(entry.Id) is not { } def || !HandleMath.HasHandles(def, entry)) return null;
        return (def, entry, sel);
    }

    private IReadOnlyList<PlacedHandle> Compute()
    {
        if (Active() is not { } a) return _handles = [];
        var session = _module.Shell.Session;
        var t = _module.Shell.CanvasTransform;
        var g = HandleMath.Of(session.Document!, a.Sel.Target, session.SourceSize);
        return _handles = HandleMath.Layout(a.Def, a.Entry, g, t.ToCanvas, t.Scale, AngleRadius);
    }

    /// <summary>角度のハンドルの、軸からの距離(DIP。当たり判定の最小の 2 倍。仮の決め)。</summary>
    private double AngleRadius => (double)FindResource("HitMin") * 2;

    protected override void OnRender(DrawingContext dc)
    {
        var handles = Compute();
        if (handles.Count == 0) return;
        var accent = (Brush)FindResource("AccentBrush");
        var onAccent = (Brush)FindResource("OnAccentBrush");
        var stroke = (double)FindResource("StrokeEmphasis");
        var size = (double)FindResource("LayerFrameHandle");
        var pen = new Pen(accent, stroke);
        pen.Freeze();
        // 矩形の外形と、角度のハンドルの軸からの線
        foreach (var grp in handles.Where(h => h.Role == HandleRole.TopLeft))
        {
            var br = handles.First(h => h.Key == grp.Key && h.Role == HandleRole.BottomRight);
            dc.DrawRectangle(null, pen, new Rect(grp.Canvas, br.Canvas));
        }
        foreach (var h in handles.Where(h => h.Role is HandleRole.Angle or HandleRole.Size)) dc.DrawLine(pen, h.Axis, h.Canvas);
        var half = HitHalf;
        foreach (var h in handles)
        {
            // 当たり判定の 16 DIP 四方(透明)と、見える 8×8 の四角
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(h.Canvas.X - half, h.Canvas.Y - half, half * 2, half * 2));
            dc.DrawRectangle(onAccent, pen, new Rect(h.Canvas.X - size / 2, h.Canvas.Y - size / 2, size, size));
        }
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        HandleMath.HitTest(_handles, p.HitPoint, HitHalf) is not null ? new PointHitTestResult(this, p.HitPoint) : null;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Active() is not { } a || HandleMath.HitTest(Compute(), p, HitHalf) is not { } h) return;
        var session = _module.Shell.Session;
        var g = HandleMath.Of(session.Document!, a.Sel.Target, session.SourceSize);
        var param = EffectSchema.FindParam(a.Def, h.Key);
        _drag = new Drag(h, a.Sel.Target, a.Sel.Salt, a.Entry, g, _module.Shell.CanvasTransform.Scale, p, param);
        session.BeginContinuous();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is not { } d)
        {
            Cursor = HandleMath.HitTest(_handles, p, HitHalf) is { } h ? CursorOf(h.Role) : null;
            return;
        }
        if (Update(d, p) is { } v) Apply(d, v);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is null) return;
        EndDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_drag is null) return;
        _drag = null;
        ReleaseMouseCapture();
        _module.Shell.Session.EndContinuous();
    }

    /// <summary>ドラッグ中のポインター p(キャンバスの DIP)での値。</summary>
    private static ParamValue? Update(Drag d, Point p)
    {
        var dImage = (p - d.StartPointer) / d.Scale / d.Geometry.K;
        var param = d.Param;
        var start = d.Start.Get(d.Handle.Key);
        switch (d.Handle.Role)
        {
            case HandleRole.Point when start is PointValue pv:
                return HandleMath.DragPoint(pv, dImage, d.Geometry.Native);
            case HandleRole.Angle when start is NumberValue a && param is not null:
                return new NumberValue(HandleMath.DragAngle(a.Value, d.Handle.Axis, p, param.Min ?? -180, param.Max ?? 180, param.Step));
            case HandleRole.Size when start is NumberValue s && param is not null:
                return new NumberValue(HandleMath.DragSize(s.Value, d.Handle.Axis, d.StartPointer, p, param.Min ?? 0, param.Max ?? double.MaxValue));
            case HandleRole.Point or HandleRole.Angle or HandleRole.Size:
                return null;
            default:
                if (start is not RectValue r || param is null) return null;
                var crop = HandleMath.IsCropRect(param);
                var input = crop ? d.Geometry.Rotated : d.Geometry.Cropped;
                var aspect = crop ? HandleMath.AspectRatio((d.Start.Get("aspect") as StringValue)?.Value, d.Geometry.Rotated) : null;
                return HandleMath.DragRect(r, d.Handle.Role, dImage, input, crop, param.Min ?? 0, param.Max ?? 1000, aspect);
        }
    }

    private void Apply(Drag d, ParamValue v)
    {
        var session = _module.Shell.Session;
        var doc = session.Document;
        if (doc is null || doc.Selected != d.Target) return;
        var stack = doc.StackOf(d.Target);
        for (var i = 0; i < stack.Count; i++)
        {
            if (stack[i].Salt == d.Salt)
            {
                session.SetEffectParam(d.Target, i, d.Handle.Key, v);
                return;
            }
        }
    }

    private static Cursor CursorOf(HandleRole r) => r switch
    {
        HandleRole.Left or HandleRole.Right => Cursors.SizeWE,
        HandleRole.Top or HandleRole.Bottom => Cursors.SizeNS,
        HandleRole.TopLeft or HandleRole.BottomRight => Cursors.SizeNWSE,
        HandleRole.TopRight or HandleRole.BottomLeft => Cursors.SizeNESW,
        HandleRole.Point => Cursors.SizeAll,
        _ => Cursors.Hand,
    };
}
