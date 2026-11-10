// コマンドバー(§9.3 の表の 15 個のアイコンボタン。左から 取り込み・履歴・比較・背景・拡大縮小・画面 のグループ)。ボタンの間隔は space-1、グループの間に divider の縦線。
// ツールチップは「名前(ショートカット)」(FR-102)。押すと Clicked を出し、Shell がショートカットと同じ動作に振り分ける(ショートカットの無いボタンは後の担当が受ける)。
// ウェルカム画面では「画像を貼り付け」「開く」「バッチ」「設定」だけを有効にする(SetWelcome)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using GlintLoop.App.Input;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Shell;

/// <summary>コマンドバーの 1 つのボタン。IsToggle は入・切のトグル(分割比較・実出力)。</summary>
public sealed record CommandDef(int Order, string Group, IconDef Icon, ShortcutAction? Action, bool IsToggle, bool EnabledOnWelcome);

public sealed class CommandBarView : DockPanel
{
    /// <summary>§9.3 のコマンドバーの表(順・グループ・ボタン・ショートカット)。</summary>
    public static IReadOnlyList<CommandDef> Commands { get; } =
    [
        new(1, "取り込み", Icons.New, ShortcutAction.New, false, false),
        new(2, "取り込み", Icons.PasteImage, ShortcutAction.PasteImage, false, true),
        new(3, "取り込み", Icons.Open, ShortcutAction.Open, false, true),
        new(4, "取り込み", Icons.EmptyLayer, ShortcutAction.AddEmptyLayer, false, false),
        new(5, "履歴", Icons.Undo, ShortcutAction.Undo, false, false),
        new(6, "履歴", Icons.Redo, ShortcutAction.Redo, false, false),
        new(7, "比較", Icons.Original, ShortcutAction.ShowOriginal, false, false),
        new(8, "比較", Icons.SplitCompare, null, true, false),
        new(9, "比較", Icons.ActualOutput, ShortcutAction.ActualOutput, true, false),
        new(10, "背景", Icons.CanvasBackground, null, false, false),
        new(11, "拡大縮小", Icons.ZoomOut, ShortcutAction.ZoomOut, false, false),
        new(12, "拡大縮小", Icons.ZoomIn, ShortcutAction.ZoomIn, false, false),
        new(13, "拡大縮小", Icons.Fit, ShortcutAction.ZoomFit, false, false),
        new(14, "画面", Icons.Batch, ShortcutAction.OpenBatch, false, true),
        new(15, "画面", Icons.Settings, ShortcutAction.OpenSettings, false, true),
    ];

    private readonly Dictionary<IconDef, ButtonBase> _buttons = new();

    public CommandBarView()
    {
        LastChildFill = false;
        SetResourceReference(MarginProperty, "Space3HorizontalThickness");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string? group = null;
        foreach (var c in Commands)
        {
            if (group is not null && group != c.Group) row.Children.Add(Divider());
            group = c.Group;
            ButtonBase b = c.IsToggle ? new ToggleButton() : new Button();
            b.SetResourceReference(StyleProperty, c.IsToggle ? "IconToggleButton" : "IconButton");
            b.Content = Icons.Create(c.Icon);
            AutomationProperties.SetName(b, c.Icon.Name);
            b.ToolTip = Shortcuts.ToolTipText(c.Icon.Name, c.Action);
            if (row.Children.Count > 0 && row.Children[^1] is ButtonBase) b.SetResourceReference(MarginProperty, "Space1LeftThickness");
            var def = c;
            b.Click += (_, _) => Clicked?.Invoke(def);
            _buttons[c.Icon] = b;
            row.Children.Add(b);
        }
        Children.Add(row);
    }

    /// <summary>ボタンが押されたとき。</summary>
    public event Action<CommandDef>? Clicked;

    public ButtonBase Button(IconDef icon) => _buttons[icon];

    public IEnumerable<ButtonBase> Buttons => _buttons.Values;

    /// <summary>ウェルカム画面のとき、§9.3 の 4 つだけを有効にする(ほかの無効の条件は後の担当が SetEnabled で入れる)。</summary>
    public void SetWelcome(bool welcome)
    {
        foreach (var c in Commands) _buttons[c.Icon].IsEnabled = !welcome || c.EnabledOnWelcome;
    }

    public void SetEnabled(IconDef icon, bool enabled) => _buttons[icon].IsEnabled = enabled;

    private static FrameworkElement Divider()
    {
        var r = new Rectangle { VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(WidthProperty, "Stroke");
        r.SetResourceReference(HeightProperty, "CommandBarDividerHeight");
        r.SetResourceReference(Shape.FillProperty, "DividerBrush");
        r.SetResourceReference(MarginProperty, "Space1HorizontalThickness");
        return r;
    }
}
