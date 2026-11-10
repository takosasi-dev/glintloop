// PNG と APNG の共通の部品(FR-68・FR-72・§9.9 の 6): チャンクの書き出し(長さ・型・中身・CRC-32)、行のフィルタの選択、zlib の圧縮。
// フィルタは各行で 5 種(None・Sub・Up・Average・Paeth)を試し、出力のバイトを符号付き(−128〜127)とみた絶対値の和が最小のもの(同点は番号の小さいもの)。
// 圧縮は ZLibStream の CompressionLevel.Optimal。補助のチャンク(eXIf・iCCP・tEXt・iTXt・zTXt・tIME)は書かない(INV-12)。
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GlintLoop.Core.Export;

internal static class PngWriter
{
    /// <summary>PNG のシグネチャ(8 バイト)。</summary>
    internal static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>色の種類(IHDR)。</summary>
    internal const byte ColorRgba = 6;
    internal const byte ColorPalette = 3;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>CRC-32(PNG の付録の式。多項式 0xEDB88320)を、前の値 crc に続けて計算する(最初は 0)。</summary>
    internal static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    /// <summary>チャンク 1 つ(長さ・型・中身・型と中身の CRC)を書く。</summary>
    internal static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, head[4..]);
        var crc = Crc32(Crc32(0, head[4..]), data);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        // 1 回の書き込みにまとめる(上限の打ち切りがチャンクの途中で起きても、それより前のチャンクは書けている)
        var all = new byte[12 + data.Length];
        head.CopyTo(all);
        data.CopyTo(all.AsSpan(8));
        tail.CopyTo(all.AsSpan(8 + data.Length));
        output.Write(all);
    }

    /// <summary>IHDR の中身(ビット深度 8、圧縮 0、フィルタ 0、インターレースなし)。</summary>
    internal static byte[] Ihdr(int width, int height, byte colorType)
    {
        var d = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), (uint)height);
        d[8] = 8;
        d[9] = colorType;
        d[10] = 0;
        d[11] = 0;
        d[12] = 0;
        return d;
    }

    /// <summary>画像データ(行ごとにフィルタを選び、zlib で圧縮したもの)。bpp は 1 画素のバイト数(RGBA は 4、パレットは 1)。</summary>
    internal static byte[] ImageData(ReadOnlySpan<byte> pixels, int width, int height, int bpp)
    {
        var stride = width * bpp;
        if (pixels.Length != stride * height) throw new ArgumentException("画素の数が幅×高さと合わない", nameof(pixels));
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var best = new byte[stride + 1];
            var trial = new byte[stride + 1];
            for (var y = 0; y < height; y++)
            {
                var cur = pixels.Slice(y * stride, stride);
                var prev = y > 0 ? pixels.Slice((y - 1) * stride, stride) : ReadOnlySpan<byte>.Empty;
                var bestScore = long.MaxValue;
                for (byte f = 0; f <= 4; f++)
                {
                    var score = Filter(f, cur, prev, bpp, trial);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        (best, trial) = (trial, best);
                    }
                }
                z.Write(best);
            }
        }
        return ms.ToArray();
    }

    // フィルタ f で 1 行を out(先頭はフィルタの番号)に書き、符号付きの絶対値の和を返す。prev が空なら上の行は 0。
    private static long Filter(byte f, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, int bpp, byte[] output)
    {
        output[0] = f;
        long sum = 0;
        var hasPrev = !prev.IsEmpty;
        for (var i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0;
            int b = hasPrev ? prev[i] : 0;
            int c = hasPrev && i >= bpp ? prev[i - bpp] : 0;
            var predictor = f switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };
            var v = (byte)(cur[i] - predictor);
            output[i + 1] = v;
            sum += Math.Abs((int)(sbyte)v);
        }
        return sum;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}
