// 書き出しの中身の本物(IExportPipeline): 文書から計画を解決し(OutputPlan)、形式のエンコーダーと描画のフレームの列で ExportJob を回して、選んだ段の結果を output に書く。
// 上限なしは 1 回の符号化なので output へ直接書く。上限ありは試行をメモリに書き(上限で打ち切られる)、選んだ 1 つだけを output へ写す。
// ログの export_start・export_cancel・export_fail を書き、export_done は呼び手がリネームしたあとの Placed で、返したあとの取り消しの export_cancel は Abandoned で書く。Run は ExportRun.Scheduler のスレッドで呼ばれる。測定の記録(perf)には export_begin・export_end を書く。
using System.Diagnostics;
using System.IO;
using GlintLoop.Codec.Export;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Render;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Export;

public sealed class RenderExportPipeline(DocumentRenderer renderer, ISourceImages sources, IEffectCatalog catalog, ILogSink log, IReadOnlyList<IExportEncoder>? encoders = null,
    PerfRecorder? perf = null) : IExportPipeline
{
    private static readonly IReadOnlyList<IExportEncoder> Encoders =
        [new PngExportEncoder(), new JpegExportEncoder(), new GifExportEncoder(), new WebpExportEncoder(), new ApngEncoder()];

    public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(run);
        var plan = Plan(job);
        var r0 = plan.R0;
        var format = LogFormat(r0.Format);
        var limited = plan.LimitBytes is not null;
        log.Write(LogEvents.ExportStart(format, r0.Width, r0.Height, r0.FrameCount));
        var watch = Stopwatch.StartNew();
        // export_end は完成・取り消し・失敗のどれか 1 回だけ書く(§6.8)
        var begin = perf?.ExportBegin() ?? -1;
        var ended = 0;
        void End()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0) perf?.ExportEnd(begin);
        }
        var made = new List<ExportFrames>();
        IExportFrames Frames(ExportSettings settings)
        {
            var f = new ExportFrames(renderer, job.Document, settings);
            made.Add(f);
            return f;
        }
        try
        {
            var r = ExportJob.Run(plan, ExportJob.Pick(r0.Format, encoders ?? Encoders), Frames,
                () => limited ? new MemoryStream() : output,
                s =>
                {
                    if (limited) s.Dispose();
                },
                run);
            if (limited)
            {
                r.Output.Position = 0;
                r.Output.CopyTo(output);
                r.Output.Dispose();
            }
            var s = r.Settings;
            return new ExportPipelineResult(s.Width, s.Height, s.FrameCount, s.Fps, r.Steps, r.SizeFail, r.Condition)
            {
                // ms はリネームが成功した時刻まで(§9.11)。リネームは呼び手が行うので、そのあとで呼んでもらう
                Placed = () =>
                {
                    log.Write(LogEvents.ExportDone(watch.ElapsedMilliseconds, r.Bytes, s.FrameCount, format, r.Step, Math.Max(1, r.Tried.Count)));
                    End();
                },
                Abandoned = () =>
                {
                    WriteCancel();
                    End();
                },
                Failed = End,
                // 試して捨てた段は数えない(出力に効いたのは選んだ段だけ)
                ShrunkLayers = made.Where(f => f.Settings == s).SelectMany(f => f.ShrunkLayers).Distinct().Order().ToArray(),
            };
        }
        catch (OperationCanceledException)
        {
            WriteCancel();
            End();
            throw;
        }
        catch (Exception e) when (GpuErrors.IsDeviceLost(e))
        {
            // 自動でやり直すので、失敗に数えない(E-39)。測定の記録の区間はここで閉じる(やり直しは新しい区間)
            End();
            throw;
        }
        catch (Exception e)
        {
            log.Write(LogEvents.ExportFail(FailReason(e)));
            End();
            throw;
        }
    }

    // デバイスの消失で止めたものは、新しい編集と Esc の取り消しに数えない(§9.11)
    private void WriteCancel()
    {
        if (!renderer.IsDeviceLost) log.Write(LogEvents.ExportCancel());
    }

    /// <summary>export_fail の reason(E-31・E-32)。</summary>
    private static LogExportFailReason FailReason(Exception e) => GpuErrors.Unwrap(e) switch
    {
        ExportTooBigException or GifTooBigException => LogExportFailReason.TooBigGif,
        IOException or UnauthorizedAccessException => LogExportFailReason.WriteError,
        var inner when GpuErrors.IsGpuError(inner) => LogExportFailReason.GpuError,
        _ => LogExportFailReason.EncodeError,
    };

    public ExportCheck Check(ExportRequest job, ExportWritten written)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(written);
        var plan = Plan(job);
        // 制約は、はしごで選んだ段の値(キャッシュの json の値)で見る
        var settings = plan.R0 with { Width = written.Width, Height = written.Height, FrameCount = written.Frames, Fps = (int)Math.Round(written.FpsEff) };
        var layers = renderer.Layout(job.Document, Math.Max(written.Width, written.Height)).Layers;
        return new ExportCheck(plan.LimitBytes, LimitPresets.Condition(plan.Preset, settings))
        {
            ShrunkLayers = Enumerable.Range(0, layers.Count).Where(i => layers[i].Shrink.N >= 1).ToArray(),
        };
    }

    private ExportPlan Plan(ExportRequest job) => OutputPlan.Resolve(job.Document, catalog, ExportFrames.SourceSize(sources), job.FrameK);

    private static LogExportFormat LogFormat(string format) => format switch
    {
        ExportFormat.Png => LogExportFormat.Png,
        ExportFormat.Jpeg => LogExportFormat.Jpeg,
        ExportFormat.Gif => LogExportFormat.Gif,
        ExportFormat.Webp => LogExportFormat.Webp,
        _ => LogExportFormat.Apng,
    };
}
