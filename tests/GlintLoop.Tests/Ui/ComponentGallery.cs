// テスト専用の見本: §9.1 の部品の状態の表の 13 行の部品を 1 つずつ並べた UserControl。メインウィンドウの領域(LeftPaneHost など)に差し込んで、
// 名前・大きさ・Tab の巡回・フォーカスの輪(AC-21 の (2)〜(5))を調べる。後の担当が自分の UserControl を領域に差し込むときの見本にもなる。
// リストの中の部品(タイル・行)は、輪が一覧の表示の範囲で切れないよう、周りに space-1 以上の余白を取る(ItemContainerStyle の Margin)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlintLoop.App.Controls;
using GlintLoop.App.Theme;

namespace GlintLoop.Tests.Ui;

public sealed class ComponentGallery : UserControl
{
    /// <summary>ドロップダウン(閉じた状態)の見本。</summary>
    public ComboBox DropDown { get; }

    /// <summary>13 行のそれぞれ(副ボタンの行はドロップダウンを足して 2 つ): (表の行の名前, フォーカスを当てる要素, 輪の基準の要素)。</summary>
    public List<(string Row, UIElement Focus, FrameworkElement Ring)> Rows { get; } = new();

    public ComponentGallery()
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        Content = panel;
        T Add<T>(T e) where T : FrameworkElement
        {
            e.Margin = (Thickness)FindResource("Space2Thickness");
            e.VerticalAlignment = VerticalAlignment.Top;
            panel.Children.Add(e);
            return e;
        }
        Style S(string key) => (Style)FindResource(key);

        var primary = Add(new Button { Content = "コピー", Style = S("PrimaryButton") });
        Rows.Add(("主ボタン", primary, primary));
        var secondary = Add(new Button { Content = "保存" });
        Rows.Add(("副ボタン", secondary, secondary));
        // 副ボタンの行の 2 つ目の見本: ドロップダウン(閉じた状態)
        DropDown = Add(new ComboBox { Width = (double)FindResource("ExportFormatWidth") });
        AutomationProperties.SetName(DropDown, "形式");
        foreach (var s in new[] { "PNG", "GIF", "WebP" }) DropDown.Items.Add(new ComboBoxItem { Content = s });
        DropDown.SelectedIndex = 0;
        Rows.Add(("副ボタン(ドロップダウン)", DropDown, DropDown));
        var icon = Add(new Button { Content = Icons.Create(Icons.Add), Style = S("IconButton"), ToolTip = Icons.Add.Name });
        AutomationProperties.SetName(icon, Icons.Add.Name);
        Rows.Add(("アイコンボタン", icon, icon));
        var slider = Add(new Slider { Width = (double)FindResource("TimelineScrubMinWidth"), Minimum = 0, Maximum = 100, Value = 40 });
        AutomationProperties.SetName(slider, "強さ");
        Rows.Add(("スライダー", slider, slider));
        var toggle = Add(new ToggleButton { Content = "トグル", Style = S("ToggleSwitch") });
        Rows.Add(("トグル", toggle, toggle));

        var seg = Add(new ListBox { Style = S("Segmented") });
        AutomationProperties.SetName(seg, "ディザ");
        foreach (var s in new[] { "青色雑音", "誤差拡散", "なし" }) seg.Items.Add(new ListBoxItem { Content = s });
        seg.SelectedIndex = 0;
        Rows.Add(("横並びの切替ボタン", (UIElement)seg.Items[1], seg));

        var text = Add(new TextBox { Width = (double)FindResource("TimelineHoldWidth") });
        AutomationProperties.SetName(text, "名前");
        Rows.Add(("テキスト入力", text, text));
        var number = Add(new NumberBox { Width = (double)FindResource("TimelineLengthWidth"), Minimum = 0, Maximum = 10, Decimals = 1, Value = 2 });
        AutomationProperties.SetName(number, "長さ");

        var swatch = Add(new Button { Style = S("ColorSwatch"), Background = Brushes.Transparent });
        AutomationProperties.SetName(swatch, "色");
        Rows.Add(("色の四角", swatch, swatch));

        var tiles = Add(MakeList("ルックの一覧", "TileItem", ["タイルA", "タイルB"]));
        tiles.SelectedIndex = 1;
        Rows.Add(("タイル", (UIElement)tiles.Items[0], (FrameworkElement)tiles.Items[0]));
        Rows.Add(("選択中のタイル・行", (UIElement)tiles.Items[1], (FrameworkElement)tiles.Items[1]));
        var rows = Add(MakeList("スタック", "RowItem", ["行A", "行B"]));
        rows.SelectedIndex = 1;
        Rows.Add(("スタックの行", (UIElement)rows.Items[0], (FrameworkElement)rows.Items[0]));

        var tabs = Add(new TabControl());
        AutomationProperties.SetName(tabs, "左ペインのタブ");
        var look = new TabItem { Header = "ルック", Content = new TextBlock() };
        var effect = new TabItem { Header = "エフェクト", Content = new TextBlock() };
        tabs.Items.Add(look);
        tabs.Items.Add(effect);
        tabs.SelectedIndex = 0;
        Rows.Add(("タブ", effect, effect));
        Rows.Add(("選択中のタブ", look, look));
    }

    private ListBox MakeList(string name, string itemStyle, string[] items)
    {
        var list = new ListBox { Width = (double)FindResource("TileSize") };
        AutomationProperties.SetName(list, name);
        var style = new Style(typeof(ListBoxItem), (Style)FindResource(itemStyle));
        style.Setters.Add(new Setter(MarginProperty, FindResource("Space2Thickness")));
        list.ItemContainerStyle = style;
        foreach (var s in items) list.Items.Add(new ListBoxItem { Content = s });
        return list;
    }
}
