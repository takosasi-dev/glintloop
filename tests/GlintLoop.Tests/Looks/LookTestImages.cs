// ルックのテスト(AC-27・AC-26)が使う画像。普段はコードで作る 3 枚(滑らかな色の変化と細かい模様、透過のある図形、平らな色と文字)。
// 環境変数 GLINTLOOP_REFERENCE_DIR が設定されているときだけ、そのフォルダの画像 3 枚(ファイル名の序数順)を読む(§8.1 の基準画像)。
// 出力やメッセージにファイル名を書かない(作者の決め)。呼ぶ側は「画像 1・2・3」と書く。
using System.IO;
using GlintLoop.Codec;
using GlintLoop.Core.Numerics;
using GlintLoop.Tests.Render;
using SkiaSharp;

namespace GlintLoop.Tests.Looks;

internal static class LookTestImages
{
    public const string ReferenceDirVariable = "GLINTLOOP_REFERENCE_DIR";

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <summary>基準画像のフォルダを使うか(環境変数が空でないとき)。</summary>
    public static bool UsesReference => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReferenceDirVariable));

    /// <summary>3 枚の画像(基準画像のフォルダがあればその 3 枚、無ければコードで作る 3 枚)。</summary>
    public static IReadOnlyList<CompositeImg> Three()
    {
        var dir = Environment.GetEnvironmentVariable(ReferenceDirVariable);
        if (string.IsNullOrWhiteSpace(dir)) return [Gradient(), Shapes(), FlatText()];
        var files = Directory.GetFiles(dir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        if (files.Length < 3) throw new InvalidOperationException($"{ReferenceDirVariable} のフォルダに画像が 3 枚ない({files.Length} 枚)");
        return files.Select(f =>
        {
            var d = ImageDecoder.Decode(File.ReadAllBytes(f));
            return new CompositeImg(d.Width, d.Height, d.Rgba);
        }).ToArray();
    }

    /// <summary>1: 滑らかな色の変化(色相の横の変化と明るさの縦の変化)と細かい模様(1 画素の市松・2 画素の縞・雑音の質感)。</summary>
    public static CompositeImg Gradient() => CompositeImg.Make(960, 720, (x, y) =>
    {
        var h = x / 959.0 * 6;
        var l = 0.25 + 0.6 * (1 - y / 719.0);
        var (r, g, b) = Hue(h);
        var rr = 255 * l * (0.35 + 0.65 * r);
        var gg = 255 * l * (0.35 + 0.65 * g);
        var bb = 255 * l * (0.35 + 0.65 * b);
        if (x >= 80 && x < 280 && y >= 80 && y < 240) (rr, gg, bb) = (x + y) % 2 == 0 ? (240.0, 240.0, 235.0) : (20.0, 24.0, 30.0);
        if (x >= 640 && x < 880 && y >= 460 && y < 640) (rr, gg, bb) = (x / 2) % 2 == 0 ? (200.0, 70.0, 40.0) : (30.0, 60.0, 150.0);
        var n = (int)(Hash.Hash32((uint)(y * 960 + x) ^ 0x2F11u) % 31) - 15;
        return (Clamp(rr + n), Clamp(gg + n), Clamp(bb + n), 255);
    });

    /// <summary>2: 透過のある図形(透明の背景に、不透明の円・半透明の四角・縁のぼけた輪・三角)。</summary>
    public static CompositeImg Shapes() => CompositeImg.Make(800, 800, (x, y) =>
    {
        double r = 0, g = 0, b = 0;
        var a = 0;
        var dx = x - 260.0;
        var dy = y - 280.0;
        if (dx * dx + dy * dy < 180 * 180) (r, g, b, a) = (230, 60, 50, 255);
        if (x >= 380 && x < 720 && y >= 120 && y < 420) (r, g, b, a) = (40, 110, 230, 128);
        var ex = x - 540.0;
        var ey = y - 580.0;
        var d = Math.Sqrt(ex * ex + ey * ey);
        if (d > 90 && d < 170) (r, g, b, a) = (250, 210, 60, (int)(255 * Math.Clamp(1 - Math.Abs(d - 130) / 40, 0, 1)));
        if (y > 480 && y < 760 && Math.Abs(x - 180) < (y - 480) / 2.0) (r, g, b, a) = (60, 190, 120, 230);
        return (Clamp(r), Clamp(g), Clamp(b), a);
    });

    /// <summary>3: 平らな色の面(4 色)と文字(SkiaSharp で描く。テストの中だけで使う)。</summary>
    public static CompositeImg FlatText()
    {
        const int w = 1024, h = 640;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint { IsAntialias = true })
        using (var face = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default)
        using (var font = new SKFont(face, 72))
        {
            void Rect(float x, float y, float rw, float rh, SKColor c)
            {
                paint.Color = c;
                canvas.DrawRect(x, y, rw, rh, paint);
            }
            Rect(0, 0, w / 2, h / 2, new SKColor(245, 245, 240));
            Rect(w / 2, 0, w / 2, h / 2, new SKColor(28, 36, 72));
            Rect(0, h / 2, w / 2, h / 2, new SKColor(250, 200, 40));
            Rect(w / 2, h / 2, w / 2, h / 2, new SKColor(30, 150, 140));
            paint.Color = new SKColor(20, 20, 24);
            canvas.DrawText("GlintLoop", 40, 140, SKTextAlign.Left, font, paint);
            canvas.DrawText("Sample 42", 40, 440, SKTextAlign.Left, font, paint);
            paint.Color = new SKColor(250, 250, 250);
            canvas.DrawText("Looks 24", 560, 140, SKTextAlign.Left, font, paint);
            canvas.DrawText("ABC xyz", 560, 440, SKTextAlign.Left, font, paint);
        }
        var px = bmp.Bytes;
        return new CompositeImg(w, h, px);
    }

    private static (double R, double G, double B) Hue(double h)
    {
        var i = (int)Math.Floor(h) % 6;
        var f = h - Math.Floor(h);
        return i switch
        {
            0 => (1, f, 0),
            1 => (1 - f, 1, 0),
            2 => (0, 1, f),
            3 => (0, 1 - f, 1),
            4 => (f, 0, 1),
            _ => (1, 0, 1 - f),
        };
    }

    private static int Clamp(double v) => Math.Clamp((int)Math.Round(v), 0, 255);
}
