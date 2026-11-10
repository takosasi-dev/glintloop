// 比較モードの CPU の画像処理(測定用。製品の GPU の描画の代わり)。線形光・プリマルチプライの double で計算する。
// 縮小は §9.7 の三角窓(重み 1−|d|÷ds、端は延ばす)、拡大と回転はキャッチマル・ロム双三次。回転はキャンバスの外を透明にする(§9.5 の spin)。
// 8 ビットへの量子化は §9.7 の書き出し形式(青色雑音で ±0.45 の揺らぎ、α はディザなし)。ds>16 のときの 1/2 箱形縮小は省いた。
using GlintLoop.Core.Dither;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Probes.Gif;

internal static class Resample
{
    /// <summary>straight の 8 ビット RGBA を、線形光・プリマルチプライの RGBA(double)にする。</summary>
    public static double[] ToLinearPremultiplied(int w, int h, byte[] rgba)
    {
        var d = new double[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            var a = rgba[p * 4 + 3] / 255.0;
            d[p * 4] = Srgb.Lin8(rgba[p * 4]) * a;
            d[p * 4 + 1] = Srgb.Lin8(rgba[p * 4 + 1]) * a;
            d[p * 4 + 2] = Srgb.Lin8(rgba[p * 4 + 2]) * a;
            d[p * 4 + 3] = a;
        }
        return d;
    }

    /// <summary>1 軸の重み(出力の各画素について、ソースの番号と重み。正規化済み)。</summary>
    private static (int[] Index, double[] Weight, int Taps) AxisWeights(int src, int dst)
    {
        var ds = (double)src / dst;
        if (ds > 1)
        {
            var taps = 2 * (int)Math.Ceiling(ds) + 1;
            var idx = new int[dst * taps];
            var wts = new double[dst * taps];
            for (var o = 0; o < dst; o++)
            {
                var xs = (o + 0.5) * ds;
                var first = (int)Math.Floor(xs - ds);
                var sum = 0.0;
                for (var t = 0; t < taps; t++)
                {
                    var i = first + t;
                    var dist = Math.Abs(i + 0.5 - xs);
                    var wgt = dist < ds ? 1 - dist / ds : 0;
                    idx[o * taps + t] = Math.Clamp(i, 0, src - 1);
                    wts[o * taps + t] = wgt;
                    sum += wgt;
                }
                for (var t = 0; t < taps; t++) wts[o * taps + t] /= sum;
            }
            return (idx, wts, taps);
        }
        else
        {
            const int taps = 4;
            var idx = new int[dst * taps];
            var wts = new double[dst * taps];
            for (var o = 0; o < dst; o++)
            {
                var f = (o + 0.5) * ds - 0.5;
                var x0 = (int)Math.Floor(f);
                var t = f - x0;
                var cw = CatmullRom(t);
                for (var k = 0; k < taps; k++)
                {
                    idx[o * taps + k] = Math.Clamp(x0 - 1 + k, 0, src - 1);
                    wts[o * taps + k] = cw[k];
                }
            }
            return (idx, wts, taps);
        }
    }

    private static double[] CatmullRom(double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return
        [
            (-t3 + 2 * t2 - t) * 0.5,
            (3 * t3 - 5 * t2 + 2) * 0.5,
            (-3 * t3 + 4 * t2 + t) * 0.5,
            (t3 - t2) * 0.5,
        ];
    }

    /// <summary>線形・プリマルチプライの画像を dw×dh にする(横、縦の順に 1 軸ずつ)。</summary>
    public static double[] Resize(double[] s, int sw, int sh, int dw, int dh)
    {
        var (hx, wx, tx) = AxisWeights(sw, dw);
        var mid = new double[dw * sh * 4];
        for (var y = 0; y < sh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var t = 0; t < tx; t++)
                {
                    var o = (y * sw + hx[x * tx + t]) * 4;
                    var k = wx[x * tx + t];
                    r += s[o] * k;
                    g += s[o + 1] * k;
                    b += s[o + 2] * k;
                    a += s[o + 3] * k;
                }
                var d = (y * dw + x) * 4;
                mid[d] = r;
                mid[d + 1] = g;
                mid[d + 2] = b;
                mid[d + 3] = a;
            }
        }
        var (hy, wy, ty) = AxisWeights(sh, dh);
        var outp = new double[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var t = 0; t < ty; t++)
                {
                    var o = (hy[y * ty + t] * dw + x) * 4;
                    var k = wy[y * ty + t];
                    r += mid[o] * k;
                    g += mid[o + 1] * k;
                    b += mid[o + 2] * k;
                    a += mid[o + 3] * k;
                }
                var d = (y * dw + x) * 4;
                outp[d] = r;
                outp[d + 1] = g;
                outp[d + 2] = b;
                outp[d + 3] = a;
            }
        }
        return outp;
    }

    /// <summary>
    /// 中心のまわりに θ(時計回り)回した画像を、§9.7 の書き出し形式の 8 ビット straight RGBA にする。
    /// 読み出しはキャッチマル・ロム双三次で、キャンバスの外のタップは透明。background があれば、その色の上に重ねて不透明にする。
    /// </summary>
    public static byte[] RotateToRgba8(double[] s, int w, int h, double theta, GifColor? background)
    {
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        var cx = w / 2.0;
        var cy = h / 2.0;
        double bgR = 0, bgG = 0, bgB = 0;
        if (background is { } bg)
        {
            bgR = Srgb.Lin8(bg.R);
            bgG = Srgb.Lin8(bg.G);
            bgB = Srgb.Lin8(bg.B);
        }
        var outp = new byte[w * h * 4];
        Span<double> wx = stackalloc double[4];
        Span<double> wy = stackalloc double[4];
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                // 出力の画素の中心から、逆に回した位置を読む(turn(−θ, v))
                var vx = i + 0.5 - cx;
                var vy = j + 0.5 - cy;
                var xs = cx + vx * cos + vy * sin;
                var ys = cy - vx * sin + vy * cos;
                var fx = xs - 0.5;
                var fy = ys - 0.5;
                var x0 = (int)Math.Floor(fx);
                var y0 = (int)Math.Floor(fy);
                Fill(wx, fx - x0);
                Fill(wy, fy - y0);
                double r = 0, g = 0, b = 0, a = 0;
                for (var ky = 0; ky < 4; ky++)
                {
                    var yy = y0 - 1 + ky;
                    if (yy < 0 || yy >= h) continue;
                    for (var kx = 0; kx < 4; kx++)
                    {
                        var xx = x0 - 1 + kx;
                        if (xx < 0 || xx >= w) continue;
                        var k = wx[kx] * wy[ky];
                        var o = (yy * w + xx) * 4;
                        r += s[o] * k;
                        g += s[o + 1] * k;
                        b += s[o + 2] * k;
                        a += s[o + 3] * k;
                    }
                }
                a = MathX.Sat(a);
                if (background is not null)
                {
                    r += bgR * (1 - a);
                    g += bgG * (1 - a);
                    b += bgB * (1 - a);
                    a = 1;
                }
                Quantize(outp, (j * w + i) * 4, i, j, r, g, b, a);
            }
        }
        return outp;
    }

    private static void Fill(Span<double> w, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        w[0] = (-t3 + 2 * t2 - t) * 0.5;
        w[1] = (3 * t3 - 5 * t2 + 2) * 0.5;
        w[2] = (-3 * t3 + 4 * t2 + t) * 0.5;
        w[3] = (t3 - t2) * 0.5;
    }

    /// <summary>§9.7 の量子化: q=floor(255×enc(sat(c))+0.5+n)、n=((bn+0.5)÷4096−0.5)×0.9。α は floor(255×α+0.5)。</summary>
    private static void Quantize(byte[] d, int o, int i, int j, double pr, double pg, double pb, double a)
    {
        var n = ((BlueNoise.At(i, j) + 0.5) / 4096.0 - 0.5) * 0.9;
        double r = 0, g = 0, b = 0;
        if (a > 0.0001)
        {
            r = pr / a;
            g = pg / a;
            b = pb / a;
        }
        d[o] = Q(r, n);
        d[o + 1] = Q(g, n);
        d[o + 2] = Q(b, n);
        d[o + 3] = (byte)Math.Clamp((int)Math.Floor(255 * a + 0.5), 0, 255);
    }

    private static byte Q(double c, double n) => (byte)Math.Clamp((int)Math.Floor(255 * Srgb.Enc(MathX.Sat(c)) + 0.5 + n), 0, 255);
}
