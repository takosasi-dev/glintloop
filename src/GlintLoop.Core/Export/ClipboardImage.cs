// クリップボードに載せる DIB と DIBV5 のバイト列を、静止画の straight BGRA から自作のコードで並べる(§9.10 の 2 の 5 の表)。パレットと余分なバイトを付けない。
// DIB は 24 ビットで、透明な部分を白の上に線形光で合成する(c'=enc(dec(c)×a+(1−a))、floor(255×c'+0.5))。DIBV5 は 32 ビットで、straight の値をそのまま並べる。どちらも下の行から先に並べる。
using System.Buffers.Binary;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Export;

public static class ClipboardImage
{
    private const int DibHeaderSize = 40;
    private const int DibV5HeaderSize = 124;

    /// <summary>bgra は上の行から並べた straight の BGRA(幅×高さ×4 バイト)。</summary>
    public static byte[] Dib(int width, int height, byte[] bgra)
    {
        Validate(width, height, bgra);
        var row = (width * 3 + 3) / 4 * 4;
        var size = row * height;
        var o = new byte[DibHeaderSize + size];
        var s = o.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, DibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], size);
        var at = DibHeaderSize;
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var a = bgra[i + 3];
                o[at + x * 3] = OverWhite(bgra[i], a);
                o[at + x * 3 + 1] = OverWhite(bgra[i + 1], a);
                o[at + x * 3 + 2] = OverWhite(bgra[i + 2], a);
            }
            at += row;
        }
        return o;
    }

    /// <summary>bgra は上の行から並べた straight の BGRA(幅×高さ×4 バイト)。</summary>
    public static byte[] DibV5(int width, int height, byte[] bgra)
    {
        Validate(width, height, bgra);
        var row = width * 4;
        var size = row * height;
        var o = new byte[DibV5HeaderSize + size];
        var s = o.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, DibV5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 3); // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteInt32LittleEndian(s[108..], 4); // LCS_GM_IMAGES
        for (var y = height - 1; y >= 0; y--)
            Buffer.BlockCopy(bgra, y * row, o, DibV5HeaderSize + (height - 1 - y) * row, row);
        return o;
    }

    private static byte OverWhite(byte c, byte a)
    {
        if (a == 255) return c;
        if (a == 0) return 255;
        var alpha = a / 255.0;
        return (byte)Math.Clamp(MathX.RoundHalfUpToInt(255 * Srgb.Enc(Srgb.Lin8(c) * alpha + (1 - alpha))), 0, 255);
    }

    private static void Validate(int width, int height, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.LongLength != (long)width * height * 4) throw new ArgumentException("画素のバイト数が幅×高さ×4 でない", nameof(bgra));
    }
}
