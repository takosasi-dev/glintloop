// キャンバスの受け口の検査: CanvasTransform の往復と通知、CanvasLayer に足した要素が変換どおりの位置に出ること、
// AppShell が CanvasView を CanvasHost に入れ、Session.Changed でウェルカム画面を切り替えること。
// 位置は UI スレッドの上で TranslatePoint と RenderSize で読む(ウィンドウは UseLayoutRounding なので 1 DIP までの差を許す)。
using System.Windows;
using System.Windows.Controls;
using GlintLoop.App.CanvasLayers;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class CanvasLayerTests
{
    private const double Tol = 1e-9;

    /// <summary>レイアウトの丸めを許す差(DIP)。</summary>
    private const double LayoutTol = 1;

    [Test]
    public void CanvasTransform_往復と通知と仮のフィット()
    {
        var t = new CanvasTransform();
        var calls = 0;
        t.Changed += () => calls++;
        Check.Equal(1.0, t.Scale, "最初の倍率");
        Check.Equal(new Vector(), t.Offset, "最初のずれ");

        t.Set(2.5, new Vector(10, -4));
        Check.Equal(1, calls, "変えたときの通知");
        t.Set(2.5, new Vector(10, -4));
        Check.Equal(1, calls, "同じ値では通知しない");

        var p = new Point(12.25, 7.5);
        var c = t.ToCanvas(p);
        Check.Near(12.25 * 2.5 + 10, c.X, Tol, "文書 → キャンバスの x");
        Check.Near(7.5 * 2.5 - 4, c.Y, Tol, "文書 → キャンバスの y");
        var back = t.ToDocument(c);
        Check.Near(p.X, back.X, Tol, "往復の x");
        Check.Near(p.Y, back.Y, Tol, "往復の y");

        var r = new Rect(3, 4, 40, 30);
        var cr = t.ToCanvas(r);
        Check.Near(3 * 2.5 + 10, cr.X, Tol, "矩形の x");
        Check.Near(40 * 2.5, cr.Width, Tol, "矩形の幅");
        var rb = t.ToDocument(cr);
        Check.Near(r.Y, rb.Y, Tol, "矩形の往復の y");
        Check.Near(r.Height, rb.Height, Tol, "矩形の往復の高さ");
        Check.True(t.ToCanvas(Rect.Empty).IsEmpty, "空の矩形");
        Check.Near(5, t.ToDocumentLength(t.ToCanvasLength(5)), Tol, "長さの往復");
        var m = t.Matrix.Transform(p);
        Check.Near(c.X, m.X, Tol, "行列の x");
        Check.Near(c.Y, m.Y, Tol, "行列の y");

        Check.Throws<ArgumentOutOfRangeException>(() => t.Set(0, new Vector()), "倍率 0");
        Check.Throws<ArgumentOutOfRangeException>(() => t.Set(double.NaN, new Vector()), "倍率 NaN");
        Check.Throws<ArgumentOutOfRangeException>(() => t.Set(1, new Vector(double.PositiveInfinity, 0)), "ずれが無限");
        Check.Equal(1, calls, "例外のときは通知しない");

        // 仮のフィット: 400×300 のキャンバスに 200×50 の文書 → 倍率 2、縦の中央
        t.FitTo(new Size(400, 300), new Size(200, 50));
        Check.Equal(2.0, t.Scale, "フィットの倍率");
        Check.Equal(new Vector(0, 100), t.Offset, "フィットのずれ");
        Check.Equal(2, calls, "フィットの通知");
        t.FitTo(new Size(0, 300), new Size(200, 50));
        Check.Equal(2, calls, "大きさ 0 のフィットは何もしない");
    }

    [Test]
    public void CanvasLayer_足した要素が変換どおりの位置に出て変換が変わると動く()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            Check.True(ReferenceEquals(app.Shell.Window.CanvasHost.Content, app.CanvasView), "CanvasHost に CanvasView が入っていない");
            Check.True(ReferenceEquals(app.CanvasView.Transform, app.CanvasTransform), "変換が同じものでない");
            var view = app.CanvasView;
            Check.Equal(0, view.Children.IndexOf(view.ImageLayer), "画像の層が下");
            Check.Equal(1, view.Children.IndexOf(view.OverlayLayer), "重ね描きの層が上");
        });

        Border image = null!, frame = null!, handle = null!;
        FreeDraw free = null!;
        app.Ui(() =>
        {
            image = new Border();
            CanvasLayer.SetDocumentRect(image, new Rect(0, 0, 100, 50));
            app.CanvasView.ImageLayer.Add(image);
            frame = new Border();
            CanvasLayer.SetDocumentRect(frame, new Rect(10, 20, 40, 30));
            handle = new Border { Width = 8, Height = 8 };
            CanvasLayer.SetDocumentPoint(handle, new Point(50, 60));
            free = new FreeDraw();
            app.CanvasView.OverlayLayer.Add(frame);
            app.CanvasView.OverlayLayer.Add(handle);
            app.CanvasView.OverlayLayer.Add(free);
            app.CanvasTransform.Set(2, new Vector(16, 8));
            app.Shell.Window.UpdateLayout();
        });
        CheckPlaced(app, image, new Rect(0, 0, 100, 50), "画像");
        CheckPlaced(app, frame, new Rect(10, 20, 40, 30), "枠");
        CheckHandle(app, handle, new Point(50, 60), "ハンドル");
        var rendersBefore = app.Ui(() =>
        {
            var v = app.CanvasView;
            Check.True(v.ActualWidth > 0 && v.ActualHeight > 0, "CanvasView が大きさを持っていない");
            Check.Near(v.ActualWidth, free.ActualWidth, LayoutTol, "置き方の無い子の幅");
            Check.Near(v.ActualHeight, free.ActualHeight, LayoutTol, "置き方の無い子の高さ");
            return free.Renders;
        });

        app.Ui(() =>
        {
            app.CanvasTransform.Set(0.5, new Vector(-4, 12));
            app.Shell.Window.UpdateLayout();
        });
        CheckPlaced(app, image, new Rect(0, 0, 100, 50), "変換を変えたあとの画像");
        CheckPlaced(app, frame, new Rect(10, 20, 40, 30), "変換を変えたあとの枠");
        CheckHandle(app, handle, new Point(50, 60), "変換を変えたあとのハンドル");
        Check.True(app.Ui(() => free.Renders) > rendersBefore, "置き方の無い子が描き直されていない");

        app.Ui(() =>
        {
            CanvasLayer.SetDocumentRect(frame, new Rect(30, 0, 10, 10));
            app.Shell.Window.UpdateLayout();
        });
        CheckPlaced(app, frame, new Rect(30, 0, 10, 10), "矩形を変えたあとの枠");
    }

    [Test]
    public void AppShell_文書の有無が変わるとウェルカム画面を切り替える()
    {
        using var app = TestApp.Start();
        Check.True(app.Ui(() => app.Shell.Window.WelcomeHost.Visibility == Visibility.Visible), "起動直後にウェルカム画面でない");
        app.Ui(() => app.Shell.Session.NewEmptyCanvas(64, 48));
        app.Ui(() =>
        {
            Check.Equal(Visibility.Collapsed, app.Shell.Window.WelcomeHost.Visibility, "文書ができたあとのウェルカム画面");
            Check.True(app.Shell.Window.LeftPane.IsEnabled, "文書ができたあとの左ペイン");
        });
        app.Ui(() => app.Shell.Session.CloseDocument());
        app.Ui(() =>
        {
            Check.Equal(Visibility.Visible, app.Shell.Window.WelcomeHost.Visibility, "閉じたあとのウェルカム画面");
            Check.True(!app.Shell.Window.LeftPane.IsEnabled, "閉じたあとの左ペイン");
        });

        // UI スレッドの外(取り込みの経路)で上がった通知も、UI スレッドに回して切り替える
        var worker = new Thread(() => app.Shell.Session.NewEmptyCanvas(64, 48));
        worker.Start();
        worker.Join();
        Check.True(UiHost.WaitUntil(() => app.Shell.Window.WelcomeHost.Visibility == Visibility.Collapsed, TimeSpan.FromSeconds(5)),
            "UI スレッドの外の変更でウェルカム画面が切り替わらない");
    }

    private static void CheckPlaced(TestApp app, FrameworkElement e, Rect doc, string what)
    {
        var (pos, size, expected) = app.Ui(() =>
        {
            var p = e.TranslatePoint(new Point(), app.CanvasView);
            return (p, e.RenderSize, app.CanvasTransform.ToCanvas(doc));
        });
        Check.Near(expected.X, pos.X, LayoutTol, what + " の x");
        Check.Near(expected.Y, pos.Y, LayoutTol, what + " の y");
        Check.Near(expected.Width, size.Width, LayoutTol, what + " の幅");
        Check.Near(expected.Height, size.Height, LayoutTol, what + " の高さ");
    }

    private static void CheckHandle(TestApp app, FrameworkElement e, Point doc, string what)
    {
        var (center, size, expected) = app.Ui(() =>
        {
            var c = e.TranslatePoint(new Point(e.RenderSize.Width / 2, e.RenderSize.Height / 2), app.CanvasView);
            return (c, e.RenderSize, app.CanvasTransform.ToCanvas(doc));
        });
        Check.Near(expected.X, center.X, LayoutTol, what + " の中心の x");
        Check.Near(expected.Y, center.Y, LayoutTol, what + " の中心の y");
        Check.Near(8, size.Width, LayoutTol, what + " の幅は倍率で変わらない");
    }

    /// <summary>置き方を持たず、変換を見て自分で描く子の見本(描いた回数を数える)。</summary>
    private sealed class FreeDraw : FrameworkElement
    {
        public int Renders { get; private set; }

        protected override void OnRender(System.Windows.Media.DrawingContext dc) => Renders++;
    }
}
