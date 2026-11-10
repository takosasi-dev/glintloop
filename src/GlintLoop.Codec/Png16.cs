// 16 ビットの PNG を、各標本を round(v16÷257) にした 8 ビットの PNG に書き直す(仕様書 FR-8・AC-10 の (1))。
// SkiaSharp 4.153.1 は 16 ビットを 8 ビットへ切り捨て(v16>>8)で読み、16 ビットのまま受け取る色の型も変換できないと返す(実物で確認)ので、
// 圧縮を解いてフィルターを戻し、8 ビットにして詰め直したものを SkiaSharp に読ませる。ICC などの付加情報はそのまま残す。
using System.Buffers.Binary;
using System.IO.Compression;

namespace GlintLoop.Codec;

public static class Png16
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>PNG の IHDR のビット深度が 16 か。</summary>
    public static bool IsSixteenBit(ReadOnlySpan<byte> png) =>
        png.Length >= 33 && png[..8].SequenceEqual(Signature) && png.Slice(12, 4).SequenceEqual("IHDR"u8) && png[24] == 16;

    /// <summary>16 ビットの標本を 8 ビットにする: round(v÷257) = floor((2v+257)÷514)。</summary>
    public static byte To8(int v16) => (byte)((2 * v16 + 257) / 514);

    /// <summary>
    /// 16 ビットの PNG を 8 ビットの PNG に書き直す。壊れていれば InvalidDataException。
    /// sBIT・bKGD(16 ビットの値を持つ)と APNG の塊(acTL・fcTL・fdAT。先頭の画像だけを読む)は捨て、tRNS の値は 8 ビットに直す。
    /// </summary>
    public static byte[] ToEightBit(byte[] png)
    {
        if (!IsSixteenBit(png)) throw new InvalidDataException("16 ビットの PNG でない");
        var chunks = ReadChunks(png);
        var ihdr = chunks[0];
        if (ihdr.Type != "IHDR" || ihdr.Length != 13) throw new InvalidDataException("IHDR が無い");
        var ih = png.AsSpan(ihdr.Offset, 13);
        var width = BinaryPrimitives.ReadInt32BigEndian(ih);
        var height = BinaryPrimitives.ReadInt32BigEndian(ih[4..]);
        int colorType = ih[9], interlace = ih[12];
        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException("16 ビットに使えない色の型") };
        if (width <= 0 || height <= 0 || interlace > 1) throw new InvalidDataException("IHDR の値が不正");

        using var idat = new MemoryStream();
        foreach (var c in chunks.Where(c => c.Type == "IDAT")) idat.Write(png, c.Offset, c.Length);
        if (idat.Length == 0) throw new InvalidDataException("IDAT が無い");
        idat.Position = 0;

        using var outZ = new MemoryStream();
        using (var inflate = new ZLibStream(idat, CompressionMode.Decompress))
        using (var deflate = new ZLibStream(outZ, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (var (pw, ph) in Passes(width, height, interlace == 1))
                ConvertPass(inflate, deflate, pw, ph, channels);
        }

        using var o = new MemoryStream();
        o.Write(Signature);
        var newIhdr = ih.ToArray();
        newIhdr[8] = 8;
        WriteChunk(o, "IHDR", newIhdr);
        var wroteIdat = false;
        foreach (var c in chunks.Skip(1))
        {
            switch (c.Type)
            {
                case "IDAT":
                    if (!wroteIdat) WriteChunk(o, "IDAT", outZ.ToArray());
                    wroteIdat = true;
                    break;
                case "IEND":
                    break;
                case "sBIT" or "bKGD" or "acTL" or "fcTL" or "fdAT":
                    break;
                case "tRNS":
                    WriteChunk(o, "tRNS", ConvertTrns(png.AsSpan(c.Offset, c.Length)));
                    break;
                default:
                    WriteChunk(o, c.Type, png.AsSpan(c.Offset, c.Length).ToArray());
                    break;
            }
        }
        WriteChunk(o, "IEND", []);
        return o.ToArray();
    }

    private readonly record struct Chunk(string Type, int Offset, int Length);

    private static List<Chunk> ReadChunks(byte[] png)
    {
        var list = new List<Chunk>();
        var p = 8;
        while (p + 8 <= png.Length)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            var type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            if (len > int.MaxValue || p + 12L + len > png.Length) throw new InvalidDataException("塊が途中で切れている");
            list.Add(new Chunk(type, p + 8, (int)len));
            p += 12 + (int)len;
            if (type == "IEND") break;
        }
        if (list.Count == 0) throw new InvalidDataException("塊が無い");
        return list;
    }

    // Adam7 の 7 つの部分画像の大きさ(インターレースでなければ全体 1 つ)。幅か高さが 0 の部分画像は行を持たない。
    private static IEnumerable<(int W, int H)> Passes(int width, int height, bool adam7)
    {
        if (!adam7)
        {
            yield return (width, height);
            yield break;
        }
        int[] xs = [0, 4, 0, 2, 0, 1, 0], ys = [0, 0, 4, 0, 2, 0, 1], dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
        for (var i = 0; i < 7; i++)
        {
            var pw = width > xs[i] ? (width - xs[i] + dx[i] - 1) / dx[i] : 0;
            var ph = height > ys[i] ? (height - ys[i] + dy[i] - 1) / dy[i] : 0;
            if (pw > 0 && ph > 0) yield return (pw, ph);
        }
    }

    // 1 つの部分画像の行を読み、フィルターを戻して 8 ビットにし、フィルター 0 の行として書く。
    private static void ConvertPass(Stream inflate, Stream deflate, int width, int height, int channels)
    {
        var bpp = channels * 2;
        var rowBytes = checked(width * bpp);
        var prev = new byte[rowBytes];
        var cur = new byte[rowBytes];
        var outRow = new byte[1 + width * channels];
        for (var y = 0; y < height; y++)
        {
            var filter = inflate.ReadByte();
            if (filter < 0) throw new InvalidDataException("画素のデータが途中で切れている");
            inflate.ReadExactly(cur);
            Unfilter(filter, cur, prev, bpp);
            for (int i = 0, j = 1; i < rowBytes; i += 2, j++) outRow[j] = To8((cur[i] << 8) | cur[i + 1]);
            deflate.Write(outRow);
            (prev, cur) = (cur, prev);
        }
    }

    private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = bpp; i < cur.Length; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                break;
            case 2:
                for (var i = 0; i < cur.Length; i++) cur[i] = (byte)(cur[i] + prev[i]);
                break;
            case 3:
                for (var i = 0; i < cur.Length; i++) cur[i] = (byte)(cur[i] + (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1));
                break;
            case 4:
                for (var i = 0; i < cur.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    cur[i] = (byte)(cur[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                }
                break;
            default:
                throw new InvalidDataException("未知のフィルター");
        }
    }

    // 16 ビットの tRNS(灰は 2 バイト、RGB は 6 バイト)の各値を 8 ビットの範囲の 2 バイトに直す。
    private static byte[] ConvertTrns(ReadOnlySpan<byte> trns)
    {
        var o = new byte[trns.Length];
        for (var i = 0; i + 1 < trns.Length; i += 2) o[i + 1] = To8((trns[i] << 8) | trns[i + 1]);
        return o;
    }

    private static void WriteChunk(Stream o, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)body.Length);
        o.Write(u32);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        o.Write(t);
        o.Write(body);
        var crc = ~Crc(Crc(0xFFFFFFFFu, t), body);
        BinaryPrimitives.WriteUInt32BigEndian(u32, crc);
        o.Write(u32);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    // PNG の CRC-32(多項式 0xEDB88320)。初期値と最後の反転は呼ぶ側で行う。
    private static uint Crc(uint c, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}
