// キャンバスの 1 つの層: 子を CanvasTransform で文書の画素からキャンバスの DIP へ置く Panel。画像の層と重ね描きの層がこの型を使う。
// 子の置き方は付属のプロパティで決める。DocumentRect は文書の矩形をその DIP の矩形に引き伸ばし、DocumentPoint は子の中心をその点に置く(大きさは子の DesiredSize の DIP のまま)。どちらも無い子は層の全体に置く。
// 変換が変わると測り直して並べ直し、置き方の無い子(自分で OnRender で変換を見て描く子)には InvalidateVisual を送る。UI スレッドで使う。
using System.Windows;
using System.Windows.Controls;

namespace GlintLoop.App.CanvasLayers;

public sealed class CanvasLayer : Panel
{
    public CanvasLayer(CanvasTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Transform = transform;
        transform.Changed += OnTransformChanged;
    }

    public CanvasTransform Transform { get; }

    /// <summary>子を置く文書の矩形(文書の画素)。子はその矩形のキャンバスの DIP の矩形に引き伸ばされる。</summary>
    public static readonly DependencyProperty DocumentRectProperty = DependencyProperty.RegisterAttached(
        "DocumentRect", typeof(Rect?), typeof(CanvasLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    /// <summary>子の中心を置く文書の点(文書の画素)。子の大きさは DesiredSize(DIP)のままで、倍率で変わらない。</summary>
    public static readonly DependencyProperty DocumentPointProperty = DependencyProperty.RegisterAttached(
        "DocumentPoint", typeof(Point?), typeof(CanvasLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static Rect? GetDocumentRect(UIElement element) => (Rect?)element.GetValue(DocumentRectProperty);

    public static void SetDocumentRect(UIElement element, Rect? value) => element.SetValue(DocumentRectProperty, value);

    public static Point? GetDocumentPoint(UIElement element) => (Point?)element.GetValue(DocumentPointProperty);

    public static void SetDocumentPoint(UIElement element, Point? value) => element.SetValue(DocumentPointProperty, value);

    /// <summary>子を足す(Children.Add と同じ。後に足したものが上に重なる)。</summary>
    public void Add(UIElement element) => Children.Add(element);

    /// <summary>子を外す(Children.Remove と同じ)。</summary>
    public void Remove(UIElement element) => Children.Remove(element);

    private void OnTransformChanged()
    {
        InvalidateMeasure();
        InvalidateArrange();
        foreach (UIElement c in InternalChildren)
        {
            if (GetDocumentRect(c) is null && GetDocumentPoint(c) is null) c.InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var free = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (UIElement c in InternalChildren)
        {
            if (GetDocumentRect(c) is { } r) c.Measure(Transform.ToCanvas(r) is { IsEmpty: false } cr ? cr.Size : new Size());
            else if (GetDocumentPoint(c) is not null) c.Measure(free);
            else c.Measure(availableSize);
        }
        // 層の大きさは親(CanvasView)が決める。子の大きさで層を広げない。
        return new Size();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement c in InternalChildren)
        {
            if (GetDocumentRect(c) is { } r)
            {
                var cr = Transform.ToCanvas(r);
                c.Arrange(cr.IsEmpty ? new Rect() : cr);
            }
            else if (GetDocumentPoint(c) is { } p)
            {
                var cp = Transform.ToCanvas(p);
                var d = c.DesiredSize;
                c.Arrange(new Rect(cp.X - d.Width / 2, cp.Y - d.Height / 2, d.Width, d.Height));
            }
            else
            {
                c.Arrange(new Rect(finalSize));
            }
        }
        return finalSize;
    }
}
