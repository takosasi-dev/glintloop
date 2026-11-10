// パラメータ欄の型ごとの既定の部品(§9.2 の UI の自動生成の規則)。数値はスライダーと数値入力、bool はトグル、enum は 4 個以下なら横並びの切替・5 個以上なら一覧、
// color は色の四角・16 進入力・スポイト、point は X・Y、rect は 4 辺の数値入力。どの行も右端に「既定値に戻す」を持つ。text・font は登録が無いときの最低限の入力欄。
// スライダーのドラッグは BeginContinuous〜EndContinuous で Undo の 1 件にし、ダブルクリックで既定値に戻す(FR-27)。
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlintLoop.App.Controls;
using GlintLoop.App.Theme;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

internal abstract class ParamRow
{
    protected ParamRow(ParamRowContext ctx, string key, string label, ParamDef? def)
    {
        Ctx = ctx;
        Key = key;
        Def = def;
        Root = new StackPanel();
        Root.SetResourceReference(FrameworkElement.MarginProperty, "Space2BottomThickness");
        AutomationProperties.SetAutomationId(Root, Id(""));
        LabelText = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        LabelText.SetResourceReference(FrameworkElement.StyleProperty, "TextBody");
        LabelText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Root.Children.Add(LabelText);
        Body = new DockPanel { LastChildFill = true };
        Root.Children.Add(Body);
        ResetButton = new Button { Content = ResetIcon(), ToolTip = StackTexts.ResetToDefault };
        ResetButton.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        ResetButton.SetResourceReference(FrameworkElement.MarginProperty, "Space1LeftThickness");
        AutomationProperties.SetName(ResetButton, StackTexts.ResetToDefault);
        AutomationProperties.SetAutomationId(ResetButton, Id(".reset"));
        ResetButton.Click += (_, _) => ResetToDefault();
        DockPanel.SetDock(ResetButton, Dock.Right);
        Body.Children.Add(ResetButton);
    }

    protected ParamRowContext Ctx { get; }

    public string Key { get; }

    public ParamDef? Def { get; }

    public StackPanel Root { get; }

    protected TextBlock LabelText { get; }

    protected DockPanel Body { get; }

    protected Button ResetButton { get; }

    protected string Id(string suffix) => "param." + Key + suffix;

    /// <summary>行の値に部品を合わせる(セッションへは送らない)。</summary>
    public abstract void Update(StackEntry e);

    /// <summary>既定値に戻す(FR-27)。</summary>
    public virtual void ResetToDefault()
    {
        if (Def is not null) Ctx.Set(Key, Def.Default);
    }

    /// <summary>visibleWhen の条件(§9.2)。判定はハンドルと同じ EffectSchema.IsVisible(宣言の無い行は見える)。</summary>
    public bool IsVisible(StackEntry e) => Def is null || EffectSchema.IsVisible(Def, e);

    /// <summary>本体に部品を足す(右端の「既定値に戻す」の左に詰める)。</summary>
    protected void AddBody(UIElement e) => Body.Children.Add(e);

    /// <summary>「既定値に戻す」の形(§9.1 のアイコンの一覧に無いので自作の Geometry。左回りの矢印)。</summary>
    private static FrameworkElement ResetIcon()
    {
        var p = new System.Windows.Shapes.Path { Data = ResetGeometry, Stretch = Stretch.Uniform };
        p.SetResourceReference(FrameworkElement.WidthProperty, "IconSize");
        p.SetResourceReference(FrameworkElement.HeightProperty, "IconSize");
        p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimaryBrush");
        p.SetResourceReference(System.Windows.Shapes.Shape.StrokeThicknessProperty, "Stroke");
        return p;
    }

    private static readonly Geometry ResetGeometry = MakeReset();

    private static Geometry MakeReset()
    {
        var g = Geometry.Parse("M3,8 A5,5 0 1 0 5,4 M5,1 L5,4 L2,4");
        g.Freeze();
        return g;
    }

    /// <summary>数値の表示の桁(step から。0.01 → 2、0.1 → 1、1 → 0)。</summary>
    public static int DecimalsOf(ParamDef p)
    {
        if (p.Type == ParamType.Int) return 0;
        var step = StepOf(p);
        var d = 0;
        while (d < 6 && Math.Abs(step * Math.Pow(10, d) - Math.Round(step * Math.Pow(10, d))) > 1e-9) d++;
        return d;
    }

    /// <summary>刻み(宣言に無ければ §9.4 の規則: 整数・角度 1、実数は範囲の幅が 100 以上で 1、10 以上で 0.1、それ未満で 0.01)。</summary>
    public static double StepOf(ParamDef p)
    {
        if (p.Step is { } s && s > 0) return s;
        if (p.Type is ParamType.Int or ParamType.Angle) return 1;
        var range = (p.Max ?? 1) - (p.Min ?? 0);
        return range >= 100 ? 1 : range >= 10 ? 0.1 : 0.01;
    }

    protected NumberBox MakeNumber(string automationId, string name, double min, double max, int decimals)
    {
        var n = new NumberBox { Minimum = min, Maximum = max, Decimals = decimals };
        n.Width = Ctx.Resource<double>("HitMin") * 2;
        AutomationProperties.SetName(n, name);
        AutomationProperties.SetAutomationId(n, automationId);
        return n;
    }

    protected TextBlock MakeUnit(string unit)
    {
        var t = new TextBlock { Text = StackTexts.UnitText(unit), VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
        t.SetResourceReference(FrameworkElement.MarginProperty, "Space1LeftThickness");
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return t;
    }

    /// <summary>スライダー(ドラッグは連続する変更の 1 件。ダブルクリックで既定値)。</summary>
    protected Slider MakeSlider(string automationId, string name, double min, double max, double step, Action<double> changed, Action reset)
    {
        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(s, name);
        AutomationProperties.SetAutomationId(s, automationId);
        s.ValueChanged += (_, e) =>
        {
            if (Ctx.View.Updating) return;
            changed(e.NewValue);
        };
        s.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => Ctx.BeginContinuous()));
        s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Ctx.EndContinuous()));
        s.MouseDoubleClick += (_, e) =>
        {
            reset();
            e.Handled = true;
        };
        return s;
    }
}

/// <summary>強さ(FR-28。0〜100%、既定 100)。</summary>
internal sealed class AmountRow : ParamRow
{
    private readonly Slider _slider;
    private readonly NumberBox _number;

    /// <summary>強さの行のキー(パラメータの key は英小文字で始まるので重ならない。aberration には amount という key がある)。</summary>
    public const string RowKey = "_amount";

    public AmountRow(ParamRowContext ctx) : base(ctx, RowKey, StackTexts.Amount, null)
    {
        _number = MakeNumber(Id(".number"), StackTexts.Amount, 0, 100, 0);
        _number.ValueCommitted += (_, v) => Ctx.SetAmount(v);
        _slider = MakeSlider(Id(".slider"), StackTexts.Amount, 0, 100, 1, v => Ctx.SetAmount(v), ResetToDefault);
        var unit = MakeUnit("%");
        DockPanel.SetDock(unit, Dock.Right);
        DockPanel.SetDock(_number, Dock.Right);
        AddBody(unit);
        AddBody(_number);
        AddBody(_slider);
    }

    public override void Update(StackEntry e)
    {
        _slider.Value = e.Amount;
        _number.Value = e.Amount;
    }

    public override void ResetToDefault() => Ctx.SetAmount(100);
}

/// <summary>float・int・angle: スライダーと数値入力(単位を右に)。</summary>
internal sealed class NumberRow : ParamRow
{
    private readonly Slider _slider;
    private readonly NumberBox _number;
    private readonly int _decimals;

    public NumberRow(ParamRowContext ctx, ParamDef p) : base(ctx, p.Key, p.Label, p)
    {
        var min = p.Min ?? 0;
        var max = p.Max ?? 1;
        _decimals = DecimalsOf(p);
        _number = MakeNumber(Id(".number"), p.Label, min, max, _decimals);
        _number.ValueCommitted += (_, v) => Ctx.Set(Key, new NumberValue(v));
        _slider = MakeSlider(Id(".slider"), p.Label, min, max, StepOf(p),
            v => Ctx.Set(Key, new NumberValue(Math.Round(v, _decimals, MidpointRounding.AwayFromZero))), ResetToDefault);
        var unit = MakeUnit(p.Type == ParamType.Angle ? "deg" : p.Unit);
        DockPanel.SetDock(unit, Dock.Right);
        DockPanel.SetDock(_number, Dock.Right);
        AddBody(unit);
        AddBody(_number);
        AddBody(_slider);
    }

    public Slider Slider => _slider;

    public override void Update(StackEntry e)
    {
        if (e.Get(Key) is not NumberValue n) return;
        _slider.Value = n.Value;
        _number.Value = n.Value;
    }
}

/// <summary>bool: トグル。</summary>
internal sealed class BoolRow : ParamRow
{
    private readonly CheckBox _toggle;

    public BoolRow(ParamRowContext ctx, ParamDef p) : base(ctx, p.Key, p.Label, p)
    {
        LabelText.Visibility = Visibility.Collapsed;
        _toggle = new CheckBox { Content = p.Label, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_toggle, p.Label);
        AutomationProperties.SetAutomationId(_toggle, Id(".toggle"));
        // UIA の Toggle は Click を出さないので、Checked と Unchecked で受ける(値を合わせている間は Ctx が送らない)
        _toggle.Checked += (_, _) => Ctx.Set(Key, new BoolValue(true));
        _toggle.Unchecked += (_, _) => Ctx.Set(Key, new BoolValue(false));
        AddBody(_toggle);
    }

    public override void Update(StackEntry e)
    {
        if (e.Get(Key) is BoolValue b) _toggle.IsChecked = b.Value;
    }
}

/// <summary>enum: 選択肢が 4 個以下なら横並びの切替ボタン、5 個以上なら一覧(ドロップダウン)。</summary>
internal sealed class EnumRow : ParamRow
{
    private readonly Selector _choice;
    private readonly IReadOnlyList<EnumOption> _options;

    /// <summary>横並びの切替ボタンにする選択肢の数の上限(§9.2)。</summary>
    public const int SegmentedMax = 4;

    public EnumRow(ParamRowContext ctx, ParamDef p) : base(ctx, p.Key, p.Label, p)
    {
        _options = p.Options ?? [];
        if (_options.Count <= SegmentedMax)
        {
            var seg = new ListBox();
            seg.SetResourceReference(FrameworkElement.StyleProperty, "Segmented");
            foreach (var o in _options) seg.Items.Add(new ListBoxItem { Content = o.Label });
            _choice = seg;
        }
        else
        {
            var combo = new ComboBox();
            foreach (var o in _options) combo.Items.Add(new ComboBoxItem { Content = o.Label });
            _choice = combo;
        }
        AutomationProperties.SetName(_choice, p.Label);
        AutomationProperties.SetAutomationId(_choice, Id(".choice"));
        _choice.SelectionChanged += (_, _) =>
        {
            var i = _choice.SelectedIndex;
            if (i >= 0 && i < _options.Count) Ctx.Set(Key, new StringValue(_options[i].Value));
        };
        AddBody(_choice);
    }

    public bool IsSegmented => _choice is ListBox;

    public override void Update(StackEntry e)
    {
        if (e.Get(Key) is not StringValue s) return;
        var i = -1;
        for (var k = 0; k < _options.Count; k++)
        {
            if (_options[k].Value == s.Value) i = k;
        }
        _choice.SelectedIndex = i;
    }
}

/// <summary>color: 色の四角(クリックで色の選択)・16 進入力・スポイト(FR-29)。</summary>
internal sealed class ColorRow : ParamRow
{
    private readonly Button _swatch;
    private readonly TextBox _hex;
    private readonly ToggleButton _dropper;
    private readonly ColorPickerPopup _picker;
    private string _value = "";
    private bool _syncDropper;

    public ColorRow(ParamRowContext ctx, ParamDef p) : base(ctx, p.Key, p.Label, p)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        _swatch = new Button();
        _swatch.SetResourceReference(FrameworkElement.StyleProperty, "ColorSwatch");
        AutomationProperties.SetName(_swatch, p.Label);
        AutomationProperties.SetAutomationId(_swatch, Id(".swatch"));
        _picker = new ColorPickerPopup(ctx, _swatch, () => _value, v => Ctx.Set(Key, new StringValue(v)));
        _swatch.Click += (_, _) => _picker.Open();
        _hex = new TextBox();
        _hex.Width = Ctx.Resource<double>("HitMin") * 3;
        _hex.SetResourceReference(FrameworkElement.MarginProperty, "Space1HorizontalThickness");
        AutomationProperties.SetName(_hex, StackTexts.Hex);
        AutomationProperties.SetAutomationId(_hex, Id(".hex"));
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            CommitHex();
            e.Handled = true;
        };
        _hex.LostKeyboardFocus += (_, _) => CommitHex();
        _dropper = new ToggleButton { Content = Icons.Create(Icons.Eyedropper), ToolTip = Icons.Eyedropper.Name };
        _dropper.SetResourceReference(FrameworkElement.StyleProperty, "IconToggleButton");
        AutomationProperties.SetName(_dropper, StackTexts.Eyedropper);
        AutomationProperties.SetAutomationId(_dropper, Id(".eyedropper"));
        var dropper = ctx.View.Module.Eyedropper;
        _dropper.Checked += (_, _) =>
        {
            if (_syncDropper) return;
            if (dropper.IsActive) dropper.Cancel();
            dropper.Start(Ctx.Target, Ctx.Salt, Key);
        };
        _dropper.Unchecked += (_, _) =>
        {
            if (_syncDropper) return;
            if (dropper.IsActive && dropper.ActiveKey == Key) dropper.Cancel();
        };
        dropper.Changed += () =>
        {
            _syncDropper = true;
            try
            {
                _dropper.IsChecked = dropper.IsActive && dropper.ActiveKey == Key;
            }
            finally
            {
                _syncDropper = false;
            }
        };
        panel.Children.Add(_swatch);
        panel.Children.Add(_hex);
        panel.Children.Add(_dropper);
        AddBody(panel);
    }

    /// <summary>16 進入力を反映する。#RRGGBBAA はそのまま、#RRGGBB は AA を保つ(# は省いてよい)。読めなければ直前の値に戻す。</summary>
    private void CommitHex()
    {
        var s = _hex.Text.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (ColorText.TryNormalizeRgb(s, out var rgb)) s = rgb + (_value.Length == 9 ? _value[7..] : "FF");
        if (ColorText.TryNormalizeRgba(s, out var rgba))
        {
            if (rgba != _value) Ctx.Set(Key, new StringValue(rgba));
            else _hex.Text = _value;
        }
        else
        {
            _hex.Text = _value;
        }
    }

    public override void Update(StackEntry e)
    {
        if (e.Get(Key) is not StringValue s) return;
        _value = s.Value;
        if (!_hex.IsKeyboardFocused || _hex.Text != _value) _hex.Text = _value;
        _swatch.Background = new SolidColorBrush(ColorOf(_value));
        _picker.Sync();
    }

    /// <summary>#RRGGBBAA → Color(読めなければ透明)。</summary>
    public static Color ColorOf(string rgba)
    {
        if (!ColorText.TryNormalizeRgba(rgba, out var n)) return Colors.Transparent;
        byte B(int i) => byte.Parse(n.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(B(7), B(1), B(3), B(5));
    }
}

/// <summary>色の選択(色の四角を押すと開く): R・G・B・A の 4 本のスライダー(0〜255)。ドラッグは連続する変更の 1 件。</summary>
internal sealed class ColorPickerPopup
{
    private readonly ParamRowContext _ctx;
    private readonly Popup _popup;
    private readonly Slider[] _sliders = new Slider[4];
    private readonly Func<string> _get;
    private bool _sync;

    public ColorPickerPopup(ParamRowContext ctx, UIElement anchor, Func<string> get, Action<string> set)
    {
        _ctx = ctx;
        _get = get;
        var panel = new StackPanel();
        for (var i = 0; i < 4; i++)
        {
            var row = new DockPanel();
            var label = new TextBlock { Text = StackTexts.ColorChannels[i], VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
            label.Width = ctx.Resource<double>("IconSize");
            row.Children.Add(label);
            var s = new Slider { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, TickFrequency = 1, IsSnapToTickEnabled = true };
            s.Width = ctx.Resource<double>("TileSize") * 2;
            AutomationProperties.SetName(s, StackTexts.ColorPicker + " " + StackTexts.ColorChannels[i]);
            s.ValueChanged += (_, _) =>
            {
                if (_sync || _ctx.View.Updating) return;
                set(Compose());
            };
            s.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _ctx.BeginContinuous()));
            s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _ctx.EndContinuous()));
            row.Children.Add(s);
            _sliders[i] = s;
            panel.Children.Add(row);
        }
        var card = new Border { Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "Surface1Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        card.SetResourceReference(Border.BorderThicknessProperty, "StrokeThickness");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        card.SetResourceReference(Border.PaddingProperty, "Space3Thickness");
        _popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, Child = card, AllowsTransparency = true };
        _popup.Opened += (_, _) =>
        {
            var a = Motion.DoubleTo(1, Motion.Duration(_ctx.View, MotionToken.FlyoutIn), Motion.Easing(_ctx.View, MotionToken.FlyoutIn), 0);
            card.BeginAnimation(UIElement.OpacityProperty, a);
            if (a is null) card.Opacity = 1;
        };
    }

    public void Open()
    {
        Sync();
        _popup.IsOpen = true;
    }

    public void Sync()
    {
        var c = ColorRow.ColorOf(_get());
        _sync = true;
        try
        {
            _sliders[0].Value = c.R;
            _sliders[1].Value = c.G;
            _sliders[2].Value = c.B;
            _sliders[3].Value = c.A;
        }
        finally
        {
            _sync = false;
        }
    }

    private string Compose()
    {
        uint v = 0;
        foreach (var s in _sliders) v = (v << 8) | (uint)Math.Clamp((int)Math.Round(s.Value), 0, 255);
        return ColorText.Rgba(v);
    }
}

/// <summary>point: X と Y の数値入力(画像の幅と高さを 0〜1 にした値)。キャンバスのハンドルは EffectHandleOverlay。</summary>
internal sealed class PointRow : ParamRow
{
    private readonly NumberBox _x;
    private readonly NumberBox _y;

    /// <summary>point の数値入力の桁(仮の決め)。</summary>
    public const int Decimals = 3;

    public PointRow(ParamRowContext ctx, ParamDef p) : base(ctx, p.Key, p.Label, p)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        _x = MakeNumber(Id(".x"), p.Label + " " + StackTexts.PointAxes[0], 0, 1, Decimals);
        _y = MakeNumber(Id(".y"), p.Label + " " + StackTexts.PointAxes[1], 0, 1, Decimals);
        _y.SetResourceReference(FrameworkElement.MarginProperty, "Space1LeftThickness");
        _x.ValueCommitted += (_, v) => Ctx.Set(Key, new PointValue(v, _y.Value));
        _y.ValueCommitted += (_, v) => Ctx.Set(Key, new PointValue(_x.Value, v));
        panel.Children.Add(_x);
        panel.Children.Add(_y);
        AddBody(panel);
    }

    public override void Update(StackEntry e)
    {
        if (e.Get(Key) is not PointValue v) return;
        _x.Value = v.X;
        _y.Value = v.Y;
    }
}

/// <summary>rect: 4 辺(左・上・右・下)の数値入力。キャンバスのハンドルは EffectHandleOverlay。</summary>
internal sealed class RectRow : ParamRow
{
    private readonly NumberBox[] _edges = new NumberBox[4];
    private static readonly string[] Ids = [".l", ".t", ".r", ".b"];

    public RectRow(ParamRowContext ctx, ParamDef p) : base(ctx, p.Key, p.Label, p)
    {
        var grid = new UniformGrid { Columns = 4 };
        var decimals = DecimalsOf(p);
        for (var i = 0; i < 4; i++)
        {
            var n = MakeNumber(Id(Ids[i]), p.Label + " " + StackTexts.RectEdges[i], p.Min ?? 0, p.Max ?? 1000, decimals);
            n.Width = double.NaN;
            n.SetResourceReference(FrameworkElement.MarginProperty, "Space1RightThickness");
            var index = i;
            n.ValueCommitted += (_, v) => Commit(index, v);
            _edges[i] = n;
            grid.Children.Add(n);
        }
        AddBody(grid);
    }

    private void Commit(int index, double v)
    {
        var vals = _edges.Select(e => e.Value).ToArray();
        vals[index] = v;
        Ctx.Set(Key, new RectValue(vals[0], vals[1], vals[2], vals[3]));
    }

    public override void Update(StackEntry e)
    {
        if (e.Get(Key) is not RectValue r) return;
        _edges[0].Value = r.L;
        _edges[1].Value = r.T;
        _edges[2].Value = r.R;
        _edges[3].Value = r.B;
    }
}

/// <summary>text・font の登録が無いときの最低限の入力欄(1 つの TextBox。フォーカスを失ったときに反映)。部品は担当 text が登録する。</summary>
internal sealed class TextRow : ParamRow
{
    private readonly TextBox _box;

    public TextRow(ParamRowContext ctx, ParamDef p) : base(ctx, p.Key, p.Label, p)
    {
        _box = new TextBox { AcceptsReturn = p.Type == ParamType.Text, TextWrapping = TextWrapping.Wrap };
        // maxLength は TextBox.MaxLength(UTF-16 の単位で数え、改行も数える)に入れず、反映のとき ParamRules が E-27 の数え方で切る
        AutomationProperties.SetName(_box, p.Label);
        AutomationProperties.SetAutomationId(_box, Id(".text"));
        _box.LostKeyboardFocus += (_, _) => Ctx.Set(Key, new StringValue(_box.Text));
        AddBody(_box);
    }

    public override void Update(StackEntry e)
    {
        if (e.Get(Key) is StringValue s && !_box.IsKeyboardFocused) _box.Text = s.Value;
    }
}

/// <summary>登録された部品(ParamEditorRegistry)の行。値の表示と変更は部品が自分で行う。</summary>
internal sealed class CustomRow : ParamRow
{
    public CustomRow(ParamRowContext ctx, ParamDef p, FrameworkElement editor) : base(ctx, p.Key, p.Label, p)
    {
        AddBody(editor);
    }

    public override void Update(StackEntry e)
    {
    }
}
