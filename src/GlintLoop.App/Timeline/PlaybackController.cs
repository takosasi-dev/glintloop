// 再生の制御(FR-60): 再生を始めた時刻からの経過時間で、表示するフレーム k を TimeModel.FrameAt で決めて FrameCursor に入れる。描画は知らない(キャンバスが Frames.Changed で描く)。
// 時計とタイマーは差し込み(IClock・ITimerFactory)なので、テストは偽の時計で経過時間を進める。描画が遅れても経過時間の式で決めるので、間のフレームは飛ぶ(遅れない)。
// タイマーは 1 回だけ鳴るものを、次のフレームの開始時刻(または 1 周期の終わり)に合わせて掛け直す。「1 回」は P に達したら最後のフレームのまま止める。
using GlintLoop.App.Playback;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Timelines;

public sealed class PlaybackController : IDisposable
{
    private readonly FrameCursor _frames;
    private readonly IClock _clock;
    private readonly Func<TimeSettings?> _time;
    private readonly IOneShotTimer _timer;
    private DateTimeOffset _startedAt;
    private bool _ticking;
    private bool _disposed;

    /// <param name="frames">表示するフレームの位置(Set と SetPlaying だけを呼ぶ)。</param>
    /// <param name="time">いまの文書の時間の設定(文書が無ければ null)。</param>
    public PlaybackController(FrameCursor frames, IClock clock, ITimerFactory timers, Func<TimeSettings?> time)
    {
        _frames = frames;
        _clock = clock;
        _time = time;
        _timer = timers.Create();
        _timer.Elapsed += Tick;
        _frames.Changed += OnFramesChanged;
    }

    /// <summary>再生中か(FrameCursor.IsPlaying と同じ)。</summary>
    public bool IsPlaying => _frames.IsPlaying;

    /// <summary>再生を始めてからの経過時間(ms。再生中でなければ 0)。</summary>
    public long ElapsedMs => IsPlaying ? ElapsedSince(_startedAt) : 0;

    /// <summary>
    /// 再生を始める(FR-60)。経過時間 0 から数えるので、表示はフレーム 0 から始まる(「1 回」で終わったあとも先頭から)。
    /// 文書が無いときは何もしない。再生中なら何もしない。
    /// </summary>
    public void Play()
    {
        if (_disposed || IsPlaying || _time() is null) return;
        _startedAt = _clock.UtcNow;
        _frames.SetPlaying(true);
        Tick();
    }

    /// <summary>止める。表示しているフレームはそのまま。</summary>
    public void Stop()
    {
        _timer.Stop();
        _frames.SetPlaying(false);
    }

    /// <summary>再生と停止を切り替える(Space と再生・停止ボタン)。</summary>
    public void Toggle()
    {
        if (IsPlaying) Stop();
        else Play();
    }

    /// <summary>時間の設定が変わったとき(再生中なら、新しい設定でいまのフレームとタイマーを決め直す)。</summary>
    public void TimeChanged()
    {
        if (IsPlaying) Tick();
    }

    /// <summary>経過時間からいまのフレームを決めて入れ、次のフレームの開始時刻にタイマーを掛け直す。「1 回」で P に達したら止める。</summary>
    public void Tick()
    {
        if (_disposed || !IsPlaying) return;
        var time = _time();
        if (time is null)
        {
            Stop();
            return;
        }
        var elapsed = ElapsedSince(_startedAt);
        var k = TimeModel.FrameAt(elapsed, time);
        _ticking = true;
        try
        {
            _frames.Set(k);
        }
        finally
        {
            _ticking = false;
        }
        if (!IsPlaying) return;
        if (TimeModel.PlaybackEnded(elapsed, time))
        {
            Stop();
            return;
        }
        // 入れたあとの時刻から測る(表示の受け手が時間を使っても、次のフレームを遅らせない。過ぎていればすぐに鳴らして飛ばす)
        var wait = Math.Max(1, NextChangeMs(elapsed, time) - ElapsedSince(_startedAt));
        _timer.Start(TimeSpan.FromMilliseconds(wait));
    }

    /// <summary>
    /// elapsed のあとで表示が変わる最初の経過時間(ms)。次のフレームの開始時刻 S_(k+1)、最後のフレームなら 1 周期の終わり P。
    /// 1 周期が 0 ms 以下(あり得ないが)や、同じ時刻になる計算のときは 1 ms 先にする。
    /// </summary>
    public static long NextChangeMs(long elapsed, TimeSettings time)
    {
        var n = TimeModel.FrameCount(time);
        var p = TimeModel.PeriodMs(time);
        if (p <= 0) return elapsed + 1;
        var cycleStart = elapsed - elapsed % p;
        var r = elapsed - cycleStart;
        var k = TimeModel.FrameAt(r, time with { Loop = LoopMode.Infinite });
        var next = k + 1 < n ? TimeModel.StartMs(k + 1, time.Fps) : p;
        return Math.Max(elapsed + 1, cycleStart + next);
    }

    private long ElapsedSince(DateTimeOffset start)
    {
        var ticks = (_clock.UtcNow - start).Ticks;
        return ticks <= 0 ? 0 : ticks / TimeSpan.TicksPerMillisecond;
    }

    // ほかの手で止められた(文書の入れ替えで AppShell が Reset した、スクラブで止めた)ら、タイマーを外す
    private void OnFramesChanged()
    {
        if (_ticking) return;
        if (!IsPlaying) _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frames.Changed -= OnFramesChanged;
        _timer.Elapsed -= Tick;
        _timer.Dispose();
    }
}
