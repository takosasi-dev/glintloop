// デバイスの消失からの復帰の手順(FR-93・E-39)。検出 → 書き出しの後始末を待つ → 同じアダプター(LUID)で作り直す → 失敗なら 1000 ms 後にもう一度 → それも失敗なら WARP → だめならクラッシュの入口(FR-104)。
// 手順は UI スレッドの上で進め、待ちはタイマー(偽の時計で進められる)と Task の続きだけにして、UI スレッドも描画のスレッドも止めない。デバイスの操作は RenderService が専用スレッドで行う(INV-5)。
// ログは device_lost・warp_fallback・device_recreated の順(§9.11)。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;

namespace GlintLoop.Render;

public sealed class DeviceRecovery : IDisposable
{
    /// <summary>1 回目の作り直しが失敗してから 2 回目までの待ち(FR-93)。</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(1000);

    private readonly IClock _clock;
    private readonly ILogSink _log;
    private readonly Action<Action> _post;
    private readonly IOneShotTimer _timer;
    private readonly List<Func<Task>> _before = [];
    private RenderService? _service;
    private GpuAdapterRequest _request;
    private DateTimeOffset _lostAt;
    private Action<Exception>? _fatalHandler;
    private Exception? _pendingFatal;
    private bool _fatalRaised;
    private bool _disposed;

    /// <summary>UI スレッドで作る。post は UI スレッドへ回す関数(製品は Dispatcher.BeginInvoke)。</summary>
    public DeviceRecovery(IClock clock, ITimerFactory timers, ILogSink log, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(post);
        _clock = clock;
        _log = log;
        _post = post;
        _timer = timers.Create();
        _timer.Elapsed += OnRetryTimer;
    }

    /// <summary>2 回目の作り直しを待っている間か(テストの待ち合わせ用)。</summary>
    public bool IsWaitingForRetry => _timer.IsRunning;

    /// <summary>デバイスを作り直して、待たせていた依頼を再開したとき(UI スレッド)。受け手は描画をやり直す(FR-93)。</summary>
    public event Action? Recovered;

    /// <summary>消失から WARP に切り替えたとき(UI スレッド。Recovered のあと)。</summary>
    public event Action? SwitchedToSoftware;

    /// <summary>
    /// 処理されない例外として扱う入口(FR-104。製品は CrashHandler につなぐ)。UI スレッドで 1 回だけ呼ぶ。
    /// 入れる前に起きた失敗(起動時に WARP も作れないなど)は、入れた時点で渡す。
    /// </summary>
    public Action<Exception>? FatalHandler
    {
        get => _fatalHandler;
        set
        {
            _fatalHandler = value;
            if (value is not null && _pendingFatal is { } pending)
            {
                _pendingFatal = null;
                value(pending);
            }
        }
    }

    /// <summary>作り直しの前に済ませておく後始末を足す(実行中の書き出しを止めて .tmp を消すなど。E-39)。UI スレッドで呼ばれ、返した Task が終わるまで作り直さない。</summary>
    public void RegisterBeforeRecreate(Func<Task> before)
    {
        ArgumentNullException.ThrowIfNull(before);
        _before.Add(before);
    }

    /// <summary>service の消失を見張る。</summary>
    public void Attach(RenderService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        service.SetLostHandler(() => _post(OnLost));
    }

    /// <summary>処理されない例外として扱う(UI スレッド)。何度呼んでも、入口へ渡すのは最初の 1 回だけ。</summary>
    public void RaiseFatal(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_fatalRaised || _disposed) return;
        _fatalRaised = true;
        if (_fatalHandler is { } handler) handler(e);
        else _pendingFatal = e;
    }

    private void OnLost()
    {
        if (_disposed || _service is not { } service) return;
        _log.Write(LogEvents.DeviceLost());
        _lostAt = _clock.UtcNow;
        _request = service.SameAdapterRequest;
        var before = Task.WhenAll(_before.Select(b => b()));
        Then(before, _ => Attempt(first: true));
    }

    private void OnRetryTimer() => Attempt(first: false);

    /// <summary>task が終わったら UI スレッドで next を動かす(ここでは待たない)。</summary>
    private void Then<T>(T task, Action<T> next) where T : Task =>
        task.ContinueWith(_ => _post(() =>
        {
            if (!_disposed) next(task);
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private void Attempt(bool first)
    {
        if (_disposed || _service is not { } service) return;
        Then(service.RecreateAsync(_request), t =>
        {
            if (t.Result is not { } failure)
            {
                Complete(service, switched: false);
            }
            else if (first)
            {
                _timer.Start(RetryDelay);
            }
            else if (_request.Kind == GpuAdapterKind.Warp)
            {
                GiveUp(service, failure);
            }
            else
            {
                SwitchToWarp(service);
            }
        });
    }

    private void SwitchToWarp(RenderService service) => Then(service.RecreateAsync(GpuAdapterRequest.Warp), t =>
    {
        if (t.Result is { } failure) GiveUp(service, failure);
        else Complete(service, switched: true);
    });

    private void Complete(RenderService service, bool switched)
    {
        if (switched) _log.Write(LogEvents.WarpFallback(LogWarpReason.RecreateFailed));
        var ms = (long)Math.Max(0, Math.Round((_clock.UtcNow - _lostAt).TotalMilliseconds));
        _log.Write(LogEvents.DeviceRecreated(ms, service.IsWarp ? LogAdapter.Warp : LogAdapter.Hardware));
        _ = service.ResumeAsync();
        Recovered?.Invoke();
        if (switched) SwitchedToSoftware?.Invoke();
    }

    private void GiveUp(RenderService service, Exception failure)
    {
        _ = service.AbandonAsync();
        RaiseFatal(failure);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Elapsed -= OnRetryTimer;
        _timer.Dispose();
    }
}
