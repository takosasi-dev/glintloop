// 「サンプルで試す」の画像(仕様書 FR-14)。1024×768 のグラデーションと図形と文字「GlintLoop」を SkiaSharp でコードから描く。
// 外部の画像素材を使わない。乱数と時刻を使わないので、同じ PC・同じビルドでは毎回同じ画素と PNG のバイト列になる。
// .glint には生成した画像を PNG にして入れる(§9.11 の 4)ので、PNG のバイト列を返す。
using SkiaSharp;

namespace GlintLoop.Codec;

public static class SampleImage
{
    public const int Width = 1024;
    public const int Height = 768;

    /// <summary>サンプルの画像を描いて PNG のバイト列にする。</summary>
    public static byte[] CreatePng()
    {
        using var srgb = SKColorSpace.CreateSrgb();
        using var bmp = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb));
        using (var canvas = new SKCanvas(bmp))
        {
            // 背景: 左上の紺から右下の橙への斜めのグラデーション
            using (var bg = new SKPaint())
            {
                bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(Width, Height),
                    [new SKColor(24, 32, 96), new SKColor(120, 48, 140), new SKColor(250, 150, 60)],
                    [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
                canvas.DrawRect(new SKRect(0, 0, Width, Height), bg);
            }
            using var paint = new SKPaint { IsAntialias = true };
            // 図形: 円・角の丸い四角・輪
            paint.Color = new SKColor(255, 220, 90, 230);
            canvas.DrawCircle(250, 300, 150, paint);
            paint.Color = new SKColor(60, 200, 210, 200);
            canvas.DrawRoundRect(new SKRect(560, 140, 900, 420), 40, 40, paint);
            paint.Color = new SKColor(240, 90, 120, 220);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 28;
            canvas.DrawOval(new SKRect(440, 450, 820, 620), paint);
            paint.Style = SKPaintStyle.Fill;
            // 文字
            paint.Color = new SKColor(255, 255, 255, 255);
            using var font = new SKFont(SKTypeface.Default, 120) { Edging = SKFontEdging.Antialias };
            canvas.DrawText("GlintLoop", Width / 2f, 700, SKTextAlign.Center, font, paint);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("サンプルを PNG にできない");
        return data.ToArray();
    }
}
