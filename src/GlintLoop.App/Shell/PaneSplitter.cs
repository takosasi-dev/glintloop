// ペインの境界(§9.3 の 4 か所): Tab で止まり、マウスのドラッグと矢印キー(8 DIP、Shift で 32 DIP。§9.12)で動かす。動かした量は Moved で渡す。
// 見た目は透明な隙間(space-2 の幅)で、フォーカスの輪だけを描く。矢印キーはショートカットの振り分け(PaneBoundary*)から MoveBy で受ける。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Input;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Shell;

public sealed class PaneSplitter : Control
{
    private Point? _dragFrom;

    public PaneSplitter(Orientation orientation, string name)
    {
        Orientation = orientation;
        Focusable = true;
        IsTabStop = true;
        FocusVisualStyle = null;
        Background = System.Windows.Media.Brushes.Transparent;
        Template = null;
        Cursor = orientation == Orientation.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
        FocusRing.SetIsEnabled(this, true);
        AutomationProperties.SetName(this, name);
        ShortcutScope.SetKind(this, orientation == Orientation.Vertical ? FocusKind.PaneBoundaryHorizontal : FocusKind.PaneBoundaryVertical);
    }

    /// <summary>Vertical は縦の線(左右に動く)、Horizontal は横の線(上下に動く)。</summary>
    public Orientation Orientation { get; }

    /// <summary>動かした量(DIP。右か下が正)。</summary>
    public event Action<PaneSplitter, double>? Moved;

    /// <summary>矢印キーの 1 回の量: 通常は space-2(8)、Shift つきは space-6(32)。</summary>
    public void MoveBy(int direction, bool large)
    {
        var step = (double)FindResource(large ? "Space6" : "Space2");
        Moved?.Invoke(this, direction * step);
    }

    protected override void OnRender(System.Windows.Media.DrawingContext dc) =>
        dc.DrawRectangle(Background, null, new Rect(RenderSize));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragFrom = e.GetPosition(Parent as IInputElement ?? this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is not { } from || !IsMouseCaptured) return;
        var p = e.GetPosition(Parent as IInputElement ?? this);
        var d = Orientation == Orientation.Vertical ? p.X - from.X : p.Y - from.Y;
        if (d == 0) return;
        _dragFrom = p;
        Moved?.Invoke(this, d);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragFrom = null;
        ReleaseMouseCapture();
    }
}
