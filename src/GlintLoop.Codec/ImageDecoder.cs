// 画像のバイト列のヘッダーを読む処理と、sRGB 8 ビット・straight の RGBA へのデコード(仕様書 FR-8・FR-9・FR-11・FR-12)。
// ヘッダーは、PNG は IHDR を自分で読み(IDAT が無いと SKCodec が読めない。AC-55 の (7))、ほかは SKCodec.Create の Info だけを読む。画素のメモリを確保しない(INV-11)。
// デコードは SKCodec.GetPixels で sRGB へ変換して先頭のフレームを読み、EXIF の向きを Orientation で適用する。
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GlintLoop.Codec;

/// <summary>デコードに失敗した(壊れている・途中で切れている・SkiaSharp が読めない)。import.decode_error になる。</summary>
public sealed class ImageDecodeException(string message) : Exception(message);

/// <summary>ヘッダーから読んだ形式と大きさ(向きを適用する前の幅と高さ)。</summary>
public readonly record struct ImageHeader(ImageFormat Format, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>デコードの結果(向きを適用したあとの画素)。</summary>
public sealed record DecodedPixels(int Width, int Height, byte[] Rgba, bool Animated);

public static class ImageDecoder
{
    /// <summary>
    /// 先頭バイトで形式を判定し、ヘッダーの幅と高さを読む。対応していない形式は null(<paramref name="failure"/> が非対応)。
    /// 形式は分かるがヘッダーが読めないときも null(decode_error)。DIB はここでは扱わない(DibReader)。
    /// </summary>
    public static ImageHeader? ReadHeader(byte[] bytes, out ImportFailReason failure)
    {
        failure = ImportFailReason.Unsupported;
        if (ImageFormats.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, ImageFormats.SniffLength))) is not { } format) return null;
        failure = ImportFailReason.DecodeError;
        if (format == ImageFormat.Png && ReadPngSize(bytes) is { } png) return new ImageHeader(format, png.Width, png.Height);
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return null;
            return new ImageHeader(format, info.Width, info.Height);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    // 署名(8)・長さ(4)・IHDR(4)・本体(13)・CRC(4) の 33 バイトがそろっているとき、IHDR の幅と高さを返す(16・20 バイト目のビッグエンディアン)。
    // 0 と 2^31 以上は PNG の値として不正。読めないときは null で、SKCodec の判定に任せる
    private static (int Width, int Height)? ReadPngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        var w = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue) return null;
        return ((int)w, (int)h);
    }

    /// <summary>
    /// 画素をデコードする。ICC プロファイルが指す色空間から sRGB へ変換し(プロファイルが無い・読めないときは sRGB として扱う)、
    /// アニメーションは先頭のフレームだけを読み、EXIF の向きを適用する。16 ビットの PNG は round(v÷257) で 8 ビットにする。
    /// 失敗は ImageDecodeException。
    /// </summary>
    public static DecodedPixels Decode(byte[] bytes)
    {
        var src = bytes;
        if (Png16.IsSixteenBit(bytes))
        {
            try
            {
                src = Png16.ToEightBit(bytes);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
            {
                throw new ImageDecodeException("16 ビットの PNG を読めない");
            }
        }

        using var data = SKData.CreateCopy(src);
        using var codec = SKCodec.Create(data) ?? throw new ImageDecodeException("SKCodec.Create が null");
        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0) throw new ImageDecodeException("大きさが 0");
        var animated = codec.FrameCount > 1;
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul, srgb);
        var px = new byte[(long)w * h * 4];
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        SKCodecResult result;
        try
        {
            result = codec.GetPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, new SKCodecOptions(0));
        }
        finally
        {
            handle.Free();
        }
        if (result != SKCodecResult.Success) throw new ImageDecodeException("GetPixels が " + result);
        var oriented = Orientation.Apply(px, w, h, ExifValue(codec.EncodedOrigin), out var ow, out var oh);
        return new DecodedPixels(ow, oh, oriented, animated);
    }

    /// <summary>SKEncodedOrigin を EXIF の向きの値(1〜8)にする。</summary>
    public static int ExifValue(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => 2,
        SKEncodedOrigin.BottomRight => 3,
        SKEncodedOrigin.BottomLeft => 4,
        SKEncodedOrigin.LeftTop => 5,
        SKEncodedOrigin.RightTop => 6,
        SKEncodedOrigin.RightBottom => 7,
        SKEncodedOrigin.LeftBottom => 8,
        _ => 1,
    };
}
