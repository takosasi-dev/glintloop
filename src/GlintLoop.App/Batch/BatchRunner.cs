// バッチの実行の中身(FR-87): 画像を 1 個ずつ読み(失敗は import.*)、1 枚だけの土台の文書にして、通常の書き出しと同じ中身(IExportPipeline)で出力先の .tmp に書いてリネームする(キャッシュは使わない)。
// 1 個が失敗しても残りを続け、失敗の理由は §9.12 の文言にする。取り消しはフレームの切れ目で止まり(.tmp は AtomicFile が消す)、途中の 1 個は数えず、失敗にも載せない。
// デバイスの消失(E-39)ではその 1 個を失敗にせず、.tmp を消してから作り直しを待ち、同じ 1 個をやり直す(あきらめたら実行を止める)。Run は BelowNormal の専用スレッド(ExportScheduler)で呼ぶ。進みは report に渡す。
using System.IO;
using System.Globalization;
using GlintLoop.App.Text;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Batch;

/// <summary>失敗の一覧の 1 行(ファイルの名前と理由の文)。</summary>
public sealed record BatchFailure(string Name, string Reason);

/// <summary>進みの 1 回。Done は終えた個数(失敗も数える)、Current は処理中の 1 個の名前(無ければ null)、Failure はこの回に増えた失敗。</summary>
public sealed record BatchStep(int Done, string? Current, BatchFailure? Failure);

/// <summary>1 回の実行の入力。Document と FrameK は「実行」を押した時点の値(静止形式で描くスクラブバーの位置)。</summary>
public sealed record BatchJob(IReadOnlyList<string> Inputs, string Folder, SessionDocument? Document, int FrameK);

/// <summary>device は描画の窓口(消失を見張る。無ければ null を返す。createPipeline のあとに呼ぶ)。</summary>
public sealed class BatchRunner(ImageImporter importer, IEffectCatalog catalog, IFileSystem fileSystem, ILogSink log, Func<ISourceImages, IExportPipeline> createPipeline, Func<RenderService?>? device = null)
{
    /// <summary>1 個の 1 回の処理。Stop は消失で止める合図(利用者の取り消しもつなぐ)、Ended は .tmp の後始末まで済んだ合図。</summary>
    private sealed class Attempt(CancellationToken cancel)
    {
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>始める時点でデバイスが失われていた(何も書かずにやり直す)。</summary>
    private sealed class LostBeforeStart : Exception;

    private readonly BatchSourceImages _sources = new();
    private readonly object _gate = new();
    private IExportPipeline? _pipeline;
    private RenderService? _service;
    private Attempt? _attempt;

    public void Run(BatchJob job, TaskScheduler scheduler, int parallelism, Action<BatchStep> report, CancellationToken cancel)
    {
        var inputs = new HashSet<string>(job.Inputs, StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var path in job.Inputs)
        {
            if (cancel.IsCancellationRequested) return;
            var name = Path.GetFileName(path);
            report(new BatchStep(done, name, null));
            string? reason;
            while (true)
            {
                var attempt = Begin(cancel);
                try
                {
                    reason = One(job, path, new ExportRun(scheduler, parallelism, attempt.Stop.Token), inputs, written);
                    break;
                }
                catch (Exception) when (cancel.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (attempt.Stop.IsCancellationRequested || IsLossToRetry(e))
                {
                    // 消失。.tmp は消えているので、作り直しを待ってやり直す(E-39)
                }
                finally
                {
                    End(attempt);
                }
                if (!WaitForDevice(cancel)) return;
            }
            done++;
            report(new BatchStep(done, null, reason is null ? null : new BatchFailure(name, reason)));
        }
    }

    /// <summary>
    /// デバイスが失われた(UI スレッド)。処理中の 1 個を止める。返した Task が終わると、その 1 個の .tmp の後始末が済んでいる(作り直しの前に待つ。E-39)。
    /// 次の 1 回は始める前に消失を確かめて待つので、ここで止めるのは処理中のものだけでよい。
    /// </summary>
    public Task SuspendForDeviceLoss()
    {
        lock (_gate)
        {
            if (_attempt is null) return Task.CompletedTask;
            _attempt.Stop.Cancel();
            return _attempt.Ended.Task;
        }
    }

    private Attempt Begin(CancellationToken cancel)
    {
        var attempt = new Attempt(cancel);
        lock (_gate) _attempt = attempt;
        return attempt;
    }

    private void End(Attempt attempt)
    {
        lock (_gate)
        {
            _attempt = null;
            attempt.Stop.Dispose();
        }
        attempt.Ended.TrySetResult();
    }

    // 登録(Begin)のあとで起きた消失は SuspendForDeviceLoss が End まで作り直しを待たせるので、処理の中で見る IsDeviceLost は真のまま。偽なら窓口が知らない例外で、やり直しても直らない
    private bool IsLossToRetry(Exception e) => e is LostBeforeStart || (GpuErrors.IsDeviceLost(e) && _service is { IsDeviceLost: true });

    /// <summary>デバイスが使えるようになるまで待つ。あきらめたとき(FR-93)と取り消しでは false。</summary>
    private bool WaitForDevice(CancellationToken cancel)
    {
        if (_service is not { } service) return true;
        try
        {
            service.DeviceSettled.Wait(cancel);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        return !service.IsDeviceLost;
    }

    /// <summary>1 個を処理する。失敗の理由の文を返す(無ければ null)。取り消しと消失は例外で返す。</summary>
    private string? One(BatchJob job, string path, ExportRun run, HashSet<string> inputs, HashSet<string> written)
    {
        var image = importer.LoadSingle(path, out var failure);
        if (image is null) return failure is { } f ? NoticeTexts.Format(f.Notice) : NoticeTexts.Format(NoticeKeys.ImportDecodeError);

        _sources.Set(image);
        var doc = BatchDocument.Build(job.Document, image, LayerNames.FromPath(path));
        var format = doc.Output.Format;
        var inPipeline = false;
        ExportPipelineResult? result = null;
        try
        {
            var pipeline = _pipeline ??= createPipeline(_sources);
            _service ??= device?.Invoke();
            if (_service is { IsDeviceLost: true }) throw new LostBeforeStart();
            var n = MotionInfo.FrameCount(doc, catalog);
            var k = ExportFormat.IsAnimated(format) ? 0 : Math.Clamp(job.FrameK, 0, n - 1);
            var output = BatchNames.Pick(job.Folder, path, format, p => written.Contains(p) || inputs.Contains(p) || fileSystem.Exists(p));
            long bytes = 0;
            AtomicFile.Write(output, s =>
            {
                inPipeline = true;
                result = pipeline.Run(new ExportRequest(doc, k, n), s, run);
                inPipeline = false;
                result.ThrowIfCancelled(run.Cancel);
                s.Flush();
                bytes = s.Length;
            }, fileSystem);
            result!.Placed?.Invoke();
            written.Add(output);
            if (result.SizeFail)
            {
                var limit = OutputPlan.Limit(doc.Output.Limit).Bytes ?? 0;
                return NoticeTexts.Format(NoticeKeys.ExportSizeFail, ("limit", limit.ToString(CultureInfo.InvariantCulture)), ("size", bytes.ToString(CultureInfo.InvariantCulture)));
            }
            return null;
        }
        catch (Exception e) when (!run.Cancel.IsCancellationRequested && !IsLossToRetry(e))
        {
            var cause = e is AggregateException a ? a.Flatten().InnerException ?? e : e;
            var writeError = cause is IOException or UnauthorizedAccessException;
            // 中身の例外は中身がログに書く。中身の外(.tmp を作る・リネーム)の失敗はここで書く(E-34)
            if (writeError && !inPipeline) log.Write(LogEvents.ExportFail(LogExportFailReason.WriteError));
            if (!inPipeline) result?.Failed?.Invoke();
            return cause switch
            {
                ExportTooBigException big => NoticeTexts.Format(NoticeKeys.ExportGifTooBig, ("fmt", big.FormatName)),
                GifTooBigException => NoticeTexts.Format(NoticeKeys.ExportGifTooBig, ("fmt", OutputPlan.TooBigFormatName(format))),
                _ when writeError => NoticeTexts.Format(NoticeKeys.SaveFailed),
                _ => NoticeTexts.Format(NoticeKeys.ExportFailed),
            };
        }
        finally
        {
            _sources.Clear();
        }
    }
}
