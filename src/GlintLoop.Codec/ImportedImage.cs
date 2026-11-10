// 取り込んで読めた 1 枚の画像(ソース。仕様書 §6 の用語と §9.7 の規則 2)。画素は sRGB の 8 ビット・straight アルファの RGBA。
// EXIF の向きと ICC の変換を反映済みで、GPU の上限を超えたときは 1/2 の箱形縮小をしたあとのもの。
// SHA-256・バイト数・元のバイト列は、縮小やデコードの前のファイル(またはクリップボードのデータ)のもの(FR-57・§9.11 の assets)。
namespace GlintLoop.Codec;

public sealed class ImportedImage
{
    public ImportedImage(int width, int height, byte[] pixels, ImageFormat format, byte[] encoded, string sha256,
        long sourcePixels, bool animated, bool gpuShrunk)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.LongLength != (long)width * height * 4) throw new ArgumentException("画素のバイト数が幅×高さ×4 でない", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
        Format = format;
        Encoded = encoded;
        Sha256 = sha256;
        SourcePixels = sourcePixels;
        Animated = animated;
        GpuShrunk = gpuShrunk;
    }

    /// <summary>画素の幅(向きの適用と GPU の上限の縮小のあと)。</summary>
    public int Width { get; }

    /// <summary>画素の高さ(向きの適用と GPU の上限の縮小のあと)。</summary>
    public int Height { get; }

    /// <summary>sRGB 8 ビット・straight アルファの RGBA(R,G,B,A の順・行優先・幅×高さ×4 バイト)。書き換えないこと。</summary>
    public byte[] Pixels { get; }

    public ImageFormat Format { get; }

    /// <summary>元のバイト列(ファイルの中身、またはクリップボードの PNG・DIB のバイト列。サンプルは生成した PNG)。.glint に再エンコードせずに入れる。</summary>
    public byte[] Encoded { get; }

    /// <summary>元のバイト列の SHA-256(小文字の 16 進 64 桁)。同じ値の画像は同じ内容とみなす(D-16・FR-57)。</summary>
    public string Sha256 { get; }

    /// <summary>元のバイト列のバイト数(FR-57 の合計に数える値)。</summary>
    public long ByteCount => Encoded.LongLength;

    /// <summary>ヘッダーの幅×高さ(GPU の上限の縮小の前。FR-57 の画素数の合計に数える値)。</summary>
    public long SourcePixels { get; }

    /// <summary>アニメーションを含むファイル(2 フレーム以上の GIF・WebP)で、先頭のフレームだけを使った(FR-9。import.animated)。</summary>
    public bool Animated { get; }

    /// <summary>GPU のテクスチャの最大寸法に収めるために縮小した(§9.7 の規則 2。import.gpu_shrunk)。</summary>
    public bool GpuShrunk { get; }

    /// <summary>.glint の assets の ext(png・jpg・webp・bmp・ico・gif・dib)。</summary>
    public string Ext => Format.ExtName();

    /// <summary>ログの import_ok の fmt(png・jpeg・webp・bmp・ico・gif・dib)。</summary>
    public string LogFormat => Format.LogName();

    /// <summary>バイト列の SHA-256 を小文字の 16 進 64 桁で返す。</summary>
    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
