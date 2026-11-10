// 空のキャンバスのダイアログ(FR-44・§9.3 の画面の表): 見出し(subtitle)「空のキャンバス」、幅と高さの数値入力(16〜4096 の整数、既定 1024 と 768)、
// 塗りの色の部品(色の四角・16 進数の入力・スポイト。既定は不透明の白)、「作成」(既定のボタン)と「キャンセル」。幅 400 DIP。
// 「作成」か Enter で、その大きさと塗りの empty のレイヤー 1 枚(名前「キャンバス」)だけの新しい文書を作る。Esc とキャンセルは何も作らずに閉じる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Controls;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Layers;

public sealed class EmptyCanvasDialog : StackPanel
{
    /// <summary>幅と高さの既定(FR-44)。</summary>
    public const int DefaultWidth = 1024;
    public const int DefaultHeight = 768;

    private readonly EditorSession _session;
    private readonly DialogHost _host;
    private string _fill = ColorText.OpaqueWhite;

    public EmptyCanvasDialog(EditorSession session, DialogHost host)
    {
        _session = session;
        _host = host;
        AutomationProperties.SetName(this, LayerTexts.Get("empty_canvas.title"));
        AutomationProperties.SetAutomationId(this, "EmptyCanvasDialog");

        var title = new TextBlock { Text = LayerTexts.Get("empty_canvas.title") };
        title.SetResourceReference(StyleProperty, "TextSubtitle");
        title.Margin = new Thickness(0, 0, 0, Token("Space4"));
        Children.Add(title);

        WidthBox = Box(LayerTexts.Get("empty_canvas.width"), "EmptyCanvasWidth", DefaultWidth);
        HeightBox = Box(LayerTexts.Get("empty_canvas.height"), "EmptyCanvasHeight", DefaultHeight);
        var size = new Grid();
        size.ColumnDefinitions.Add(new ColumnDefinition());
        size.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Token("Space2")) });
        size.ColumnDefinitions.Add(new ColumnDefinition());
        var ws = Section(LayerTexts.Get("empty_canvas.width"), WidthBox);
        var hs = Section(LayerTexts.Get("empty_canvas.height"), HeightBox);
        Grid.SetColumn(hs, 2);
        size.Children.Add(ws);
        size.Children.Add(hs);
        Children.Add(size);

        FillSwatch = new Button { Background = new SolidColorBrush(LayerSettingsView.ToColor(_fill)) };
        FillSwatch.SetResourceReference(StyleProperty, "ColorSwatch");
        AutomationProperties.SetName(FillSwatch, LayerTexts.Fill);
        AutomationProperties.SetAutomationId(FillSwatch, "EmptyCanvasFillSwatch");
        FillSwatch.Click += (_, _) => FillPickRequested?.Invoke(this);
        FillHex = new TextBox { Text = _fill };
        AutomationProperties.SetName(FillHex, LayerTexts.FillHex);
        AutomationProperties.SetAutomationId(FillHex, "EmptyCanvasFillHex");
        FillHex.LostKeyboardFocus += (_, _) => CommitFill();
        FillEyedropper = new Button { Content = Icons.Create(Icons.Eyedropper), ToolTip = Icons.Eyedropper.Name };
        FillEyedropper.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(FillEyedropper, Icons.Eyedropper.Name);
        AutomationProperties.SetAutomationId(FillEyedropper, "EmptyCanvasFillEyedropper");
        FillEyedropper.Click += (_, _) => EyedropperRequested?.Invoke(this);
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
        Children.Add(Section(LayerTexts.Fill, fill));

        CreateButton = new Button { Content = LayerTexts.Get("empty_canvas.create"), IsDefault = true };
        CreateButton.SetResourceReference(StyleProperty, "PrimaryButton");
        CreateButton.Click += (_, _) => Create();
        CancelButton = new Button { Content = LayerTexts.Get("empty_canvas.cancel") };
        CancelButton.Margin = new Thickness(Token("Space2"), 0, 0, 0);
        CancelButton.Click += (_, _) => _host.Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Margin = new Thickness(0, Token("Space2"), 0, 0);
        buttons.Children.Add(CreateButton);
        buttons.Children.Add(CancelButton);
        Children.Add(buttons);

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static double Token(string key) => (double)AppResources.LayoutTokens[key];

    /// <summary>UIA に見せる(AutomationId は EmptyCanvasDialog)。</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(EmptyCanvasDialog owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(EmptyCanvasDialog);
    }

    private static NumberBox Box(string name, string id, int value)
    {
        var b = new NumberBox { Minimum = DocumentRules.MinCanvasSide, Maximum = DocumentRules.MaxEmptySide, Decimals = 0, Value = value };
        AutomationProperties.SetName(b, name);
        AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    private static FrameworkElement Section(string label, FrameworkElement body)
    {
        var p = new StackPanel();
        p.Margin = new Thickness(0, 0, 0, Token("Space3"));
        var t = new TextBlock { Text = label };
        t.SetResourceReference(StyleProperty, "TextCaption");
        Fx.SetText(t, "TextSecondaryBrush");
        t.Margin = new Thickness(0, 0, 0, Token("Space1"));
        p.Children.Add(t);
        p.Children.Add(body);
        return p;
    }

    public NumberBox WidthBox { get; }
    public NumberBox HeightBox { get; }
    public Button FillSwatch { get; }
    public TextBox FillHex { get; }
    public Button FillEyedropper { get; }
    public Button CreateButton { get; }
    public Button CancelButton { get; }

    /// <summary>いまの塗りの色(<c>#RRGGBBAA</c>)。</summary>
    public string Fill => _fill;

    /// <summary>色の四角を押した(色選択は FR-29 の色の部品の担当がつなぐ。結果は SetFill で入れる)。</summary>
    public event Action<EmptyCanvasDialog>? FillPickRequested;

    /// <summary>スポイトを押した(FR-29 の担当がつなぐ)。</summary>
    public event Action<EmptyCanvasDialog>? EyedropperRequested;

    /// <summary>塗りの色を入れる(<c>#RRGGBBAA</c>。読めない値は無視する)。</summary>
    public void SetFill(string rgba)
    {
        if (!ColorText.TryNormalizeRgba(rgba, out var f)) return;
        _fill = f;
        FillHex.Text = f;
        FillSwatch.Background = new SolidColorBrush(LayerSettingsView.ToColor(f));
    }

    private void CommitFill()
    {
        var text = FillHex.Text.Trim();
        if (ColorText.TryNormalizeRgb(text, out var rgb)) text = rgb + _fill[7..];
        if (ColorText.TryNormalizeRgba(text, out var f)) SetFill(f);
        else FillHex.Text = _fill;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // ボタンにフォーカスがあるときは、そのボタンの押下に任せる
        if (Keyboard.FocusedElement is Button b && b != CreateButton) return;
        Create();
        e.Handled = true;
    }

    /// <summary>「作成」: 入力中の欄を反映してから、新しい文書を作って閉じる。</summary>
    public void Create()
    {
        WidthBox.Commit();
        HeightBox.Commit();
        CommitFill();
        _session.NewEmptyCanvas((int)WidthBox.Value, (int)HeightBox.Value, _fill);
        _host.Close(true);
    }
}
