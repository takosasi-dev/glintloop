// アイコンボタンの中身(Icons.Create)の色の検査: ボタンのテーマの既定の文字色(黒)を継がず、テンプレートの文字色(text-primary、
// 切替がオンなら accent-text)で描かれること。ダークとライトの両方で、コマンドバーの全部のボタンを見る(2026-10-29 に作者が見つけた不具合)。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using GlintLoop.App.Theme;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class IconColorTests
{
    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static List<ButtonBase> CommandBarButtons(TestApp app)
    {
        var list = new List<ButtonBase>();
        void Walk(DependencyObject d)
        {
            if (d is ButtonBase b) list.Add(b);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(app.Shell.CommandBar);
        return list;
    }

    /// <summary>アイコンの中身が実際に塗る色(グリフは Foreground、Geometry は Fill)。</summary>
    private static Color IconColor(ButtonBase b) => b.Content switch
    {
        TextBlock t => ((SolidColorBrush)t.Foreground).Color,
        Path p => ((SolidColorBrush)p.Fill).Color,
        var c => throw new InvalidOperationException($"アイコンでない中身: {c?.GetType().Name}"),
    };

    private static void CheckAll(TestApp app, string theme)
    {
        var buttons = CommandBarButtons(app);
        Check.True(buttons.Count >= 10, $"{theme}: コマンドバーのボタンが少ない({buttons.Count} 個)");
        var expected = BrushColor("TextPrimaryBrush");
        foreach (var b in buttons)
        {
            if (b is ToggleButton { IsChecked: true }) continue;
            Check.Equal(expected, IconColor(b), $"{theme}: 「{System.Windows.Automation.AutomationProperties.GetName(b)}」のアイコンの色");
        }
    }

    [Test]
    public void AC19_アイコンボタンの中身はダークとライトでtext_primaryの色で描かれる()
    {
        var os = new FakeOsInfo { AppsUseLightTheme = false };
        using var app = TestApp.Start(os: os);
        app.Ui(() => CheckAll(app, "ダーク"));
        // 実際に描いた画素でも確かめる: ウィンドウを描き、使えるボタンの範囲の中に text-primary に近い画素(グリフ)がある
        app.Ui(() =>
        {
            var root = app.Shell.Window.RootLayer;
            var shot = Snapshot.Of(app.Shell.Window, root);
            var expected = BrushColor("TextPrimaryBrush");
            foreach (var b in CommandBarButtons(app).Where(x => x.IsEnabled && x.Content is TextBlock))
            {
                var r = b.TransformToAncestor(root).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
                var best = int.MaxValue;
                for (var y = r.Top; y < r.Bottom; y += 0.5)
                for (var x = r.Left; x < r.Right; x += 0.5)
                {
                    var (_, pr, pg, pb) = shot.AtDip(new Point(x, y));
                    best = Math.Min(best, Math.Max(Math.Abs(pr - expected.R), Math.Max(Math.Abs(pg - expected.G), Math.Abs(pb - expected.B))));
                }
                Check.True(best <= 8, $"「{System.Windows.Automation.AutomationProperties.GetName(b)}」の範囲に text-primary {expected} の画素が無い(いちばん近くて差 {best})");
            }
        });
        app.Ui(() => os.AppsUseLightTheme = true);
        UiHost.Idle();
        app.Ui(() => CheckAll(app, "ライト"));
    }

    [Test]
    public void AC19_切替のアイコンボタンはオンのときaccent_textの色で描かれる()
    {
        using var app = TestApp.Start();
        app.Ui(() => app.Shell.Settings.ReduceMotion = ReduceMotionSetting.On);
        UiHost.Idle();
        // 文書が無いとコマンドバーの切替は使えないので、ComponentGallery と同じく領域に切替を 1 つ置いて調べる
        ToggleButton? toggle = null;
        app.Ui(() =>
        {
            app.Shell.Window.SetWelcome(false);
            toggle = new ToggleButton { Content = Icons.Create(Icons.Show) };
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "IconToggleButton");
            app.Shell.Window.CanvasHost.Content = toggle;
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.Equal(BrushColor("TextPrimaryBrush"), IconColor(toggle!), "オフの切替のアイコンの色");
            toggle!.IsChecked = true;
        });
        // 色の移り(Fx.Text の 120 ms)が終わるまで待つ
        var accent = app.Ui(() => BrushColor("AccentTextBrush"));
        UiHost.WaitUntil(() => app.Ui(() => IconColor(toggle!)) == accent, TimeSpan.FromSeconds(1));
        app.Ui(() => Check.Equal(accent, IconColor(toggle!), "オンの切替のアイコンの色"));
        app.Ui(() => toggle!.IsChecked = false);
        var primary = app.Ui(() => BrushColor("TextPrimaryBrush"));
        UiHost.WaitUntil(() => app.Ui(() => IconColor(toggle!)) == primary, TimeSpan.FromSeconds(1));
        app.Ui(() => Check.Equal(primary, IconColor(toggle!), "オフに戻した切替のアイコンの色"));
    }
}
