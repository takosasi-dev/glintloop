// AC-24 の (3)(FR-39)と (4)(FR-40): 切り抜きの「正方形に切り抜く」「余白を自動で切る」と余白の「正方形にする」。
// 値の計算は CropTools(純粋な関数)で、ボタンはパラメータ欄の上で UIA で押して、EditorSession の rect と合成の寸法を確かめる。
using System.Windows.Automation;
using GlintLoop.App.Stack;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Stack.StackTestKit;

namespace GlintLoop.Tests.Stack;

public sealed class CropToolsAc24Tests
{
    private static RectValue Rect(double l, double t, double r, double b) => new(l, t, r, b);

    [Test]
    public void AC24_3_正方形に切り抜くと正方形にするの値()
    {
        Check.Equal(Rect(250, 0, 250, 0), CropTools.SquareCrop(new SizeD(1000, 500)), "1000×500 の切り抜き");
        Check.Equal(Rect(0, 500, 0, 500), CropTools.SquarePad(new SizeD(1000, 500)), "1000×500 の余白");
        Check.Equal(Rect(0, 250, 0, 250), CropTools.SquareCrop(new SizeD(500, 1000)), "500×1000 の切り抜き");
        Check.Equal(Rect(500, 0, 500, 0), CropTools.SquarePad(new SizeD(500, 1000)), "500×1000 の余白");
        Check.Equal(Rect(0, 0, 0, 0), CropTools.SquareCrop(new SizeD(300, 300)), "正方形はそのまま");
        // 1000×301: 出力(round)の幅と高さの差が 1 px 以内
        var input = new SizeD(1000, 301);
        var crop = CompositeGeometry.CropSize(input, CropTools.SquareCrop(input));
        Check.True(Math.Abs(Math.Round(crop.W) - Math.Round(crop.H)) <= 1, $"1000×301 の切り抜きの出力 {crop.W}×{crop.H}");
        var pad = CompositeGeometry.PadSize(input, CropTools.SquarePad(input));
        Check.True(Math.Abs(Math.Round(pad.W) - Math.Round(pad.H)) <= 1, $"1000×301 の余白の出力 {pad.W}×{pad.H}");
    }

    private static byte[] Centered(int w, int h, int x0, int y0, int cw, int ch, (byte R, byte G, byte B, byte A) inner, (byte R, byte G, byte B, byte A) outer)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = x >= x0 && x < x0 + cw && y >= y0 && y < y0 + ch ? inner : outer;
            StackTestKit.SetPixel(px, w, x, y, c.R, c.G, c.B, c.A);
        }
        return px;
    }

    [Test]
    public void AC24_4_余白を自動で切るの値()
    {
        var transparent = Centered(200, 100, 50, 25, 100, 50, (10, 20, 30, 255), (0, 0, 0, 0));
        Check.Equal(Rect(250, 250, 250, 250), CropTools.AutoTrim(200, 100, transparent), "中央だけ不透明");
        var colored = Centered(200, 100, 50, 25, 100, 50, (0xFF, 0x88, 0x00, 255), (0x33, 0x66, 0x99, 255));
        Check.Equal(Rect(250, 250, 250, 250), CropTools.AutoTrim(200, 100, colored), "背景色 #336699");
        // 四隅の 1 つが左上と 7 違うと、色による除去を行わない(透明も無いので何も除かない)
        var corner = (byte[])colored.Clone();
        StackTestKit.SetPixel(corner, 200, 199, 99, 0x33 + 7, 0x66, 0x99, 255);
        Check.Equal(Rect(0, 0, 0, 0), CropTools.AutoTrim(200, 100, corner), "四隅が 7 違う");
        // 差 6 までは同じ色
        var near = (byte[])colored.Clone();
        StackTestKit.SetPixel(near, 200, 199, 99, 0x33 + 6, 0x66, 0x99, 255);
        StackTestKit.SetPixel(near, 200, 0, 99, 0x33, 0x66 - 6, 0x99, 255);
        Check.Equal(Rect(250, 250, 250, 250), CropTools.AutoTrim(200, 100, near), "四隅の差 6");
        // 四隅が不透明でないときは透明だけを除く(alpha 8 以下)
        var semi = Centered(200, 100, 50, 25, 100, 50, (0xFF, 0x88, 0x00, 255), (0x33, 0x66, 0x99, 8));
        Check.Equal(Rect(250, 250, 250, 250), CropTools.AutoTrim(200, 100, semi), "alpha 8 は透明");
        var semi9 = Centered(200, 100, 50, 25, 100, 50, (0xFF, 0x88, 0x00, 255), (0x33, 0x66, 0x99, 9));
        Check.Equal(Rect(0, 0, 0, 0), CropTools.AutoTrim(200, 100, semi9), "alpha 9 は除かない");
        // 全体が透明・全体が 1 色は値を変えない(null)
        Check.True(CropTools.AutoTrim(200, 100, Img.Solid(200, 100, 0, 0, 0, 0)) is null, "全体が透明");
        Check.True(CropTools.AutoTrim(200, 100, Img.Solid(200, 100, 9, 9, 9)) is null, "全体が 1 色");
        // 幅 3000 で左に 8 画素の余白 → l=floor(8÷3000×1000)=2
        var wide = Centered(3000, 4, 8, 0, 2992, 4, (200, 0, 0, 255), (0, 0, 0, 0));
        Check.Equal(Rect(2, 0, 0, 0), CropTools.AutoTrim(3000, 4, wide), "幅 3000 の左 8 画素");
    }

    private static void SelectAdded(TestApp app, StackModule m, string id)
    {
        app.Ui(() =>
        {
            var o = m.AddEffect(id);
            Check.True(o.Changed, id + " を足せない");
        });
        UiHost.Idle();
    }

    private static RectValue RectOf(TestApp app, string id) => app.Ui(() =>
        (RectValue)app.Shell.Session.Document!.Layers[0].Stack.Single(e => e.Id == id).Get("rect")!);

    [Test]
    public void AC24_3_ボタンで正方形に切り抜くと500x500になり正方形にすると1000x1000になる()
    {
        using var app = TestApp.Start();
        var m = Module(app);
        Import(app, 1000, 500, Img.Solid(1000, 500, 40, 80, 120));
        SelectAdded(app, m, "crop");
        var root = Uia.Root(app.Hwnd);
        Uia.Invoke(Uia.FindById(root, "params.squareCrop"));
        UiHost.Idle();
        Check.Equal(Rect(250, 0, 250, 0), RectOf(app, "crop"), "crop の rect");
        Check.Equal(new SizeD(500, 500), app.Ui(() => app.Shell.Session.CompositeNative()), "切り抜いた合成");
        // 切り抜きを消して余白
        app.Ui(() => app.Shell.Session.RemoveEffect(Target.OfLayer(0), 0));
        SelectAdded(app, m, "pad");
        Uia.Invoke(Uia.FindById(root, "params.squarePad"));
        UiHost.Idle();
        Check.Equal(Rect(0, 500, 0, 500), RectOf(app, "pad"), "pad の rect");
        Check.Equal(new SizeD(1000, 1000), app.Ui(() => app.Shell.Session.CompositeNative()), "余白を足した合成");
    }

    [Test]
    public void AC24_4_ボタンで余白を自動で切る()
    {
        using var app = TestApp.Start();
        var m = Module(app);
        Import(app, 200, 100, Centered(200, 100, 50, 25, 100, 50, (0xFF, 0x88, 0x00, 255), (0x33, 0x66, 0x99, 255)));
        SelectAdded(app, m, "crop");
        Uia.Invoke(Uia.FindById(Uia.Root(app.Hwnd), "params.autoTrim"));
        UiHost.Idle();
        Check.Equal(Rect(250, 250, 250, 250), RectOf(app, "crop"), "crop の rect");
        Check.Equal(2, app.Ui(() => app.Shell.Session.UndoCount), "追加と自動の切り抜きで 2 件(最初の取り込みは履歴を空にする)");
    }
}
