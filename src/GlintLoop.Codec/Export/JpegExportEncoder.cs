// JPEG の書き出し(FR-69・§9.9 の 5): 透明な部分を背景色の上に線形光で over 合成して不透明にし、SkiaSharp の JPEG エンコーダーで書く。
// 品質 1〜100、クロマサブサンプリングは 4:2:0(品質 91 以上は 4:4:4)。色空間を付けないので APP2(ICC)を書かず、EXIF(APP1)・COM・サムネイルも書かない。
// プログレッシブは指定しない(Skia の既定のベースライン。AC-37 の (3) のテストで SOF0 であることを確かめる)。
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Numerics;
using SkiaSharp;

namespace GlintLoop.Codec.Export;

public sealed class JpegExportEncoder : IExportEncoder
{
    /// <summary>この品質以上で 4:4:4 にする(FR-69)。</summary>
    public const int Full444Quality = 91;

    public string Format => ExportFormat.Jpeg;

    public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        ExportEncoding.CheckFrames(frames, settings, 1);
        var px = ExportEncoding.Frame(frames, 0, run.Cancel);
        var bg = OutputPlan.ParseRgb(settings.JpegBackground) ?? new GifColor(255, 255, 255);
        var opaque = OverBackground(px, bg);
        var quality = Math.Clamp(settings.Quality, 1, 100);
        var downsample = quality >= Full444Quality ? SKJpegEncoderDownsample.Downsample444 : SKJpegEncoderDownsample.Downsample420;
        byte[] bytes;
        using (var bmp = SkiaExport.Bitmap(settings.Width, settings.Height, opaque, SKAlphaType.Opaque))
        using (var pm = bmp.PeekPixels())
        using (var data = pm.Encode(new SKJpegEncoderOptions(quality, downsample, SKJpegEncoderAlphaOption.Ignore))
                          ?? throw new InvalidOperationException("JPEG に符号化できない"))
        {
            bytes = data.ToArray();
        }
        run.Cancel.ThrowIfCancellationRequested();
        return ExportEncoding.Run(output, limitBytes, s => s.Write(bytes));
    }

    /// <summary>straight RGBA を背景色 bg の上に線形光で over 合成した不透明の RGBA(C=lin(Cs)×α+lin(Cb)×(1−α) を enc して round)。</summary>
    public static byte[] OverBackground(byte[] rgba, GifColor bg)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        var bl = new[] { Srgb.Lin8(bg.R), Srgb.Lin8(bg.G), Srgb.Lin8(bg.B) };
        var o = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = rgba[i + 3];
            if (a == 255)
            {
                o[i] = rgba[i];
                o[i + 1] = rgba[i + 1];
                o[i + 2] = rgba[i + 2];
            }
            else
            {
                var al = a / 255.0;
                for (var c = 0; c < 3; c++)
                {
                    var v = Srgb.Lin8(rgba[i + c]) * al + bl[c] * (1 - al);
                    o[i + c] = (byte)MathX.RoundHalfUpToInt(255 * Srgb.Enc(MathX.Sat(v)));
                }
            }
            o[i + 3] = 255;
        }
        return o;
    }
}
