// PNG の書き出し(FR-68): 8 ビット RGBA(色の種類 6)、インターレースなしの 1 枚。チャンクは IHDR・IDAT・IEND だけ(INV-12)。
// 書くフレームは設定の FrameK(スクラブバーの位置。§9.9 の 5)で、フレームの列の index 0 として受け取る。
// SkiaSharp を使わず自作にするのは、色空間を付けた画像で ICC が付く恐れを避けるため。
namespace GlintLoop.Core.Export;

public sealed class PngExportEncoder : IExportEncoder
{
    public string Format => ExportFormat.Png;

    public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ExportEncoding.CheckFrames(frames, settings, 1);
        var px = ExportEncoding.Frame(frames, 0, run.Cancel);
        var data = PngWriter.ImageData(px, settings.Width, settings.Height, 4);
        run.Cancel.ThrowIfCancellationRequested();
        return ExportEncoding.Run(output, limitBytes, s => Write(s, settings.Width, settings.Height, data));
    }

    internal static void Write(Stream s, int width, int height, byte[] imageData)
    {
        s.Write(PngWriter.Signature);
        PngWriter.WriteChunk(s, "IHDR", PngWriter.Ihdr(width, height, PngWriter.ColorRgba));
        PngWriter.WriteChunk(s, "IDAT", imageData);
        PngWriter.WriteChunk(s, "IEND", []);
    }
}
