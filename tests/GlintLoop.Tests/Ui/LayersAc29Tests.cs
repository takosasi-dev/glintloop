// AC-29: レイヤーの画面(FR-46・FR-50・FR-51・FR-55・§9.3)を、UIA と、座標を引数に取る関数で調べる。
// (1) 一覧の行、(2) 右ペインの縦の配分、(3) 枠とハンドルの表示、(4) ドラッグの計算、(5) クリックで選ぶ、(6) サムネイル(WARP)、(10) 空のキャンバスのダイアログ、
// (11) のうちコマンドバーの「空のレイヤー」。(7) 元画像は担当 canvas、(8) スポイトは担当 stack、(9) ルックのギャラリーは担当 looks の範囲。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Layers;
using GlintLoop.App.Shell;
using GlintLoop.App.Stack;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Ui.LayersTestKit;

namespace GlintLoop.Tests.Ui;

public sealed class LayersAc29Tests
{
    private const double Tol = 1e-9;

    // ================================================================ (1) 一覧の行

    [Test]
    public void AC29_1_一覧は全体と最前面からの行で部品の並びと大きさが表のとおり()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("背景", 400, 300), Empty("ステッカー", 100, 100), Empty("文字", 100, 100)));
        // 右ペインを最小の幅 280 にする
        app.Ui(() => app.Shell.Window.SetPaneWidths(app.Shell.Window.LeftPaneWidth, 280, PaneSide.Right));
        UiHost.Idle();

        var rows = Rows(app);
        Check.SequenceEqual(["全体", "文字", "ステッカー", "背景"], rows.Select(r => r.Current.Name).ToList(), "行の並び");
        var all = rows[0];
        foreach (var n in new[] { "表示", "複製", "削除", "サムネイル" })
            Check.True(Child(all, n) is null, $"「全体」の行に「{n}」がある");
        foreach (var row in rows.Skip(1))
        {
            var name = row.Current.Name;
            var toggle = Child(row, "表示") ?? throw new CheckFailedException(name + " の行に「表示」が無い");
            Check.Equal(ToggleState.On, Uia.ToggleStateOf(toggle), name + " の表示のトグル");
            var thumb = Child(row, "サムネイル") ?? throw new CheckFailedException(name + " の行にサムネイルが無い");
            var text = row.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, name))) ?? throw new CheckFailedException(name + " の名前の文字が無い");
            var dup = Child(row, "複製") ?? throw new CheckFailedException(name + " の行に「複製」が無い");
            var del = Child(row, "削除") ?? throw new CheckFailedException(name + " の行に「削除」が無い");
            var r = Dip(app, row);
            Check.Near(40, r.Height, 0.5, name + " の行の高さ");
            var parts = new[] { Dip(app, toggle), Dip(app, thumb), Dip(app, text), Dip(app, dup), Dip(app, del) };
            foreach (var (p, what) in new[] { (parts[0], "表示"), (parts[1], "サムネイル"), (parts[3], "複製"), (parts[4], "削除") })
            {
                Check.Near(32, p.Width, 0.5, $"{name} の{what}の幅");
                Check.Near(32, p.Height, 0.5, $"{name} の{what}の高さ");
            }
            Check.True(parts[2].Width >= 56 - 0.5, $"{name} の名前の幅 {parts[2].Width:F1}");
            for (var i = 1; i < parts.Length; i++)
                Check.True(parts[i].Left >= parts[i - 1].Right - 0.5, $"{name} の部品 {i} が左の部品と重なるか順が違う");
        }
        // レイヤーが 1 枚の文書では、削除ボタンが無効
        Open(app, Doc(Empty("一枚", 64, 64)));
        var only = Rows(app)[1];
        Check.True(!Child(only, "削除")!.Current.IsEnabled, "1 枚の文書の削除ボタンが有効");
    }

    [Test]
    public void AC29_1_長い名前は省略して全文をToolTipとNameに入れ表示がオフの行は淡色()
    {
        using var app = TestApp.Start();
        var longName = new string('長', 40);
        Open(app, Doc(Empty("背景", 400, 300), Empty(longName, 100, 100), Empty("隠し", 100, 100, visible: false)));
        app.Ui(() => app.Shell.Window.SetPaneWidths(app.Shell.Window.LeftPaneWidth, 280, PaneSide.Right));
        UiHost.Idle();
        var rows = Rows(app);
        Check.Equal(longName, rows[2].Current.Name, "長い名前の行の Name");
        var m = Module(app);
        app.Ui(() =>
        {
            var row = RowOf(m, 1);
            var t = row.NameText;
            Check.Equal(TextTrimming.CharacterEllipsis, t.TextTrimming, "省略の指定");
            Check.Equal(TextWrapping.NoWrap, t.TextWrapping, "1 行");
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check.True(t.DesiredSize.Width > t.ActualWidth + 1, $"40 文字が欄に収まっている(省略されない): {t.DesiredSize.Width:F1} ≤ {t.ActualWidth:F1}");
            Check.Equal(longName, (string)t.ToolTip, "ToolTip の全文");
            Check.Equal("TextPrimaryBrush", Fx.GetText(t), "オンの行の名前の色");
            Check.Equal("TextPrimaryBrush", Fx.GetStroke(row.Thumb!), "オンの行のサムネイルの色");
            var hidden = RowOf(m, 2);
            Check.Equal("TextSecondaryBrush", Fx.GetText(hidden.NameText), "オフの行の名前の色");
            Check.Equal("TextSecondaryBrush", Fx.GetStroke(hidden.Thumb!), "オフの行のサムネイルの色");
            Check.True(hidden.VisibleToggle!.IsChecked == false, "オフの行のトグル");
        });
        Check.Equal(ToggleState.Off, Uia.ToggleStateOf(Child(rows[1], "表示")!), "オフの行のトグルの UIA");
    }

    [Test]
    public void AC29_1_行の選択でスタックと設定の対象が替わり矢印キーで1行ずつ動いてパンは起きない()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100), Empty("C", 100, 100)));
        var m = Module(app);
        var rows = Rows(app);
        var undo = app.Ui(() => app.Shell.Session.UndoCount);
        Uia.Select(rows[2]);
        UiHost.Idle();
        Check.Equal(Target.OfLayer(1), app.Ui(() => app.Shell.Session.Selected), "B の行を選んだあとの対象");
        Check.Equal(1, app.Ui(() => m.Settings.LayerIndex), "設定の対象");
        Check.True(app.Ui(() => RowOf(m, 1).IsSelected), "選択中の行の見た目(RowItem の IsSelected)");
        Uia.Select(rows[0]);
        UiHost.Idle();
        Check.Equal(Target.All, app.Ui(() => app.Shell.Session.Selected), "「全体」を選んだあと");
        Check.True(app.Ui(() => !m.ShowsSettings), "全体ではレイヤーの設定を出さない");

        var offset = app.Ui(() =>
        {
            app.CanvasTransform.Set(1, new Vector(5, 6));
            m.List.FocusSelected();
            return app.CanvasTransform.Offset;
        });
        var seen = new List<Target>();
        for (var i = 0; i < 4; i++)
        {
            Key(app, System.Windows.Input.Key.Down);
            seen.Add(app.Ui(() => app.Shell.Session.Selected));
        }
        Check.SequenceEqual([Target.OfLayer(2), Target.OfLayer(1), Target.OfLayer(0), Target.OfLayer(0)], seen, "下の矢印キーで 1 行ずつ、端で止まる");
        Key(app, System.Windows.Input.Key.Up);
        Check.Equal(Target.OfLayer(1), app.Ui(() => app.Shell.Session.Selected), "上の矢印キー");
        Check.Equal(offset, app.Ui(() => app.CanvasTransform.Offset), "矢印キーでキャンバスがパンした");
        Check.True(app.Ui(() => Keyboard.FocusedElement == RowOf(m, 1)), "フォーカスが選んだ行に移らない");
        Check.Equal(undo, app.Ui(() => app.Shell.Session.UndoCount), "選ぶだけで履歴が増えた");
    }

    // ================================================================ (2) 右ペインの縦の配分

    private static RightPaneHeights Heights(TestApp app) => app.Ui(() =>
    {
        app.Shell.Window.UpdateLayout();
        return app.Shell.Window.RightHeights!.Value;
    });

    [Test]
    public void AC29_2_右ペインの縦の配分の式はAC29の数のとおり()
    {
        // ウィンドウの高さ H のとき右ペインの高さは H − 48 − 72 − 8×2(§9.3)
        var m = PaneLayout.Metrics.From(AppResources.LayoutTokens);
        static double Pane(double h) => h - 48 - 72 - 16;
        var h800 = PaneLayout.Initial(m, Pane(800));
        Check.Equal(new RightPaneHeights(176, 220, 220), h800, "1280×800 の初期の配分");
        Check.Equal(664.0, h800.Total + 32 + 16, "1280×800 の合計");
        var h640 = PaneLayout.Initial(m, Pane(640));
        Check.Equal(new RightPaneHeights(136, 160, 160), h640, "1120×640 の初期の配分");
        Check.Equal(504.0, h640.Total + 32 + 16, "1120×640 の合計");
        var r740 = PaneLayout.Resize(m, h800, Pane(740));
        Check.Equal(new RightPaneHeights(176, 220, 160), r740, "800 → 740");
        var r700 = PaneLayout.Resize(m, r740, Pane(700));
        Check.Equal(new RightPaneHeights(176, 180, 160), r700, "→ 700");
        var r640 = PaneLayout.Resize(m, r700, Pane(640));
        Check.Equal(new RightPaneHeights(136, 160, 160), r640, "→ 640");
        Check.Equal(new RightPaneHeights(136, 160, 320), PaneLayout.Resize(m, r640, Pane(800)), "640 → 800");
        var b = PaneLayout.MoveBoundary(m, h800, 0, 8);
        Check.Equal(new RightPaneHeights(184, 212, 220), b, "下矢印");
        b = PaneLayout.MoveBoundary(m, b, 0, 32);
        Check.Equal(new RightPaneHeights(216, 180, 220), b, "Shift+下矢印 1 回目");
        Check.Equal(new RightPaneHeights(236, 160, 220), PaneLayout.MoveBoundary(m, b, 0, 32), "Shift+下矢印 2 回目");
        var u = h800;
        for (var i = 0; i < 6; i++) u = PaneLayout.MoveBoundary(m, u, 0, -8);
        Check.Equal(128.0, u.Layers, "上矢印 6 回");
        Check.Equal(u, PaneLayout.MoveBoundary(m, u, 0, -8), "上矢印 7 回目");
    }

    [Test]
    public void AC29_2_一覧とスタックの境界の矢印キーは上下の2つだけを変え最小で止まり縦の配分は保存しない()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 64, 64)));
        var start = Heights(app);
        // 1280×800 の初期の配分の絶対値(ウィンドウの大きさが画面の画素に丸まる分の 1 DIP まで許す)
        Check.True(Math.Abs(start.Layers - 176) <= 1 && Math.Abs(start.Stack - 220) <= 1 && Math.Abs(start.Params - 220) <= 1,
            $"1280×800 の初期の配分: 期待 (176, 220, 220)、実際 {start}");
        app.Ui(() => Keyboard.Focus(app.Shell.Window.LayersStackSplitter));
        Key(app, System.Windows.Input.Key.Down);
        var d1 = Heights(app);
        Check.Equal(start with { Layers = start.Layers + 8, Stack = start.Stack - 8 }, d1, "下矢印 1 回");
        for (var i = 0; i < 20; i++) Key(app, System.Windows.Input.Key.Down, ModifierKeys.Shift);
        var d2 = Heights(app);
        Check.Equal(160.0, d2.Stack, "スタックの最小で止まる");
        Check.Equal(start.Params, d2.Params, "パラメータ欄が変わった");
        Check.Equal(start.Layers + start.Stack - 160, d2.Layers, "一覧が受ける");
        for (var i = 0; i < 40; i++) Key(app, System.Windows.Input.Key.Up);
        var d3 = Heights(app);
        Check.Equal(128.0, d3.Layers, "一覧の最小で止まる");
        Check.Equal(start.Params, d3.Params, "上矢印でパラメータ欄が変わった");

        // 縦の配分は保存しない: 起動し直すと、境界を動かす前と同じ配分
        using var app2 = app.Restart();
        Check.Equal(start, Heights(app2), "起動し直したあとの配分");
    }

    // ================================================================ (3) 枠とハンドルの表示

    [Test]
    public void AC29_3_枠は土台でないオンのレイヤーでハンドルを持つ行を選んでいないときだけ出る()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 200, 100), Empty("C", 200, 100, visible: false)));
        var m = Module(app);
        int? row = null;
        app.Ui(() =>
        {
            m.SelectedStackRow = () => row;
            var s = app.Shell.Session;
            s.Select(Target.OfLayer(1));
            Check.Equal(1, m.Frame.ShownLayer, "B を選んで行を選んでいないとき");
            var crop = s.AddEffect(Target.OfLayer(1), "crop").Row!.Value;
            var blur = s.AddEffect(Target.OfLayer(1), "blur").Row!.Value;
            row = crop;
            m.StackSelectionChanged();
            Check.Equal(null, m.Frame.ShownLayer, "切り抜きの行を選んでいるとき");
            row = blur;
            m.StackSelectionChanged();
            Check.Equal(1, m.Frame.ShownLayer, "ぼかしの行を選んでいるとき");
            row = null;
            m.StackSelectionChanged();
            s.Select(Target.All);
            Check.Equal(null, m.Frame.ShownLayer, "全体");
            s.Select(Target.OfLayer(0));
            Check.Equal(null, m.Frame.ShownLayer, "土台");
            s.Select(Target.OfLayer(2));
            Check.Equal(null, m.Frame.ShownLayer, "表示がオフのレイヤー");
            var stack = s.Document!.Layers[1].Stack;
            Check.True(HandleMath.HasHandles(app.Shell.Catalog.Find("crop")!, stack[crop]), "切り抜きはハンドルを持つ");
            Check.True(!HandleMath.HasHandles(app.Shell.Catalog.Find("blur")!, stack[blur]), "ぼかし(ガウス)はハンドルを持たない");
            // 方向のぼかしは角度のハンドルを持つ(visibleWhen で角度が見える)
            s.SetEffectParam(Target.OfLayer(1), blur, "kind", new StringValue("directional"));
            Check.True(HandleMath.HasHandles(app.Shell.Catalog.Find("blur")!, s.Document!.Layers[1].Stack[blur]), "方向のぼかし");
        });
    }

    [Test]
    public void AC29_3_ぼかしガウスの行を選んでもハンドルは出ず枠が出て方向のぼかしではハンドルが出て枠が消える()
    {
        // 画面の上の部品(キャンバスの重ね描きの層のエフェクトのハンドルとレイヤーの枠)で確かめる。行はスタックの部品で選ぶ(つなぎは統合のまま)
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 200, 100)));
        var layers = Module(app);
        var stack = app.Ui(() => app.Shell.Modules.OfType<StackModule>().Single());
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.Select(Target.OfLayer(1));
            var blur = s.AddEffect(Target.OfLayer(1), "blur").Row!.Value;
            stack.SelectRow(blur);
            Check.True(app.CanvasView.OverlayLayer.Children.Contains(stack.Handles), "エフェクトのハンドルの部品が重ね描きの層に無い");
            Check.Equal(blur, stack.SelectedRow?.Row ?? -1, "ぼかしの行を選んだ");
            Check.Equal("gaussian", ((StringValue)s.Document!.Layers[1].Stack[blur].Get("kind")!).Value, "既定はガウス(テストの前提)");
            Check.Equal(0, stack.Handles.Current.Count, "ぼかし(ガウス)の行でエフェクトのハンドルが出た");
            Check.True(!stack.SelectedRowHasHandles, "ぼかし(ガウス)の行をハンドルを持つ行と数えた");
            Check.Equal(1, layers.Frame.ShownLayer, "ぼかし(ガウス)の行を選んでいるときに枠が出ない");

            s.SetEffectParam(Target.OfLayer(1), blur, "kind", new StringValue("directional"));
            Check.Equal(blur, stack.SelectedRow?.Row ?? -1, "値を変えたら行の選択が外れた");
            var hs = stack.Handles.Current;
            Check.Equal(1, hs.Count, "方向のぼかしのハンドルの数");
            Check.Equal(HandleRole.Angle, hs[0].Role, "方向のぼかしのハンドルは角度");
            Check.True(stack.SelectedRowHasHandles, "方向のぼかしの行をハンドルを持つ行と数えない");
            Check.Equal(null, layers.Frame.ShownLayer, "方向のぼかしの行を選んでいるときに枠が出た");

            s.SetEffectParam(Target.OfLayer(1), blur, "kind", new StringValue("gaussian"));
            Check.Equal(0, stack.Handles.Current.Count, "ガウスに戻したのにハンドルが残った");
            Check.Equal(1, layers.Frame.ShownLayer, "ガウスに戻したのに枠が出ない");
        });
    }

    [Test]
    public void AC29_3_枠の線はaccentでハンドルの中心はon_accentに描かれる()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 200, 100)));
        app.Ui(() =>
        {
            app.CanvasTransform.Set(1, new Vector(20, 20));
            app.Shell.Window.UpdateLayout();
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            var accent = ((SolidColorBrush)w.FindResource("AccentBrush")).Color;
            var onAccent = ((SolidColorBrush)w.FindResource("OnAccentBrush")).Color;
            var m = app.Shell.Modules.OfType<LayersModule>().Single();
            var r = m.Frame.FrameDip;
            Check.Near(120, r.X, Tol, "枠の x(DIP)");
            Check.Near(200, r.Width, Tol, "枠の幅(DIP)");
            var snap = Snapshot.Of(w, w.RootLayer);
            Point ToWin(Point p) => app.CanvasView.TranslatePoint(p, w);
            var edges = new[] { new Point(r.Left + r.Width / 2, r.Top), new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Left, r.Top + r.Height / 2), new Point(r.Right, r.Top + r.Height / 2) };
            foreach (var e in edges)
                Check.True(snap.Near(ToWin(e), accent, 4), $"辺の中点 {e} が {snap.Describe(ToWin(e))}");
            foreach (var c in new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft })
                Check.True(snap.Near(ToWin(c), onAccent, 4), $"ハンドルの中心 {c} が {snap.Describe(ToWin(c))}");
        });
    }

    [Test]
    public void AC29_3_ハンドルの当たり判定は中心から8DIP以内で優先はハンドル枠の内側そのほか()
    {
        var f = new Rect(100, 100, 200, 100);
        foreach (var (corner, hit) in new[] { (f.TopLeft, FrameHit.TopLeft), (f.TopRight, FrameHit.TopRight), (f.BottomRight, FrameHit.BottomRight), (f.BottomLeft, FrameHit.BottomLeft) })
        {
            foreach (var d in new[] { new Vector(8, 8), new Vector(-8, -8), new Vector(8, -8), new Vector(-8, 8), new Vector(0, 0), new Vector(8, 0) })
                Check.Equal(hit, LayerFrameMath.HitTest(f, corner + d, 16), $"{hit} の {d}");
            foreach (var d in new[] { new Vector(8.5, 0), new Vector(0, -8.5), new Vector(-8.5, 0), new Vector(0, 8.5) })
                Check.True(LayerFrameMath.HitTest(f, corner + d, 16) != hit, $"{hit} の {d} で当たった");
        }
        Check.Equal(FrameHit.Inside, LayerFrameMath.HitTest(f, new Point(200, 150), 16), "枠の内側");
        Check.Equal(FrameHit.TopLeft, LayerFrameMath.HitTest(f, new Point(105, 105), 16), "ハンドルは枠の内側より優先");
        Check.Equal(FrameHit.None, LayerFrameMath.HitTest(f, new Point(50, 150), 16), "そのほか");
        Check.Equal(FrameCursor.Move, LayerFrameMath.CursorFor(FrameHit.Inside), "内側は移動");
        Check.Equal(FrameCursor.SizeNwse, LayerFrameMath.CursorFor(FrameHit.TopLeft), "左上");
        Check.Equal(FrameCursor.SizeNwse, LayerFrameMath.CursorFor(FrameHit.BottomRight), "右下");
        Check.Equal(FrameCursor.SizeNesw, LayerFrameMath.CursorFor(FrameHit.TopRight), "右上");
        Check.Equal(FrameCursor.SizeNesw, LayerFrameMath.CursorFor(FrameHit.BottomLeft), "左下");
        Check.Equal(FrameCursor.Arrow, LayerFrameMath.CursorFor(FrameHit.None), "そのほか");
    }

    // ================================================================ (4) ドラッグの計算

    [Test]
    public void AC29_4_枠の内側のドラッグは表示とプロキシとDPIの倍率で合成の画素に直す()
    {
        var p0 = Placement.Identity;
        var press = new Point(500, 500);
        Placement Drag(double dx, double dy, double zoom, double lout, double dpi) =>
            LayerFrameMath.Move(p0, press, press + new Vector(dx, dy), LayerFrameMath.DipPerNative(zoom, lout / 400, dpi));
        Check.Equal(new Placement(15, -10, 100), Drag(15, -10, 1, 400, 1), "100%");
        Check.Equal(new Placement(30, -20, 100), Drag(15, -10, 0.5, 400, 1), "50%");
        Check.Equal(new Placement(7.5, -5, 100), Drag(15, -10, 2, 400, 1), "200%");
        Check.Equal(new Placement(30, -20, 100), Drag(15, -10, 1, 200, 1), "長辺 200 の 100%");
        Check.Equal(new Placement(15, -7.5, 100), Drag(10, -5, 1, 400, 1.5), "DPI 150%");
        Check.Equal(new Placement(1000, 0, 100), Drag(1000, 0, 1, 400, 1), "合成の外へ出しても制限しない");
    }

    [Test]
    public void AC29_4_隅のハンドルのドラッグは反対の隅を止めて距離の比で拡縮する()
    {
        var comp = new SizeD(400, 300);
        var p0 = Placement.Identity;
        var f = LayerFrameMath.FrameExact(comp, new SizeD(200, 100), p0);
        Check.Equal(new Rect(100, 100, 200, 100), f, "始めの枠");
        Placement S(FrameHit c, double x, double y) => LayerFrameMath.Scale(comp, f, p0, c, new Point(x, y));
        Check.Equal(new Placement(50, 25, 150), S(FrameHit.BottomRight, 400, 250), "右下を (400, 250) へ");
        Check.Equal(new Placement(-95, -47.5, 5), S(FrameHit.BottomRight, 100, 100), "右下を反対の隅へ");
        Check.Equal(new Placement(300, 150, 400), S(FrameHit.BottomRight, 1000, 1000), "右下を (1000, 1000) へ");
        var p = S(FrameHit.BottomRight, 500, 100);
        Check.Equal(178.9, p.Scale, "右下を (500, 100) への scale");
        Check.Near(78.9, p.X, 0.05 + 1e-9, "右下を (500, 100) への x");
        Check.Near(39.4, p.Y, 0.05 + 1e-9, "右下を (500, 100) への y");
        Check.Equal(new Placement(50, 25, 50), S(FrameHit.TopLeft, 200, 150), "左上を (200, 150) へ");
        Check.Equal(new Placement(-50, 25, 50), S(FrameHit.TopRight, 200, 150), "右上を (200, 150) へ");
    }

    [Test]
    public void AC29_4_枠のドラッグ1回はUndoの1件で設定の欄が新しい値を出す()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 200, 100)));
        var m = Module(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            app.CanvasTransform.Set(1, new Vector(0, 0));
            m.Refresh();
            var undo = s.UndoCount;
            // 枠の内側を (+15, −10) DIP
            m.Frame.PointerDown(new Point(200, 150));
            m.Frame.PointerMove(new Point(205, 147));
            m.Frame.PointerMove(new Point(215, 140));
            m.Frame.PointerUp(new Point(215, 140));
            Check.Equal(new Placement(15, -10, 100), s.Document!.Layers[1].Place, "移動の結果");
            Check.Equal(undo + 1, s.UndoCount, "移動のドラッグの件数");
            Check.Equal(15.0, m.Settings.XBox.Value, "位置 X の欄");
            Check.Equal(-10.0, m.Settings.YBox.Value, "位置 Y の欄");
            s.Undo();
            Check.Equal(Placement.Identity, s.Document!.Layers[1].Place, "Ctrl+Z で戻る");

            // 右下の隅のハンドルを (400, 250) へ(倍率 1 なのでキャンバスの DIP が合成の画素)
            m.Refresh();
            m.Frame.PointerDown(new Point(300, 200));
            m.Frame.PointerMove(new Point(350, 220));
            m.Frame.PointerMove(new Point(400, 250));
            m.Frame.PointerUp(new Point(400, 250));
            Check.Equal(new Placement(50, 25, 150), s.Document!.Layers[1].Place, "拡縮の結果");
            Check.Equal(150.0, m.Settings.ScaleBox.Value, "拡縮の欄");
            Check.Equal(undo + 1, s.UndoCount, "拡縮のドラッグの件数");
            s.Undo();
            Check.Equal(Placement.Identity, s.Document!.Layers[1].Place, "拡縮の Ctrl+Z");

            // (1000, 0) DIP の移動で合成の外へ出しても、位置は制限されずレイヤーは残る(E-17)
            m.Refresh();
            m.Frame.PointerDown(new Point(200, 150));
            m.Frame.PointerMove(new Point(1200, 150));
            m.Frame.PointerUp(new Point(1200, 150));
            Check.Equal(1000.0, s.Document!.Layers[1].Place.X, "外へ出した x");
            Check.Equal(2, s.Document!.Layers.Count, "レイヤーが残る");
        });
    }

    // ================================================================ (5) クリックで選ぶ

    private static readonly IReadOnlyList<Rect> AbcFrames = [new Rect(0, 0, 400, 300), new Rect(50, 40, 200, 100), new Rect(150, 100, 200, 100)];

    [Test]
    public void AC29_5_クリックは点を含む最も手前のオンのレイヤーで選択中の枠が含むときは変えない()
    {
        bool[] on = [true, true, true];
        var p = new Point(160, 110);
        Check.Equal(2, LayerFrameMath.PickLayer(AbcFrames, on, Target.OfLayer(0), p), "土台 A を選んでいるとき");
        Check.Equal(null, LayerFrameMath.PickLayer(AbcFrames, on, Target.OfLayer(1), p), "B を選んでいるとき");
        Check.Equal(1, LayerFrameMath.PickLayer(AbcFrames, on, Target.OfLayer(2), new Point(60, 50)), "C を選んでいて A と B だけに入る点");
        Check.Equal(2, LayerFrameMath.PickLayer(AbcFrames, on, Target.All, p), "全体を選んでいるとき");
        Check.Equal(1, LayerFrameMath.PickLayer(AbcFrames, [true, true, false], Target.OfLayer(0), p), "C がオフ");
        Check.Equal(null, LayerFrameMath.PickLayer(AbcFrames, on, Target.OfLayer(0), new Point(-5, 10)), "合成の外");
        Check.True(LayerFrameMath.IsClick(new Point(0, 0), new Point(3.9, 0)), "3.9 DIP はクリック");
        Check.True(!LayerFrameMath.IsClick(new Point(0, 0), new Point(4.0, 0)), "4.0 DIP はクリックでない");
    }

    [Test]
    public void AC29_5_キャンバスのクリックの選択は履歴を増やさずハンドルの上と4DIPの移動では変わらない()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 200, 100, new Placement(-50, -60, 100)), Empty("C", 200, 100, new Placement(50, 0, 100))));
        var m = Module(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            app.CanvasTransform.Set(1, new Vector(0, 0));
            s.Select(Target.OfLayer(0));
            var undo = s.UndoCount;
            void Click(Point a, Point b)
            {
                m.Frame.PointerDown(a);
                m.Frame.PointerMove(b);
                m.Frame.PointerUp(b);
            }
            Check.Equal(3, m.Frame.Frames.Count, "枠の数");
            Check.Equal(AbcFrames[1], m.Frame.Frames[1], "B の枠");
            Check.Equal(AbcFrames[2], m.Frame.Frames[2], "C の枠");
            Click(new Point(160, 110), new Point(160, 114));
            Check.Equal(Target.OfLayer(0), s.Selected, "4.0 DIP の移動で選択が変わった");
            Click(new Point(160, 110), new Point(160, 113.9));
            Check.Equal(Target.OfLayer(2), s.Selected, "3.9 DIP の移動のクリックで C");
            s.Select(Target.OfLayer(1));
            Click(new Point(160, 110), new Point(160, 110));
            Check.Equal(Target.OfLayer(1), s.Selected, "B を選んでいるときは B のまま");
            // B の右下のハンドルの上のクリックは選択を変えない(C の枠の中でもある)
            Click(new Point(250, 140), new Point(250, 140));
            Check.Equal(Target.OfLayer(1), s.Selected, "ハンドルの上のクリック");
            Check.Equal(new Placement(-50, -60, 100), s.Document!.Layers[1].Place, "動かさないハンドルのクリックで配置が変わった");
            s.Select(Target.OfLayer(2));
            Click(new Point(60, 50), new Point(60, 50));
            Check.Equal(Target.OfLayer(1), s.Selected, "C を選んでいて (60, 50)");
            Click(new Point(500, 500), new Point(500, 500));
            Check.Equal(Target.OfLayer(1), s.Selected, "合成の外");
            Check.Equal(undo, s.UndoCount, "選択で履歴が増えた");
        });
    }

    // ================================================================ (6) サムネイル

    private static byte[] Rgba(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> f)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = f(x, y);
            var i = (y * w + x) * 4;
            px[i] = c.R;
            px[i + 1] = c.G;
            px[i + 2] = c.B;
            px[i + 3] = c.A;
        }
        return px;
    }

    [Test]
    public void AC29_6_FR55_行のサムネイルは単体の64pxでエンジンと一致し絵に効く変更の300ms後に1回だけ作り直す()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        var basePng = SkiaBytes.Png(400, 300, Img.Solid(400, 300, 40, 80, 120));
        var sticker = SkiaBytes.Png(160, 80, Rgba(160, 80, (x, y) => x < 80 ? ((byte)200, (byte)30, (byte)30, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)));
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        foreach (var (file, png) in new[] { ("base.png", basePng), ("sticker.png", sticker) })
        {
            var o = importer.Drop(new MemoryImportData().WithFiles(dir.Write(file, png)));
            Check.Equal(1, o.Added, file);
            o.CompleteDisplay(); // 最初の表示を終えた知らせ(表示の担当が呼ぶ。E-20 の門を出る)
        }
        var m = Module(app);
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        UiHost.Idle();
        Check.True(app.Ui(() => RowOf(m, 1).ShownThumbnail is null), "できる前に単色の枠でない");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(299)));
        Check.Equal(0, app.Ui(() => m.Thumbnails.RenderCount), "300 ms より前に描き始めた");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1)));
        Check.True(UiHost.WaitUntil(() => m.Thumbnails.Get(0) is not null && m.Thumbnails.Get(1) is not null, TimeSpan.FromSeconds(20)), "サムネイルができない");
        Check.Equal(2, app.Ui(() => m.Thumbnails.RenderCount), "最初の描画の回数");

        // 64 px の画像は、同じ設定でエンジンから描いた画像とバイト列が一致する
        var doc = app.Ui(() => app.Shell.Session.Document!);
        var single = SingleLayerView.Of(doc, 1);
        var expected = renderer.RenderAsync(single, 64, DocumentPixelFormat.Pbgra32).GetAwaiter().GetResult();
        var thumb = app.Ui(() => m.Thumbnails.Get(1)!);
        Check.Equal(64, thumb.Width, "サムネイルの幅");
        Check.Equal(32, thumb.Height, "サムネイルの高さ");
        Check.SequenceEqual(expected.Pixels, thumb.Pixels, "エンジンの画像とのバイト列");
        app.Ui(() =>
        {
            var row = RowOf(m, 1);
            Check.True(ReferenceEquals(row.ShownThumbnail, thumb), "行が出している画像");
            Check.True(row.ShowsChecker, "透明な部分に市松模様が無い");
            var area = row.ThumbnailArea;
            Check.Near(30, area.Width, 0.01, "32 DIP の枠(線 1 を除く)に収めた幅");
            Check.Near(15, area.Height, 0.01, "縦横比を保った高さ");
        });

        // 配置・不透明度・ブレンド・表示・名前を変えても作り直さない
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetLayerPlacement(1, 30, 10, 50);
            s.SetLayerOpacity(1, 40);
            s.SetLayerBlend(1, BlendMode.Screen);
            s.SetLayerVisible(1, false);
            s.SetLayerName(1, "別の名前");
            app.Clock.Advance(TimeSpan.FromSeconds(1));
        });
        UiHost.Idle();
        Check.Equal(2, app.Ui(() => m.Thumbnails.RenderCount), "絵に効かない変更で作り直した");
        Check.True(app.Ui(() => ReferenceEquals(m.Thumbnails.Get(1), thumb)), "絵に効かない変更でサムネイルが替わった");

        // 形・ルック・仕上げを 100 ms 間隔で 5 回変えると、最後の変更の 300 ms 後に 1 回だけ
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            var row = s.AddEffect(Target.OfLayer(1), "exposure").Row!.Value;
            for (var i = 1; i <= 4; i++)
            {
                app.Clock.Advance(TimeSpan.FromMilliseconds(100));
                s.SetEffectParam(Target.OfLayer(1), row, "ev", new NumberValue(i * 0.5));
            }
            Check.True(RowOf(m, 1).ShownThumbnail is null, "変更のあと、できるまで単色の枠でない");
            app.Clock.Advance(TimeSpan.FromMilliseconds(299));
            Check.Equal(2, m.Thumbnails.RenderCount, "最後の変更から 300 ms より前に描いた");
            app.Clock.Advance(TimeSpan.FromMilliseconds(1));
            Check.Equal(3, m.Thumbnails.RenderCount, "5 回の変更で 1 回でない");
        });
        Check.True(UiHost.WaitUntil(() => m.Thumbnails.Get(1) is { } t && !ReferenceEquals(t, thumb), TimeSpan.FromSeconds(20)), "作り直しが済まない");
        var doc2 = app.Ui(() => app.Shell.Session.Document!);
        var expected2 = renderer.RenderAsync(SingleLayerView.Of(doc2, 1), 64, DocumentPixelFormat.Pbgra32).GetAwaiter().GetResult();
        Check.SequenceEqual(expected2.Pixels, app.Ui(() => m.Thumbnails.Get(1)!.Pixels), "作り直した画像とエンジンの画像");
        Check.Equal(3, app.Ui(() => m.Thumbnails.RenderCount), "ほかの行まで作り直した");
    }

    // ================================================================ (10) 空のキャンバスのダイアログ

    [Test]
    public void AC29_10_空のキャンバスのダイアログは幅400で部品がUIAで見つかりEnterで作りEscで閉じる()
    {
        using var app = TestApp.Start();
        var m = Module(app);
        app.Ui(() => _ = m.ShowEmptyCanvasDialog(MotionCause.Keyboard));
        UiHost.Idle();
        var root = Uia.Root(app.Hwnd);
        var card = Uia.FindById(root, "EmptyCanvasDialog");
        Check.Near(400, app.Ui(() =>
        {
            // ダイアログの面(DialogHost が作る Border。AutomationId は Dialog)の幅
            DependencyObject d = app.Shell.Dialogs.Top!;
            while (d is not Border b || AutomationProperties.GetAutomationId(b) != "Dialog") d = VisualTreeHelper.GetParent(d);
            return ((Border)d).ActualWidth;
        }), 0.5, "ダイアログの幅");
        foreach (var id in new[] { "EmptyCanvasWidth", "EmptyCanvasHeight", "EmptyCanvasFillSwatch", "EmptyCanvasFillHex", "EmptyCanvasFillEyedropper" })
            Uia.FindById(card, id);
        var create = Uia.Find(card, "作成");
        Uia.Find(card, "キャンセル");
        Check.True(app.Ui(() => app.Shell.Dialogs.Top is EmptyCanvasDialog { CreateButton.IsDefault: true }), "「作成」が既定のボタンでない");
        Check.True(create.Current.IsEnabled, "作成が無効");

        // Esc は何も作らずに閉じる
        app.Ui(() => Keyboard.Focus(((EmptyCanvasDialog)app.Shell.Dialogs.Top!).WidthBox));
        Key(app, System.Windows.Input.Key.Escape);
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen && !app.Shell.Session.HasDocument), "Esc で閉じない、または文書ができた");

        // Enter で作る(幅の欄に入力中の値を反映してから)
        app.Ui(() => _ = m.ShowEmptyCanvasDialog(MotionCause.Keyboard));
        UiHost.Idle();
        app.Ui(() =>
        {
            var d = (EmptyCanvasDialog)app.Shell.Dialogs.Top!;
            Keyboard.Focus(d.WidthBox);
            d.WidthBox.Text = "640";
        });
        Key(app, System.Windows.Input.Key.Enter);
        app.Ui(() =>
        {
            Check.True(!app.Shell.Dialogs.IsOpen, "Enter で閉じない");
            var doc = app.Shell.Session.Document ?? throw new CheckFailedException("Enter で文書ができない");
            Check.Equal(new EmptySize(640, 768), doc.Layers.Single().Size, "Enter で作った大きさ");
        });
    }

    // ================================================================ (11) コマンドバーの「空のレイヤー」

    [Test]
    public void AC29_11_コマンドバーの空のレイヤーはCtrlShiftNと同じ動作でツールチップは表の形()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 64, 48)));
        var button = Uia.Find(Uia.Root(app.Hwnd), "空のレイヤー");
        Check.Equal("空のレイヤー(Ctrl+Shift+N)", app.Ui(() => (string)app.Shell.CommandBar.Button(Icons.EmptyLayer).ToolTip), "ツールチップ");
        Uia.Invoke(button);
        UiHost.Idle();
        app.Ui(() =>
        {
            var doc = app.Shell.Session.Document!;
            Check.Equal(2, doc.Layers.Count, "空のレイヤーが足されない");
            Check.Equal("空のレイヤー", doc.Layers[1].Name, "名前");
            Check.Equal(Target.OfLayer(1), doc.Selected, "足したレイヤーを選ぶ");
        });
    }

    // ================================================================ 行のドラッグのドロップ先(FR-52 の関数)

    [Test]
    public void FR52_行のドラッグのドロップ先は隙間から決まり全体の上へは置けない()
    {
        // 行の中央: 全体 20、C 60、B 100、A 140(行の高さ 40)
        IReadOnlyList<double> mids = [20, 60, 100, 140];
        Check.Equal(1, LayerListMath.DropGap(mids, 0), "全体の上は隙間 1 に直す");
        Check.Equal(1, LayerListMath.DropGap(mids, 50), "C の上半分");
        Check.Equal(2, LayerListMath.DropGap(mids, 70), "C の下半分");
        Check.Equal(4, LayerListMath.DropGap(mids, 190), "最後の行の下");
        // 下から A(0)・B(1)・C(2)。C を A の下へ → 番号 0、A を C の上へ → 番号 2
        Check.Equal(0, LayerListMath.DropTarget(3, 2, 4), "C を最後へ");
        Check.Equal(2, LayerListMath.DropTarget(3, 0, 1), "A を先頭へ");
        Check.Equal(2, LayerListMath.DropTarget(3, 0, 0), "全体の上は先頭のレイヤーの上");
        Check.Equal(null, LayerListMath.DropTarget(3, 1, 2), "元の位置の上");
        Check.Equal(null, LayerListMath.DropTarget(3, 1, 3), "元の位置の下");
        Check.Equal(1, LayerListMath.DropTarget(3, 2, 3), "C を B の下へ");
        Check.Equal(0, LayerListMath.RowOf(Target.All, 3), "全体の行");
        Check.Equal(1, LayerListMath.RowOf(Target.OfLayer(2), 3), "最前面の行");
        Check.Equal(Target.OfLayer(0), LayerListMath.TargetOfRow(3, 3), "最後の行は土台");
    }
}
