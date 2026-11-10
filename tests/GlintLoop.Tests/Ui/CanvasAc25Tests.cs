// AC-25(表示。FR-15〜FR-18・FR-20)と AC-29 の (7)(11) の検査。座標の計算は ViewMath・CanvasViewport・OutputSizing の関数に値を与えて調べ、
// 画面の配線は TestApp(画面の外のウィンドウ)で、キーは UiInput、ボタンは UIA で調べる。描画の結果の画素は WARP で描いて、同じ文書を直接描いたものとバイト列で比べる。
// キャンバスの担当の部品は app.Shell.Modules から CanvasModule を取り出す(Canvas(app))。取り込みは ImageImporter と一時フォルダの PNG で行う。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.CanvasLayers;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class CanvasAc25Tests
{
    private const double Tol = 1e-9;

    /// <summary>レイアウトの丸めを許す差(DIP)。</summary>
    private const double LayoutTol = 1;

    private static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(60);

    // ================================================================ 関数(画面を立てない)

    [Test]
    public void AC25_1_倍率の関数_フィットの範囲と1_25倍の段と100パーセントの大きさ()
    {
        var view = new Size(448, 240);
        Check.Near(8, ViewMath.FitZoom(view, new SizeD(16, 16), 1, 1), Tol, "出力 16×16 のフィット(上限で止まる)");
        Check.Near(0.1, ViewMath.FitZoom(view, new SizeD(4096, 4096), 1, 1), Tol, "出力 4096×4096 のフィット(下限で止まる)");
        // 収まる最大: 出力 480×360 は 448÷480 と 240÷360 の小さい方
        Check.Near(240.0 / 360, ViewMath.FitZoom(view, new SizeD(1024, 768), 480.0 / 1024, 1), Tol, "1024×768(出力 480×360)のフィット");

        Check.Near(1.25, ViewMath.StepZoom(1, 1), Tol, "100% から拡大 1 回");
        Check.Near(1.5625, ViewMath.StepZoom(1, 2), Tol, "100% から拡大 2 回");
        Check.Near(0.8, ViewMath.StepZoom(1, -1), Tol, "100% から縮小 1 回");
        Check.Near(8, ViewMath.StepZoom(1, 100), Tol, "拡大を繰り返すと 800% で止まる");
        Check.Near(0.1, ViewMath.StepZoom(1, -100), Tol, "縮小を繰り返すと 10% で止まる");
        Check.Near(8, ViewMath.StepZoom(7, 1), Tol, "800% を超える 1 回は 800% で止まる");

        // 100% の表示の大きさは「出力の幅と高さ ÷ DPI の倍率」
        var outScale = 480.0 / 1024;
        var at100 = ViewMath.ImageSize(new SizeD(1024, 768), 1, outScale, 1);
        Check.Near(480, at100.Width, 1e-6, "DPI 100% の幅");
        Check.Near(360, at100.Height, 1e-6, "DPI 100% の高さ");
        var at150 = ViewMath.ImageSize(new SizeD(1024, 768), 1, outScale, 1.5);
        Check.Near(320, at150.Width, 1e-6, "DPI 150% の幅");
        Check.Near(240, at150.Height, 1e-6, "DPI 150% の高さ");
        // 変換の Scale(合成のネイティブの 1 画素の DIP)は倍率 × Lout÷max ÷ DPI
        Check.Near(2 * outScale / 1.5, ViewMath.DipPerNative(2, outScale, 1.5), Tol, "変換の Scale");
    }

    [Test]
    public void FR73_出力の長辺は既定がGIFで480_ほかで4096まで_指定の種類ごとの式で16から4096に直す()
    {
        var d = OutputSettings.Default;
        Check.Equal("gif", d.Format, "既定の形式");
        var n = new SizeD(1024, 768);
        Check.Equal(480, OutputSizing.LongSide(d, n, n), "GIF の既定 min(1024, 480)");
        Check.Equal(1024, OutputSizing.LongSide(d with { Format = "png" }, n, n), "PNG の既定 min(1024, 4096)");
        Check.Equal(4096, OutputSizing.LongSide(d with { Format = "png" }, new SizeD(8000, 100), new SizeD(8000, 100)), "PNG の既定の上限 4096");
        Check.Equal(300, OutputSizing.LongSide(d, n, new SizeD(300, 200)), "既定は土台のソースの長辺(切り抜きの前)");
        Check.Equal(16, OutputSizing.LongSide(d, new SizeD(8, 8), new SizeD(8, 8)), "16 未満は 16 に直す");
        Check.Equal(640, OutputSizing.LongSide(d with { Size = new OutputSize("long", 640) }, n, n), "長辺");
        Check.Equal(400, OutputSizing.LongSide(d with { Size = new OutputSize("width", 400) }, n, n), "幅(横長)");
        Check.Equal(400, OutputSizing.LongSide(d with { Size = new OutputSize("height", 300) }, n, n), "高さ 300 → 長辺 300×1024÷768");
        Check.Equal(512, OutputSizing.LongSide(d with { Size = new OutputSize("scale", 50) }, n, n), "倍率 50%");
        Check.Equal(4096, OutputSizing.LongSide(d with { Size = new OutputSize("scale", 400) }, n, n), "4096 を超える指定は 4096");
        Check.Near(480.0 / 1024, OutputSizing.Scale(480, n), Tol, "Lout÷max(Wc, Hc)");
    }

    [Test]
    public void AC25_2_位置の関数_小さい軸は中央_大きい軸は隙間を作らずドラッグと矢印の量だけ動く()
    {
        var view = new Size(400, 300);
        // 小さい画像は中央(ドラッグしても動かない)
        Check.Equal(new Vector(100, 75), ViewMath.Constrain(new Vector(-500, 999), new Size(200, 150), view), "小さい画像は中央");
        // 大きい画像はドラッグの移動量だけ動く(隙間ができない範囲の中)
        var start = new Vector(-300, -200);
        var big = new Size(1000, 800);
        Check.Equal(start + new Vector(40, -25), ViewMath.Constrain(start + new Vector(40, -25), big, view), "大きい画像は移動量だけ動く");
        Check.Equal(new Vector(0, -500), ViewMath.Constrain(new Vector(50, -900), big, view), "端の先へは動かない");
        // 軸ごと: 幅だけ大きいとき、縦は中央
        Check.Equal(new Vector(-10, 100), ViewMath.Constrain(new Vector(-10, 0), new Size(1000, 100), view), "縦は中央、横は動く");
        Check.Equal(new Vector(32, 0), ViewMath.PanDelta(1, 0, false), "右の矢印は画像を右へ 32 DIP");
        Check.Equal(new Vector(0, -128), ViewMath.PanDelta(0, -1, true), "Shift つきの上の矢印は上へ 128 DIP");

        // 倍率を変えても、基準の点の下の文書の点は動かない
        var off = ViewMath.ZoomAbout(new Point(120, 80), new Vector(20, 30), 2, 3);
        Check.Near((120 - 20) / 2.0, (120 - off.X) / 3, Tol, "基準の点の文書の x");
        Check.Near((80 - 30) / 2.0, (80 - off.Y) / 3, Tol, "基準の点の文書の y");
    }

    [Test]
    public void AC25_1_CanvasViewport_文書を始めるとフィット_レイヤーを足しても倍率と位置は変わらず_拡大縮小で外れる()
    {
        var t = new CanvasTransform();
        var v = new CanvasViewport(t);
        v.SetDpi(1);
        // 大きさがまだ無いうちに文書が来ても、大きさが来たときにフィットする
        v.Start(new SizeD(16, 16), 1);
        v.SetViewSize(new Size(448, 240));
        Check.Near(8, v.Zoom, Tol, "16×16 のフィット");
        Check.True(v.IsFit, "フィットの状態");
        Check.Near(8, t.Scale, Tol, "変換の Scale(DPI 100%・出力 16)");
        Check.Equal(new Vector((448 - 128) / 2.0, (240 - 128) / 2.0), t.Offset, "中央");

        v.Start(new SizeD(4096, 4096), 1);
        Check.Near(0.1, v.Zoom, Tol, "4096×4096 のフィット");

        v.Start(new SizeD(1024, 768), 480.0 / 1024);
        v.SetZoom(2);
        Check.True(!v.IsFit, "倍率を変えるとフィットの状態から外れる");
        var before = (t.Scale, t.Offset);
        // レイヤーを足しても合成の寸法と出力の長辺は変わらない(UpdateGeometry に同じ値が来る)
        v.UpdateGeometry(new SizeD(1024, 768), 480.0 / 1024);
        Check.Near(2, v.Zoom, Tol, "レイヤーを足したあとの倍率");
        Check.Equal(before, (t.Scale, t.Offset), "レイヤーを足したあとの表示位置");
        // キャンバスの大きさが変わっても、フィットでないときは倍率を保つ
        v.SetViewSize(new Size(600, 400));
        Check.Near(2, v.Zoom, Tol, "大きさが変わったあとの倍率(フィットでない)");
        v.Fit();
        var fit = v.Zoom;
        v.SetViewSize(new Size(448, 240));
        Check.True(v.Zoom < fit, "フィットの状態では大きさの変化でフィットし直す");
        Check.Near(ViewMath.FitZoom(new Size(448, 240), new SizeD(1024, 768), 480.0 / 1024, 1), v.Zoom, Tol, "フィットし直した倍率");

        // DPI 150%・100% の表示の大きさ(320×240 DIP)
        v.SetDpi(1.5);
        v.SetZoom(1);
        var r = t.ToCanvas(new Rect(0, 0, 1024, 768));
        Check.Near(320, r.Width, 1e-6, "DPI 150% の 100% の幅");
        Check.Near(240, r.Height, 1e-6, "DPI 150% の 100% の高さ");

        // ドラッグの関数: 大きい画像は移動量だけ動き、小さい画像は動かない
        v.SetDpi(1);
        v.SetZoom(4);
        var o0 = t.Offset;
        v.BeginDrag(new Point(100, 100));
        v.DragTo(new Point(130, 90));
        v.EndDrag();
        Check.Equal(o0 + new Vector(30, -10), t.Offset, "ドラッグの移動量だけ動く");
        v.SetZoom(0.1);
        var small = t.Offset;
        v.BeginDrag(new Point(100, 100));
        v.DragTo(new Point(200, 150));
        Check.Equal(small, t.Offset, "小さい画像はドラッグしても動かない");
    }

    // ================================================================ 画面(GPU なし)

    [Test]
    public void AC25_1_取り込み直後はフィット_Ctrlプラスマイナスと0と1とホイール_200パーセントでレイヤーを足しても変わらない()
    {
        using var app = TestApp.Start();
        var m = Canvas(app);
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        Check.Equal(1, importer.Sample().Added, "サンプルを取り込めない");
        UiHost.Idle();
        var dpi = app.DpiScale;
        app.Ui(() =>
        {
            var view = new Size(app.CanvasView.ActualWidth, app.CanvasView.ActualHeight);
            Check.Equal(new SizeD(1024, 768), app.Shell.Session.CompositeNative(), "サンプルの合成");
            Check.Near(ViewMath.FitZoom(view, new SizeD(1024, 768), 480.0 / 1024, dpi), m.Viewport.Zoom, Tol, "取り込み直後はフィット");
            var r = app.CanvasTransform.ToCanvas(new Rect(0, 0, 1024, 768));
            Check.True(r.Width <= view.Width + 1e-6 && r.Height <= view.Height + 1e-6, "フィットはキャンバスに収まる");
            Check.True(Math.Abs(r.Width - view.Width) < 1e-6 || Math.Abs(r.Height - view.Height) < 1e-6, "フィットは収まる最大");
        });

        Key(app, System.Windows.Input.Key.D1, ModifierKeys.Control);
        app.Ui(() =>
        {
            Check.Near(1, m.Viewport.Zoom, Tol, "Ctrl+1 で 100%");
            var r = app.CanvasTransform.ToCanvas(new Rect(0, 0, 1024, 768));
            Check.Near(480 / dpi, r.Width, 1e-6, "100% の幅は出力の幅 ÷ DPI の倍率");
            Check.Near(360 / dpi, r.Height, 1e-6, "100% の高さ");
        });
        Key(app, System.Windows.Input.Key.OemPlus, ModifierKeys.Control);
        Check.Near(1.25, app.Ui(() => m.Viewport.Zoom), Tol, "Ctrl+プラス 1 回");
        Key(app, System.Windows.Input.Key.Add, ModifierKeys.Control | ModifierKeys.Shift);
        Check.Near(1.5625, app.Ui(() => m.Viewport.Zoom), Tol, "Ctrl+Shift+テンキーのプラス 2 回目");
        Key(app, System.Windows.Input.Key.D1, ModifierKeys.Control);
        Key(app, System.Windows.Input.Key.OemMinus, ModifierKeys.Control);
        Check.Near(0.8, app.Ui(() => m.Viewport.Zoom), Tol, "100% から Ctrl+マイナス 1 回");
        for (var i = 0; i < 20; i++) Key(app, System.Windows.Input.Key.Subtract, ModifierKeys.Control);
        Check.Near(0.1, app.Ui(() => m.Viewport.Zoom), Tol, "Ctrl+マイナスを繰り返すと 10% で止まる");
        for (var i = 0; i < 20; i++) Key(app, System.Windows.Input.Key.OemPlus, ModifierKeys.Control);
        Check.Near(8, app.Ui(() => m.Viewport.Zoom), Tol, "Ctrl+プラスを繰り返すと 800% で止まる");
        Key(app, System.Windows.Input.Key.D0, ModifierKeys.Control);
        app.Ui(() => Check.True(m.Viewport.IsFit, "Ctrl+0 でフィット"));

        // Ctrl+ホイール: 1 ノッチ(120)で 1 回。ホイールのイベントを CanvasView に起こす
        Key(app, System.Windows.Input.Key.D1, ModifierKeys.Control);
        app.Modifiers.Current = ModifierKeys.Control;
        app.Ui(() => Wheel(app, 120));
        Check.Near(1.25, app.Ui(() => m.Viewport.Zoom), Tol, "Ctrl+ホイール上 1 ノッチ");
        app.Ui(() => Wheel(app, -240));
        Check.Near(0.8, app.Ui(() => m.Viewport.Zoom), Tol, "Ctrl+ホイール下 2 ノッチ");
        app.Ui(() => Wheel(app, 60));
        Check.Near(0.8, app.Ui(() => m.Viewport.Zoom), Tol, "半ノッチでは変わらない");
        app.Ui(() => Wheel(app, 60));
        Check.Near(1, app.Ui(() => m.Viewport.Zoom), Tol, "半ノッチ 2 回で 1 回");
        app.Modifiers.Current = ModifierKeys.None;
        app.Ui(() => Wheel(app, 120));
        Check.Near(1, app.Ui(() => m.Viewport.Zoom), Tol, "Ctrl なしのホイールでは変わらない");

        // 200% にしてから 2 枚目をレイヤーとして足しても、倍率と表示位置は変わらない
        app.Ui(() => m.Viewport.SetZoom(2));
        var before = app.Ui(() => (app.CanvasTransform.Scale, app.CanvasTransform.Offset));
        using var dir = new TempDir();
        Drop(app, dir, "b.png", 64, 48, Img.Solid(64, 48, 10, 200, 30));
        app.Ui(() =>
        {
            Check.Equal(2, app.Shell.Session.Document!.Layers.Count, "レイヤーの数");
            Check.Near(2, m.Viewport.Zoom, Tol, "レイヤーを足したあとの倍率");
            Check.Equal(before, (app.CanvasTransform.Scale, app.CanvasTransform.Offset), "レイヤーを足したあとの表示位置");
        });

        // 空のキャンバスで始めてもフィット
        app.Ui(() => app.Shell.Session.NewEmptyCanvas(16, 16));
        app.Ui(() =>
        {
            Check.True(m.Viewport.IsFit, "空のキャンバスで始めるとフィット");
            Check.Near(8, m.Viewport.Zoom, Tol, "16×16 の空のキャンバスのフィット(上限)");
        });
    }

    [Test]
    public void AC25_2_キャンバスにフォーカスがあるとき矢印キーで32DIP_Shiftで128DIP動き_ほかの部品では動かない()
    {
        using var app = TestApp.Start();
        var m = Canvas(app);
        Check.Equal(1, new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink()).Sample().Added, "サンプル");
        UiHost.Idle();
        app.Ui(() =>
        {
            m.Viewport.SetZoom(4);
            Keyboard.Focus(m.Surface);
            Check.True(m.Surface.IsKeyboardFocused, "キャンバスにフォーカスが入らない");
        });
        var o0 = app.Ui(() => app.CanvasTransform.Offset);
        Key(app, System.Windows.Input.Key.Right, ModifierKeys.None);
        Check.Equal(o0 + new Vector(32, 0), app.Ui(() => app.CanvasTransform.Offset), "右の矢印で画像の左端が右へ 32 DIP");
        Key(app, System.Windows.Input.Key.Up, ModifierKeys.Shift);
        Check.Equal(o0 + new Vector(32, -128), app.Ui(() => app.CanvasTransform.Offset), "Shift+上で上へ 128 DIP");
        Key(app, System.Windows.Input.Key.Left, ModifierKeys.None);
        Key(app, System.Windows.Input.Key.Down, ModifierKeys.Shift);
        Check.Equal(o0, app.Ui(() => app.CanvasTransform.Offset), "左と Shift+下で戻る");

        // ほかの部品(コマンドバーのボタン)にフォーカスがあるときは動かない
        app.Ui(() => Keyboard.Focus(app.Shell.CommandBar.Button(Icons.ZoomIn)));
        Key(app, System.Windows.Input.Key.Right, ModifierKeys.None);
        Check.Equal(o0, app.Ui(() => app.CanvasTransform.Offset), "ボタンにフォーカスがあるときの矢印キー");

        // 画像がキャンバスより小さいときは中央に置かれ、矢印でもドラッグでも動かない
        app.Ui(() =>
        {
            m.Viewport.SetZoom(0.1);
            Keyboard.Focus(m.Surface);
        });
        var center = app.Ui(() => app.CanvasTransform.Offset);
        app.Ui(() =>
        {
            var view = new Size(app.CanvasView.ActualWidth, app.CanvasView.ActualHeight);
            var r = app.CanvasTransform.ToCanvas(new Rect(0, 0, 1024, 768));
            Check.Near(view.Width / 2, r.X + r.Width / 2, 1e-6, "小さい画像の中心 x");
            Check.Near(view.Height / 2, r.Y + r.Height / 2, 1e-6, "小さい画像の中心 y");
        });
        Key(app, System.Windows.Input.Key.Right, ModifierKeys.None);
        app.Ui(() =>
        {
            m.Viewport.BeginDrag(new Point(10, 10));
            m.Viewport.DragTo(new Point(60, 40));
            m.Viewport.EndDrag();
        });
        Check.Equal(center, app.Ui(() => app.CanvasTransform.Offset), "小さい画像は矢印とドラッグで動かない");
    }

    [Test]
    public void AC29_11_コマンドバーの15個は表の順で_ウェルカム画面と編集画面と履歴の端の有効と無効_ツールチップの形()
    {
        using var app = TestApp.Start();
        var root = Uia.Root(app.Hwnd);
        var names = CommandBarView.Commands.Select(c => c.Icon.Name).ToList();
        Check.Equal(15, names.Count, "ボタンの数");

        // ウェルカム画面: 有効なのは「画像を貼り付け」「開く」「バッチ」「設定」(§9.3)
        var enabled = names.Where(n => Uia.Find(root, n).Current.IsEnabled).ToList();
        Check.SequenceEqual(["画像を貼り付け", "開く", "バッチ", "設定"], enabled, "ウェルカム画面で有効なボタン");

        app.Ui(() => app.Shell.Session.NewEmptyCanvas(64, 48));
        UiHost.Idle();
        var rects = names.Select(n => (Name: n, Rect: Uia.RectDip(Uia.Find(root, n), app.DpiScale))).ToList();
        Check.SequenceEqual(names, rects.OrderBy(r => r.Rect.X).Select(r => r.Name).ToList(), "左からの並び(表の順)");
        for (var i = 1; i < rects.Count; i++)
            Check.True(rects[i].Rect.Left >= rects[i - 1].Rect.Right - 0.5, $"「{rects[i].Name}」が前のボタンに重なる");
        Check.SequenceEqual(names, CommandBarView.Commands.OrderBy(c => c.Order).Select(c => c.Icon.Name).ToList(), "表の番号の順");
        Check.SequenceEqual(
            ["新規", "画像を貼り付け", "開く", "空のレイヤー", "元に戻す", "やり直す", "元画像", "分割比較", "実出力", "キャンバスの背景", "縮小", "拡大", "フィット", "バッチ", "設定"],
            names, "§9.3 の表の名前");

        // 編集画面: 履歴の端の「元に戻す」「やり直す」と、書き出しが完成するまでの「実出力」だけが無効
        var disabled = names.Where(n => !Uia.Find(root, n).Current.IsEnabled).ToList();
        Check.SequenceEqual(["元に戻す", "やり直す", "実出力"], disabled, "文書を始めた直後に無効なボタン");

        app.Ui(() => app.Shell.Session.AddEffect(Target.OfLayer(0), "invert"));
        UiHost.Idle();
        Check.True(Uia.Find(root, "元に戻す").Current.IsEnabled, "変更のあとの「元に戻す」");
        Check.True(!Uia.Find(root, "やり直す").Current.IsEnabled, "変更のあとの「やり直す」");
        Uia.Invoke(Uia.Find(root, "元に戻す"));
        UiHost.Idle();
        Check.Equal(0, app.Ui(() => app.Shell.Session.Document!.Layers[0].Stack.Count), "「元に戻す」のボタンで戻る");
        Check.True(!Uia.Find(root, "元に戻す").Current.IsEnabled, "履歴の先頭の「元に戻す」");
        Check.True(Uia.Find(root, "やり直す").Current.IsEnabled, "Undo のあとの「やり直す」");
        Key(app, System.Windows.Input.Key.Y, ModifierKeys.Control);
        Check.Equal(1, app.Ui(() => app.Shell.Session.Document!.Layers[0].Stack.Count), "Ctrl+Y でやり直す");
        Key(app, System.Windows.Input.Key.Z, ModifierKeys.Control);
        Check.Equal(0, app.Ui(() => app.Shell.Session.Document!.Layers[0].Stack.Count), "Ctrl+Z で元に戻す");

        // ツールチップは「名前(ショートカット)」(ショートカットが無いボタンは名前だけ)
        app.Ui(() =>
        {
            var cb = app.Shell.CommandBar;
            Check.Equal("新規(Ctrl+N)", cb.Button(Icons.New).ToolTip as string, "新規のツールチップ");
            Check.Equal("空のレイヤー(Ctrl+Shift+N)", cb.Button(Icons.EmptyLayer).ToolTip as string, "空のレイヤーのツールチップ");
            Check.Equal("元画像(Backslash(\\))", cb.Button(Icons.Original).ToolTip as string, "元画像のツールチップ");
            Check.Equal("分割比較", cb.Button(Icons.SplitCompare).ToolTip as string, "分割比較のツールチップ(ショートカットなし)");
            Check.Equal("縮小(Ctrl+マイナス)", cb.Button(Icons.ZoomOut).ToolTip as string, "縮小のツールチップ");
            Check.Equal("フィット(Ctrl+0)", cb.Button(Icons.Fit).ToolTip as string, "フィットのツールチップ");
        });

        // 文書を閉じるとウェルカム画面の状態に戻る
        app.Ui(() => app.Shell.Session.CloseDocument());
        UiHost.Idle();
        enabled = names.Where(n => Uia.Find(root, n).Current.IsEnabled).ToList();
        Check.SequenceEqual(["画像を貼り付け", "開く", "バッチ", "設定"], enabled, "閉じたあとに有効なボタン");
    }

    // ================================================================ 画面と描画(WARP)

    [Test]
    public void D38_描画は最新だけを出し_連続する変更で溜めない_WriteableBitmapは描画の結果と一致する()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var m = Canvas(app);
        using var dir = new TempDir();
        Drop(app, dir, "a.png", 120, 80, Img.Pattern(120, 80, alpha: false));
        WaitPresented(app, m);
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        CheckPictureMatches(app, m, renderer, "取り込み直後");
        app.Ui(() =>
        {
            Check.Equal(new Rect(0, 0, 120, 80), CanvasLayer.GetDocumentRect(m.Picture), "表示の矩形は合成のネイティブ寸法");
            Check.True(ReferenceEquals(m.Picture.Parent, app.CanvasView.ImageLayer), "画像の層に置く");
            Check.True(m.Picture.Image.Source is System.Windows.Media.Imaging.WriteableBitmap, "WriteableBitmap に載せる(D-38)");
        });

        var (started, discarded, presented) = app.Ui(() =>
        {
            var s = app.Shell.Session;
            var counts = (m.Processed.Started, m.Processed.Discarded, m.Processed.PresentedCount);
            var row = s.AddEffect(Target.OfLayer(0), "blur").Row!.Value;
            for (var i = 1; i <= 5; i++) s.SetEffectParam(Target.OfLayer(0), row, "radius", new NumberValue(i * 3));
            return counts;
        });
        WaitPresented(app, m);
        app.Ui(() =>
        {
            Check.Equal(started + 2, m.Processed.Started, "6 回の変更で描き始めたのは 2 回(最初と最新)");
            Check.Equal(discarded + 1, m.Processed.Discarded, "古い結果を 1 回捨てた");
            Check.Equal(presented + 1, m.Processed.PresentedCount, "出したのは最新の 1 回");
        });
        CheckPictureMatches(app, m, renderer, "連続する変更のあと");
    }

    [Test]
    public void AC25_1_プロキシの長辺2048を超える文書でも100パーセントは出力の画素が基準()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var m = Canvas(app);
        using var dir = new TempDir();
        Drop(app, dir, "big.png", 2400, 1600, Img.Solid(2400, 1600, 30, 90, 200));
        WaitPresented(app, m);
        Key(app, System.Windows.Input.Key.D1, ModifierKeys.Control);
        app.Shell.Window.Dispatcher.Invoke(() => app.Shell.Window.UpdateLayout());
        var dpi = app.DpiScale;
        app.Ui(() =>
        {
            Check.Equal(2048, m.Processed.PresentedImage!.Width, "プロキシの長辺は 2048");
            Check.Equal(new Rect(0, 0, 2400, 1600), CanvasLayer.GetDocumentRect(m.Picture), "表示の矩形は合成のネイティブ寸法(プロキシの画素ではない)");
            // GIF の既定の出力は長辺 480(min(2400, 480))。100% の表示は 480×320 ÷ DPI の倍率
            Check.Near(1, m.Viewport.Zoom, Tol, "100%");
            Check.Near(480.0 / 2400 / dpi, app.CanvasTransform.Scale, 1e-12, "変換の Scale = 出力の画素 ÷ ネイティブ ÷ DPI");
            Check.Near(480 / dpi, m.Picture.RenderSize.Width, LayoutTol, "100% の表示の幅(DIP)");
            Check.Near(320 / dpi, m.Picture.RenderSize.Height, LayoutTol, "100% の表示の高さ(DIP)");
        });
    }

    [Test]
    public void AC25_3_AC29_7_元画像を押している間はすべてのスタックを空にした文書の表示と一致し_離すと戻り履歴は動かない()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var m = Canvas(app);
        using var dir = new TempDir();
        Drop(app, dir, "base.png", 400, 300, Img.Pattern(400, 300, alpha: false));
        Drop(app, dir, "second.png", 100, 100, Img.Solid(100, 100, 240, 200, 20));
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            var crop = s.AddEffect(Target.OfLayer(0), "crop").Row!.Value;
            Check.True(s.SetEffectParam(Target.OfLayer(0), crop, "rect", new RectValue(250, 0, 250, 0)).Changed, "切り抜き");
            Check.True(s.AddEffect(Target.OfLayer(1), "blur").Changed, "2 枚目のぼかし");
            Check.True(s.SetLayerPlacement(1, 30, 10, 50).Changed, "2 枚目の配置");
            Check.True(s.SetLayerOpacity(1, 70).Changed, "2 枚目の不透明度");
            Check.True(s.AddEffect(Target.All, "vignette").Changed, "全体の vignette");
            Check.Equal(new SizeD(200, 300), s.CompositeNative(), "切り抜いた合成");
        });
        WaitPresented(app, m);
        var (zoom, scale, offset, undo, doc) = app.Ui(() =>
            (m.Viewport.Zoom, app.CanvasTransform.Scale, app.CanvasTransform.Offset, app.Shell.Session.UndoCount, app.Shell.Session.Document!));

        // Backslash を押している間
        app.Ui(() => UiInput.KeyDown(app.Shell.Window, System.Windows.Input.Key.OemBackslash));
        Check.True(UiHost.WaitUntil(() => m.Mode == CanvasMode.Original && !m.Original.IsBusy, RenderWait), "元画像の表示にならない");
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var expected = renderer.RenderProxyAsync(OriginalView.Of(doc)).GetAwaiter().GetResult();
        app.Ui(() =>
        {
            Check.Equal(new SizeD(400, 300), m.Original.PresentedImage!.Layout.Native, "合成は 400×300 に戻る");
            Check.True(m.OriginalPicture.CopyPixels()!.AsSpan().SequenceEqual(expected.Pixels), "表示がすべてのスタックを空にした文書の表示と一致しない");
            Check.Equal(Visibility.Collapsed, m.Picture.Visibility, "加工後の表示を隠す");
            Check.Equal(Visibility.Visible, m.OriginalPicture.Visibility, "加工前の表示を出す");
            // 同じ倍率(出力の画素の基準)・同じ中心
            Check.Near(zoom, m.Viewport.Zoom, Tol, "倍率は変わらない");
            Check.Equal((scale, offset), (app.CanvasTransform.Scale, app.CanvasTransform.Offset), "変換は変わらない");
            var r = m.OriginalRect;
            Check.Near(100, r.X + r.Width / 2, 1e-9, "中心 x は加工後の合成の中心");
            Check.Near(150, r.Y + r.Height / 2, 1e-9, "中心 y");
            // 加工前の出力: Lout=min(400, 480)=400 で 400×300。加工後は Lout=400 で 267×400(長辺 300 に対して 400)。
            var origOut = 400.0 / 400;
            var dip = ViewMath.ImageSize(new SizeD(400, 300), zoom, origOut, m.Viewport.Dpi);
            Check.Near(dip.Width, app.CanvasTransform.ToCanvasLength(r.Width), 1e-9, "加工前の表示の幅は同じ倍率の出力の幅");
            Check.Near(dip.Height, app.CanvasTransform.ToCanvasLength(r.Height), 1e-9, "加工前の表示の高さ");
            // 2 枚目の配置・不透明度・ブレンド・表示は今のまま、スタックは空
            var od = m.Original.PresentedDocument!;
            Check.Equal(doc.Layers[1].Place, od.Layers[1].Place, "2 枚目の配置");
            Check.Equal(70, od.Layers[1].Opacity, "2 枚目の不透明度");
            Check.Equal(doc.Layers[1].Blend, od.Layers[1].Blend, "2 枚目のブレンド");
            Check.True(od.Layers.All(l => l.Stack.Count == 0) && od.All.Stack.Count == 0, "スタックが空でない");
            Check.Equal(undo, app.Shell.Session.UndoCount, "押している間に履歴が動いた");
            Check.True(ReferenceEquals(doc, app.Shell.Session.Document), "文書が変わった");
        });

        app.Ui(() => UiInput.KeyUp(app.Shell.Window, System.Windows.Input.Key.OemBackslash));
        app.Ui(() =>
        {
            Check.Equal(CanvasMode.Normal, m.Mode, "離すと加工後に戻る");
            Check.Equal(Visibility.Visible, m.Picture.Visibility, "加工後の表示");
            Check.Equal(Visibility.Collapsed, m.OriginalPicture.Visibility, "加工前の表示を隠す");
            Check.Equal(undo, app.Shell.Session.UndoCount, "離したあとの履歴");
        });
        CheckPictureMatches(app, m, renderer, "離したあと");

        // 加工前の見た目が変わる変更(配置)のあとで押すと、描き終えるまで古い加工前を出さない
        app.Ui(() =>
        {
            Check.True(app.Shell.Session.SetLayerPlacement(1, -40, 20, 50).Changed, "2 枚目の配置を変える");
            UiInput.KeyDown(app.Shell.Window, System.Windows.Input.Key.OemBackslash);
            Check.Equal(CanvasMode.Normal, m.Mode, "配置を変えた直後に古い加工前を出した");
        });
        Check.True(UiHost.WaitUntil(() => m.Mode == CanvasMode.Original && !m.Original.IsBusy, RenderWait), "配置を変えたあとの元画像の表示にならない");
        var moved = app.Ui(() => app.Shell.Session.Document!);
        var expected2 = renderer.RenderProxyAsync(OriginalView.Of(moved)).GetAwaiter().GetResult();
        app.Ui(() => Check.True(m.OriginalPicture.CopyPixels()!.AsSpan().SequenceEqual(expected2.Pixels), "配置を変えたあとの加工前の表示が一致しない"));
        // スタックの値だけの変更(加工前の見た目は変わらない)では、押している間の表示は切れず、加工前を描き直さない
        var startedBefore = app.Ui(() => m.Original.Started);
        app.Ui(() =>
        {
            Check.True(app.Shell.Session.SetEffectParam(Target.OfLayer(1), 0, "radius", new NumberValue(20)).Changed, "ぼかしの値を変える");
            Check.Equal(CanvasMode.Original, m.Mode, "スタックの値の変更で元画像の表示が切れた");
        });
        UiHost.Idle();
        Check.Equal(startedBefore, app.Ui(() => m.Original.Started), "スタックの値の変更で加工前を描き直した");
        app.Ui(() => UiInput.KeyUp(app.Shell.Window, System.Windows.Input.Key.OemBackslash));
        Check.Equal(CanvasMode.Normal, app.Ui(() => m.Mode), "2 回目に離したとき");

        // ボタンの Click(Router.Invoke)だけでは元画像にならない(押している間だけ)
        app.Ui(() => app.Shell.Router.Invoke(GlintLoop.App.Input.ShortcutAction.ShowOriginal, app.Shell.CommandBar.Button(Icons.Original)));
        Check.Equal(CanvasMode.Normal, app.Ui(() => m.Mode), "ボタンのクリックのあと");
    }

    [Test]
    public void AC25_4_分割比較_縦線は中央から矢印で2パーセントずつ動きパンは起きず_左がソースで右が加工後()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var m = Canvas(app);
        using var dir = new TempDir();
        Drop(app, dir, "a.png", 200, 100, Img.Solid(200, 100, 200, 40, 40));
        app.Ui(() => Check.True(app.Shell.Session.AddEffect(Target.OfLayer(0), "invert").Changed, "invert"));
        WaitPresented(app, m);
        var root = Uia.Root(app.Hwnd);
        var toggle = Uia.Find(root, "分割比較");
        Check.True(Uia.TryFind(root, SplitLine.AutomationName) is null, "切のときは縦線が無い");
        Uia.Toggle(toggle);
        Check.True(UiHost.WaitUntil(() => m.Mode == CanvasMode.Split && m.Original.PresentedImage is not null && !m.Original.IsBusy, RenderWait), "分割比較にならない");
        Check.Equal(ToggleState.On, Uia.ToggleStateOf(toggle), "トグルの状態");
        Check.True(Uia.TryFind(root, SplitLine.AutomationName) is not null, "縦線が UIA で見つからない");
        app.Ui(() =>
        {
            Check.Near(0.5, m.SplitPosition, Tol, "縦線の初期位置は中央");
            Check.True(m.Line.Focusable && KeyboardNavigation.GetIsTabStop(m.Line), "縦線は Tab で止まる");
            var r = app.CanvasTransform.ToCanvas(new Rect(0, 0, 200, 100));
            Check.Near(r.X + r.Width / 2, m.SplitCanvasX, 1e-9, "縦線は合成の中央");
        });

        // 左の列がソース、右の列が加工後
        app.Ui(() =>
        {
            app.Shell.Window.UpdateLayout();
            var w = app.Shell.Window;
            var snap = Snapshot.Of(w, w.RootLayer);
            var r = app.CanvasTransform.ToCanvas(new Rect(0, 0, 200, 100));
            var y = r.Y + r.Height / 2;
            var left = app.CanvasView.TranslatePoint(new Point(r.X + r.Width * 0.25, y), w.RootLayer);
            var right = app.CanvasView.TranslatePoint(new Point(r.X + r.Width * 0.75, y), w.RootLayer);
            var src = PixelColor(m.OriginalPicture, 100, 50);
            var fx = PixelColor(m.Picture, 100, 50);
            Check.True(Math.Abs(src.R - fx.R) > 100, "ソースと加工後の色が近すぎて検査にならない");
            Check.True(snap.Near(left, src, 4), $"左の列がソースでない: {snap.Describe(left)}、期待 {src}");
            Check.True(snap.Near(right, fx, 4), $"右の列が加工後でない: {snap.Describe(right)}、期待 {fx}");
        });

        // 縦線にフォーカス: 左右の矢印で 2% ずつ動き、パンは起きない
        app.Ui(() =>
        {
            m.Viewport.SetZoom(8);
            Keyboard.Focus(m.Line);
        });
        var o0 = app.Ui(() => app.CanvasTransform.Offset);
        Key(app, System.Windows.Input.Key.Right, ModifierKeys.None);
        Check.Near(0.52, app.Ui(() => m.SplitPosition), 1e-9, "右の矢印で 2%");
        Key(app, System.Windows.Input.Key.Left, ModifierKeys.None);
        Key(app, System.Windows.Input.Key.Left, ModifierKeys.None);
        Check.Near(0.48, app.Ui(() => m.SplitPosition), 1e-9, "左の矢印 2 回で 4%");
        Check.Equal(o0, app.Ui(() => app.CanvasTransform.Offset), "縦線にフォーカスがあるときのパン");

        // キャンバスにフォーカス: 左右の矢印はパンで、縦線は動かない
        app.Ui(() => Keyboard.Focus(m.Surface));
        Key(app, System.Windows.Input.Key.Right, ModifierKeys.None);
        Check.Equal(o0 + new Vector(32, 0), app.Ui(() => app.CanvasTransform.Offset), "キャンバスにフォーカスがあるときのパン");
        Check.Near(0.48, app.Ui(() => m.SplitPosition), 1e-9, "パンのとき縦線は動かない");

        // ドラッグの関数: キャンバスの x を与えると、その位置の合成の割合になる
        app.Ui(() =>
        {
            var r = app.CanvasTransform.ToCanvas(new Rect(0, 0, 200, 100));
            m.SetSplitFromCanvasX(r.X + r.Width * 0.3);
            Check.Near(0.3, m.SplitPosition, 1e-9, "ドラッグの位置");
            m.SetSplitFromCanvasX(r.X - 1000);
            Check.Near(0, m.SplitPosition, Tol, "左端で止まる");
        });

        Uia.Toggle(toggle);
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.Equal(CanvasMode.Normal, m.Mode, "切ると通常の表示");
            Check.Equal(Visibility.Collapsed, m.Line.Visibility, "切ると縦線を隠す");
            Check.True(m.Picture.Clip is null, "切ると加工後の切り取りを外す");
        });
    }

    [Test]
    public void AC25_5_背景_透明な画素に市松模様と黒白灰と選んだ色が見え_不透明な画素は変わらず出力も変わらない()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var m = Canvas(app);
        using var dir = new TempDir();
        const int W = 64, H = 32;
        var rgba = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = W / 2; x < W; x++)
            {
                var i = (y * W + x) * 4;
                rgba[i] = 10;
                rgba[i + 1] = 200;
                rgba[i + 2] = 30;
                rgba[i + 3] = 255;
            }
        }
        Drop(app, dir, "half.png", W, H, rgba);
        WaitPresented(app, m);
        var pixels = app.Ui(() => m.Picture.CopyPixels()!);
        Check.Equal(BgPreviewMode.Checker, app.Ui(() => app.Shell.Settings.BgPreview.Mode), "既定は市松模様");

        // 合成の左上から DIP で測った点(透明な左半分の中と、不透明な右半分の中)
        (Point Canvas, Snapshot Snap) At(double dx, double dy)
        {
            var w = app.Shell.Window;
            w.UpdateLayout();
            var tl = app.CanvasTransform.ToCanvas(new Point(0, 0));
            return (app.CanvasView.TranslatePoint(new Point(tl.X + dx, tl.Y + dy), w.RootLayer), Snapshot.Of(w, w.RootLayer));
        }
        var green = Color.FromRgb(10, 200, 30);
        Color Token(string key) => app.Ui(() => (Color)app.Shell.Window.FindResource(key));
        var c1 = Token("CanvasChecker1");
        var c2 = Token("CanvasChecker2");
        Check.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), c1, "市松模様の 1 色目");
        Check.Equal(Color.FromRgb(0xCC, 0xCC, 0xCC), c2, "市松模様の 2 色目");
        void CheckAt(double dx, double dy, Color c, string what) => app.Ui(() =>
        {
            var (p, snap) = At(dx, dy);
            Check.True(snap.Near(p, c, 4), $"{what}: {snap.Describe(p)}、期待 #{c.R:X2}{c.G:X2}{c.B:X2}");
        });
        double opaqueX = 0;
        app.Ui(() => opaqueX = app.CanvasTransform.ToCanvasLength(W * 0.75));
        Check.True(app.Ui(() => app.CanvasTransform.ToCanvasLength(W / 2.0)) > 40, "透明な部分が升目を測るには小さい");

        CheckAt(4, 4, c1, "市松模様の左上の升目");
        CheckAt(12, 4, c2, "市松模様の右の升目(8 DIP 先)");
        CheckAt(4, 12, c2, "市松模様の下の升目");
        CheckAt(12, 12, c1, "市松模様の斜めの升目");
        CheckAt(20, 4, c1, "市松模様の 16 DIP 先の升目");
        CheckAt(opaqueX, 8, green, "不透明な画素(市松模様)");

        foreach (var (mode, key) in new[] { (BgPreviewMode.Black, "CanvasBlack"), (BgPreviewMode.White, "CanvasWhite"), (BgPreviewMode.Gray, "CanvasGray") })
        {
            app.Ui(() => app.Shell.Settings.BgPreview = app.Shell.Settings.BgPreview with { Mode = mode });
            var c = Token(key);
            CheckAt(4, 4, c, $"{mode} の透明な画素");
            CheckAt(12, 4, c, $"{mode} の透明な画素(隣)");
            CheckAt(opaqueX, 8, green, $"{mode} の不透明な画素");
        }
        Check.Equal(Color.FromRgb(0, 0, 0), Token("CanvasBlack"), "黒");
        Check.Equal(Color.FromRgb(0x80, 0x80, 0x80), Token("CanvasGray"), "灰");

        // 選んだ色(一覧の入力欄から入れる)
        // ピッカーは開かない(ポップアップは実際の画面に出る。INV-18)。中の部品は開かなくても動く
        app.Ui(() =>
        {
            m.Picker.ColorInput.Text = "#123456";
            m.Picker.ColorInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(app.Shell.Window)!, 0, System.Windows.Input.Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
        });
        Check.Equal(new BgPreview(BgPreviewMode.Custom, 0x123456), app.Ui(() => app.Shell.Settings.BgPreview), "選んだ色が bgPreview に入る");
        CheckAt(4, 4, Color.FromRgb(0x12, 0x34, 0x56), "選んだ色の透明な画素");
        CheckAt(opaqueX, 8, green, "選んだ色の不透明な画素");

        // 一覧で選ぶ(2 番目が黒)
        app.Ui(() =>
        {
            m.Picker.List.SelectedIndex = 1;
        });
        Check.Equal(BgPreviewMode.Black, app.Ui(() => app.Shell.Settings.BgPreview.Mode), "一覧で選んだ黒");
        Check.Equal(0x123456, app.Ui(() => app.Shell.Settings.BgPreview.Color), "選んだ色は残る");
        Check.SequenceEqual(["市松模様", "黒", "白", "灰", "選んだ色"], CanvasBackground.Items.Select(i => i.Label).ToList(), "一覧の項目");

        // 背景を変えても描画の結果(出力に使う画素)は変わらない
        app.Ui(() => Check.True(m.Picture.CopyPixels()!.AsSpan().SequenceEqual(pixels), "背景を変えると描画の結果が変わった"));
        Check.Equal(0, app.Ui(() => m.Processed.Discarded), "背景の変更で描き直さない");
    }

    // ================================================================ 道具

    private static CanvasModule Canvas(TestApp app) => app.Ui(() => app.Shell.Modules.OfType<CanvasModule>().Single());

    private static void Drop(TestApp app, TempDir dir, string name, int w, int h, byte[] rgba)
    {
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        var o = importer.Drop(new MemoryImportData().WithFiles(dir.Write(name, SkiaBytes.Png(w, h, rgba))));
        Check.Equal(1, o.Added, name + " を取り込めない");
        UiHost.Idle();
    }

    /// <summary>今の文書の描画が出るまで待つ。</summary>
    private static void WaitPresented(TestApp app, CanvasModule m) =>
        Check.True(UiHost.WaitUntil(() => !m.Processed.IsBusy && ReferenceEquals(m.Processed.PresentedDocument, app.Shell.Session.Document), RenderWait),
            "描画が出ない");

    /// <summary>表示の WriteableBitmap が、今の文書を直接描いたプロキシとバイト列で一致する。</summary>
    private static void CheckPictureMatches(TestApp app, CanvasModule m, DocumentRenderer renderer, string what)
    {
        var doc = app.Ui(() => app.Shell.Session.Document!);
        var expected = renderer.RenderProxyAsync(doc).GetAwaiter().GetResult();
        app.Ui(() =>
        {
            var px = m.Picture.CopyPixels()!;
            Check.Equal(expected.Width, m.Picture.Bitmap!.PixelWidth, what + " の幅");
            Check.Equal(expected.Height, m.Picture.Bitmap!.PixelHeight, what + " の高さ");
            Check.True(px.AsSpan().SequenceEqual(expected.Pixels), what + ": 表示の画素が描画の結果と一致しない");
        });
    }

    private static Color PixelColor(DocumentPicture p, int x, int y)
    {
        var px = p.CopyPixels()!;
        var i = (y * p.Bitmap!.PixelWidth + x) * 4;
        return Color.FromArgb(px[i + 3], px[i + 2], px[i + 1], px[i]);
    }

    /// <summary>修飾キーを入れてキーを 1 回押す(UI スレッドで起こす)。</summary>
    private static void Key(TestApp app, System.Windows.Input.Key key, ModifierKeys modifiers)
    {
        app.Modifiers.Current = modifiers;
        try
        {
            app.Ui(() => UiInput.Key(app.Shell.Window, key));
        }
        finally
        {
            app.Modifiers.Current = ModifierKeys.None;
        }
    }

    /// <summary>CanvasView にホイールのイベントを起こす(UI スレッドで呼ぶ)。</summary>
    private static void Wheel(TestApp app, int delta) =>
        app.CanvasView.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
}
