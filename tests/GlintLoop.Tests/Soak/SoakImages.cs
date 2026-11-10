// AC-61 の 3 枚の画像をコードで作り、一時フォルダへ書く: 4000×3000 の JPEG、2000×2000 の透過 PNG、1920×1080 のスクリーンショット風の PNG。
// 計測の前に 1 回だけ作り、以降はファイルのパスだけを持つ(周ごとに大きな配列を作ると、メモリの計測を乱す)。
using GlintLoop.Tests.Codec;

namespace GlintLoop.Tests.Soak;

internal static class SoakImages
{
    /// <summary>周ごとに順に使う 3 枚のパス。</summary>
    public static string[] Write(TempDir dir) =>
    [
        dir.Write("soak-photo.jpg", SkiaBytes.Jpeg(4000, 3000, Img.Pattern(4000, 3000, alpha: false), 90)),
        dir.Write("soak-transparent.png", SkiaBytes.Png(2000, 2000, Transparent(2000, 2000))),
        dir.Write("soak-screenshot.png", SkiaBytes.Png(1920, 1080, ScreenshotLike(1920, 1080))),
    ];

    /// <summary>α の模様の外側に、完全に透明な余白を持つ画像。</summary>
    private static byte[] Transparent(int w, int h)
    {
        const int margin = 300;
        var px = Img.Pattern(w, h, alpha: true);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x < margin || y < margin || x >= w - margin || y >= h - margin) px[(y * w + x) * 4 + 3] = 0;
            }
        }
        return px;
    }

    /// <summary>無地の背景に、タイトルバー・横の一覧・文字のような短い横線を置いた画像。</summary>
    private static byte[] ScreenshotLike(int w, int h)
    {
        var px = Img.Solid(w, h, 243, 243, 243);
        Fill(px, w, 0, 0, w, 40, 32, 64, 120);
        Fill(px, w, 0, 40, 280, h - 40, 225, 228, 232);
        for (var row = 0; row < 36; row++)
        {
            var y = 60 + row * 28;
            Fill(px, w, 20, y, 120 + row * 37 % 120, 10, 90, 96, 104);
            Fill(px, w, 320, y, 400 + row * 211 % 1100, 10, 40, 44, 48);
        }
        return px;
    }

    private static void Fill(byte[] px, int w, int left, int top, int width, int height, byte r, byte g, byte b)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = r;
                px[i + 1] = g;
                px[i + 2] = b;
            }
        }
    }
}
