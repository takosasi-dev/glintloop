// GIF の書き出し(FR-70・FR-74・§9.8): 既存の GifEncoder を包み、数えるストリームで上限を超えた時点で打ち切る(§9.9 の 4 の 5)。
// フレームは 2 回供給する(集計と符号化。§9.8 の 1)。書き出しのフレームの列は、同じ番号で何度呼んでも同じ画素を返す(Q-I では描き直す)。
// 上限(§9.8 の 10)の外の設定は、フレームを読む前に ExportTooBigException で拒否する。
using GlintLoop.Core.Gif;

namespace GlintLoop.Core.Export;

public sealed class GifExportEncoder : IExportEncoder
{
    public string Format => ExportFormat.Gif;

    public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var n = settings.FrameCount;
        ExportEncoding.CheckFrames(frames, settings, n);
        if (!OutputPlan.IsWithinLimits(settings)) throw new ExportTooBigException(settings);
        var options = settings.ToGifOptions(run.Parallelism, run.Scheduler);
        var source = new FrameSource(frames, n, run.Cancel);
        return ExportEncoding.Run(output, limitBytes, s => GifEncoder.Encode(source, s, options, run.Cancel));
    }

    /// <summary>GifEncoder に渡す供給元(1 回目と 2 回目のどちらも、書き出しのフレームの列から取り出す)。</summary>
    private sealed class FrameSource(IExportFrames frames, int count, CancellationToken ct) : IGifFrameSource
    {
        public int Width => frames.Width;

        public int Height => frames.Height;

        public int FrameCount => count;

        public byte[] GetFrame(GifPass pass, int index) => frames.GetFrame(index, ct);
    }
}
