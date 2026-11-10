// キーボードフォーカスの輪(仕様書 §9.1): 部品の外側に間隔 2 DIP・幅 2 DIP で、focus-ring の色で描く。部品の中には描かない。
// スタイルで FocusRing.IsEnabled を付けた部品に、フォーカスがある間だけ装飾(Adorner)を、ウィンドウの装飾の層に足す(スクロールの枠の層だと縁で輪が切れるため)。
// スクロールの外へ出た部分は、枠の表示の範囲を輪の幅だけ広げた矩形で切る。Within は中の子にフォーカスがある間(横並びの切替ボタン)。マウスの押下で得たフォーカスには出さない。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GlintLoop.App.Theme;

public static class FocusRing
{
    /// <summary>この部品にキーボードフォーカスがあるとき輪を出す。</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FocusRing), new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>この部品の中の子にキーボードフォーカスがあるとき、この部品の外側に輪を出す。</summary>
    public static readonly DependencyProperty WithinProperty = DependencyProperty.RegisterAttached(
        "Within", typeof(bool), typeof(FocusRing), new PropertyMetadata(false, OnWithinChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    public static bool GetWithin(DependencyObject d) => (bool)d.GetValue(WithinProperty);

    public static void SetWithin(DependencyObject d, bool value) => d.SetValue(WithinProperty, value);

    private static readonly DependencyProperty AdornerProperty = DependencyProperty.RegisterAttached("Adorner", typeof(RingAdorner), typeof(FocusRing));

    /// <summary>いま輪を出しているか(テスト用)。</summary>
    public static bool IsShown(UIElement e) => e.GetValue(AdornerProperty) is not null;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement u) return;
        if ((bool)e.NewValue) u.IsKeyboardFocusedChanged += OnFocusChanged;
        else u.IsKeyboardFocusedChanged -= OnFocusChanged;
    }

    private static void OnWithinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement u) return;
        if ((bool)e.NewValue) u.IsKeyboardFocusWithinChanged += OnFocusChanged;
        else u.IsKeyboardFocusWithinChanged -= OnFocusChanged;
    }

    private static void OnFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement u) return;
        var on = (bool)e.NewValue && InputManager.Current.MostRecentInputDevice is not (MouseDevice or StylusDevice);
        if (on) Show(u);
        else Hide(u);
    }

    /// <summary>
    /// テスト用: u のキーボードフォーカスが focused に変わったときと同じ処理(OnFocusChanged)を通す。非アクティブなウィンドウの部品に
    /// キーボードフォーカスを当てずに輪を出し入れするためのもの。輪の印(IsEnabled・Within)が無い部品には何もしない(ふだんも変化を受けないため)。
    /// </summary>
    internal static void RaiseKeyboardFocusForTest(UIElement u, bool focused)
    {
        if (!GetIsEnabled(u) && !GetWithin(u)) return;
        OnFocusChanged(u, new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusedProperty, !focused, focused));
    }

    private static void Show(UIElement u)
    {
        if (u.GetValue(AdornerProperty) is not null) return;
        var layer = OutermostLayer(u) ?? AdornerLayer.GetAdornerLayer(u);
        if (layer is null) return;
        var a = new RingAdorner(u);
        layer.Add(a);
        u.SetValue(AdornerProperty, a);
    }

    private static void Hide(UIElement u)
    {
        if (u.GetValue(AdornerProperty) is not RingAdorner a) return;
        a.Detach();
        (VisualTreeHelper.GetParent(a) as AdornerLayer)?.Remove(a);
        u.ClearValue(AdornerProperty);
    }

    /// <summary>祖先のうち、いちばん外側の AdornerDecorator(ウィンドウのもの)の装飾の層。</summary>
    private static AdornerLayer? OutermostLayer(DependencyObject d)
    {
        AdornerLayer? found = null;
        for (var p = d; p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is AdornerDecorator { AdornerLayer: { } l }) found = l;
        }
        return found;
    }

    /// <summary>部品の外側に輪を塗る装飾。色と寸法はリソース(FocusRingBrush・FocusRingGap・FocusRingWidth)から取る。</summary>
    private sealed class RingAdorner : Adorner
    {
        private static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
            "RingBrush", typeof(Brush), typeof(RingAdorner), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public RingAdorner(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
            SetResourceReference(RingBrushProperty, "FocusRingBrush");
            adorned.LayoutUpdated += OnLayoutUpdated;
            UpdateClip();
        }

        public void Detach() => AdornedElement.LayoutUpdated -= OnLayoutUpdated;

        private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateClip();

        // 祖先のスクロールの枠(ScrollContentPresenter)の表示の範囲を、輪の外側の大きさ(間隔 + 幅)だけ広げた矩形の交わりで切る
        private void UpdateClip()
        {
            var scope = AdornedElement as FrameworkElement;
            var grow = (double)(scope?.TryFindResource("FocusRingGap") ?? 0.0) + (double)(scope?.TryFindResource("FocusRingWidth") ?? 0.0);
            Rect? clip = null;
            for (var p = VisualTreeHelper.GetParent(AdornedElement); p is not null; p = VisualTreeHelper.GetParent(p))
            {
                if (p is not ScrollContentPresenter presenter) continue;
                var r = presenter.TransformToVisual(AdornedElement).TransformBounds(new Rect(presenter.RenderSize));
                r.Inflate(grow, grow);
                clip = clip is { } c ? Rect.Intersect(c, r) : r;
            }
            if (clip is not { } next)
            {
                if (Clip is not null) Clip = null;
                return;
            }
            if (next.IsEmpty) next = new Rect(0, 0, 0, 0);
            if (Clip is RectangleGeometry g && g.Rect == next) return;
            Clip = new RectangleGeometry(next);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = (Brush?)GetValue(RingBrushProperty);
            if (brush is null) return;
            var scope = AdornedElement as FrameworkElement;
            var gap = (double)(scope?.TryFindResource("FocusRingGap") ?? 0.0);
            var width = (double)(scope?.TryFindResource("FocusRingWidth") ?? 0.0);
            var inner = new Rect(AdornedElement.RenderSize);
            inner.Inflate(gap, gap);
            var outer = inner;
            outer.Inflate(width, width);
            var ring = new PathGeometry { FillRule = FillRule.EvenOdd };
            ring.AddGeometry(new RectangleGeometry(outer));
            ring.AddGeometry(new RectangleGeometry(inner));
            dc.DrawGeometry(brush, null, ring);
        }
    }
}
