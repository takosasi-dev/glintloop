// 時刻とタイマーの差し込み口(仕様書 §11 Phase 3 の前書きの (d))。製品は実時計、テストは偽の時計を渡す。
// タイマーは 1 回だけ鳴るもの(設定の 500 ms 後の保存・通知バーの 5 秒など)。鳴るスレッドは実装が決める(製品は UI スレッド)。
namespace GlintLoop.Core.Platform;

/// <summary>現在の時刻と、ローカル日付を決めるタイムゾーン。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>ログのファイル名の日付(ローカル日付。FR-91)を決めるタイムゾーン。</summary>
    TimeZoneInfo LocalZone { get; }

    /// <summary>経過時間を測るための単調な時刻(差だけに意味がある)。製品は Stopwatch、既定は UtcNow から求める(偽の時計はこれで進む)。</summary>
    TimeSpan Monotonic => UtcNow - DateTimeOffset.UnixEpoch;
}

/// <summary>実際の時計。</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeZoneInfo LocalZone => TimeZoneInfo.Local;

    public TimeSpan Monotonic => System.Diagnostics.Stopwatch.GetElapsedTime(0);
}

/// <summary>1 回だけ鳴るタイマー。Start をもう一度呼ぶと、そこから測り直す(最後の変更から 500 ms の保存に使う)。</summary>
public interface IOneShotTimer : IDisposable
{
    bool IsRunning { get; }

    void Start(TimeSpan due);

    void Stop();

    event Action? Elapsed;
}

/// <summary>タイマーを作る口。</summary>
public interface ITimerFactory
{
    IOneShotTimer Create();
}
