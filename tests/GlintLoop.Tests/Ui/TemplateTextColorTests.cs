// 部品の中身に部品(TextBlock・StackPanel・Grid)を入れた所の文字色の検査(§9.1 の部品の状態の表)。中身は論理の子で、文字色を部品自身から継ぐので、
// テンプレートの ContentPresenter に付けた色(Fx.Text)が届かないことがある。ダークとライトで、中身が実際に塗る色をテンプレートの色と比べる。
// 対象: 左ペインのタブの中身(エフェクトのタブの見出し)とタブの見出し、ルックのタイル、レイヤーとスタックの行、項目の DataTemplate を使う ComboBox。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using GlintLoop.App.Layers;
using GlintLoop.App.Looks;
using GlintLoop.App.Screens;
using GlintLoop.App.Stack;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class TemplateTextColorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static Color ColorOf(Brush? b) => b is SolidColorBrush s ? s.Color : throw new InvalidOperationException($"単色でないブラシ: {b?.GetType().Name ?? "null"}");

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    /// <summary>部品のテンプレートの中で中身を出す ContentPresenter の文字色(テンプレートが付けた色)。</summary>
    private static Color PresenterColor(Control c, string? name = null)
    {
        var p = Descendants<ContentPresenter>(c).First(x => ReferenceEquals(x.TemplatedParent, c) && (name is null || x.Name == name));
        return ColorOf((Brush)p.GetValue(TextElement.ForegroundProperty));
    }

    /// <summary>サンプルを取り込み、エフェクトを 2 つ足した編集画面(GPU なし)。</summary>
    private static TestApp StartEditing(FakeOsInfo os)
    {
        var app = TestApp.Start(os: os);
        var screens = app.Ui(() => app.Shell.Modules.OfType<ScreensModule>().Single());
        app.Ui(() => { _ = screens.Imports.SampleAsync(); });
        Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Session.HasDocument && !screens.Imports.IsImporting), Wait), "サンプルの取り込みが終わらない");
        app.Ui(() =>
        {
            var stack = app.Shell.Modules.OfType<StackModule>().Single();
            foreach (var id in app.Shell.Catalog.Effects.Select(e => e.Id).Take(2)) stack.AddEffect(id);
        });
        UiHost.Idle();
        return app;
    }

    private static void SwitchToLight(TestApp app, FakeOsInfo os)
    {
        app.Ui(() => os.AppsUseLightTheme = true);
        UiHost.Idle();
    }

    [Test]
    public void AC19_左ペインのタブの中身はtext_primaryを継ぎ_見出しは状態の色のまま()
    {
        var os = new FakeOsInfo { AppsUseLightTheme = false };
        using var app = StartEditing(os);
        foreach (var theme in new[] { "ダーク", "ライト" })
        {
            if (theme == "ライト") SwitchToLight(app, os);
            app.Ui(() => app.Shell.Modules.OfType<LooksModule>().Single().SelectTab(LeftPaneTab.Effects));
            UiHost.Idle();
            var pane = app.Ui(() => app.Shell.Modules.OfType<LooksModule>().Single().LeftPane);
            // タブの見出しの文字色は 120 ms で移るので、選んだタブが accent-text になるまで待つ
            var accent = app.Ui(() => BrushColor("AccentTextBrush"));
            UiHost.WaitUntil(() => app.Ui(() => PresenterColor(pane.EffectsTab, "Header")) == accent, TimeSpan.FromSeconds(1));
            app.Ui(() =>
            {
                var primary = BrushColor("TextPrimaryBrush");
                var secondary = BrushColor("TextSecondaryBrush");
                // 中身: エフェクトのタブのステージの見出し(色を自分で付けていない TextBlock。タブの文字色を継ぐ)
                var headers = Descendants<TextBlock>(pane.EffectsTab.Content as DependencyObject ?? throw new InvalidOperationException("エフェクトのタブの中身が無い"))
                    .Where(t => AutomationProperties.GetAutomationId(t).StartsWith("effects.stage.", StringComparison.Ordinal) && t.IsVisible).ToList();
                Check.True(headers.Count >= 1, $"{theme}: エフェクトのタブにステージの見出しが無い");
                foreach (var h in headers) Check.Equal(primary, ColorOf(h.Foreground), $"{theme}: エフェクトのタブの見出し「{h.Text}」の色");
                // 見出し: 選んだタブは accent-text、ほかは text-secondary(テンプレートの色のまま変わらない)
                Check.Equal(accent, PresenterColor(pane.EffectsTab, "Header"), $"{theme}: 選んだタブの見出しの色");
                foreach (var tab in new[] { pane.LooksTab, pane.MotionTab })
                {
                    Check.Equal(secondary, PresenterColor(tab, "Header"), $"{theme}: 選んでいないタブ「{tab.Header}」の見出しの色");
                    var label = Descendants<TextBlock>(tab).First(t => t.Text == (string)tab.Header);
                    Check.Equal(secondary, ColorOf(label.Foreground), $"{theme}: 選んでいないタブ「{tab.Header}」の見出しの文字が塗る色");
                }
                var selected = Descendants<TextBlock>(pane.EffectsTab).First(t => t.Text == (string)pane.EffectsTab.Header);
                Check.Equal(accent, ColorOf(selected.Foreground), $"{theme}: 選んだタブの見出しの文字が塗る色");
            });
        }
    }

    [Test]
    public void AC19_タイルとレイヤーとスタックの行の中身の文字はテンプレートの文字色で描かれる()
    {
        var os = new FakeOsInfo { AppsUseLightTheme = false };
        using var app = StartEditing(os);
        foreach (var theme in new[] { "ダーク", "ライト" })
        {
            if (theme == "ライト") SwitchToLight(app, os);
            // スタックの行を 1 つ選んで、選んだ行と選んでいない行の両方を見る
            app.Ui(() => app.Shell.Modules.OfType<StackModule>().Single().SelectRow(0));
            UiHost.Idle();
            app.Ui(() =>
            {
                var primary = BrushColor("TextPrimaryBrush");

                // ルックのタイル(中身は StackPanel。名前の TextBlock)
                var gallery = app.Shell.Modules.OfType<LooksModule>().Single().Gallery;
                var tiles = gallery.List.Items.OfType<LookTile>().ToList();
                Check.True(tiles.Count >= 2, $"{theme}: タイルが少ない");
                if (!tiles.Any(t => t.IsSelected)) tiles[0].IsSelected = true;
                gallery.UpdateLayout();
                Check.True(tiles.Any(t => t.IsSelected) && tiles.Any(t => !t.IsSelected), $"{theme}: 選んだタイルと選んでいないタイルがそろわない");
                foreach (var tile in tiles)
                {
                    var name = Descendants<TextBlock>(tile).First(t => t.Text == tile.DisplayName);
                    var state = tile.IsSelected ? "選んだ" : "選んでいない";
                    Check.Equal(PresenterColor(tile), ColorOf(name.Foreground), $"{theme}: {state}タイル「{tile.DisplayName}」の名前の色とテンプレートの色");
                    Check.Equal(primary, ColorOf(name.Foreground), $"{theme}: {state}タイル「{tile.DisplayName}」の名前の色");
                }

                // レイヤーの行(中身は Grid。名前の TextBlock。表示中のレイヤーは text-primary)
                var rows = app.Shell.Modules.OfType<LayersModule>().Single().List.Rows;
                Check.True(rows.Any(r => r.IsSelected) && rows.Any(r => !r.IsSelected), $"{theme}: 選んだレイヤーの行と選んでいない行がそろわない");
                foreach (var row in rows)
                {
                    var state = row.IsSelected ? "選んだ" : "選んでいない";
                    Check.Equal(PresenterColor(row), ColorOf(row.NameText.Foreground), $"{theme}: {state}レイヤーの行「{row.NameText.Text}」の名前の色とテンプレートの色");
                }

                // スタックの行(中身は Grid。エフェクトの名前の TextBlock は色を自分で付けず、行の文字色を継ぐ)
                var stack = app.Shell.Modules.OfType<StackModule>().Single().StackView;
                var items = stack.RowItems;
                Check.True(items.Any(r => r.IsSelected) && items.Any(r => !r.IsSelected), $"{theme}: 選んだスタックの行と選んでいない行がそろわない");
                foreach (var item in items)
                {
                    var name = AutomationProperties.GetName(item);
                    var label = Descendants<TextBlock>(item).First(t => t.Text == name);
                    var state = item.IsSelected ? "選んだ" : "選んでいない";
                    Check.Equal(PresenterColor(item), ColorOf(label.Foreground), $"{theme}: {state}スタックの行「{name}」の名前の色とテンプレートの色");
                }
            });
        }
    }

    private sealed record Item(string Name);

    [Test]
    public void AC19_項目のDataTemplateを使うComboBoxの選んだ項目の文字はtext_primaryで描かれる()
    {
        // 担当 text のフォントの一覧(FontParamEditor)と同じ形: データの項目を DataTemplate の TextBlock で出す
        var os = new FakeOsInfo { AppsUseLightTheme = false };
        using var app = TestApp.Start(os: os);
        foreach (var theme in new[] { "ダーク", "ライト" })
        {
            if (theme == "ライト") SwitchToLight(app, os);
            ComboBox? combo = null;
            app.Ui(() =>
            {
                app.Shell.Window.SetWelcome(false);
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Item.Name)));
                combo = new ComboBox { ItemTemplate = new DataTemplate(typeof(Item)) { VisualTree = text }, ItemsSource = new[] { new Item("a"), new Item("b") } };
                combo.SelectedIndex = 0;
                app.Shell.Window.CanvasHost.Content = combo;
            });
            UiHost.Idle();
            app.Ui(() =>
            {
                var site = (ContentPresenter)combo!.Template.FindName("ContentSite", combo);
                var shown = Descendants<TextBlock>(site).Single(t => t.Text == "a");
                Check.Equal(BrushColor("TextPrimaryBrush"), ColorOf(shown.Foreground), $"{theme}: 選んだ項目の文字の色");
            });
        }
    }
}
