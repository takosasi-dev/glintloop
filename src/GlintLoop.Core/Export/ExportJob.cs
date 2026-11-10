// 1 回の書き出しの取りまとめ(§9.9 の 3・4、FR-75・FR-76): はしごを作り、探索の順に試行を符号化し、収まった試行の結果をそのまま書き出しの結果にする。
// 試行は上限で打ち切り(打ち切った結果は捨てる)、同じ段は 2 回符号化しない。size_fail のときだけ最後の段を打ち切りなしで 1 回作り直す。
// 書き先は呼ぶ側が作る(createOutput)。使わなかった書き先は discard に返す。RunAsync は ExportRun.Scheduler(BelowNormal の専用スレッド。Q-F)の上で動かす。
namespace GlintLoop.Core.Export;

/// <summary>
/// 書き出しの結果。Output は結果を書いた書き先(createOutput が返したもの。閉じるのは呼ぶ側)、Step は使った段の番号(r_Step)、StepCount は R。
/// Steps は調整の内容の文字(無ければ null)、SizeFail は上限に収まらなかったこと(FR-76)、Condition は制約の警告の {内容}(無ければ null)、Tried は試行した段の番号の順。
/// </summary>
public sealed record ExportJobResult(
    ExportSettings Settings,
    Stream Output,
    long Bytes,
    int Step,
    int StepCount,
    string? Steps,
    bool SizeFail,
    string? Condition,
    IReadOnlyList<int> Tried);

public static class ExportJob
{
    /// <summary>
    /// plan の書き出しを行う。frames は段の設定からフレームの列を作る関数(段ごとに大きさや fps が変わる)。上限が無ければ r_0 をそのまま書く。
    /// GIF・APNG の上限(§9.8 の 10)の外の段は、符号化せずに「収まらない」とする。使う段がその外なら ExportTooBigException。
    /// </summary>
    public static ExportJobResult Run(ExportPlan plan, IExportEncoder encoder, Func<ExportSettings, IExportFrames> frames,
        Func<Stream> createOutput, Action<Stream> discard, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(createOutput);
        ArgumentNullException.ThrowIfNull(discard);
        ArgumentNullException.ThrowIfNull(run);
        if (encoder.Format != plan.R0.Format) throw new ArgumentException($"エンコーダーの形式 {encoder.Format} が設定の {plan.R0.Format} と違う", nameof(encoder));

        var steps = new List<ExportSettings> { plan.R0 };
        if (plan.LimitBytes is not null) steps.AddRange(Ladder.Build(plan.R0, plan.Composite).Select(s => s.Settings));
        var last = steps.Count - 1;
        var kept = new Dictionary<int, (Stream Stream, EncodeResult Result)>();
        var open = new List<Stream>();

        (Stream, EncodeResult) Encode(int i, long? limit)
        {
            run.Cancel.ThrowIfCancellationRequested();
            var s = steps[i];
            var output = createOutput();
            open.Add(output);
            var r = encoder.Encode(new ProgressFrames(frames(s), run.Progress, i, last), s, output, limit, run);
            return (output, r);
        }

        void Discard(Stream s)
        {
            open.Remove(s);
            discard(s);
        }

        try
        {
            int chosen;
            LimitSearchResult search;
            (Stream Stream, EncodeResult Result) result;
            if (plan.LimitBytes is not long limit)
            {
                if (!OutputPlan.IsWithinLimits(plan.R0)) throw new ExportTooBigException(plan.R0);
                search = new LimitSearchResult(0, false, []);
                chosen = 0;
                result = Encode(0, null);
            }
            else
            {
                search = LimitSearch.Run(last, i =>
                {
                    if (!OutputPlan.IsWithinLimits(steps[i])) return false;
                    var (s, r) = Encode(i, limit);
                    if (r.Overflowed || r.Bytes > limit)
                    {
                        Discard(s);
                        return false;
                    }
                    kept[i] = (s, r);
                    return true;
                });
                chosen = search.Chosen;
                if (search.SizeFail)
                {
                    // 最後の段を打ち切りなしで作り直す(試行の回数には数えない)
                    if (!OutputPlan.IsWithinLimits(steps[chosen])) throw new ExportTooBigException(steps[chosen]);
                    result = Encode(chosen, null);
                }
                else
                {
                    result = kept[chosen];
                }
            }

            foreach (var s in open.ToArray())
            {
                if (!ReferenceEquals(s, result.Stream)) Discard(s);
            }
            var settings = steps[chosen];
            return new ExportJobResult(
                settings,
                result.Stream,
                result.Result.Bytes,
                chosen,
                last,
                Ladder.Describe(plan.User, settings),
                search.SizeFail,
                LimitPresets.Condition(plan.Preset, settings),
                search.Tried);
        }
        catch
        {
            foreach (var s in open.ToArray()) Discard(s);
            throw;
        }
    }

    /// <summary>Run を run.Scheduler の上で動かす。</summary>
    public static Task<ExportJobResult> RunAsync(ExportPlan plan, IExportEncoder encoder, Func<ExportSettings, IExportFrames> frames,
        Func<Stream> createOutput, Action<Stream> discard, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Task.Factory.StartNew(() => Run(plan, encoder, frames, createOutput, discard, run),
            run.Cancel, TaskCreationOptions.DenyChildAttach, run.Scheduler);
    }

    /// <summary>形式の名前のエンコーダーを選ぶ(JPEG と WebP は Codec のものを渡す)。</summary>
    public static IExportEncoder Pick(string format, IReadOnlyList<IExportEncoder> encoders)
    {
        ArgumentNullException.ThrowIfNull(encoders);
        return encoders.FirstOrDefault(e => e.Format == format) ?? throw new ArgumentException("エンコーダーが無い形式: " + format, nameof(format));
    }

    /// <summary>取り出したフレームの番号から進みを知らせるフレームの列(GIF は 2 回の供給のそれぞれで 1〜N と進む)。</summary>
    private sealed class ProgressFrames(IExportFrames inner, IProgress<ExportProgress>? progress, int step, int stepCount) : IExportFrames
    {
        public int Width => inner.Width;

        public int Height => inner.Height;

        public int FrameCount => inner.FrameCount;

        public byte[] GetFrame(int index, CancellationToken ct)
        {
            var px = inner.GetFrame(index, ct);
            progress?.Report(new ExportProgress(index + 1, inner.FrameCount, step, stepCount));
            return px;
        }
    }
}
