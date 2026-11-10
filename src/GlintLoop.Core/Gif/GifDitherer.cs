// 1 フレームの画素をパレットの番号にする(仕様書 §9.8 の 5、D-23)。透明画素は常に T。
// 青色雑音は座標 (i,j) の bn だけで c1 と c2 を選ぶので、動かない領域の番号はフレーム間で変わらない。
// 誤差拡散はフレームごとに誤差を 0 から始め、上の行から左→右(蛇行しない)。誤差は各成分を ±0.25 に切ってから配る。
using GlintLoop.Core.Dither;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Gif;

internal static class GifDitherer
{
    private const int CacheBits = 16;
    private const int CacheSize = 1 << CacheBits;

    /// <summary>
    /// フレームの画素(straight RGBA、幅×高さ×4)を番号の配列にする。透明(α&lt;128 でマット色なし)の画素は transparentIndex。
    /// 透明画素があるのに transparentIndex が負なら例外。
    /// </summary>
    internal static byte[] MapFrame(ReadOnlySpan<byte> rgba, int width, int height, GifNearest palette,
        GifDither dither, in GifPixel.Matte matte, int transparentIndex)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("画素の数が幅×高さと合わない", nameof(rgba));
        return dither switch
        {
            GifDither.BlueNoise => MapBlueNoise(rgba, width, height, palette, matte, transparentIndex),
            GifDither.ErrorDiffusion => MapErrorDiffusion(rgba, width, height, palette, matte, transparentIndex),
            GifDither.None => MapNearest(rgba, width, height, palette, matte, transparentIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(dither)),
        };
    }

    private static byte TransparentOrThrow(int transparentIndex) =>
        transparentIndex >= 0 ? (byte)transparentIndex : throw new InvalidOperationException("透明画素があるのに透明インデックスが無い");

    // 色の鍵。マット色があると α で結果が変わるので 4 バイトすべてを使う。
    private static uint Key(ReadOnlySpan<byte> rgba, int o, in GifPixel.Matte matte) =>
        (uint)rgba[o] | ((uint)rgba[o + 1] << 8) | ((uint)rgba[o + 2] << 16) | (matte.Has ? (uint)rgba[o + 3] << 24 : 0xFF000000u);

    private static int Slot(uint key) => (int)(Hash.Hash32(key) >> (32 - CacheBits));

    private static Lab LabAt(ReadOnlySpan<byte> rgba, int o, in GifPixel.Matte matte) =>
        GifPixel.ToLab(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3], matte);

    private static byte[] MapNearest(ReadOnlySpan<byte> rgba, int width, int height, GifNearest palette,
        in GifPixel.Matte matte, int transparentIndex)
    {
        var result = new byte[width * height];
        // 色ごとの結果を覚える表(直接写像。結果には影響しない)
        var keys = new uint[CacheSize];
        var vals = new byte[CacheSize];
        var filled = new bool[CacheSize];
        for (int p = 0, o = 0; p < result.Length; p++, o += 4)
        {
            if (GifPixel.IsTransparent(rgba[o + 3], matte))
            {
                result[p] = TransparentOrThrow(transparentIndex);
                continue;
            }
            var key = Key(rgba, o, matte);
            var s = Slot(key);
            if (!filled[s] || keys[s] != key)
            {
                keys[s] = key;
                vals[s] = (byte)palette.Find(LabAt(rgba, o, matte));
                filled[s] = true;
            }
            result[p] = vals[s];
        }
        return result;
    }

    /// <summary>青色雑音の 1 画素分の材料(c1・c2・w)。座標に依らない。</summary>
    internal static (int C1, int C2, double W) BlueNoiseChoice(in Lab p, GifNearest palette)
    {
        var c1 = palette.Find(p);
        var l1 = palette[c1];
        var q = new Lab(2 * p.L - l1.L, 2 * p.A - l1.A, 2 * p.B - l1.B);
        var c2 = palette.Find(q, c1);
        if (c2 < 0) c2 = c1;
        var l2 = palette[c2];
        double dx = l2.L - l1.L, dy = l2.A - l1.A, dz = l2.B - l1.B;
        var num = (p.L - l1.L) * dx + (p.A - l1.A) * dy + (p.B - l1.B) * dz;
        var den = Math.Max(dx * dx + dy * dy + dz * dz, 1e-12);
        return (c1, c2, MathX.Sat(num / den));
    }

    /// <summary>(bn(i mod 64, j mod 64)+0.5)÷4096 &lt; w なら c2。</summary>
    internal static bool BlueNoisePicksSecond(int i, int j, double w) => (BlueNoise.At(i, j) + 0.5) / 4096.0 < w;

    private static byte[] MapBlueNoise(ReadOnlySpan<byte> rgba, int width, int height, GifNearest palette,
        in GifPixel.Matte matte, int transparentIndex)
    {
        var result = new byte[width * height];
        var keys = new uint[CacheSize];
        var c1s = new byte[CacheSize];
        var c2s = new byte[CacheSize];
        var ws = new double[CacheSize];
        var filled = new bool[CacheSize];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var p = j * width + i;
                var o = p * 4;
                if (GifPixel.IsTransparent(rgba[o + 3], matte))
                {
                    result[p] = TransparentOrThrow(transparentIndex);
                    continue;
                }
                var key = Key(rgba, o, matte);
                var s = Slot(key);
                if (!filled[s] || keys[s] != key)
                {
                    var (c1, c2, w) = BlueNoiseChoice(LabAt(rgba, o, matte), palette);
                    keys[s] = key;
                    c1s[s] = (byte)c1;
                    c2s[s] = (byte)c2;
                    ws[s] = w;
                    filled[s] = true;
                }
                result[p] = BlueNoisePicksSecond(i, j, ws[s]) ? c2s[s] : c1s[s];
            }
        }
        return result;
    }

    private static double Clip(double e) => e < -0.25 ? -0.25 : e > 0.25 ? 0.25 : e;

    private static byte[] MapErrorDiffusion(ReadOnlySpan<byte> rgba, int width, int height, GifNearest palette,
        in GifPixel.Matte matte, int transparentIndex)
    {
        var result = new byte[width * height];
        // 今の行と次の行の誤差(L,a,b)。透明画素に配られた誤差は、その画素が使わないので捨てたことになる。
        var cur = new double[width * 3];
        var next = new double[width * 3];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var p = j * width + i;
                var o = p * 4;
                if (GifPixel.IsTransparent(rgba[o + 3], matte))
                {
                    result[p] = TransparentOrThrow(transparentIndex);
                    continue;
                }
                var src = LabAt(rgba, o, matte);
                var pp = new Lab(src.L + cur[i * 3], src.A + cur[i * 3 + 1], src.B + cur[i * 3 + 2]);
                var c = palette.Find(pp);
                result[p] = (byte)c;
                var cl = palette[c];
                var eL = Clip(pp.L - cl.L);
                var eA = Clip(pp.A - cl.A);
                var eB = Clip(pp.B - cl.B);
                if (i + 1 < width) Spread(cur, i + 1, eL, eA, eB, 7.0 / 16.0);
                if (j + 1 < height)
                {
                    if (i > 0) Spread(next, i - 1, eL, eA, eB, 3.0 / 16.0);
                    Spread(next, i, eL, eA, eB, 5.0 / 16.0);
                    if (i + 1 < width) Spread(next, i + 1, eL, eA, eB, 1.0 / 16.0);
                }
            }
            (cur, next) = (next, cur);
            Array.Clear(next);
        }
        return result;
    }

    private static void Spread(double[] row, int i, double eL, double eA, double eB, double f)
    {
        row[i * 3] += eL * f;
        row[i * 3 + 1] += eA * f;
        row[i * 3 + 2] += eB * f;
    }
}
