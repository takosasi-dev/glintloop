// レイヤーの設定(FR-48・FR-49・FR-54): 対象がレイヤーで、スタックの行を選んでいないときにパラメータ欄に出す。
// 不透明度(スライダーと数値入力)・位置 X と Y・拡縮(スライダーと数値入力)・「中央に置く」「100% にする」・ブレンド・名前、empty のレイヤーでは塗りの色(色の四角・16 進数の入力・スポイト)。
// 土台では位置・拡縮・2 つのボタンを出さず、ブレンドを無効表示にする。数値入力は FR-27 の規則(NumberBox)。スライダーのドラッグは離した時点で Undo の 1 件。
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Controls;
using GlintLoop.App.Theme;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Layers;

public sealed class LayerSettingsView : Border
{
    private readonly EditorSession _session;
    private readonly StackPanel _panel = new();
    private readonly FrameworkElement _placeRow;
    private readonly FrameworkElement _scaleRow;
    private readonly FrameworkElement _buttonsRow;
    private readonly FrameworkElement _fillRow;
    private bool _syncing;
    private int _layer = -1;

    public LayerSettingsView(EditorSession session)
    {
        _session = session;
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _panel,
        };
        Child = scroll;
        SetResourceReference(PaddingProperty, "Space3HorizontalThickness");

        OpacitySlider = MakeSlider(0, 100, LayerTexts.Opacity, "LayerOpacitySlider");
        OpacityBox = MakeBox(0, 100, 0, LayerTexts.Opacity, "LayerOpacityBox");
        _panel.Children.Add(Section(LayerTexts.Opacity, SliderWithBox(OpacitySlider, OpacityBox)));
        OpacitySlider.ValueChanged += (_, e) => Edit(i => _session.SetLayerOpacity(i, e.NewValue));
        OpacityBox.ValueCommitted += (_, v) => Edit(i => _session.SetLayerOpacity(i, v));

        XBox = MakeBox(-DocumentRules.PlaceLimit, DocumentRules.PlaceLimit, 2, LayerTexts.X, "LayerXBox");
        YBox = MakeBox(-DocumentRules.PlaceLimit, DocumentRules.PlaceLimit, 2, LayerTexts.Y, "LayerYBox");
        XBox.ValueCommitted += (_, v) => EditPlace(p => p with { X = v });
        YBox.ValueCommitted += (_, v) => EditPlace(p => p with { Y = v });
        var xy = new Grid();
        xy.ColumnDefinitions.Add(new ColumnDefinition());
        xy.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Token("Space2")) });
        xy.ColumnDefinitions.Add(new ColumnDefinition());
        var xs = Section(LayerTexts.X, XBox);
        var ys = Section(LayerTexts.Y, YBox);
        Grid.SetColumn(ys, 2);
        xy.Children.Add(xs);
        xy.Children.Add(ys);
        _placeRow = xy;
        _panel.Children.Add(xy);

        ScaleSlider = MakeSlider(DocumentRules.MinScale, DocumentRules.MaxScale, LayerTexts.Scale, "LayerScaleSlider");
        ScaleBox = MakeBox(DocumentRules.MinScale, DocumentRules.MaxScale, 1, LayerTexts.Scale, "LayerScaleBox");
        ScaleSlider.ValueChanged += (_, e) => EditPlace(p => p with { Scale = e.NewValue });
        ScaleBox.ValueCommitted += (_, v) => EditPlace(p => p with { Scale = v });
        _scaleRow = Section(LayerTexts.Scale, SliderWithBox(ScaleSlider, ScaleBox));
        _panel.Children.Add(_scaleRow);

        CenterButton = new Button { Content = LayerTexts.Center };
        AutomationProperties.SetAutomationId(CenterButton, "LayerCenterButton");
        CenterButton.Click += (_, _) => EditPlace(p => p with { X = 0, Y = 0 });
        Scale100Button = new Button { Content = LayerTexts.Scale100 };
        AutomationProperties.SetAutomationId(Scale100Button, "LayerScale100Button");
        Scale100Button.Click += (_, _) => EditPlace(p => p with { Scale = 100 });
        var buttons = new WrapPanel();
        Scale100Button.Margin = new Thickness(Token("Space2"), 0, 0, 0);
        buttons.Children.Add(CenterButton);
        buttons.Children.Add(Scale100Button);
        buttons.Margin = SectionMargin();
        _buttonsRow = buttons;
        _panel.Children.Add(buttons);

        BlendBox = new ComboBox();
        AutomationProperties.SetName(BlendBox, LayerTexts.Blend);
        AutomationProperties.SetAutomationId(BlendBox, "LayerBlendBox");
        foreach (var b in Enum.GetValues<BlendMode>())
        {
            var item = new ComboBoxItem { Content = LayerTexts.BlendName(b), Tag = b };
            AutomationProperties.SetName(item, LayerTexts.BlendName(b));
            BlendBox.Items.Add(item);
        }
        BlendBox.SelectionChanged += (_, _) =>
        {
            if (BlendBox.SelectedItem is ComboBoxItem { Tag: BlendMode b }) Edit(i => _session.SetLayerBlend(i, b));
        };
        _panel.Children.Add(Section(LayerTexts.Blend, BlendBox));

        NameBox = new TextBox();
        AutomationProperties.SetName(NameBox, LayerTexts.Name);
        AutomationProperties.SetAutomationId(NameBox, "LayerNameBox");
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitName();
            e.Handled = true;
        };
        NameBox.LostKeyboardFocus += (_, _) => CommitName();
        _panel.Children.Add(Section(LayerTexts.Name, NameBox));

        FillSwatch = new Button();
        FillSwatch.SetResourceReference(StyleProperty, "ColorSwatch");
        AutomationProperties.SetName(FillSwatch, LayerTexts.Fill);
        AutomationProperties.SetAutomationId(FillSwatch, "LayerFillSwatch");
        FillSwatch.Click += (_, _) => FillPickRequested?.Invoke(_layer);
        FillHex = new TextBox();
        AutomationProperties.SetName(FillHex, LayerTexts.FillHex);
        AutomationProperties.SetAutomationId(FillHex, "LayerFillHex");
        FillHex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitFill();
            e.Handled = true;
        };
        FillHex.LostKeyboardFocus += (_, _) => CommitFill();
        FillEyedropper = new Button { Content = Icons.Create(Icons.Eyedropper), ToolTip = Icons.Eyedropper.Name };
        FillEyedropper.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(FillEyedropper, Icons.Eyedropper.Name);
        AutomationProperties.SetAutomationId(FillEyedropper, "LayerFillEyedropper");
        FillEyedropper.Click += (_, _) => EyedropperRequested?.Invoke(_layer);
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Token("Space2")) });
        fill.ColumnDefinitions.Add(new ColumnDefinition());
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Token("Space2")) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(FillHex, 2);
        Grid.SetColumn(FillEyedropper, 4);
        fill.Children.Add(FillSwatch);
        fill.Children.Add(FillHex);
        fill.Children.Add(FillEyedropper);
        _fillRow = Section(LayerTexts.Fill, fill);
        _panel.Children.Add(_fillRow);
    }

    private static double Token(string key) => (double)AppResources.LayoutTokens[key];

    private static Thickness SectionMargin() => new(0, 0, 0, Token("Space3"));

    private static FrameworkElement Section(string label, FrameworkElement body)
    {
        var p = new StackPanel { Margin = SectionMargin() };
        var t = new TextBlock { Text = label };
        t.SetResourceReference(StyleProperty, "TextCaption");
        Fx.SetText(t, "TextSecondaryBrush");
        t.Margin = new Thickness(0, 0, 0, Token("Space1"));
        p.Children.Add(t);
        p.Children.Add(body);
        return p;
    }

    private static Grid SliderWithBox(Slider s, NumberBox b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Token("Space2")) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(b, 2);
        g.Children.Add(s);
        g.Children.Add(b);
        return g;
    }

    private Slider MakeSlider(double min, double max, string name, string id)
    {
        var s = new Slider { Minimum = min, Maximum = max, SmallChange = 1, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(s, name);
        AutomationProperties.SetAutomationId(s, id);
        s.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _session.BeginContinuous()));
        s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _session.EndContinuous()));
        return s;
    }

    private static NumberBox MakeBox(double min, double max, int decimals, string name, string id)
    {
        var b = new NumberBox { Minimum = min, Maximum = max, Decimals = decimals };
        AutomationProperties.SetName(b, name);
        AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    public Slider OpacitySlider { get; }
    public NumberBox OpacityBox { get; }
    public NumberBox XBox { get; }
    public NumberBox YBox { get; }
    public Slider ScaleSlider { get; }
    public NumberBox ScaleBox { get; }
    public Button CenterButton { get; }
    public Button Scale100Button { get; }
    public ComboBox BlendBox { get; }
    public TextBox NameBox { get; }
    public Button FillSwatch { get; }
    public TextBox FillHex { get; }
    public Button FillEyedropper { get; }

    /// <summary>いま設定を出しているレイヤーの番号(出していないときは −1)。</summary>
    public int LayerIndex => _layer;

    /// <summary>色の四角を押した(色選択は FR-29 の色の部品の担当がつなぐ)。引数はレイヤーの番号。</summary>
    public event Action<int>? FillPickRequested;

    /// <summary>スポイトを押した(FR-29 のスポイトの担当がつなぐ)。引数はレイヤーの番号。</summary>
    public event Action<int>? EyedropperRequested;

    /// <summary>番号 index のレイヤーの値を入れる(文書が変わるたびに呼ぶ)。</summary>
    public void Bind(SessionDocument doc, int index)
    {
        _syncing = true;
        try
        {
            _layer = index;
            var l = doc.Layers[index];
            var isBase = index == 0;
            OpacitySlider.Value = l.Opacity;
            OpacityBox.Value = l.Opacity;
            XBox.Value = l.Place.X;
            YBox.Value = l.Place.Y;
            ScaleSlider.Value = l.Place.Scale;
            ScaleBox.Value = l.Place.Scale;
            var place = isBase ? Visibility.Collapsed : Visibility.Visible;
            _placeRow.Visibility = place;
            _scaleRow.Visibility = place;
            _buttonsRow.Visibility = place;
            BlendBox.IsEnabled = !isBase;
            BlendBox.SelectedIndex = (int)(isBase ? BlendMode.Normal : l.Blend);
            if (!NameBox.IsKeyboardFocusWithin || NameBox.Text.Length == 0) NameBox.Text = l.Name;
            var empty = l.Kind == LayerKind.Empty;
            _fillRow.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty)
            {
                var fill = l.Fill ?? ColorText.Transparent;
                FillHex.Text = fill;
                FillSwatch.Background = new SolidColorBrush(ToColor(fill));
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary><c>#RRGGBBAA</c> を色にする。</summary>
    public static Color ToColor(string rgba)
    {
        var v = uint.Parse(rgba.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
    }

    private void Edit(Func<int, EditOutcome> op)
    {
        if (_syncing || _layer < 0 || _session.Document is not { } doc || _layer >= doc.Layers.Count) return;
        op(_layer);
        // 値が変わらなかった(範囲の端に直した結果が同じ・読めない値)ときも、欄を文書の値に戻す
        if (_session.Document is { } now && _layer < now.Layers.Count) Bind(now, _layer);
    }

    private void EditPlace(Func<Placement, Placement> change) => Edit(i =>
    {
        var p = change(_session.Document!.Layers[i].Place);
        return _session.SetLayerPlacement(i, p.X, p.Y, p.Scale);
    });

    private void CommitName()
    {
        if (_syncing || _layer < 0) return;
        var text = NameBox.Text;
        if (_session.Document is { } doc && _layer < doc.Layers.Count && doc.Layers[_layer].Name != text)
            Edit(i => _session.SetLayerName(i, text));
        if (_session.Document is { } now && _layer < now.Layers.Count) NameBox.Text = now.Layers[_layer].Name;
    }

    private void CommitFill()
    {
        if (_syncing || _layer < 0 || _session.Document is not { } doc || _layer >= doc.Layers.Count) return;
        var text = FillHex.Text.Trim();
        var current = doc.Layers[_layer].Fill ?? ColorText.Transparent;
        // #RRGGBB は、いまの AA を保って #RRGGBBAA にする(仮の決め)
        if (ColorText.TryNormalizeRgb(text, out var rgb)) text = rgb + current[7..];
        if (ColorText.TryNormalizeRgba(text, out var rgba) && rgba != current) Edit(i => _session.SetLayerFill(i, rgba));
        else if (_session.Document is { } now) Bind(now, _layer);
    }
}
