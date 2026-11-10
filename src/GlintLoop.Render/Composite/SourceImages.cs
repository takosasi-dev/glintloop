// 文書の描画が読む、取り込んだ画像の画素の受け口(§9.7 の規則 2)。SHA-256 から sRGB 8 ビット・straight の RGBA と幅・高さを返す。
// Render は Codec を参照しないので、画面の側が SessionImportTarget.Images などを包んで渡す(FuncSourceImages)。
// Find は描画の専用スレッドからも、Layout を呼んだスレッドからも呼ばれる。読むだけにして、ほかのスレッドの書き換えと重ねない。
namespace GlintLoop.Render.Composite;

/// <summary>取り込んだ 1 枚の画像。Rgba は sRGB 8 ビット・straight で、行を詰めた幅×高さ×4 バイト(ICC と EXIF の向きは反映済み)。</summary>
public sealed record SourceImage(int Width, int Height, byte[] Rgba)
{
    public SourceImage Validate()
    {
        if (Width <= 0 || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Width), "大きさは 1 以上");
        if (Rgba.LongLength != (long)Width * Height * 4) throw new ArgumentException("画素のバイト数が幅×高さ×4 でない", nameof(Rgba));
        return this;
    }
}

/// <summary>SHA-256(小文字 16 進 64 桁)から取り込んだ画像を引く。無ければ null。</summary>
public interface ISourceImages
{
    SourceImage? Find(string sha256);
}

/// <summary>関数で引く ISourceImages(例: <c>sha =&gt; target.Images.TryGetValue(sha, out var im) ? new SourceImage(im.Width, im.Height, im.Pixels) : null</c>)。</summary>
public sealed class FuncSourceImages(Func<string, SourceImage?> find) : ISourceImages
{
    public SourceImage? Find(string sha256) => find(sha256);
}
