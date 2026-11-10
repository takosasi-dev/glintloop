// 形ステージの 4 つのエフェクト(flip・mask・chroma・edge)を、§9.4 の末尾の「catalog_points の性質の表」の行のとおりに確かめる(AC-14 の (2))。
// 画像はすべてコードで作る。描画は WARP。α と「完全に一致」は RGBA16F の読み戻しで、色の「1 以内」は最後の変換(straight の 8 ビット)で比べる。
// 位置 (x, y) は 0 から数えた列と行(左上が (0, 0))。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Effects;

public sealed class ShapeEffectsTests
{
    // ------------------------------------------------------------ 道具

    private static RenderService Warp() => RenderService.Create(GpuAdapterRequest.Warp);

    private static EffectInvocation Inv(RenderService rs, string id, params (string Key, ParamValue Value)[] values)
    {
        var def = rs.Effects.Find(id);
        Check.True(def is not null, $"{id} がカタログに無い");
        return new EffectInvocation(def!) { Values = values.ToDictionary(v => v.Key, v => v.Value), Amount = 1, Salt = 1 };
    }

    private static float[] F16(RenderService rs, byte[] img, int w, int h, params EffectInvocation[] stack) =>
        rs.RenderRgba16FAsync(img, w, h, stack, FrameContext.Still).GetAwaiter().GetResult();

    private static byte[] Rgba8(RenderService rs, byte[] img, int w, int h, params EffectInvocation[] stack) =>
        rs.RenderStraightRgba8Async(img, w, h, stack, FrameContext.Still).GetAwaiter().GetResult();

    private static float Alpha(float[] f, int w, int x, int y) => f[(y * w + x) * 4 + 3];

    /// <summary>w×h の 1 色の画像(sRGB の straight)。</summary>
    private static byte[] Solid(int w, int h, uint rgba)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) Put(px, i, rgba);
        return px;
    }

    private static void Put(byte[] px, int i, uint rgba)
    {
        px[i * 4] = (byte)(rgba >> 24);
        px[i * 4 + 1] = (byte)(rgba >> 16);
        px[i * 4 + 2] = (byte)(rgba >> 8);
        px[i * 4 + 3] = (byte)rgba;
    }

    private const uint Uniform = 0x6496C8FF; // 表の「一様な画像」の色

    private static void Corners(float[] f, int w, int h, float expected, string what)
    {
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
            Check.Equal(expected, Alpha(f, w, x, y), $"{what}: 隅 ({x}, {y}) の α");
    }

    private static int CountAlphaAtLeastHalf(float[] f) => Enumerable.Range(0, f.Length / 4).Count(i => f[i * 4 + 3] >= 0.5f);

    private static void Within1(byte[] expected, byte[] actual, int index, string what)
    {
        for (var c = 0; c < 4; c++)
        {
            var e = expected[index * 4 + c];
            var a = actual[index * 4 + c];
            if (Math.Abs(e - a) > 1) throw new CheckFailedException($"{what}: 画素 {index} のチャンネル {c} 期待 {e}±1、実際 {a}");
        }
    }

    // ------------------------------------------------------------ flip

    [Test]
    public void AC14_flipはhで左右vで上下両方で180度の回転に完全に一致する()
    {
        // 正方形でない大きさと、α を含む画素ごとに違う値で、軸の取り違えと 1 画素のずれを見分ける
        const int w = 37, h = 23;
        var img = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) Put(img, i, Hash.Hash32((uint)i) | 0x01u);
        using var rs = Warp();
        var src = F16(rs, img, w, h);
        foreach (var (fh, fv, name) in new[] { (true, false, "h"), (false, true, "v"), (true, true, "h+v") })
        {
            var got = F16(rs, img, w, h, Inv(rs, "flip", ("h", new BoolValue(fh)), ("v", new BoolValue(fv))));
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = fh ? w - 1 - x : x;
                var sy = fv ? h - 1 - y : y;
                for (var c = 0; c < 4; c++)
                {
                    var e = src[(sy * w + sx) * 4 + c];
                    var a = got[(y * w + x) * 4 + c];
                    if (BitConverter.SingleToInt32Bits(e) != BitConverter.SingleToInt32Bits(a))
                        throw new CheckFailedException($"flip {name}: ({x}, {y}) のチャンネル {c} 期待 {e}、実際 {a}");
                }
            }
        }
        // 既定(どちらも偽)は何も変えない
        var none = F16(rs, img, w, h, Inv(rs, "flip"));
        Check.True(src.AsSpan().SequenceEqual(none), "flip の既定で画素が変わった");
    }

    // ------------------------------------------------------------ mask

    [Test]
    public void AC14_maskの円は面積が31416の1パーセント以内で隅が0中心が1()
    {
        using var rs = Warp();
        var img = Solid(200, 200, Uniform);
        var f = F16(rs, img, 200, 200, Inv(rs, "mask", ("kind", new StringValue("circle")), ("size", new NumberValue(100))));
        var area = CountAlphaAtLeastHalf(f);
        Check.InRange(31416 * 0.99, 31416 * 1.01, area, "円の α≥0.5 の画素数");
        Corners(f, 200, 200, 0f, "円");
        Check.Equal(1f, Alpha(f, 200, 100, 100), "円の中心の α");
    }

    [Test]
    public void AC14_maskのinvertはαを1からαに255分の1以内で一致させる()
    {
        using var rs = Warp();
        var img = Solid(200, 200, Uniform);
        // 境界の半端な α を含むよう、ぼかしを付けた円で比べる
        (string, ParamValue)[] common = [("kind", new StringValue("circle")), ("size", new NumberValue(80)), ("feather", new NumberValue(30))];
        var plain = F16(rs, img, 200, 200, Inv(rs, "mask", common));
        var inv = F16(rs, img, 200, 200, Inv(rs, "mask", [.. common, ("invert", new BoolValue(true))]));
        var partial = 0;
        for (var i = 0; i < plain.Length / 4; i++)
        {
            Check.Near(1 - plain[i * 4 + 3], inv[i * 4 + 3], 1.0 / 255, $"画素 {i} の invert の α");
            if (plain[i * 4 + 3] is > 0.05f and < 0.95f) partial++;
        }
        Check.True(partial > 100, $"ぼかしの半端な α の画素が少ない({partial})");
    }

    [Test]
    public void AC14_maskの角丸はradius0とsize100ですべての画素のαが1()
    {
        using var rs = Warp();
        var img = Solid(200, 200, Uniform);
        var f = F16(rs, img, 200, 200,
            Inv(rs, "mask", ("kind", new StringValue("roundrect")), ("radius", new NumberValue(0)), ("size", new NumberValue(100))));
        for (var i = 0; i < f.Length / 4; i++) Check.Equal(1f, f[i * 4 + 3], $"画素 {i} の α");
    }

    [Test]
    public void AC14_maskのほかの形は中心が1隅が0で面積が5パーセント以上100パーセント未満()
    {
        using var rs = Warp();
        var img = Solid(200, 200, Uniform);
        foreach (var kind in new[] { "ellipse", "squircle", "heart", "star", "hexagon" })
        {
            var f = F16(rs, img, 200, 200, Inv(rs, "mask", ("kind", new StringValue(kind))));
            Check.Equal(1f, Alpha(f, 200, 100, 100), $"{kind}: 中心の α");
            Corners(f, 200, 200, 0f, kind);
            var ratio = CountAlphaAtLeastHalf(f) / (200.0 * 200.0);
            Check.True(ratio >= 0.05 && ratio < 1.0, $"{kind}: α≥0.5 の面積の割合 {ratio}");
        }
    }

    // ------------------------------------------------------------ chroma

    [Test]
    public void AC14_chromaは緑の左半分を抜き赤の右半分を変えない()
    {
        const int w = 128, h = 128;
        var img = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++) Put(img, y * w + x, x < w / 2 ? 0x00FF00FFu : 0xFF0000FFu);
        using var rs = Warp();
        var inv = Inv(rs, "chroma", ("key", new StringValue("#00FF00FF")), ("tol", new NumberValue(30)), ("soft", new NumberValue(20)));
        var f = F16(rs, img, w, h, inv);
        var rgba = Rgba8(rs, img, w, h, inv);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (x < w / 2) Check.True(f[i * 4 + 3] <= 0.02f, $"左半分 ({x}, {y}) の α {f[i * 4 + 3]}");
            else Within1(img, rgba, i, $"右半分 ({x}, {y})");
        }
    }

    // ------------------------------------------------------------ edge

    /// <summary>透明な 200×200 の中央(x、y とも 50〜149)に不透明な 100×100 の正方形(一様な画像の色)。</summary>
    private static byte[] CenterSquare()
    {
        var px = new byte[200 * 200 * 4];
        for (var y = 50; y < 150; y++)
        for (var x = 50; x < 150; x++) Put(px, y * 200 + x, Uniform);
        return px;
    }

    [Test]
    public void AC14_edgeはshift0で内側を変えず外側を透明のままにする()
    {
        var img = CenterSquare();
        using var rs = Warp();
        var inv = Inv(rs, "edge", ("shift", new NumberValue(0)), ("feather", new NumberValue(0)));
        var f = F16(rs, img, 200, 200, inv);
        var rgba = Rgba8(rs, img, 200, 200, inv);
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
        {
            var i = y * 200 + x;
            var inside = Math.Min(Math.Min(x - 50, 149 - x), Math.Min(y - 50, 149 - y)); // 辺の画素が 0
            var outside = Math.Max(Math.Max(50 - x, x - 149), Math.Max(50 - y, y - 149)); // 辺のすぐ外の画素が 1
            if (inside >= 2) Within1(img, rgba, i, $"内側 ({x}, {y})");
            if (outside >= 2) Check.Equal(0f, f[i * 4 + 3], $"外側 ({x}, {y}) の α");
        }
    }

    [Test]
    public void AC14_edgeはshift20で不透明な部分を各辺で4画素広げる()
    {
        var img = CenterSquare();
        using var rs = Warp();
        var inv = Inv(rs, "edge", ("shift", new NumberValue(20)), ("feather", new NumberValue(0)));
        var f = F16(rs, img, 200, 200, inv);
        // α≥0.5 の外接矩形の各辺が、元の辺(50 と 149)から 4 画素(±1)外へ動く
        int left = 200, right = -1, top = 200, bottom = -1;
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
        {
            if (Alpha(f, 200, x, y) < 0.5f) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        Check.InRange(3, 5, 50 - left, "左の辺の広がり");
        Check.InRange(3, 5, right - 149, "右の辺の広がり");
        Check.InRange(3, 5, 50 - top, "上の辺の広がり");
        Check.InRange(3, 5, bottom - 149, "下の辺の広がり");
        // 辺の中ほどの行と列でも 4 画素(±1)
        foreach (var (dx, dy, name) in new[] { (-1, 0, "左"), (1, 0, "右"), (0, -1, "上"), (0, 1, "下") })
        {
            var n = 0;
            for (var d = 1; d <= 10; d++)
            {
                var x = dx < 0 ? 50 - d : dx > 0 ? 149 + d : 100;
                var y = dy < 0 ? 50 - d : dy > 0 ? 149 + d : 100;
                if (Alpha(f, 200, x, y) >= 0.5f) n++;
            }
            Check.InRange(3, 5, n, $"{name}の辺の中ほどで広がった画素数");
        }
        // 新たに不透明になった画素の色は、最も近い α≥0.5 の画素(正方形)の色
        var rgba = Rgba8(rs, img, 200, 200, inv);
        var square = Solid(1, 1, Uniform);
        foreach (var (x, y) in new[] { (47, 100), (152, 100), (100, 47), (100, 152) })
        {
            var i = y * 200 + x;
            for (var c = 0; c < 3; c++)
                Check.Near(square[c], rgba[i * 4 + c], 1, $"({x}, {y}) のチャンネル {c}");
        }
    }
}
