// 取り込みのテスト(AC-10・AC-11)が使う画像のバイト列を、すべてコードで組み立てる道具(基準画像のフォルダを使わない。§8.5)。
// PNG(1・8・16 ビット、フィルター、Adam7)・EXIF 付き JPEG・BMP・ICO・GIF・アニメーション WebP・DIB を作る。
// 偽の文書(IImportTarget)と、一時フォルダに置くファイルの道具もここに置く。
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Gif;
using SkiaSharp;

namespace GlintLoop.Tests.Codec;

/// <summary>一時フォルダの下に作る作業用のフォルダ。Dispose で消す。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Path = Directory.CreateTempSubdirectory("glintloop-ingest-").FullName;

    public string Path { get; }

    public string Write(string name, byte[] bytes)
    {
        var p = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public string PathOf(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>偽の文書。AddImages の呼び出しを記録し、足したぶんだけ自分の状態(枚数・合計)を進める。</summary>
internal sealed class FakeTarget : IImportTarget
{
    private readonly HashSet<string> _shas = new(StringComparer.Ordinal);

    public bool HasDocument { get; set; }

    public int LayerCount { get; set; }

    public long SourceBytesTotal { get; set; }

    public long SourcePixelsTotal { get; set; }

    public List<(IReadOnlyList<ImportedLayer> Layers, ImportVia Via)> Calls { get; } = new();

    /// <summary>文書が持っている画像として SHA-256 を登録する(合計は呼ぶ側が決める)。</summary>
    public void AddKnownSource(string sha) => _shas.Add(sha);

    public bool ContainsSource(string sha256) => _shas.Contains(sha256);

    public int AddImages(IReadOnlyList<ImportedLayer> layers, ImportVia via)
    {
        Calls.Add((layers.ToArray(), via));
        HasDocument = true;
        foreach (var l in layers)
        {
            LayerCount++;
            if (_shas.Add(l.Image.Sha256))
            {
                SourceBytesTotal += l.Image.ByteCount;
                SourcePixelsTotal += l.Image.SourcePixels;
            }
        }
        return LayerCount;
    }

    /// <summary>編集中の文書(1 枚)があるとみなした偽物。</summary>
    public static FakeTarget WithDocument(int layers = 1) => new() { HasDocument = true, LayerCount = layers };
}

internal static class Img
{
    /// <summary>単色の straight の RGBA。</summary>
    public static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }
        return px;
    }

    /// <summary>位置で決まる模様(4 隅と途中で色が違う。α も変える)の straight の RGBA。</summary>
    public static byte[] Pattern(int w, int h, bool alpha)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[i + 2] = (byte)((x + y) * 37 % 256);
                px[i + 3] = alpha ? (byte)(255 - (x * 7 + y * 13) % 200) : (byte)255;
            }
        }
        return px;
    }

    public static (byte R, byte G, byte B, byte A) At(byte[] rgba, int w, int x, int y)
    {
        var i = (y * w + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    public static int MaxDiff(byte[] a, byte[] b)
    {
        var m = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return a.Length == b.Length ? m : int.MaxValue;
    }
}

/// <summary>SkiaSharp のエンコーダーで作る PNG・JPEG・WebP。</summary>
internal static class SkiaBytes
{
    public static SKBitmap ToBitmap(int w, int h, byte[] rgba, SKColorSpace? cs = null)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul, cs);
        var bmp = new SKBitmap(info);
        var p = bmp.GetPixels();
        for (var y = 0; y < h; y++) Marshal.Copy(rgba, y * w * 4, p + y * bmp.RowBytes, w * 4);
        return bmp;
    }

    public static byte[] Png(int w, int h, byte[] rgba, SKColorSpace? cs = null)
    {
        using var bmp = ToBitmap(w, h, rgba, cs);
        using var d = bmp.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG にできない");
        return d.ToArray();
    }

    public static byte[] Jpeg(int w, int h, byte[] rgba, int quality = 100)
    {
        using var bmp = ToBitmap(w, h, rgba);
        using var d = bmp.Encode(SKEncodedImageFormat.Jpeg, quality) ?? throw new InvalidOperationException("JPEG にできない");
        return d.ToArray();
    }

    /// <summary>可逆の WebP(画素が元と一致する)。</summary>
    public static byte[] WebpLossless(int w, int h, byte[] rgba)
    {
        using var bmp = ToBitmap(w, h, rgba);
        using var pm = bmp.PeekPixels();
        using var d = pm.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100)) ??
                      throw new InvalidOperationException("WebP にできない");
        return d.ToArray();
    }
}

/// <summary>PNG を手で組み立てる(SkiaSharp では作れない 16 ビット・1 ビット・Adam7・壊れた iCCP)。</summary>
internal static class PngBytes
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 標本の値 sample(x, y, c)(c は 0〜channels−1。ビット深度の範囲の値)から PNG を作る。ビット深度は 1・8・16。
    /// 行のフィルターは filterOf(行の番号) で選ぶ(0〜4)。adam7 のときは 7 つの部分画像に分けて書く。
    /// </summary>
    public static byte[] Encode(int w, int h, int bitDepth, int colorType, Func<int, int, int, int> sample,
        bool adam7 = false, Func<int, int>? filterOf = null)
    {
        var channels = Channels(colorType);
        filterOf ??= _ => 0;
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            var rowIndex = 0;
            foreach (var (x0, y0, dx, dy) in Passes(adam7))
            {
                var pw = w > x0 ? (w - x0 + dx - 1) / dx : 0;
                var ph = h > y0 ? (h - y0 + dy - 1) / dy : 0;
                if (pw == 0 || ph == 0) continue;
                var rowBytes = (pw * channels * bitDepth + 7) / 8;
                var bpp = Math.Max(1, channels * bitDepth / 8);
                var prev = new byte[rowBytes];
                var cur = new byte[rowBytes];
                for (var py = 0; py < ph; py++)
                {
                    Array.Clear(cur);
                    var y = y0 + py * dy;
                    for (var px = 0; px < pw; px++)
                    {
                        var x = x0 + px * dx;
                        for (var c = 0; c < channels; c++)
                        {
                            var v = sample(x, y, c);
                            var k = px * channels + c;
                            switch (bitDepth)
                            {
                                case 1: if (v != 0) cur[k >> 3] |= (byte)(0x80 >> (k & 7)); break;
                                case 8: cur[k] = (byte)v; break;
                                default: cur[k * 2] = (byte)(v >> 8); cur[k * 2 + 1] = (byte)v; break;
                            }
                        }
                    }
                    var f = filterOf(rowIndex++);
                    zs.WriteByte((byte)f);
                    zs.Write(Filter(f, cur, prev, bpp));
                    (prev, cur) = (cur, prev);
                }
            }
        }
        return Assemble(w, h, bitDepth, colorType, adam7, z.ToArray());
    }

    /// <summary>1 ビットの灰の単色の大きな PNG(行を流して圧縮し、画素の全体を手元に持たない)。</summary>
    public static byte[] OneBitGray(int w, int h, bool white)
    {
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (w + 7) / 8];
            if (white) Array.Fill(row, (byte)0xFF, 1, row.Length - 1);
            for (var y = 0; y < h; y++) zs.Write(row);
        }
        return Assemble(w, h, 1, 0, false, z.ToArray());
    }

    private static byte[] Assemble(int w, int h, int bitDepth, int colorType, bool adam7, byte[] idat)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[12] = (byte)(adam7 ? 1 : 0);
        using var o = new MemoryStream();
        o.Write(Signature);
        WriteChunk(o, "IHDR", ihdr);
        WriteChunk(o, "IDAT", idat);
        WriteChunk(o, "IEND", []);
        return o.ToArray();
    }

    private static int Channels(int colorType) => colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new ArgumentException() };

    private static IEnumerable<(int X0, int Y0, int Dx, int Dy)> Passes(bool adam7)
    {
        if (!adam7)
        {
            yield return (0, 0, 1, 1);
            yield break;
        }
        yield return (0, 0, 8, 8);
        yield return (4, 0, 8, 8);
        yield return (0, 4, 4, 8);
        yield return (2, 0, 4, 4);
        yield return (0, 2, 2, 4);
        yield return (1, 0, 2, 2);
        yield return (0, 1, 1, 2);
    }

    private static byte[] Filter(int f, byte[] cur, byte[] prev, int bpp)
    {
        var o = new byte[cur.Length];
        for (var i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            int pred = f switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };
            o[i] = (byte)(cur[i] - pred);
        }
        return o;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static void WriteChunk(Stream o, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)body.Length);
        o.Write(u32);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        o.Write(t);
        o.Write(body);
        var all = new byte[t.Length + body.Length];
        t.CopyTo(all, 0);
        body.CopyTo(all.AsSpan(t.Length));
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc32(all));
        o.Write(u32);
    }

    /// <summary>
    /// 2 フレームの APNG(8 ビット RGBA)。既定の画像(IDAT)が 1 枚目で、2 枚目は fdAT に入れる。
    /// acTL・fcTL・fdAT の形は APNG の仕様(acTL=フレーム数と繰り返し、fcTL=26 バイト、fdAT=通し番号+圧縮した画素)のとおり。
    /// </summary>
    public static byte[] Apng(int w, int h, byte[] first, byte[] second)
    {
        byte[] Idat(byte[] rgba)
        {
            var png = Encode(w, h, 8, 6, (x, y, c) => rgba[(y * w + x) * 4 + c]);
            var p = 8;
            while (true)
            {
                var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
                if (System.Text.Encoding.ASCII.GetString(png, p + 4, 4) == "IDAT") return png.AsSpan(p + 8, len).ToArray();
                p += 12 + len;
            }
        }
        byte[] Fctl(uint seq)
        {
            var b = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(b, seq);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), w);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), h);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), 10);  // 遅延の分子
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), 100); // 遅延の分母
            return b;
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, 2);
        var second32 = Idat(second);
        var fdat = new byte[4 + second32.Length];
        BinaryPrimitives.WriteUInt32BigEndian(fdat, 2);
        second32.CopyTo(fdat, 4);
        using var o = new MemoryStream();
        o.Write(Signature);
        WriteChunk(o, "IHDR", ihdr);
        WriteChunk(o, "acTL", actl);
        WriteChunk(o, "fcTL", Fctl(0));
        WriteChunk(o, "IDAT", Idat(first));
        WriteChunk(o, "fcTL", Fctl(1));
        WriteChunk(o, "fdAT", fdat);
        WriteChunk(o, "IEND", []);
        return o.ToArray();
    }

    /// <summary>IHDR の直後に塊を 1 つ差し込む。</summary>
    public static byte[] InsertAfterIhdr(byte[] png, string type, byte[] body)
    {
        const int ihdrEnd = 8 + 12 + 13;
        using var o = new MemoryStream();
        o.Write(png, 0, ihdrEnd);
        WriteChunk(o, type, body);
        o.Write(png, ihdrEnd, png.Length - ihdrEnd);
        return o.ToArray();
    }

    /// <summary>iCCP の本体(名前・圧縮方式 0・zlib で圧縮したプロファイル)。</summary>
    public static byte[] IccpBody(byte[] profile, bool validZlib = true)
    {
        using var o = new MemoryStream();
        o.Write("broken\0"u8);
        o.WriteByte(0);
        if (validZlib)
        {
            using var zs = new ZLibStream(o, CompressionLevel.Fastest, leaveOpen: true);
            zs.Write(profile);
        }
        else
        {
            o.Write(profile); // zlib の頭を持たない、ただのバイト列
        }
        return o.ToArray();
    }

    public static bool HasChunk(byte[] png, string type)
    {
        var p = 8;
        while (p + 8 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            if (System.Text.Encoding.ASCII.GetString(png, p + 4, 4) == type) return true;
            p += 12 + len;
        }
        return false;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>JPEG・BMP・ICO・GIF・WebP の手組み(probes/probe-skia の ImageBytes と同じ形をテストの側に写したもの)。</summary>
internal static class OtherBytes
{
    private static void U16(List<byte> o, int v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
    }

    private static void U24(List<byte> o, int v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
        o.Add((byte)(v >> 16));
    }

    private static void U32(List<byte> o, uint v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
        o.Add((byte)(v >> 16));
        o.Add((byte)(v >> 24));
    }

    /// <summary>JPEG の SOI の直後に、向き(タグ 0x0112)だけを持つ EXIF の APP1 を差し込む。</summary>
    public static byte[] JpegWithOrientation(byte[] jpeg, int orientation)
    {
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ArgumentException("JPEG の SOI が無い");
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // ビッグエンディアン、IFD0 は 8 バイト目
            0x00, 0x01,                                               // 項目 1 個
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,           // Orientation、SHORT、1 個
            0x00, (byte)orientation, 0x00, 0x00,                      // 値(左詰め)
            0x00, 0x00, 0x00, 0x00,                                   // 次の IFD なし
        ];
        var o = new List<byte>(jpeg.Length + 64) { 0xFF, 0xD8, 0xFF, 0xE1 };
        var len = 2 + 6 + tiff.Length;
        o.Add((byte)(len >> 8));
        o.Add((byte)len);
        o.AddRange("Exif\0\0"u8.ToArray());
        o.AddRange(tiff);
        o.AddRange(jpeg.AsSpan(2).ToArray());
        return o.ToArray();
    }

    /// <summary>24 ビット BI_RGB の BMP ファイル。</summary>
    public static byte[] Bmp24(int w, int h, byte[] rgba)
    {
        var stride = (w * 3 + 3) / 4 * 4;
        var o = new List<byte> { (byte)'B', (byte)'M' };
        U32(o, (uint)(14 + 40 + stride * h));
        U16(o, 0);
        U16(o, 0);
        U32(o, 14 + 40);
        o.AddRange(Dib.Build(40, w, h, 24, 0, (x, y) => Rgb(rgba, w, x, y)));
        return o.ToArray();
    }

    /// <summary>PNG を 1 個だけ入れた ICO。</summary>
    public static byte[] IcoWithPng(int w, int h, byte[] png)
    {
        var o = new List<byte>();
        U16(o, 0);
        U16(o, 1);
        U16(o, 1);
        o.Add((byte)(w >= 256 ? 0 : w));
        o.Add((byte)(h >= 256 ? 0 : h));
        o.Add(0);
        o.Add(0);
        U16(o, 1);
        U16(o, 32);
        U32(o, (uint)png.Length);
        U32(o, 6 + 16);
        o.AddRange(png);
        return o.ToArray();
    }

    /// <summary>自作の GifEncoder(Core)で作る GIF。frames は straight の RGBA。</summary>
    public static byte[] Gif(int w, int h, IReadOnlyList<byte[]> frames) =>
        GifEncoder.EncodeToArray(new GifFrameList(w, h, frames),
            new GifEncoderOptions { Dither = GifDither.None, DiffOptimization = false, Parallelism = 1 });

    /// <summary>
    /// アニメーション WebP。各フレームを SkiaSharp で可逆の WebP にし、その VP8L の塊を ANMF に入れて
    /// VP8X(アニメーションの印)・ANIM と一緒に RIFF に並べ直す。
    /// </summary>
    public static byte[] AnimatedWebp(int w, int h, IReadOnlyList<byte[]> frames, int durationMs = 100)
    {
        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());
        var vp8x = new List<byte> { 0x02, 0, 0, 0 }; // アニメーションの印だけ
        U24(vp8x, w - 1);
        U24(vp8x, h - 1);
        Chunk(body, "VP8X", vp8x);
        var anim = new List<byte>();
        U32(anim, 0xFFFFFFFF); // 背景色
        U16(anim, 0);          // 無限ループ
        Chunk(body, "ANIM", anim);
        foreach (var f in frames)
        {
            var (type, data) = ImageChunk(SkiaBytes.WebpLossless(w, h, f));
            var anmf = new List<byte>();
            U24(anmf, 0);
            U24(anmf, 0);
            U24(anmf, w - 1);
            U24(anmf, h - 1);
            U24(anmf, durationMs);
            anmf.Add(0x02); // 合成しない(no blend)、破棄しない
            Chunk(anmf, type, data);
            Chunk(body, "ANMF", anmf);
        }
        var o = new List<byte>();
        o.AddRange("RIFF"u8.ToArray());
        U32(o, (uint)body.Count);
        o.AddRange(body);
        return o.ToArray();
    }

    // 単体の WebP から画像の塊(VP8L か VP8)を取り出す。
    private static (string Type, byte[] Data) ImageChunk(byte[] webp)
    {
        var p = 12;
        while (p + 8 <= webp.Length)
        {
            var type = System.Text.Encoding.ASCII.GetString(webp, p, 4);
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(p + 4));
            if (type is "VP8L" or "VP8 ") return (type, webp.AsSpan(p + 8, len).ToArray());
            p += 8 + len + (len & 1);
        }
        throw new InvalidOperationException("WebP に画像の塊が無い");
    }

    private static void Chunk(List<byte> o, string type, IReadOnlyCollection<byte> data)
    {
        o.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        U32(o, (uint)data.Count);
        o.AddRange(data);
        if ((data.Count & 1) == 1) o.Add(0);
    }

    private static uint Rgb(byte[] rgba, int w, int x, int y)
    {
        var i = (y * w + x) * 4;
        return (uint)(rgba[i] << 16 | rgba[i + 1] << 8 | rgba[i + 2]);
    }
}

/// <summary>クリップボードの DIB のバイト列(BITMAPFILEHEADER を持たない)を組み立てる。</summary>
internal static class Dib
{
    /// <summary>
    /// headerSize(40 か 124)・幅・高さ(正なら下の行から、負なら上の行から並べる)・ビット数・圧縮(0 か 3)の DIB。
    /// value(x, y) は上から数えた (x, y) の画素の値(24 ビットは 0xRRGGBB、32 ビットはそのまま 32 ビットの値)。
    /// masks は BI_BITFIELDS の赤・緑・青のマスク(40 バイトのヘッダーではヘッダーの直後に置く)、alphaMask は V5 のアルファのマスク。
    /// 行の詰めの余りのバイトは 0xEE で埋める(読み手が余りを画素として使うと分かるように)。
    /// </summary>
    public static byte[] Build(int headerSize, int width, int height, int bitCount, int compression, Func<int, int, uint> value,
        uint[]? masks = null, uint alphaMask = 0)
    {
        var rows = Math.Abs(height);
        var stride = (width * bitCount + 31) / 32 * 4;
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
        void U32(uint v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24)); }
        U32((uint)headerSize);
        U32((uint)width);
        U32(unchecked((uint)height));
        U16(1);
        U16(bitCount);
        U32((uint)compression);
        U32((uint)(stride * rows));
        U32(0);
        U32(0);
        U32(0);
        U32(0);
        var m = masks ?? [0x00FF0000u, 0x0000FF00u, 0x000000FFu];
        if (headerSize == 124)
        {
            U32(m[0]);
            U32(m[1]);
            U32(m[2]);
            U32(alphaMask);
            U32(0x73524742); // bV5CSType = 'sRGB'
            for (var i = 0; i < 36; i++) o.Add(0);
            U32(0);
            U32(0);
            U32(0);
            U32(4);
            U32(0);
            U32(0);
            U32(0);
        }
        else if (compression == 3)
        {
            U32(m[0]);
            U32(m[1]);
            U32(m[2]);
        }
        for (var r = 0; r < rows; r++)
        {
            var y = height > 0 ? rows - 1 - r : r;
            var start = o.Count;
            for (var x = 0; x < width; x++)
            {
                var v = value(x, y);
                if (bitCount == 24)
                {
                    o.Add((byte)v);
                    o.Add((byte)(v >> 8));
                    o.Add((byte)(v >> 16));
                }
                else
                {
                    U32(v);
                }
            }
            while (o.Count - start < stride) o.Add(0xEE);
        }
        return o.ToArray();
    }
}
