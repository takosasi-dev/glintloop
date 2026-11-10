// ショートカットの一覧(FR-101・F1): §9.12 の表と同じ 37 行×3 列(キー・動作・条件)を、ダイアログの中に並べる。閉じるボタンと Esc で閉じる。
// 文字は Shortcuts.Table のとおりで、表の上に Space の注(Shortcuts.SpaceNote)を置く。幅 560×高さ 600 DIP(§9.3 の画面の表)は DialogHost に渡すキーで決まる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlintLoop.App.Input;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Shell;

public sealed class ShortcutListView : DockPanel
{
    public ShortcutListView()
    {
        var close = new Button { Content = Icons.Create(Icons.Close), HorizontalAlignment = HorizontalAlignment.Right };
        close.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(close, Icons.Close.Name);
        close.ToolTip = Icons.Close.Name;
        close.Click += (_, _) => (FindDialogHost() ?? throw new InvalidOperationException("ダイアログの外にある")).Close();
        SetDock(close, Dock.Top);
        Children.Add(close);

        // Space の行の条件「上の注のとおり」が指す文
        Note = new TextBlock { Text = Shortcuts.SpaceNote, TextWrapping = TextWrapping.Wrap };
        Note.SetResourceReference(StyleProperty, "TextBody");
        Note.SetResourceReference(MarginProperty, "Space2BottomThickness");
        SetDock(Note, Dock.Top);
        Children.Add(Note);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        foreach (var row in Shortcuts.Table)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = row.Keys, TextWrapping = TextWrapping.Wrap };
            key.SetResourceReference(StyleProperty, "TextBodyStrong");
            key.SetResourceReference(MarginProperty, "Space2RightThickness");
            var act = new TextBlock { Text = row.Action, TextWrapping = TextWrapping.Wrap };
            act.SetResourceReference(StyleProperty, "TextBody");
            act.SetResourceReference(MarginProperty, "Space2RightThickness");
            var cond = new TextBlock { Text = row.Condition, TextWrapping = TextWrapping.Wrap };
            cond.SetResourceReference(StyleProperty, "TextBody");
            cond.SetResourceReference(MarginProperty, "Space2BottomThickness");
            Grid.SetRow(key, row.Number - 1);
            Grid.SetRow(act, row.Number - 1);
            Grid.SetRow(cond, row.Number - 1);
            Grid.SetColumn(act, 1);
            Grid.SetColumn(cond, 2);
            grid.Children.Add(key);
            grid.Children.Add(act);
            grid.Children.Add(cond);
        }
        Rows = grid;
        Children.Add(grid);
    }

    /// <summary>行の並び(テスト用。子は キー・動作・条件 の順に 3 つずつ)。</summary>
    public Grid Rows { get; }

    /// <summary>表の上の Space の注(テスト用)。</summary>
    public TextBlock Note { get; }

    private DialogHost? FindDialogHost()
    {
        DependencyObject? d = this;
        while (d is not null and not DialogHost) d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as DialogHost;
    }
}
