// 右ペインの縦の配分(§9.3・AC-29 の (2))を、画面の外に出した実際のウィンドウで、絶対値で確かめる。
// 起動の直後(クライアント領域 1280×800)は (176, 220, 220)。ウィンドウの大きさは画面の画素に丸まる(この PC の 115 dpi では 800 DIP が 958 px = 799.72 DIP)ので、
// 各領域は 1 DIP まで許し、いまの右ペインの高さから式(PaneLayout.Initial)で求めた配分とは完全に一致を見る。ウィンドウの大きさを変えたとき((2) の 740・700・640・800)と、頼んだクライアント領域の大きさも確かめる。
using System.Windows;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class RightPaneHeightsTests
{
    private static RightPaneHeights Heights(TestApp app) => app.Ui(() =>
    {
        app.Shell.Window.UpdateLayout();
        return app.Shell.Window.RightHeights!.Value;
    });

    /// <summary>画面の画素 1 個(DIP)以内で一致する。</summary>
    private static void Near(TestApp app, double expected, double actual, string what)
    {
        var px = 1 / app.DpiScale;
        Check.True(Math.Abs(expected - actual) <= px + 1e-9, $"{what}: 期待 {expected}、実際 {actual}(許す差 {px:F3})");
    }

    /// <summary>配分が expected と各 1 DIP 以内で一致する(画素への丸めの分だけ許す)。</summary>
    private static void Expect(RightPaneHeights expected, RightPaneHeights actual, string what)
    {
        Check.True(Math.Abs(expected.Layers - actual.Layers) <= 1 && Math.Abs(expected.Stack - actual.Stack) <= 1 && Math.Abs(expected.Params - actual.Params) <= 1,
            $"{what}: 期待 {expected}、実際 {actual}");
    }

    [Test]
    public void AC29_2_起動の直後の1280x800のウィンドウで右ペインの縦の配分は176_220_220()
    {
        using var app = TestApp.Start();
        UiHost.Idle();
        var client = app.Ui(() => app.Shell.Window.ClientSize);
        Near(app, 1280, client.Width, "クライアント領域の幅");
        Near(app, 800, client.Height, "クライアント領域の高さ");
        var h = Heights(app);
        Expect(new RightPaneHeights(176, 220, 220), h, "1280×800 の初期の配分");
        var m = PaneLayout.Metrics.From(AppResources.LayoutTokens);
        var pane = app.Ui(() => app.Shell.Window.RightGrid.ActualHeight);
        Near(app, 664, pane, "右ペインの高さ(見出し・境界を含む)");
        Check.Equal(PaneLayout.Initial(m, pane), h, "いまの右ペインの高さから式で求めた初期の配分");
    }
    /// <summary>クライアント領域の大きさを頼み、レイアウトが済むまで待つ(UI スレッドの外から呼ぶ)。</summary>
    private static Size Resize(TestApp app, double width, double height)
    {
        app.Ui(() => app.Shell.Window.RequestClientSize(new Size(width, height)));
        UiHost.Idle();
        return app.Ui(() =>
        {
            app.Shell.Window.UpdateLayout();
            return app.Shell.Window.ClientSize;
        });
    }

    [Test]
    public void AC29_2_最初のレイアウトのあとに頼んだクライアント領域の大きさになる()
    {
        // 枠の分(外寸 − クライアント領域)は最初のレイアウトのあとでも足す(足さないと枠の分だけ小さくなる)
        using var app = TestApp.Start();
        UiHost.Idle();
        foreach (var (w, h) in new[] { (1200.0, 700.0), (1120.0, 640.0), (1280.0, 800.0) })
        {
            var client = Resize(app, w, h);
            Near(app, w, client.Width, $"{w}×{h} を頼んだあとのクライアント領域の幅");
            Near(app, h, client.Height, $"{w}×{h} を頼んだあとのクライアント領域の高さ");
        }
    }

    [Test]
    public void AC29_2_ウィンドウを800から740と700と640に縮めて800に戻すと右ペインの縦の配分がAC29のとおりに変わる()
    {
        // 実際のウィンドウの大きさを変えて確かめる(右ペインの高さは親のセルから取る。RightGrid の大きさは縮まない)
        using var app = TestApp.Start();
        UiHost.Idle();
        Expect(new RightPaneHeights(176, 220, 220), Heights(app), "1280×800 の初期の配分");
        foreach (var (h, expected) in new[]
                 {
                     (740.0, new RightPaneHeights(176, 220, 160)),
                     (700.0, new RightPaneHeights(176, 180, 160)),
                     (640.0, new RightPaneHeights(136, 160, 160)),
                     (800.0, new RightPaneHeights(136, 160, 320)),
                 })
        {
            var client = Resize(app, 1280, h);
            Near(app, h, client.Height, $"高さ {h} のクライアント領域");
            var got = Heights(app);
            Expect(expected, got, $"高さ {h} の配分");
            var pane = app.Ui(() => app.Shell.Window.MiddleGrid.ActualHeight);
            Check.True(Math.Abs(got.Total + 32 + 16 - pane) <= 1e-6, $"高さ {h} で配分の合計と見出し・間隔({got.Total + 48})が右ペインの高さ {pane} と合わない");
        }
    }
}
