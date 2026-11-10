// テストに書く最小の PNG・APNG のパーサー(AC-37 の (6))。製品のエンコーダー(PngWriter など)とコードを共有しない。
// チャンクを読み(CRC-32 もここで計算して確かめる)、IHDR・acTL・fcTL・PLTE・tRNS を取り出し、IDAT・fdAT を zlib で展開してフィルタを戻し、RGBA にする。
// 対応するのはこのソフトが書く形だけ(ビット深度 8、色の種類 6 と 3、インターレースなし)。
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Exports;

internal sealed record PngChunk(string Type, byte[] Data, long Offset);

internal sealed record ApngFrameControl(uint Sequence, int Width, int Height, int X, int Y, int DelayNum, int DelayDen, int Dispose, int Blend);

/// <summary>読んだ PNG・APNG。Frames は各フレームの RGBA(幅×高さ×4。パレットは PLTE と tRNS で色にしたもの)と、パレットの番号(色の種類 3 のとき)。</summary>
internal sealed class MiniPng
{
    public List<PngChunk> Chunks { get; } = [];
    public int Width;
    public int Height;
    public int BitDepth;
    public int ColorType;
    public int Interlace;
    public int? ActlFrames;
    public int? ActlPlays;
    public List<ApngFrameControl> Controls { get; } = [];
    public byte[]? Plte;
    public byte[]? Trns;
    public List<byte[]> Frames { get; } = [];
    public List<byte[]> Indices { get; } = [];

    public IEnumerable<string> Types => Chunks.Select(c => c.Type);
}

internal static class MiniPngParser
{
    private static readonly byte[] Sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static List<PngChunk> Chunks(byte[] png)
    {
        Check.True(png.Length >= 8 && png.AsSpan(0, 8).SequenceEqual(Sig), "PNG のシグネチャ");
        var list = new List<PngChunk>();
        var p = 8;
        while (p < png.Length)
        {
            Check.True(p + 12 <= png.Length, "チャンクの頭が途中で切れている");
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            var type = Encoding.ASCII.GetString(png, p + 4, 4);
            Check.True(p + 12 + len <= png.Length, $"チャンク {type} が途中で切れている");
            var data = png.AsSpan(p + 8, len).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + len));
            Check.Equal(crc, Crc(png.AsSpan(p + 4, 4 + len)), $"チャンク {type} の CRC");
            list.Add(new PngChunk(type, data, p));
            p += 12 + len;
        }
        return list;
    }

    // CRC-32(テストの側の別の実装。表を使わずビットごとに計算する)
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));

    private static int U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));

    /// <summary>全体を読む。APNG なら fcTL ごとのフレーム、PNG なら IDAT の 1 枚を Frames に入れる。</summary>
    public static MiniPng Parse(byte[] png)
    {
        var r = new MiniPng();
        r.Chunks.AddRange(Chunks(png));
        var ihdr = r.Chunks[0];
        Check.Equal("IHDR", ihdr.Type, "最初のチャンク");
        r.Width = (int)U32(ihdr.Data, 0);
        r.Height = (int)U32(ihdr.Data, 4);
        r.BitDepth = ihdr.Data[8];
        r.ColorType = ihdr.Data[9];
        r.Interlace = ihdr.Data[12];

        var parts = new List<List<byte[]>>();
        List<byte[]>? current = null;
        foreach (var c in r.Chunks)
        {
            switch (c.Type)
            {
                case "acTL":
                    r.ActlFrames = (int)U32(c.Data, 0);
                    r.ActlPlays = (int)U32(c.Data, 4);
                    break;
                case "PLTE":
                    r.Plte = c.Data;
                    break;
                case "tRNS":
                    r.Trns = c.Data;
                    break;
                case "fcTL":
                    r.Controls.Add(new ApngFrameControl(U32(c.Data, 0), (int)U32(c.Data, 4), (int)U32(c.Data, 8), (int)U32(c.Data, 12),
                        (int)U32(c.Data, 16), U16(c.Data, 20), U16(c.Data, 22), c.Data[24], c.Data[25]));
                    current = [];
                    parts.Add(current);
                    break;
                case "IDAT":
                    if (current is null)
                    {
                        current = [];
                        parts.Add(current);
                    }
                    current.Add(c.Data);
                    break;
                case "fdAT":
                    Check.True(current is not null, "fdAT の前に fcTL が無い");
                    current!.Add(c.Data.AsSpan(4).ToArray());
                    break;
            }
        }

        foreach (var frame in parts)
        {
            var z = frame.SelectMany(b => b).ToArray();
            var raw = Inflate(z);
            var bpp = r.ColorType == 6 ? 4 : 1;
            var stride = r.Width * bpp;
            Check.Equal((stride + 1) * r.Height, raw.Length, "展開したデータの長さ");
            var px = Unfilter(raw, r.Width, r.Height, bpp);
            if (r.ColorType == 6)
            {
                r.Frames.Add(px);
            }
            else
            {
                Check.Equal(3, r.ColorType, "色の種類");
                r.Indices.Add(px);
                var rgba = new byte[r.Width * r.Height * 4];
                for (var i = 0; i < px.Length; i++)
                {
                    var v = px[i];
                    Check.True(r.Plte is not null && v * 3 + 2 < r.Plte.Length, $"番号 {v} が PLTE の外");
                    rgba[i * 4] = r.Plte![v * 3];
                    rgba[i * 4 + 1] = r.Plte[v * 3 + 1];
                    rgba[i * 4 + 2] = r.Plte[v * 3 + 2];
                    rgba[i * 4 + 3] = r.Trns is not null && v < r.Trns.Length ? r.Trns[v] : (byte)255;
                }
                r.Frames.Add(rgba);
            }
        }
        return r;
    }

    private static byte[] Inflate(byte[] z)
    {
        using var input = new MemoryStream(z);
        using var zs = new ZLibStream(input, CompressionMode.Decompress);
        using var o = new MemoryStream();
        zs.CopyTo(o);
        return o.ToArray();
    }

    // PNG のフィルタを戻す(各行の先頭の 1 バイトが種類)。
    private static byte[] Unfilter(byte[] raw, int w, int h, int bpp)
    {
        var stride = w * bpp;
        var o = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            var f = raw[y * (stride + 1)];
            Check.True(f <= 4, $"行 {y} のフィルタの種類 {f}");
            for (var x = 0; x < stride; x++)
            {
                var v = raw[y * (stride + 1) + 1 + x];
                var a = x >= bpp ? o[y * stride + x - bpp] : 0;
                var b = y > 0 ? o[(y - 1) * stride + x] : 0;
                var c = x >= bpp && y > 0 ? o[(y - 1) * stride + x - bpp] : 0;
                int pred;
                switch (f)
                {
                    case 0: pred = 0; break;
                    case 1: pred = a; break;
                    case 2: pred = b; break;
                    case 3: pred = (a + b) / 2; break;
                    default:
                        var p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        break;
                }
                o[y * stride + x] = (byte)(v + pred);
            }
        }
        return o;
    }
}
