// 自動の書き出し(FR-78・FR-79・§9.9 の 7 と 8): 編集が止まって 600 ms 経ったら、いまの文書と出力設定で書き出しをバックグラウンドで始め、結果をキャッシュに置く。
// 新しい編集(NotifyEdit)が入ったら取り消しの合図を立て(中身はフレームの切れ目で止まり、.tmp はキャッシュが消す)、その編集から 600 ms 後にやり直す。画像の載せ方の静止画(§9.10 の 2 の 1)は、コピーが要るときに RequestStill で同じ順番に並べる。
// 公開のメンバーは UI スレッドで使い、仕事は Scheduler(BelowNormal の専用スレッド。Q-F)で 1 本ずつ順に動かす。結果と進みは post で UI スレッドへ戻す。
using System.IO;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Render;

namespace GlintLoop.App.Export;

/// <summary>書き出しの完了 1 回の内容。Reused はキャッシュのファイルを使い回した(エンコーダーを呼ばなかった)とき。</summary>
public sealed record ExportCompletion(ExportCacheEntry Entry, ExportOutcome Outcome, bool Reused);

public sealed class AutoExport : IDisposable
{
    /// <summary>編集が止まってから書き出しを始めるまで(FR-78)。</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(600);

    private readonly EditorSession _session;
    private readonly IEffectCatalog _catalog;
    private readonly ExportCache _cache;
    private readonly IOneShotTimer _timer;
    private readonly Action<Action> _post;
    private readonly Func<int> _frameK;
    private CancellationTokenSource? _cts;
    private Task _chain = Task.CompletedTask;
    private int _generation;
    private int _pipelineCalls;
    private IExportPipeline? _pipeline;
    private ExportEnvironment? _environment;
    private CancellationTokenSource? _stillCts;
    private string? _stillRunningKey;
    private ExportCompletion? _still;
    private int _stillGeneration;
    private bool _suspended;
    private bool _disposed;

    /// <summary>
    /// post は UI スレッドへ回す関数(製品は Dispatcher.BeginInvoke)。frameK はいま表示しているフレーム k(静止形式で描く 1 枚。shell.Frames.K)。
    /// </summary>
    public AutoExport(EditorSession session, IEffectCatalog catalog, ExportCache cache, ITimerFactory timers, Action<Action> post, Func<int> frameK)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(frameK);
        _session = session;
        _catalog = catalog;
        _cache = cache;
        _post = post;
        _frameK = frameK;
        _timer = timers.Create();
        _timer.Elapsed += OnTimer;
    }

    /// <summary>書き出しの中身(本物は RenderExportPipeline)。null の間は書き出さない(状態は待機のまま)。入れ替えると、やり直しの 600 ms を数え始める。</summary>
    public IExportPipeline? Pipeline
    {
        get => _pipeline;
        set
        {
            _pipeline = value;
            NotifyEdit();
        }
    }

    /// <summary>キャッシュのキーの (4)〜(6)。描画のデバイスができたら入れる。null の間は書き出さない。</summary>
    public ExportEnvironment? Environment
    {
        get => _environment;
        set
        {
            _environment = value;
            NotifyEdit();
        }
    }

    /// <summary>仕事を動かす TaskScheduler(製品は ExportBandModule が入れる ExportScheduler。BelowNormal の専用スレッド。Q-F)。</summary>
    public TaskScheduler Scheduler { get; set; } = TaskScheduler.Default;

    /// <summary>エンコードの並列度(ExportRun.Parallelism。製品は ExportScheduler.DefaultParallelism)。</summary>
    public int Parallelism { get; set; } = Math.Max(1, System.Environment.ProcessorCount);

    public ExportState State { get; private set; } = ExportState.Idle;

    /// <summary>計算中の進み(計算中でなければ null)。</summary>
    public ExportProgress? Progress { get; private set; }

    /// <summary>完了した書き出し(完了のときだけ)。</summary>
    public ExportOutcome? Outcome { get; private set; }

    /// <summary>完了したキャッシュのファイル(完了のときだけ)。</summary>
    public ExportCacheEntry? Entry { get; private set; }

    /// <summary>計算中・完了の書き出しのキー。</summary>
    public string? Key { get; private set; }

    /// <summary>計算中・完了の書き出しの依頼。</summary>
    public ExportRequest? Job { get; private set; }

    /// <summary>いまの文書と出力設定の完成したファイルがあるか(実出力を出せるか)。</summary>
    public bool IsReady => State == ExportState.Done && Entry is not null;

    /// <summary>書き出しの中身(Pipeline.Run)を呼んだ回数(テスト用。キャッシュを使い回したときは数えない)。</summary>
    public int PipelineCalls => Volatile.Read(ref _pipelineCalls);

    /// <summary>失敗したときの例外(テストとログ用)。</summary>
    public Exception? Error { get; private set; }

    /// <summary>いま並んでいる仕事の全部が終わるまでの Task(テスト用。取り消した仕事の後始末を待つ)。</summary>
    public Task Idle => _chain;

    /// <summary>状態・進み・結果が変わったとき(UI スレッド)。</summary>
    public event Action? Changed;

    /// <summary>
    /// 書き出しが完了したとき(UI スレッド。完了 1 回につき 1 回、Changed のあと)。キャッシュを使い回した完了でも鳴る(FR-105 の「書き出しの完了」と、完成を待つコピー・保存の合図を兼ねる)。
    /// 取り消し・失敗では鳴らない。
    /// </summary>
    public event Action<ExportCompletion>? Completed;

    /// <summary>静止画(RequestStill)が完成したとき(UI スレッド)。Completed は鳴らさない(FR-105 の確認は、コピーでは行わない。AC-45 の (7))。</summary>
    public event Action<ExportCompletion>? StillCompleted;

    /// <summary>キャッシュのファイルの置き場の失敗(書き出しの中身の外の失敗)を export_fail で書く先。</summary>
    public ILogSink? Log { get; set; }

    /// <summary>
    /// 編集が入った(文書・出力設定・静止形式の k が変わった)。実行中の書き出しに取り消しの合図を立て、状態を待機にして、600 ms を数え直す。
    /// </summary>
    public void NotifyEdit()
    {
        if (_disposed) return;
        CancelRunning();
        _generation++;
        SetIdle();
        if (CanExport) _timer.Start(Delay);
        else _timer.Stop();
    }

    /// <summary>
    /// 完成の扱いや失敗を捨てて、いまの文書の書き出しをすぐやり直す(E-32 の「コピー」「保存」の押下と、E-36 の完成ファイルが消えたとき)。
    /// </summary>
    public void Retry()
    {
        if (_disposed) return;
        CancelRunning();
        _generation++;
        _timer.Stop();
        SetIdle();
        OnTimer();
    }

    /// <summary>
    /// いま完成しているファイルが、いまの文書のものか(INV-9)。編集の通知(NotifyEdit)は UI スレッドの次の仕事で届くので、キーで確かめる。
    /// </summary>
    public bool IsCurrent =>
        IsReady && Job is { } job && _session.Document is { } doc && _environment is { } env && ExportCacheKey.Of(doc, job.FrameK, env) == Key;

    private bool CanExport => _pipeline is not null && _environment is not null && _session.HasDocument && !_suspended;

    /// <summary>
    /// デバイスが失われた(E-39)。実行中の書き出しを止めて、やり直しは ResumeAfterDeviceLoss まで待つ。完成済みの結果はそのまま。
    /// 返した Task が終わると、止めた仕事の .tmp の後始末が済んでいる(作り直しの前に待つ)。
    /// </summary>
    public Task SuspendForDeviceLoss()
    {
        _suspended = true;
        _timer.Stop();
        if (State != ExportState.Done && State != ExportState.Failed)
        {
            CancelRunning();
            _generation++;
            SetIdle();
        }
        return _chain;
    }

    /// <summary>デバイスを作り直した。止めた書き出しと待っていた編集を、いつもの 600 ms のあとでやり直す。完了と失敗の状態はそのまま(失敗は自動ではやり直さない)。</summary>
    public void ResumeAfterDeviceLoss()
    {
        _suspended = false;
        if (State == ExportState.Idle) NotifyEdit();
    }

    private void CancelRunning()
    {
        CancelStill();
        if (_cts is null) return;
        _cts.Cancel();
        _cts = null;
    }

    // ================================================================ 画像の載せ方の静止画(§9.10 の 2 の 1)

    private static SessionDocument AsPng(SessionDocument doc) => doc with { Output = doc.Output with { Format = ExportFormat.Png } };

    private string? StillKey(int k, out SessionDocument? png)
    {
        png = null;
        if (_session.Document is not { } doc || _environment is not { } env) return null;
        png = AsPng(doc);
        return ExportCacheKey.Of(png, ClampK(doc, k), env);
    }

    private int ClampK(SessionDocument doc, int k) => Math.Clamp(k, 0, MotionInfo.FrameCount(doc, _catalog) - 1);

    /// <summary>いまの文書の PNG・フレーム k が完成していれば返す。文書の形式が PNG で同じ k なら、自動の書き出しの結果がそれにあたる。</summary>
    public ExportCompletion? StillFor(int k)
    {
        var key = StillKey(k, out _);
        if (key is null) return null;
        if (key == Key && Entry is { } entry && Outcome is { } outcome) return new ExportCompletion(entry, outcome, true);
        return _still is { } s && s.Entry.Key == key ? s : null;
    }

    /// <summary>いまの文書の PNG・フレーム k を、まだなら作り始める(すでに同じものを作っていれば何もしない)。</summary>
    public void RequestStill(int k)
    {
        if (_disposed || !CanExport) return;
        var key = StillKey(k, out var png);
        if (key is null || png is null || key == _stillRunningKey) return;
        CancelStill();
        var pipeline = _pipeline!;
        var job = new ExportRequest(png, ClampK(png, k), MotionInfo.FrameCount(png, _catalog));
        var gen = _stillGeneration;
        var cts = new CancellationTokenSource();
        _stillCts = cts;
        _stillRunningKey = key;
        var run = new ExportRun(Scheduler, Parallelism, cts.Token);
        var work = _chain.ContinueWith(_ => Execute(job, key, pipeline, run), CancellationToken.None, TaskContinuationOptions.None, Scheduler);
        _chain = work.ContinueWith(t =>
        {
            _post(() => FinishStill(gen, t, cts));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>キャッシュのファイルを読む仕事を、書き出しと同じ順番に並べる(例外は Task に入る)。</summary>
    public Task<T> Enqueue<T>(Func<T> work)
    {
        var task = _chain.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, Scheduler);
        _chain = task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>完成した静止画を忘れる(E-36: そのファイルが消えたとき)。</summary>
    public void InvalidateStill() => _still = null;

    private void CancelStill()
    {
        _stillGeneration++;
        _still = null;
        _stillRunningKey = null;
        if (_stillCts is null) return;
        _stillCts.Cancel();
        _stillCts = null;
    }

    private void FinishStill(int gen, Task<ExportCompletion> t, CancellationTokenSource cts)
    {
        if (ReferenceEquals(_stillCts, cts)) _stillCts = null;
        cts.Dispose();
        if (_disposed || gen != _stillGeneration) return;
        _stillRunningKey = null;
        if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException) return;
        if (t.IsFaulted)
        {
            // 自動の書き出しの失敗と同じに扱う(E-32。次の編集か「コピー」「保存」の押下で 1 回だけやり直す)
            State = ExportState.Failed;
            Error = t.Exception?.InnerException;
            Entry = null;
            Outcome = null;
            Changed?.Invoke();
            return;
        }
        _still = t.Result;
        StillCompleted?.Invoke(t.Result);
    }

    private void SetIdle()
    {
        var changed = State != ExportState.Idle || Outcome is not null || Progress is not null;
        State = ExportState.Idle;
        Progress = null;
        Outcome = null;
        Entry = null;
        Key = null;
        Job = null;
        Error = null;
        if (changed) Changed?.Invoke();
    }

    private void OnTimer()
    {
        if (_disposed || !CanExport || _session.Document is not { } doc) return;
        var pipeline = _pipeline!;
        var env = _environment!;
        var n = MotionInfo.FrameCount(doc, _catalog);
        var still = !ExportFormat.IsAnimated(doc.Output.Format);
        var k = still ? Math.Clamp(_frameK(), 0, n - 1) : 0;
        var job = new ExportRequest(doc, k, n);
        var key = ExportCacheKey.Of(doc, k, env);
        var gen = _generation;
        var cts = new CancellationTokenSource();
        _cts = cts;
        State = ExportState.Running;
        Key = key;
        Job = job;
        Progress = new ExportProgress(0, still ? 1 : n, 0, 0);
        Changed?.Invoke();

        var progress = new PostedProgress(p => _post(() =>
        {
            if (gen != _generation || State != ExportState.Running) return;
            Progress = p;
            Changed?.Invoke();
        }));
        var run = new ExportRun(Scheduler, Parallelism, cts.Token, progress);
        // 仕事は 1 本ずつ順に動かす(取り消した仕事の .tmp の後始末が済んでから次を始める。同じキーの .tmp を 2 本で書かない)
        var work = _chain.ContinueWith(_ => Execute(job, key, pipeline, run), CancellationToken.None, TaskContinuationOptions.None, Scheduler);
        _chain = work.ContinueWith(t =>
        {
            _post(() => Finish(gen, t, cts));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private ExportCompletion Execute(ExportRequest job, string key, IExportPipeline pipeline, ExportRun run)
    {
        run.Cancel.ThrowIfCancellationRequested();
        var format = job.Format;
        if (_cache.TryGet(key, format) is { } hit)
        {
            // 同じ設定の 2 回目は、エンコーダーを呼ばずにキャッシュのファイルを使う(AC-39 の (5))
            var m = hit.Meta;
            var check = pipeline.Check(job, new ExportWritten(m.Width, m.Height, m.Frames, m.FpsEff, m.Steps));
            var sizeFail = check.LimitBytes is long limit && m.Bytes > limit;
            return new ExportCompletion(hit, new ExportOutcome(hit.Path, format, m.Bytes, m.Width, m.Height, m.Frames, m.FpsEff, m.Steps, sizeFail, check.Condition) { ShrunkLayers = check.ShrunkLayers }, true);
        }
        Interlocked.Increment(ref _pipelineCalls);
        ExportPipelineResult? result = null;
        var inPipeline = false;
        ExportCacheEntry entry;
        try
        {
            entry = _cache.Write(key, format, s =>
            {
                inPipeline = true;
                result = pipeline.Run(job, s, run);
                inPipeline = false;
                result.ThrowIfCancelled(run.Cancel);
                return new ExportWritten(result.Width, result.Height, result.Frames, result.FpsEff, result.Steps);
            });
        }
        catch (Exception e) when (!inPipeline && e is IOException or UnauthorizedAccessException)
        {
            // 書き出しの中身の中の失敗は中身がログを書く。.tmp の作成・リネーム・json の失敗はここで書く(E-32)
            Log?.Write(LogEvents.ExportFail(LogExportFailReason.WriteError));
            result?.Failed?.Invoke();
            throw;
        }
        var r = result!;
        r.Placed?.Invoke();
        return new ExportCompletion(entry, new ExportOutcome(entry.Path, format, entry.Meta.Bytes, r.Width, r.Height, r.Frames, r.FpsEff, r.Steps, r.SizeFail, r.Condition) { ShrunkLayers = r.ShrunkLayers }, false);
    }

    private void Finish(int gen, Task<ExportCompletion> t, CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cts, cts)) _cts = null;
        cts.Dispose();
        if (_disposed || gen != _generation) return; // 取り消した書き出し(新しい編集が入った)
        if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException || (t.Exception is { } failure && GpuErrors.IsDeviceLost(failure)))
        {
            // デバイスの消失は失敗にせず、作り直しのあとでやり直す(E-39)
            SetIdle();
            return;
        }
        if (t.IsFaulted)
        {
            State = ExportState.Failed;
            Error = t.Exception?.InnerException;
            Progress = null;
            Changed?.Invoke();
            return;
        }
        var completion = t.Result;
        State = ExportState.Done;
        Entry = completion.Entry;
        Outcome = completion.Outcome;
        Progress = null;
        Changed?.Invoke();
        // Changed の受け手が同じ呼び出しの中で編集を入れたときは、もう完了ではないので鳴らさない
        if (gen == _generation && State == ExportState.Done) Completed?.Invoke(completion);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelRunning();
        _timer.Elapsed -= OnTimer;
        _timer.Dispose();
    }

    /// <summary>進みを受けたら post で UI スレッドへ回す(Progress&lt;T&gt; は SynchronizationContext を使うので使わない)。</summary>
    private sealed class PostedProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
