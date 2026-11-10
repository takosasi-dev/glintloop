// 実出力(FR-19)の表示のために、書き出しのキャッシュのファイルをフレームの列にデコードする(UI スレッドの外で呼ぶ。NFR-7)。
// GIF は SKCodec(前のフレームの上に重ねる処理も Skia が行う)、APNG は Skia がフレームを分けないので fcTL・fdAT を自分で分けて 1 枚ずつの PNG にし、
// dispose_op・blend_op に従って重ねる。PNG・JPEG・WebP は 1 枚。画素は straight の BGRA(8 ビット、行優先、幅×高さ×4 バイト)で返す。
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GlintLoop.Codec.Preview;

/// <summary>1 フレーム。Bgra は straight の BGRA、DelayMs は表示の長さ(静止画は 0)。</summary>
public sealed record DecodedFrame(byte[] Bgra, int DelayMs);

/// <summary>デコードの結果。Plays は再生の回数(0 は無限。静止画は 1)。</summary>
public sealed record DecodedAnimation(int Width, int Height, IReadOnlyList<DecodedFrame> Frames, int Plays);

public sealed class OutputDecodeException(string message) : Exception(message);

public static class OutputFrameDecoder
{
    /// <summary>format は ExportFormat の名前(png・jpeg・gif・webp・apng)。読めないときは OutputDecodeException。</summary>
    public static DecodedAnimation Decode(byte[] bytes, string format)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return format switch
        {
            "gif" => DecodeGif(bytes),
            "apng" => DecodeApng(bytes),
            _ => DecodeStill(bytes),
        };
    }

    // ---------------------------------------------------------------- 静止画

    /// <summary>ドラッグ欄のサムネイル(§9.3): どの形式でも最初の 1 枚だけをデコードし、幅 maxWidth・高さ maxHeight に収まるように間引く(拡大しない)。読めないときは OutputDecodeException。</summary>
    public static DecodedAnimation Thumbnail(byte[] bytes, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var d = DecodeStill(bytes);
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / d.Width, (double)maxHeight / d.Height));
        var w = Math.Max(1, (int)(d.Width * scale));
        var h = Math.Max(1, (int)(d.Height * scale));
        if (w == d.Width && h == d.Height) return d;
        var from = d.Frames[0].Bgra;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var sy = (int)((long)y * d.Height / h);
            for (var x = 0; x < w; x++)
            {
                var sx = (int)((long)x * d.Width / w);
                Buffer.BlockCopy(from, (sy * d.Width + sx) * 4, px, (y * w + x) * 4, 4);
            }
        }
        return new DecodedAnimation(w, h, [new DecodedFrame(px, 0)], 1);
    }

    private static DecodedAnimation DecodeStill(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new OutputDecodeException("SKCodec.Create が null");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var px = new byte[info.BytesSize];
        Get(codec, info, px, new SKCodecOptions(0));
        return new DecodedAnimation(info.Width, info.Height, [new DecodedFrame(px, 0)], 1);
    }

    private static void Get(SKCodec codec, SKImageInfo info, byte[] px, SKCodecOptions options)
    {
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            var r = codec.GetPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, options);
            if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new OutputDecodeException("GetPixels が " + r);
        }
        finally
        {
            handle.Free();
        }
    }

    // ---------------------------------------------------------------- GIF

    private static DecodedAnimation DecodeGif(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new OutputDecodeException("SKCodec.Create が null");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var count = Math.Max(1, codec.FrameCount);
        var infos = codec.FrameInfo;
        var frames = new List<DecodedFrame>(count);
        for (var i = 0; i < count; i++)
        {
            var px = new byte[info.BytesSize];
            var required = i < infos.Length ? infos[i].RequiredFrame : -1;
            SKCodecOptions options;
            if (required >= 0 && required < frames.Count)
            {
                // 前のフレーム(重ねる土台)を写してから、その上にこのフレームを描かせる
                Buffer.BlockCopy(frames[required].Bgra, 0, px, 0, px.Length);
                options = new SKCodecOptions(i, required);
            }
            else
            {
                options = new SKCodecOptions(i);
            }
            Get(codec, info, px, options);
            var delay = i < infos.Length ? infos[i].Duration : 0;
            frames.Add(new DecodedFrame(px, delay));
        }
        // RepetitionCount: -1 は無限、0 は 1 回だけ(NETSCAPE なし)、n は n 回の繰り返し
        var rep = codec.RepetitionCount;
        var plays = rep < 0 ? 0 : rep + 1;
        return new DecodedAnimation(info.Width, info.Height, frames, plays);
    }

    // ---------------------------------------------------------------- APNG

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    private sealed record Control(int W, int H, int X, int Y, int DelayMs, byte Dispose, byte Blend)
    {
        public List<byte[]> Data { get; } = new();
    }

    private static DecodedAnimation DecodeApng(byte[] bytes)
    {
        if (bytes.Length < Signature.Length || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature)) throw new OutputDecodeException("PNG の署名が無い");
        byte[]? ihdr = null;
        var extra = new List<(string Type, byte[] Data)>();
        var controls = new List<Control>();
        var defaultIdat = new List<byte[]>();
        Control? current = null;
        var plays = 1;
        var sawActl = false;
        var pos = Signature.Length;
        while (pos + 12 <= bytes.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
            if (len < 0 || pos + 12 + len > bytes.Length) throw new OutputDecodeException("チャンクの長さが壊れている");
            var type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            var body = bytes.AsSpan(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    ihdr = body.ToArray();
                    break;
                case "PLTE":
                case "tRNS":
                    extra.Add((type, body.ToArray()));
                    break;
                case "acTL":
                    sawActl = true;
                    plays = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    break;
                case "fcTL":
                {
                    var num = BinaryPrimitives.ReadUInt16BigEndian(body[20..]);
                    var den = BinaryPrimitives.ReadUInt16BigEndian(body[22..]);
                    current = new Control(
                        (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]),
                        (int)BinaryPrimitives.ReadUInt32BigEndian(body[8..]),
                        (int)BinaryPrimitives.ReadUInt32BigEndian(body[12..]),
                        (int)BinaryPrimitives.ReadUInt32BigEndian(body[16..]),
                        num * 1000 / (den == 0 ? 100 : den),
                        body[24],
                        body[25]);
                    controls.Add(current);
                    break;
                }
                case "IDAT":
                    if (current is not null) current.Data.Add(body.ToArray());
                    else defaultIdat.Add(body.ToArray());
                    break;
                case "fdAT":
                    current?.Data.Add(body[4..].ToArray());
                    break;
            }
            pos += 12 + len;
            if (type == "IEND") break;
        }
        if (ihdr is null) throw new OutputDecodeException("IHDR が無い");
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4));
        if (!sawActl || controls.Count == 0) return DecodeStill(bytes);

        var canvas = new byte[width * height * 4];
        var frames = new List<DecodedFrame>(controls.Count);
        foreach (var c in controls)
        {
            if (c.W <= 0 || c.H <= 0 || c.X < 0 || c.Y < 0 || c.X + c.W > width || c.Y + c.H > height) throw new OutputDecodeException("fcTL の枠が画像の外");
            var sub = DecodeSub(ihdr, extra, c);
            byte[]? saved = c.Dispose == 2 ? (byte[])canvas.Clone() : null;
            Paint(canvas, width, sub, c);
            frames.Add(new DecodedFrame((byte[])canvas.Clone(), c.DelayMs));
            if (c.Dispose == 1) Clear(canvas, width, c);
            else if (saved is not null) canvas = saved;
        }
        return new DecodedAnimation(width, height, frames, plays);
    }

    /// <summary>1 フレームの IHDR(大きさだけ差し替え)・PLTE・tRNS・IDAT を並べた PNG を作り、straight の BGRA にデコードする。</summary>
    private static byte[] DecodeSub(byte[] ihdr, List<(string Type, byte[] Data)> extra, Control c)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        var h = (byte[])ihdr.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)c.W);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)c.H);
        WriteChunk(ms, "IHDR", h);
        foreach (var (t, d) in extra) WriteChunk(ms, t, d);
        foreach (var d in c.Data) WriteChunk(ms, "IDAT", d);
        WriteChunk(ms, "IEND", []);
        using var data = SKData.CreateCopy(ms.ToArray());
        using var codec = SKCodec.Create(data) ?? throw new OutputDecodeException("フレームの PNG を読めない");
        var info = new SKImageInfo(c.W, c.H, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var px = new byte[info.BytesSize];
        Get(codec, info, px, new SKCodecOptions(0));
        return px;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, head[4..]);
        s.Write(head);
        s.Write(data);
        var crc = Crc32(head[4..], data);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    private static uint[]? s_crcTable;

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var table = s_crcTable ??= MakeTable();
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    private static uint[] MakeTable()
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

    /// <summary>blend_op が 0(source)なら枠の中を置き換え、1(over)なら straight の α で上に重ねる。</summary>
    private static void Paint(byte[] canvas, int width, byte[] sub, Control c)
    {
        for (var y = 0; y < c.H; y++)
        {
            for (var x = 0; x < c.W; x++)
            {
                var s = (y * c.W + x) * 4;
                var d = ((c.Y + y) * width + c.X + x) * 4;
                var sa = sub[s + 3];
                var da = canvas[d + 3];
                if (c.Blend == 0 || sa == 255 || da == 0)
                {
                    if (c.Blend != 0 && sa == 0) continue;
                    Buffer.BlockCopy(sub, s, canvas, d, 4);
                    continue;
                }
                if (sa == 0) continue;
                // over(straight の α): a = sa + da×(1−sa)、色 = (sc×sa + dc×da×(1−sa)) ÷ a
                var inv = 255 - sa;
                var outA255 = sa * 255 + da * inv; // a×255×255 の単位
                for (var ch = 0; ch < 3; ch++)
                {
                    var num = sub[s + ch] * sa * 255 + canvas[d + ch] * da * inv;
                    canvas[d + ch] = (byte)((num + outA255 / 2) / outA255);
                }
                canvas[d + 3] = (byte)((outA255 + 127) / 255);
            }
        }
    }

    private static void Clear(byte[] canvas, int width, Control c)
    {
        for (var y = 0; y < c.H; y++) Array.Clear(canvas, ((c.Y + y) * width + c.X) * 4, c.W * 4);
    }
}
