// GPU のテクスチャの最大寸法を超える画像を、1/2 の箱形縮小の繰り返しで収める純粋な関数(仕様書 §9.7 の規則 2・E-11)。
// 縮小後の辺は ceil(辺÷2)。辺が奇数のときは端の 1 列(行)を複製してから、2×2 の平均を線形光・プリマルチプライで取る(INV-7)。
// 入出力は sRGB 8 ビット・straight の RGBA。丸めは floor(x+0.5)。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Codec;

public static class GpuFit
{
    /// <summary>
    /// 幅か高さが <paramref name="maxSide"/> を超える間、1/2 の箱形縮小を繰り返す。縮めたら <paramref name="shrunk"/> が true。
    /// 収まっていれば同じ配列を返す。
    /// </summary>
    public static byte[] Fit(byte[] rgba, int width, int height, int maxSide, out int outWidth, out int outHeight, out bool shrunk)
    {
        if (maxSide < 1) throw new ArgumentOutOfRangeException(nameof(maxSide));
        shrunk = false;
        while (width > maxSide || height > maxSide)
        {
            rgba = Halve(rgba, width, height, out width, out height);
            shrunk = true;
        }
        outWidth = width;
        outHeight = height;
        return rgba;
    }

    /// <summary>1 回の 1/2 の箱形縮小。</summary>
    public static byte[] Halve(byte[] rgba, int width, int height, out int outWidth, out int outHeight)
    {
        int ow = (width + 1) / 2, oh = (height + 1) / 2;
        var dst = new byte[(long)ow * oh * 4];
        for (var oy = 0; oy < oh; oy++)
        {
            int y0 = oy * 2, y1 = Math.Min(y0 + 1, height - 1);
            for (var ox = 0; ox < ow; ox++)
            {
                int x0 = ox * 2, x1 = Math.Min(x0 + 1, width - 1);
                int i00 = (y0 * width + x0) * 4, i01 = (y0 * width + x1) * 4, i10 = (y1 * width + x0) * 4, i11 = (y1 * width + x1) * 4;
                int a00 = rgba[i00 + 3], a01 = rgba[i01 + 3], a10 = rgba[i10 + 3], a11 = rgba[i11 + 3];
                var sumA = a00 + a01 + a10 + a11;
                var o = (oy * ow + ox) * 4;
                if (sumA == 0) continue; // 4 画素とも透明なら (0,0,0,0)
                for (var c = 0; c < 3; c++)
                {
                    // プリマルチプライの線形の値の和 ÷ α の和 = 平均の色(straight の線形)
                    var premul = Srgb.Lin8(rgba[i00 + c]) * a00 + Srgb.Lin8(rgba[i01 + c]) * a01 +
                                 Srgb.Lin8(rgba[i10 + c]) * a10 + Srgb.Lin8(rgba[i11 + c]) * a11;
                    dst[o + c] = (byte)MathX.RoundHalfUpToInt(255.0 * Srgb.Enc(MathX.Sat(premul / sumA)));
                }
                dst[o + 3] = (byte)((sumA + 2) / 4);
            }
        }
        outWidth = ow;
        outHeight = oh;
        return dst;
    }
}
