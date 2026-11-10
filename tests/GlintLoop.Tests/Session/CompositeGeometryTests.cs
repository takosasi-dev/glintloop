// 合成の大きさの計算(§9.7 の規則 1・3・11)の検査。AC-17 の (2) の枠の座標、(5) の合成の大きさ、(7) の n と縮めた大きさを、
// GPU を使わずに計算の関数だけで確かめる(画素の比較は描画の担当の AC-17 のテストが行う)。
// 形ステージの寸法の式(§9.4 の回転・切り抜き・余白)とプロキシ・サムネイルの長辺も確かめる。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Session.SessionTestKit;

namespace GlintLoop.Tests.Session;

public sealed class CompositeGeometryTests
{
    /// <summary>100×100 の土台の上の 20×10 のレイヤーの、長辺 lout での枠。</summary>
    private static FrameRect Frame20x10(double x, double y, double scale, int lout = 100, int w = 20, int h = 10)
    {
        var output = CompositeGeometry.Output(new SizeD(100, 100), lout);
        var image = CompositeGeometry.LayerImage(new SizeD(w, h), scale, output.S);
        return CompositeGeometry.Frame(output, x, y, image);
    }

    private static void Span(FrameRect clipped, int x0, int x1, int y0, int y1, string what)
    {
        Check.Equal(x0, clipped.Qx, what + " x の始め");
        Check.Equal(x1, clipped.Qx + clipped.W - 1, what + " x の終わり");
        Check.Equal(y0, clipped.Qy, what + " y の始め");
        Check.Equal(y1, clipped.Qy + clipped.H - 1, what + " y の終わり");
    }

    [Test]
    public void AC17_2_配置の枠の座標()
    {
        Span(Frame20x10(0, 0, 100).ClipTo(100, 100), 40, 59, 45, 54, "(0, 0, 100)");
        Check.Equal(200, Frame20x10(0, 0, 100).W * Frame20x10(0, 0, 100).H, "赤の画素数");
        Span(Frame20x10(10.5, -3.25, 100).ClipTo(100, 100), 51, 70, 42, 51, "(10.5, −3.25, 100)");
        Check.Equal(41, Frame20x10(0.5, 0, 100).Qx, "(0.5, 0, 100) の x");
        Check.Equal(40, Frame20x10(-0.5, 0, 100).Qx, "(−0.5, 0, 100) の x");
        var half = Frame20x10(0, 0, 50);
        Check.Equal((10, 5), (half.W, half.H), "(0, 0, 50) の大きさ");
        Span(half.ClipTo(100, 100), 45, 54, 48, 52, "(0, 0, 50)");
        var right = Frame20x10(45, 0, 100).ClipTo(100, 100);
        Span(right, 85, 99, 45, 54, "(45, 0, 100)");
        Check.Equal(15, right.W, "(45, 0, 100) の列数");
        Check.True(Frame20x10(200, 0, 100).ClipTo(100, 100).IsEmpty, "(200, 0, 100) が合成と重なる(E-17)");
        var big = Frame20x10(0, 0, 100, w: 400, h: 400).ClipTo(100, 100);
        Span(big, 0, 99, 0, 99, "400×400 は全面");
        var checker = Frame20x10(7, 3, 100);
        Check.Equal((47, 48, 20, 10), (checker.Qx, checker.Qy, checker.W, checker.H), "(7, 3, 100) は拡大縮小しない");
        var s2 = Frame20x10(10, 0, 100, lout: 200);
        Check.Equal((100, 90, 40, 20), (s2.Qx, s2.Qy, s2.W, s2.H), "長辺 200 で (10, 0, 100)");
    }

    [Test]
    public void AC17_5_合成の大きさは土台の形ステージで決まる()
    {
        var s = NewSession();
        s.AddImportedImages([Image(1, 400, 300)]);
        s.AddImportedImages([Image(2, 1000, 1000)]);
        var layout = s.Layout(400, 16384);
        Check.Equal((400, 300), (layout.Output.Wo, layout.Output.Ho), "1000×1000 を足しても 400×300");

        s.AddEffect(Target.OfLayer(0), "crop");
        s.SetEffectParam(Target.OfLayer(0), 0, "rect", new RectValue(250, 0, 250, 0));
        s.AddImportedImages([Image(3, 20, 10)]);
        Check.Equal(new SizeD(200, 300), s.CompositeNative(), "切り抜いたネイティブ寸法");
        var cropped = s.Layout(300, 16384);
        Check.Equal((200, 300), (cropped.Output.Wo, cropped.Output.Ho), "長辺 300");
        var red = cropped.Layers[2];
        Check.Equal(100.0, s.Document!.Layers[2].Place.Scale, "20×10 の scale");
        Span(red.Frame.ClipTo(200, 300), 90, 109, 145, 154, "20×10 の赤の枠");
        Check.Equal(cropped.Output.Wo, cropped.Layers[0].Image.Wl, "土台の Wl は Wo");
        Check.Equal(cropped.Output.Ho, cropped.Layers[0].Image.Hl, "土台の Hl は Ho");

        s.AddImportedImages([Image(4, 50, 50)]);
        s.MoveLayer(3, 0);
        var native = s.CompositeNative();
        Check.Equal(new SizeD(50, 50), native, "50×50 が土台");
        var lout = CompositeGeometry.ProxyLongSide(native);
        Check.Equal(50, lout, "長辺(元より大きくしない)");
        var moved = s.Layout(lout, 16384);
        Check.Equal((50, 50), (moved.Output.Wo, moved.Output.Ho), "50×50");
    }

    [Test]
    public void AC17_7_描画の大きさの上限のnと縮めた大きさ()
    {
        var big = CompositeGeometry.LayerImage(new SizeD(3000, 2000), 400, 1);
        Check.Equal(new LayerDims(12000, 8000), big, "scale 400 の Wl×Hl");
        Check.Equal(new RenderShrink(2, 3000, 2000), CompositeGeometry.Shrink(big, 8192), "8192 で 3000×2000×400");
        Check.Equal(new RenderShrink(0, 4096, 4096), CompositeGeometry.Shrink(new LayerDims(4096, 4096), 16384), "4096×4096");
        Check.Equal(new RenderShrink(1, 2049, 2048), CompositeGeometry.Shrink(new LayerDims(4097, 4096), 16384), "4097×4096");
        Check.Equal(new RenderShrink(1, 4097, 1), CompositeGeometry.Shrink(new LayerDims(8193, 2), 8192), "辺だけが上限を超える");

        var s = NewSession();
        s.AddImportedImages([Image(1, 4000, 3000)]);
        s.AddImportedImages([Image(2, 3000, 2000)]);
        s.SetLayerPlacement(1, 0, 0, 400);
        var layout = s.Layout(4000, 8192);
        Check.Equal((4000, 3000), (layout.Output.Wo, layout.Output.Ho), "出力");
        Check.Equal(new RenderShrink(2, 3000, 2000), layout.Layers[1].Shrink, "文書の上での n");
        Check.Equal(0, layout.Layers[0].Shrink.N, "土台は縮めない");
        Check.True(layout.Layers[1].Frame.ClipTo(4000, 3000) == new FrameRect(0, 0, 4000, 3000), "枠が出力の全面を覆う");
    }

    [Test]
    public void 規則1_出力の大きさとプロキシとサムネイルの長辺()
    {
        var c = new SizeD(4000, 3000);
        Check.Equal(new OutputDims(0.512, 2048, 1536), CompositeGeometry.Output(c, CompositeGeometry.ProxyLongSide(c)), "プロキシ");
        Check.Equal(2048, CompositeGeometry.ProxyLongSide(c), "プロキシの長辺");
        Check.Equal(192, CompositeGeometry.ThumbnailLongSideOf(c), "サムネイルの長辺");
        Check.Equal(101, CompositeGeometry.ProxyLongSide(new SizeD(100.2, 50)), "切り上げ");
        Check.Equal(100, CompositeGeometry.ThumbnailLongSideOf(new SizeD(100, 30)), "元より大きくしない");
        var tiny = CompositeGeometry.Output(new SizeD(1000, 1), 100);
        Check.Equal((100, 1), (tiny.Wo, tiny.Ho), "高さは 1 以上");
        Check.Equal(new LayerDims(1, 1), CompositeGeometry.LayerImage(new SizeD(1, 1), 5, 1), "Wl と Hl は 1 以上");
    }

    [Test]
    public void 規則3_回転と切り抜きと余白のネイティブ寸法()
    {
        var src = new SizeD(400, 200);
        var r90 = CompositeGeometry.RotateSize(src, 90, false);
        Check.Near(200, r90.W, 1e-9, "90 度の幅");
        Check.Near(400, r90.H, 1e-9, "90 度の高さ");
        var r45 = CompositeGeometry.RotateSize(src, 45, false);
        Check.Near(600 / Math.Sqrt(2), r45.W, 1e-9, "45 度の expand の幅");
        var in45 = CompositeGeometry.RotateSize(src, 45, true);
        var k = Math.Min(400 / r45.W, 200 / r45.H);
        Check.Near(400 * k, in45.W, 1e-9, "inside の幅");
        Check.Near(200 * k, in45.H, 1e-9, "inside の高さ");
        Check.Near(2.0, in45.W / in45.H, 1e-12, "inside は元と同じ縦横比");
        var pad = CompositeGeometry.PadSize(new SizeD(200, 100), new RectValue(100, 0, 50, 1000));
        Check.Equal(new SizeD(215, 200), pad, "余白(短辺 100 の‰)");

        // 回転 → 切り抜き → 余白の順。目がオフのものは使わない。
        var stack = new List<StackEntry>
        {
            Entry("rotate", ("angle", new NumberValue(90)), ("fit", new StringValue("expand"))),
            Entry("crop", ("rect", new RectValue(100, 0, 0, 500))),
            Entry("pad", ("rect", new RectValue(0, 0, 0, 100))) with { Enabled = false },
        };
        var n = CompositeGeometry.NativeSize(src, stack);
        Check.Near(180, n.W, 1e-9, "回転後の幅 200 の 1−0.1");
        Check.Near(200, n.H, 1e-9, "回転後の高さ 400 の 1−0.5");
    }

    private static StackEntry Entry(string id, params (string Key, ParamValue Value)[] ps) =>
        new(id, true, EffectOrigin.User, 1, 100, ps.Select(p => new KeyValuePair<string, ParamValue>(p.Key, p.Value)).ToArray());
}
