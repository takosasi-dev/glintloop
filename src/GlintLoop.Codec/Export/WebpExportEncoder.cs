// WebP の静止画の書き出し(FR-71・§9.9 の 5): SkiaSharp の WebP エンコーダーで、品質 1〜100(非可逆)かロスレスで 1 枚を書く。アニメーション WebP は書かない。
// 画素は straight のまま(Unpremul)渡し、色空間を付けないので ICCP を書かない。EXIF・XMP も書かない(INV-12)。
// ロスレスのときの品質は圧縮の手間の指定として、そのまま渡す。
using GlintLoop.Core.Export;
using SkiaSharp;

namespace GlintLoop.Codec.Export;

public sealed class WebpExportEncoder : IExportEncoder
{
    public string Format => ExportFormat.Webp;

    public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        ExportEncoding.CheckFrames(frames, settings, 1);
        var px = ExportEncoding.Frame(frames, 0, run.Cancel);
        var quality = Math.Clamp(settings.Quality, 1, 100);
        var compression = settings.Lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy;
        byte[] bytes;
        using (var bmp = SkiaExport.Bitmap(settings.Width, settings.Height, px, SKAlphaType.Unpremul))
        using (var pm = bmp.PeekPixels())
        using (var data = pm.Encode(new SKWebpEncoderOptions(compression, quality))
                          ?? throw new InvalidOperationException("WebP に符号化できない"))
        {
            bytes = data.ToArray();
        }
        run.Cancel.ThrowIfCancellationRequested();
        return ExportEncoding.Run(output, limitBytes, s => s.Write(bytes));
    }
}
