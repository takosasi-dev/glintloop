// 「このソフトについて」の画面(FR-103・§9.3 の画面の表。幅 560×高さ 520 DIP。設定の上に重ねて開き、閉じると設定に戻る)。
// バージョン、MIT ライセンスの全文(LICENSE)、依存ライブラリの名前・バージョン・ライセンス(THIRD-PARTY-NOTICES.md の表と同じ内容)を上から並べる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Screens;

public sealed class AboutView : DockPanel
{
    public AboutView(DialogHost host, string version)
    {
        AutomationProperties.SetAutomationId(this, "About");
        var header = new DockPanel();
        header.SetResourceReference(MarginProperty, "Space4BottomThickness");
        var close = new Button { Content = Icons.Create(Icons.Close), ToolTip = Icons.Close.Name };
        close.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(close, Icons.Close.Name);
        close.Click += (_, _) => host.Close(null, MotionCause.Pointer);
        SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(Text(ScreenTexts.Get("about.title"), "TextSubtitle"));
        SetDock(header, Dock.Top);
        Children.Add(header);

        var body = new StackPanel();
        VersionText = Text(ScreenTexts.Get("about.version") + " " + version, "TextBody");
        VersionText.SetResourceReference(MarginProperty, "Space4BottomThickness");
        body.Children.Add(VersionText);

        body.Children.Add(Text(ScreenTexts.Get("about.license"), "TextBodyStrong", bottom: true));
        LicenseText = Text(AboutContent.LicenseText.TrimEnd('\n'), "TextCaption");
        LicenseText.SetResourceReference(MarginProperty, "Space4BottomThickness");
        AutomationProperties.SetAutomationId(LicenseText, "LicenseText");
        body.Children.Add(LicenseText);

        // THIRD-PARTY-NOTICES.md を埋め込んでいないあいだは表が 0 件なので、見出しごと出さない
        if (AboutContent.Tables.Count > 0) body.Children.Add(Text(ScreenTexts.Get("about.dependencies"), "TextBodyStrong", bottom: true));
        var tables = new List<Grid>();
        foreach (var t in AboutContent.Tables)
        {
            body.Children.Add(Text(t.Heading, "TextBody", bottom: true));
            var g = TableGrid(t);
            g.SetResourceReference(MarginProperty, "Space4BottomThickness");
            tables.Add(g);
            body.Children.Add(g);
        }
        DependencyTables = tables;
        Children.Add(body);
    }

    public TextBlock VersionText { get; }

    public TextBlock LicenseText { get; }

    /// <summary>依存ライブラリの表(THIRD-PARTY-NOTICES.md の表の順)。各 Grid の子は、行ごとに列の数だけ TextBlock が並ぶ(先頭の行は列の名前)。</summary>
    public IReadOnlyList<Grid> DependencyTables { get; }

    private static TextBlock Text(string s, string style, bool bottom = false)
    {
        var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, style);
        if (bottom) t.SetResourceReference(MarginProperty, "Space2BottomThickness");
        return t;
    }

    private static Grid TableGrid(NoticeTable t)
    {
        var g = new Grid();
        foreach (var _ in t.Columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(IReadOnlyList<string> cells, int row, bool head)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var cell = Text(c < cells.Count ? cells[c] : "", head ? "TextBodyStrong" : "TextCaption");
                cell.SetResourceReference(MarginProperty, "Space1Thickness");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
        }
        Row(t.Columns, 0, head: true);
        for (var i = 0; i < t.Rows.Count; i++) Row(t.Rows[i], i + 1, head: false);
        return g;
    }
}
