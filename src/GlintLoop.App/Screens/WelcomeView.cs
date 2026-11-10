// ウェルカム画面(§9.3 の画面の表・FR-7・FR-14): 見出し(title)「画像を貼る・ドロップする・開く」、drop-overlay の破線の枠のドロップ領域、4 つのボタン。
// 「画像を貼り付け」「開く」はショートカットと同じ動作(Router.Invoke)、「サンプルで試す」は取り込み、「空のキャンバス」は差し込み口(EmptyCanvasRequested。担当 layers のダイアログ)を呼ぶ。
// 取り込みが 200 ms を超えたら中央に進捗リングを出す。余白は space-6、ボタンの間隔は space-2。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlintLoop.App.Screens;

public sealed class WelcomeView : Grid
{
    private readonly ImportCoordinator _imports;

    public WelcomeView(ImportCoordinator imports)
    {
        _imports = imports;
        AutomationProperties.SetAutomationId(this, "Welcome");
        var frame = new Rectangle { StrokeDashArray = new DoubleCollection([4, 2]), IsHitTestVisible = false };
        frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        frame.SetResourceReference(Shape.StrokeThicknessProperty, "StrokeEmphasis");
        frame.SetResourceReference(MarginProperty, "Space6Thickness");
        DropArea = frame;
        Children.Add(frame);

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = ScreenTexts.Get("welcome.title"), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(StyleProperty, "TextTitle");
        title.SetResourceReference(MarginProperty, "Space4BottomThickness");
        panel.Children.Add(title);

        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        PasteButton = AddButton(row, "welcome.paste", primary: true);
        OpenButton = AddButton(row, "welcome.open");
        SampleButton = AddButton(row, "welcome.sample");
        EmptyCanvasButton = AddButton(row, "welcome.empty_canvas");
        panel.Children.Add(row);
        Children.Add(panel);

        Ring = new ProgressRing { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(Ring);

        _imports.StateChanged += UpdateRing;
    }

    /// <summary>ドロップ領域の破線の枠。</summary>
    public Rectangle DropArea { get; }

    public Button PasteButton { get; }

    public Button OpenButton { get; }

    public Button SampleButton { get; }

    public Button EmptyCanvasButton { get; }

    public ProgressRing Ring { get; }

    private static Button AddButton(Panel row, string key, bool primary = false)
    {
        var b = new Button { Content = ScreenTexts.Get(key) };
        if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.SetResourceReference(MarginProperty, "Space1Thickness");
        AutomationProperties.SetName(b, ScreenTexts.Get(key));
        row.Children.Add(b);
        return b;
    }

    private void UpdateRing()
    {
        Ring.Visibility = _imports.IsSlow ? Visibility.Visible : Visibility.Collapsed;
        Ring.Update();
    }
}
