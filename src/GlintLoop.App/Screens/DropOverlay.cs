// ドラッグ中の受け入れ表示(FR-3): ウィンドウ全体を drop-overlay(accent の 12% の塗りと accent の 2 DIP の破線の枠)で覆い、
// 文書が無いときは「ここにドロップして取り込む」、あるときは「ここにドロップしてレイヤーを追加」を中央に出す。ポインターの当たり判定は持たない(下の部品にドロップを渡す)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlintLoop.App.Screens;

public sealed class DropOverlay : Grid
{
    private readonly TextBlock _text;

    public DropOverlay()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "DropOverlay");
        var face = new Rectangle { StrokeDashArray = new DoubleCollection([4, 2]) };
        face.SetResourceReference(Shape.FillProperty, "DropOverlayBrush");
        face.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        face.SetResourceReference(Shape.StrokeThicknessProperty, "StrokeEmphasis");
        Children.Add(face);
        _text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _text.SetResourceReference(StyleProperty, "TextSubtitle");
        Children.Add(_text);
    }

    /// <summary>出しているか。</summary>
    public bool IsShowing => Visibility == Visibility.Visible;

    /// <summary>出している文。</summary>
    public string Text => _text.Text;

    public void Show(bool hasDocument)
    {
        _text.Text = ScreenTexts.Get(hasDocument ? "drop.add_layer" : "drop.import");
        AutomationProperties.SetName(this, _text.Text);
        Visibility = Visibility.Visible;
    }

    public void Hide() => Visibility = Visibility.Collapsed;
}
