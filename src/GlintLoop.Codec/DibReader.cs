// クリップボードの DIB・DIBV5 のバイト列を、自作のコードで RGBA に読む(仕様書 §9.10 の 5 の DIB の規則。シリアライザーを使わない。INV-10)。
// 読むのは BITMAPINFOHEADER(40 バイト)か BITMAPV5HEADER(124 バイト)の、24・32 ビットの BI_RGB と 32 ビットの BI_BITFIELDS だけ。
// それ以外と、ヘッダーが短い・画素のバイト数が足りないものは「読めない形式」として null を返す。
using System.Buffers.Binary;
using System.Numerics;

namespace GlintLoop.Codec;

public static class DibReader
{
    private const int BiRgb = 0;
    private const int BiBitfields = 3;

    /// <summary>DIB のヘッダーの値(読めると判定できたもの)。</summary>
    public readonly record struct Header(int Width, int Height, bool BottomUp, int BitCount, int Compression, int HeaderSize,
        uint RedMask, uint GreenMask, uint BlueMask, uint AlphaMask, long PixelOffset, long Stride);

    /// <summary>ヘッダーだけを読む。読めない形式なら null(画素は確保しない。INV-11 の判定に使う)。</summary>
    public static Header? ReadHeader(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 4) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (size is not (40 or 124) || dib.Length < size) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (width <= 0 || rawHeight == 0 || rawHeight == int.MinValue) return null;
        if (bitCount is not (24 or 32)) return null;
        if (compression is not (BiRgb or BiBitfields)) return null;
        if (compression == BiBitfields && bitCount != 32) return null;

        uint r, g, b, a = 0;
        long offset = size;
        if (compression == BiBitfields)
        {
            if (size == 40)
            {
                // 40 バイトのヘッダーでは、3 つのマスクがヘッダーの直後に並ぶ
                if (dib.Length < 52) return null;
                r = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
                g = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
                b = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
                offset = 52;
            }
            else
            {
                r = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
                g = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
                b = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
            }
        }
        else
        {
            (r, g, b) = (0x00FF0000u, 0x0000FF00u, 0x000000FFu);
        }
        if (size == 124 && bitCount == 32) a = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
        // 色表(24・32 ビットでは普通は無い)が置かれていれば、画素はその後ろから始まる
        offset += clrUsed * 4L;

        var height = Math.Abs((long)rawHeight);
        var stride = ((long)width * bitCount + 31) / 32 * 4;
        if (offset + stride * height > dib.Length) return null;
        return new Header(width, (int)height, rawHeight > 0, bitCount, compression, size, r, g, b, a, offset, stride);
    }

    /// <summary>
    /// 画素を straight の RGBA(上の行から)に読む。読めない形式なら null。
    /// 32 ビットで、アルファのマスクが無い(V5 でない、または 0)か、アルファがすべて 0 のときは不透明(α=255)として読む。
    /// </summary>
    public static byte[]? Read(ReadOnlySpan<byte> dib, out int width, out int height)
    {
        width = height = 0;
        if (ReadHeader(dib) is not { } h) return null;
        width = h.Width;
        height = h.Height;
        var px = new byte[(long)h.Width * h.Height * 4];
        var anyAlpha = false;
        for (var y = 0; y < h.Height; y++)
        {
            var srcRow = h.BottomUp ? h.Height - 1 - y : y;
            var row = dib.Slice((int)(h.PixelOffset + srcRow * h.Stride), (int)h.Stride);
            var o = y * h.Width * 4;
            if (h.BitCount == 24)
            {
                for (var x = 0; x < h.Width; x++, o += 4)
                {
                    px[o] = row[x * 3 + 2];
                    px[o + 1] = row[x * 3 + 1];
                    px[o + 2] = row[x * 3];
                    px[o + 3] = 255;
                }
                continue;
            }
            for (var x = 0; x < h.Width; x++, o += 4)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]);
                px[o] = Channel(v, h.RedMask);
                px[o + 1] = Channel(v, h.GreenMask);
                px[o + 2] = Channel(v, h.BlueMask);
                var a = h.AlphaMask != 0 ? Channel(v, h.AlphaMask) : (byte)255;
                px[o + 3] = a;
                if (h.AlphaMask != 0 && a != 0) anyAlpha = true;
            }
        }
        if (h.BitCount == 32 && h.AlphaMask != 0 && !anyAlpha)
        {
            for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        }
        return px;
    }

    // マスクで取り出した値を 8 ビットに直す。マスクの幅が 8 ビットでなければ round(v×255÷最大値)。マスクが 0 なら 0。
    private static byte Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        var shift = BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        var value = (v & mask) >> shift;
        if (max == 255) return (byte)value;
        return (byte)((value * 255UL * 2 + max) / (2UL * max));
    }
}
