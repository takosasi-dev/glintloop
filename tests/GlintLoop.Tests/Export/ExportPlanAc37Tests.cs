// 書き出しの出力サイズ(AC-37 の (1)、FR-73・§9.9 の 1)と、計画の解決(形式ごとのフレーム数・静止形式のフレームの位置)の検査。GPU を使わない。
// 文書は SessionTestKit のカタログで組み立て、ソースの寸法は表で引く(画素は持たない)。出力の幅と高さは OutputPlan.Resolve の User から読む。
// 名前空間は GlintLoop.Tests.Exports(Core の名前空間 GlintLoop.Core.Export と紛れないよう複数形)。
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Exports;

public sealed class ExportPlanAc37Tests
{
    /// <summary>土台(と足したレイヤー)の寸法から作った文書と、ソースの寸法の表。</summary>
    internal static (SessionDocument Doc, Func<string, SizeD> Size) Doc(params (int W, int H)[] layers)
    {
        var s = SessionTestKit.NewSession();
        var sizes = new Dictionary<string, SizeD>(StringComparer.Ordinal);
        var imgs = layers.Select((l, i) =>
        {
            var im = SessionTestKit.Image(i + 1, l.W, l.H);
            sizes[im.Sha256] = new SizeD(l.W, l.H);
            return im;
        }).ToArray();
        s.AddImportedImages(imgs);
        for (var i = 1; i < s.Document!.Layers.Count; i++) s.SetLayerPlacement(i, 0, 0, 100);
        return (s.Document!, sha => sizes[sha]);
    }

    private static (int W, int H, int L) Out(SessionDocument doc, Func<string, SizeD> size, string format, string mode, double? value)
    {
        var d = doc with { Output = doc.Output with { Format = format, Size = new OutputSize(mode, value) } };
        var u = OutputPlan.Resolve(d, SessionTestKit.Catalog, size, 0).User;
        return (u.Width, u.Height, u.LongSide);
    }

    private static void Dims((int W, int H, int L) actual, int w, int h, string what)
    {
        Check.Equal(w, actual.W, what + " の幅");
        Check.Equal(h, actual.H, what + " の高さ");
    }

    [Test]
    public void AC37_1_出力サイズの表()
    {
        var (a, sa) = Doc((4000, 3000));
        Dims(Out(a, sa, "png", "long", 480), 480, 360, "4000×3000 の長辺 480");
        Dims(Out(a, sa, "png", "width", 480), 480, 360, "4000×3000 の幅 480");
        Dims(Out(a, sa, "png", "height", 480), 640, 480, "4000×3000 の高さ 480");
        Dims(Out(a, sa, "png", "scale", 50), 2000, 1500, "4000×3000 の倍率 50%");
        Dims(Out(a, sa, "png", "scale", 10), 400, 300, "4000×3000 の倍率 10%");
        Dims(Out(a, sa, "png", "scale", 400), 4096, 3072, "4000×3000 の倍率 400%(上限で止まる)");

        var (b, sb) = Doc((3000, 4000));
        Dims(Out(b, sb, "png", "height", 480), 360, 480, "3000×4000 の高さ 480");
        Dims(Out(b, sb, "png", "width", 480), 480, 640, "3000×4000 の幅 480");

        var (c, sc) = Doc((1000, 333));
        Dims(Out(c, sc, "png", "long", 480), 480, 160, "1000×333 の長辺 480");

        var (d, sd) = Doc((8, 8));
        Dims(Out(d, sd, "png", "scale", 100), 16, 16, "8×8 の倍率 100%(E-15)");

        var (e, se) = Doc((1, 1));
        Dims(Out(e, se, "png", "long", 480), 480, 480, "1×1 の長辺 480(拡大を許す)");
    }

    [Test]
    public void AC37_1_既定の長辺と土台より大きいレイヤー()
    {
        var (g, sg) = Doc((300, 200));
        Check.Equal(300, Out(g, sg, "gif", "auto", null).L, "土台の長辺 300 の GIF の既定");
        var (p, sp) = Doc((5000, 2500));
        Check.Equal(4096, Out(p, sp, "png", "auto", null).L, "土台の長辺 5000 の PNG の既定");
        var (q, sq) = Doc((5000, 2500));
        Check.Equal(480, Out(q, sq, "gif", "auto", null).L, "土台の長辺 5000 の GIF の既定");

        var (one, s1) = Doc((4000, 3000));
        var (two, s2) = Doc((4000, 3000), (6000, 6000));
        Check.Equal(2, two.Layers.Count, "レイヤーの数");
        Check.Equal(100.0, two.Layers[1].Place.Scale, "足したレイヤーの scale");
        foreach (var (mode, v) in new (string, double?)[] { ("auto", null), ("long", 480), ("scale", 50), ("height", 480) })
        {
            Check.Equal(Out(one, s1, "png", mode, v), Out(two, s2, "png", mode, v), $"6000×6000 のレイヤーを足した {mode} の大きさ");
        }
    }

    [Test]
    public void AC37_計画の形式ごとのフレーム数とフレームの位置()
    {
        var (doc, size) = Doc((200, 100));
        var time = new TimeSettings(20, 20, LoopMode.Infinite, 300);

        // モーションが無い: アニメの形式も 1 フレーム(FR-67・FR-70)、静止形式は k=0
        var still = doc with { Time = time, Output = doc.Output with { Format = "gif" } };
        var r = OutputPlan.Resolve(still, SessionTestKit.Catalog, size, 7).R0;
        Check.Equal(1, r.FrameCount, "モーションなしの GIF のフレーム数");
        Check.Equal(1, r.TimelineFrames, "モーションなしの GIF の N");
        var png = OutputPlan.Resolve(still with { Output = still.Output with { Format = "png" } }, SessionTestKit.Catalog, size, 7).R0;
        Check.Equal(0, png.FrameK, "モーションなしの PNG の k");

        // モーションがある: GIF・APNG は N=40、静止形式はスクラブバーの位置(0〜N−1 に切る)
        var s = SessionTestKit.NewSession();
        s.AddImportedImages([SessionTestKit.Image(1, 200, 100)]);
        Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足す");
        var moving = s.Document! with { Time = time };
        Func<string, SizeD> sz = _ => new SizeD(200, 100);
        foreach (var f in new[] { "gif", "apng" })
        {
            var a = OutputPlan.Resolve(moving with { Output = moving.Output with { Format = f } }, SessionTestKit.Catalog, sz, 7).R0;
            Check.Equal(40, a.FrameCount, f + " のフレーム数");
            Check.Equal(40, a.TimelineFrames, f + " の N");
            Check.Equal(300L, a.HoldMs, f + " の停止時間");
        }
        foreach (var f in new[] { "png", "jpeg", "webp" })
        {
            var a = OutputPlan.Resolve(moving with { Output = moving.Output with { Format = f } }, SessionTestKit.Catalog, sz, 7).R0;
            Check.Equal(1, a.FrameCount, f + " のフレーム数");
            Check.Equal(40, a.TimelineFrames, f + " の N");
            Check.Equal(7, a.FrameK, f + " の k");
            var far = OutputPlan.Resolve(moving with { Output = moving.Output with { Format = f } }, SessionTestKit.Catalog, sz, 99).R0;
            Check.Equal(39, far.FrameK, f + " の k(N−1 に切る)");
        }

        // 形式ごとのオプションの既定(AC-37 の (3)(4)(5))
        var defaults = OutputPlan.Resolve(moving with { Output = OutputSettings.Default with { Format = "jpeg" } }, SessionTestKit.Catalog, sz, 0).R0;
        Check.Equal(90, defaults.Quality, "JPEG の品質の既定");
        var webp = OutputPlan.Resolve(moving with { Output = OutputSettings.Default with { Format = "webp" } }, SessionTestKit.Catalog, sz, 0).R0;
        Check.Equal(85, webp.Quality, "WebP の品質の既定");
        Check.Equal(false, webp.Lossless, "WebP のロスレスの既定");
        var gif = OutputPlan.Resolve(moving with { Output = OutputSettings.Default }, SessionTestKit.Catalog, sz, 0).R0;
        Check.Equal(256, gif.Colors, "GIF の色数の既定");
        Check.Equal(GifDither.BlueNoise, gif.Dither, "GIF のディザの既定");
        Check.Equal(true, gif.DiffOptimization, "GIF の差分の最適化の既定");
        Check.Equal<GifColor?>(null, gif.Matte, "GIF のマット色の既定");
    }
}
