// probe-skia が読ませるテスト用の画像(BMP・ICO・EXIF 付き JPEG・iCCP 付き PNG)のバイト列を、自分のコードで組み立てる。
// どれも SkiaSharp のエンコーダーでは作れない形式か、作れない付加情報なので、形式の資料どおりに手で並べる。
// 基準画像は使わない(仕様書 §8.5)。
using System.IO.Compression;

namespace GlintLoop.Probes.Skia;

static class ImageBytes
{
    static void U16(List<byte> o, int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
    static void U32(List<byte> o, uint v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24)); }
    static void I32(List<byte> o, int v) => U32(o, unchecked((uint)v));

    /// <summary>24 ビット BI_RGB の BMP。rgba は上の行から並べた straight の RGBA。</summary>
    public static byte[] Bmp24(int w, int h, byte[] rgba)
    {
        int stride = (w * 3 + 3) / 4 * 4;
        var o = new List<byte>();
        o.Add((byte)'B'); o.Add((byte)'M');
        U32(o, (uint)(14 + 40 + stride * h)); U16(o, 0); U16(o, 0); U32(o, 14 + 40);
        U32(o, 40); I32(o, w); I32(o, h); U16(o, 1); U16(o, 24); U32(o, 0); U32(o, (uint)(stride * h));
        I32(o, 0); I32(o, 0); U32(o, 0); U32(o, 0);
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                o.Add(rgba[i + 2]); o.Add(rgba[i + 1]); o.Add(rgba[i]);
            }
            for (int p = w * 3; p < stride; p++) o.Add(0);
        }
        return o.ToArray();
    }

    /// <summary>32 ビット BI_BITFIELDS・BITMAPV5HEADER(アルファのマスクあり、sRGB)の BMP。</summary>
    public static byte[] Bmp32V5(int w, int h, byte[] rgba)
    {
        var o = new List<byte>();
        o.Add((byte)'B'); o.Add((byte)'M');
        U32(o, (uint)(14 + 124 + w * 4 * h)); U16(o, 0); U16(o, 0); U32(o, 14 + 124);
        U32(o, 124); I32(o, w); I32(o, h); U16(o, 1); U16(o, 32); U32(o, 3); U32(o, (uint)(w * 4 * h));
        I32(o, 0); I32(o, 0); U32(o, 0); U32(o, 0);
        U32(o, 0x00FF0000); U32(o, 0x0000FF00); U32(o, 0x000000FF); U32(o, 0xFF000000);
        U32(o, 0x73524742);                         // bV5CSType = 'sRGB'
        for (int i = 0; i < 36; i++) o.Add(0);      // bV5Endpoints
        U32(o, 0); U32(o, 0); U32(o, 0);            // ガンマ
        U32(o, 4); U32(o, 0); U32(o, 0); U32(o, 0); // Intent=LCS_GM_IMAGES、プロファイルなし
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                o.Add(rgba[i + 2]); o.Add(rgba[i + 1]); o.Add(rgba[i]); o.Add(rgba[i + 3]);
            }
        return o.ToArray();
    }

    /// <summary>PNG を 1 個だけ入れた ICO(Vista 以降の形式)。</summary>
    public static byte[] IcoWithPng(int w, int h, byte[] png)
    {
        var o = new List<byte>();
        U16(o, 0); U16(o, 1); U16(o, 1);
        o.Add((byte)(w >= 256 ? 0 : w)); o.Add((byte)(h >= 256 ? 0 : h)); o.Add(0); o.Add(0);
        U16(o, 1); U16(o, 32); U32(o, (uint)png.Length); U32(o, 6 + 16);
        o.AddRange(png);
        return o.ToArray();
    }

    /// <summary>32 ビットの DIB(高さは 2 倍で書き、AND マスクを付ける)を 1 個入れた ICO。</summary>
    public static byte[] IcoWithBmp(int w, int h, byte[] rgba)
    {
        var dib = new List<byte>();
        U32(dib, 40); I32(dib, w); I32(dib, h * 2); U16(dib, 1); U16(dib, 32); U32(dib, 0); U32(dib, 0);
        I32(dib, 0); I32(dib, 0); U32(dib, 0); U32(dib, 0);
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                dib.Add(rgba[i + 2]); dib.Add(rgba[i + 1]); dib.Add(rgba[i]); dib.Add(rgba[i + 3]);
            }
        int maskStride = (w + 31) / 32 * 4;
        for (int y = h - 1; y >= 0; y--)
            for (int b = 0; b < maskStride; b++)
            {
                byte m = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int x = b * 8 + bit;
                    if (x < w && rgba[(y * w + x) * 4 + 3] == 0) m |= (byte)(0x80 >> bit);
                }
                dib.Add(m);
            }
        var o = new List<byte>();
        U16(o, 0); U16(o, 1); U16(o, 1);
        o.Add((byte)w); o.Add((byte)h); o.Add(0); o.Add(0);
        U16(o, 1); U16(o, 32); U32(o, (uint)dib.Count); U32(o, 6 + 16);
        o.AddRange(dib);
        return o.ToArray();
    }

    /// <summary>JPEG の SOI の直後に、向き(タグ 0x0112)だけを持つ EXIF の APP1 を差し込む。</summary>
    public static byte[] JpegWithOrientation(byte[] jpeg, int orientation)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ArgumentException("JPEG の SOI が無い");
        byte[] tiff =
        {
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // ビッグエンディアン、IFD0 は 8 バイト目
            0x00, 0x01,                                               // 項目 1 個
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,           // Orientation、SHORT、1 個
            0x00, (byte)orientation, 0x00, 0x00,                      // 値(左詰め)
            0x00, 0x00, 0x00, 0x00,                                   // 次の IFD なし
        };
        var o = new List<byte>(jpeg.Length + 64) { 0xFF, 0xD8, 0xFF, 0xE1 };
        int len = 2 + 6 + tiff.Length;
        o.Add((byte)(len >> 8)); o.Add((byte)len);
        o.AddRange("Exif\0\0"u8.ToArray());
        o.AddRange(tiff);
        o.AddRange(jpeg.AsSpan(2).ToArray());
        return o.ToArray();
    }

    public static bool PngHasChunk(byte[] png, string type)
    {
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            if (System.Text.Encoding.ASCII.GetString(png, p + 4, 4) == type) return true;
            p += 12 + len;
        }
        return false;
    }

    /// <summary>PNG の IHDR の直後に iCCP の塊を差し込む(既に iCCP があれば何もしない)。</summary>
    public static byte[] PngInsertIccp(byte[] png, byte[] icc)
    {
        if (PngHasChunk(png, "iCCP")) return png;
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(icc);
        var body = new List<byte>();
        body.AddRange("iCCP"u8.ToArray());
        body.AddRange("probe-p3\0"u8.ToArray());
        body.Add(0); // 圧縮方式 0
        body.AddRange(ms.ToArray());
        int dlen = body.Count - 4;
        var chunk = new List<byte> { (byte)(dlen >> 24), (byte)(dlen >> 16), (byte)(dlen >> 8), (byte)dlen };
        chunk.AddRange(body);
        uint crc = Crc32(body.ToArray());
        chunk.Add((byte)(crc >> 24)); chunk.Add((byte)(crc >> 16)); chunk.Add((byte)(crc >> 8)); chunk.Add((byte)crc);
        const int ihdrEnd = 8 + 12 + 13;
        var o = new List<byte>();
        o.AddRange(png.AsSpan(0, ihdrEnd).ToArray());
        o.AddRange(chunk);
        o.AddRange(png.AsSpan(ihdrEnd).ToArray());
        return o.ToArray();
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    public static bool Contains(byte[] hay, ReadOnlySpan<byte> needle) => hay.AsSpan().IndexOf(needle) >= 0;
}
