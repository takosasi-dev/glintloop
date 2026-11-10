// 取り込める画像の形式と、ファイルの先頭バイトによる形式の判定(仕様書 FR-8。拡張子は見ない)。
// ログの fmt(§9.11 の 6)と .glint の ext(§9.11 の 4)の 2 通りの綴りもここで決める(jpeg と jpg が違う)。
// TIFF・HEIC・AVIF・SVG・PSD・動画などは、どの形式にも当たらないので非対応になる。
namespace GlintLoop.Codec;

/// <summary>取り込みの形式。Dib はクリップボードの DIB・DIBV5 だけで、ファイルの先頭バイトからは判定しない。</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    Webp,
    Bmp,
    Ico,
    Gif,
    Dib,
}

public static class ImageFormats
{
    /// <summary>判定に読む先頭のバイト数。</summary>
    public const int SniffLength = 32;

    /// <summary>
    /// 先頭バイトで 6 形式(PNG・JPEG・WebP・BMP・ICO・GIF)のどれかを判定する。当たらなければ null(非対応)。
    /// BMP は "BM" だけでは文字のファイルにも当たるので、14 バイト目からの情報ヘッダーの大きさも確かめる。
    /// </summary>
    public static ImageFormat? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return ImageFormat.Png;
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return ImageFormat.Jpeg;
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)))
            return ImageFormat.Gif;
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ImageFormat.Webp;
        if (head.Length >= 18 && head[0] == (byte)'B' && head[1] == (byte)'M')
        {
            var infoSize = head[14] | (head[15] << 8) | (head[16] << 16) | (head[17] << 24);
            if (infoSize is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124) return ImageFormat.Bmp;
            return null;
        }
        if (head.Length >= 6 && head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0 && (head[4] | (head[5] << 8)) > 0)
            return ImageFormat.Ico;
        return null;
    }

    /// <summary>ログの import_ok の fmt の値(png・jpeg・webp・bmp・ico・gif・dib)。</summary>
    public static string LogName(this ImageFormat f) => f switch
    {
        ImageFormat.Png => "png",
        ImageFormat.Jpeg => "jpeg",
        ImageFormat.Webp => "webp",
        ImageFormat.Bmp => "bmp",
        ImageFormat.Ico => "ico",
        ImageFormat.Gif => "gif",
        ImageFormat.Dib => "dib",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>.glint の assets の ext の値(png・jpg・webp・bmp・ico・gif・dib)。</summary>
    public static string ExtName(this ImageFormat f) => f switch
    {
        ImageFormat.Png => "png",
        ImageFormat.Jpeg => "jpg",
        ImageFormat.Webp => "webp",
        ImageFormat.Bmp => "bmp",
        ImageFormat.Ico => "ico",
        ImageFormat.Gif => "gif",
        ImageFormat.Dib => "dib",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>先頭バイトが ZIP の署名(50 4B 03 04)か。.glint は拡張子でなく、これで見分ける(§9.11 の 4 の (1))。</summary>
    public static bool IsZipSignature(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;
}
