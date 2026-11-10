// EXIF の向き 1〜8 を RGBA の画素に適用して、正しく表示したときの姿勢にする(仕様書 FR-11)。
// SKCodec.GetPixels は向きを適用しない(§8.4 の測定 2 の 4)ので、SKCodec.EncodedOrigin の値を見てここで並べ替える。
// 5〜8 は幅と高さが入れ替わる。
namespace GlintLoop.Codec;

public static class Orientation
{
    /// <summary>
    /// 向き <paramref name="exif"/>(EXIF の 1〜8。範囲外は 1 とみなす)を適用した画素を返す。1 のときは同じ配列を返す。
    /// 出力の (x, y) が読む元の位置: 2 は (W−1−x, y)、3 は (W−1−x, H−1−y)、4 は (x, H−1−y)、5 は (y, x)、
    /// 6 は (y, H−1−x)、7 は (W−1−y, H−1−x)、8 は (W−1−y, x)(W・H は元の幅と高さ)。
    /// </summary>
    public static byte[] Apply(byte[] rgba, int width, int height, int exif, out int outWidth, out int outHeight)
    {
        if (exif is < 2 or > 8)
        {
            outWidth = width;
            outHeight = height;
            return rgba;
        }
        var swap = exif >= 5;
        int ow = swap ? height : width, oh = swap ? width : height;
        var dst = new byte[rgba.LongLength];
        ReadOnlySpan<uint> src32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
        Span<uint> dst32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
        for (var y = 0; y < oh; y++)
        {
            for (var x = 0; x < ow; x++)
            {
                int sx, sy;
                switch (exif)
                {
                    case 2: sx = width - 1 - x; sy = y; break;
                    case 3: sx = width - 1 - x; sy = height - 1 - y; break;
                    case 4: sx = x; sy = height - 1 - y; break;
                    case 5: sx = y; sy = x; break;
                    case 6: sx = y; sy = height - 1 - x; break;
                    case 7: sx = width - 1 - y; sy = height - 1 - x; break;
                    default: sx = width - 1 - y; sy = x; break;
                }
                dst32[y * ow + x] = src32[sy * width + sx];
            }
        }
        outWidth = ow;
        outHeight = oh;
        return dst;
    }
}
