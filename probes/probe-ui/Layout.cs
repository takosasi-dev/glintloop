// probe-ui の窓の中身: 左に操作の板(半透明の下地で、どの組み合わせでも文字が読める)、右に何も描かない「のぞき窓」。
// 文字の色は下地の明暗から決める。下地の明暗は、Fluent のときはコントロールの明暗(Fluent のボタンは半透明で、同じ明暗の下地が要る)、
// None のときは「透明なら DWM のダークの属性(20)、不透明なら塗りの明暗」。見本のカードはコントロールの明暗に合わせた不透明の板。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlintLoop.Probes.Ui;

static partial class Program
{
    // 色は probe の中だけの直書き(製品ではない)。板は 90% の不透明度なので、後ろが黒でも白でも文字が読める
    static readonly Color PlateDarkColor = Color.FromArgb(0xE6, 0x20, 0x20, 0x20);
    static readonly Color PlateLightColor = Color.FromArgb(0xE6, 0xF3, 0xF3, 0xF3);
    static readonly Color TextOnDark = Color.FromRgb(0xFF, 0xFF, 0xFF);
    static readonly Color SubOnDark = Color.FromRgb(0xD0, 0xD0, 0xD0);
    static readonly Color TextOnLight = Color.FromRgb(0x1A, 0x1A, 0x1A);
    static readonly Color SubOnLight = Color.FromRgb(0x40, 0x40, 0x40);
    static readonly Color CardDarkColor = Color.FromRgb(0x2B, 0x2B, 0x2B);
    static readonly Color CardLightColor = Color.FromRgb(0xFF, 0xFF, 0xFF);

    static Border plate = null!, peepCaption = null!, card = null!;
    static Rectangle peep = null!;
    static TextBlock heading = null!, statusText = null!, measureText = null!;
    static Button sampleButton = null!;
    static readonly List<TextBlock> subTexts = [];

    // 下地(板)が暗いか。Fluent ならコントロールの明暗(ThemeMode=Dark でダークの属性が off でも、ボタンの白い字が読める)。
    // None(WPF の既定テーマのボタンは不透明で明るい)なら、透明のとき DWM の素材の明暗(20 の読み戻し)、不透明のとき塗りの明暗
    static bool PlateDark() => AppTheme() != "none" ? ControlsDark() : glassOn ? (ReadDarkValue() ?? OsDark()) : ThemeDark();

    // コントロールのテーマが暗いか。None は WPF の既定テーマ(Aero2)で、明るい色しか無い
    static bool ControlsDark() => AppTheme() switch
    {
        "light" => false,
        "dark" => true,
        "system" => OsDark(),
        _ => false,
    };

    static SolidColorBrush B(Color c) => new(c);

    static Window BuildWindow()
    {
        var w = new Window { Title = "GlintLoop probe-ui(測定 3)", Width = 1120, Height = 760, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(640) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        w.Content = grid;

        // ---- 左: 操作の板 ----
        var root = new StackPanel();
        plate = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root } };
        grid.Children.Add(plate);

        heading = new TextBlock { Text = "GlintLoop probe-ui(測定 3: Mica とテーマ)", FontSize = 20, FontWeight = FontWeights.SemiBold };
        root.Children.Add(heading);
        root.Children.Add(Sub("右の点線の枠(のぞき窓)には何も描いていない。枠の中が真っ黒・真っ白ではなく、壁紙の色がうっすら混ざった灰色なら Mica。" +
                              "窓をクリックして前面にしてから「背景を測る」を押すと、枠の中の平均の色が出る。窓を壁紙の色の違う場所へ動かして、もう一度測り、値が変わるかを見る。", new Thickness(0, 4, 0, 12)));

        WrapPanel Row(string label)
        {
            var p = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            p.Children.Add(new TextBlock { Text = label, Width = 140, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(p);
            return p;
        }
        void Btn(WrapPanel p, string text, string id)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(10, 4, 10, 4) };
            b.Click += (_, _) => Click(id, text);
            p.Children.Add(b);
        }
        var r1 = Row("ThemeMode");
        foreach (var t in new[] { "none", "light", "dark", "system" }) Btn(r1, t, "theme:" + t);
        var r2 = Row("背景の素材(38)");
        foreach (var b in new[] { "none", "mica", "acrylic", "tabbed", "auto" }) Btn(r2, b, "backdrop:" + b);
        var r3 = Row("ダークの属性(20)");
        Btn(r3, "on", "dark:on");
        Btn(r3, "off", "dark:off");
        var r4 = Row("透明な背景");
        Btn(r4, "on", "glass:on");
        Btn(r4, "off(不透明)", "glass:off");
        var r5 = Row("まとめて");
        Btn(r5, "全部かけ直す", "reapply");
        Btn(r5, "背景を測る", "measure");

        statusText = Sub("", new Thickness(0, 6, 0, 0));
        statusText.FontFamily = new FontFamily("Consolas, Yu Gothic UI");
        root.Children.Add(statusText);

        // 見本のカード(コントロールのテーマに合わせた不透明の板)
        var cp = new StackPanel();
        card = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Child = cp };
        root.Children.Add(card);
        cp.Children.Add(new TextBlock { Text = "見本のカード(コントロールのテーマに合わせた不透明の板)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var size in new[] { 9.0, 11.0, 12.0, 14.0, 20.0 })
            cp.Children.Add(new TextBlock { Text = $"{size}px: GlintLoop 画像のエフェクトと GIF の書き出し 0123456789", FontSize = size, Margin = new Thickness(0, 2, 0, 2) });

        var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        sampleButton = new Button { Content = "ボタン", Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(12, 4, 12, 4) };
        controls.Children.Add(sampleButton);
        controls.Children.Add(new CheckBox { Content = "チェック", IsChecked = true, Margin = new Thickness(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(new RadioButton { Content = "ラジオ", IsChecked = true, Margin = new Thickness(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Center });
        var combo = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 8, 4) };
        combo.Items.Add("GIF"); combo.Items.Add("PNG"); combo.Items.Add("WebP"); combo.SelectedIndex = 0;
        controls.Children.Add(combo);
        controls.Children.Add(new TextBox { Text = "入力欄", Width = 110, Margin = new Thickness(0, 0, 8, 4) });
        controls.Children.Add(new Slider { Width = 140, Minimum = 0, Maximum = 100, Value = 40, VerticalAlignment = VerticalAlignment.Center });
        cp.Children.Add(controls);
        cp.Children.Add(new ProgressBar { Height = 6, Value = 60, Margin = new Thickness(0, 0, 0, 8) });

        // 1 物理画素の線と市松模様(150%・200% でぼやけるかを見る)
        cp.Children.Add(new TextBlock { Text = "下の線と市松模様が、どの拡大率でもくっきり見えるか(ぼやけ・にじみが無いか)", Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap });
        var lines = new Canvas { Width = 400, Height = 70, SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetEdgeMode(lines, EdgeMode.Aliased);
        for (int i = 0; i < 20; i++)
            lines.Children.Add(new Line { X1 = 4 + i * 8, Y1 = 0, X2 = 4 + i * 8, Y2 = 70, Stroke = Brushes.Gray, StrokeThickness = 1 });
        var checker = new Rectangle { Width = 200, Height = 70, Fill = Checker() };
        Canvas.SetLeft(checker, 190);
        lines.Children.Add(checker);
        cp.Children.Add(lines);

        // ---- 右: のぞき窓(何も描かない) ----
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var capPanel = new StackPanel();
        peepCaption = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Child = capPanel };
        right.Children.Add(peepCaption);
        capPanel.Children.Add(new TextBlock { Text = "のぞき窓(この下の点線の枠の中は何も描かない)", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        capPanel.Children.Add(Sub("Mica: 暗い(明るい)灰色に壁紙の色がうっすら混ざる。窓を動かすと色味が変わる。窓が非アクティブだと単色の灰色になる。\n" +
                                  "効いていない(38=NONE・AUTO): ダークの属性 on なら真っ黒(R0 G0 B0)、off なら真っ白。\nAcrylic: 後ろのほかの窓がぼけて透ける。", new Thickness(0, 4, 0, 4)));
        measureText = new TextBlock { Text = "(まだ測っていない)", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        capPanel.Children.Add(measureText);

        // 塗り(Fill)は付けない。点線の枠だけ描く(中は DWM の素材がそのまま見える)
        peep = new Rectangle { Stroke = Brushes.Gray, StrokeThickness = 1, StrokeDashArray = [4, 4], Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(peep, 1);
        right.Children.Add(peep);
        return w;
    }

    static TextBlock Sub(string text, Thickness margin)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        subTexts.Add(t);
        return t;
    }

    // 板とカードの色を、今の状態(ThemeMode・20 の読み戻し・透明かどうか)に合わせて塗り直す
    static void RefreshColors()
    {
        if (window is null || plate is null) return;
        bool pd = PlateDark(), cd = ControlsDark();
        foreach (var b in new[] { plate, peepCaption })
        {
            b.Background = B(pd ? PlateDarkColor : PlateLightColor);
            TextElement.SetForeground(b, B(pd ? TextOnDark : TextOnLight));
        }
        foreach (var t in subTexts)
            if (!IsInside(t, card)) t.Foreground = B(pd ? SubOnDark : SubOnLight);
        card.Background = B(cd ? CardDarkColor : CardLightColor);
        TextElement.SetForeground(card, B(cd ? TextOnDark : TextOnLight));
        if (!glassOn && hwnd != IntPtr.Zero) PaintOpaque();
    }

    static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (var d = child; d is not null; d = LogicalTreeHelper.GetParent(d))
            if (d == parent) return true;
        return false;
    }

    static Brush Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        g.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        g.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(1, 1, 1, 1))));
        return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 2, 2), ViewportUnits = BrushMappingMode.Absolute };
    }
}
