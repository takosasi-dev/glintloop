// 時間のモデル(FR-58・FR-60・FR-61、§9.8 の 7 の遅延)の純粋な関数。フレーム数 N、位相 t、フレーム k の開始時刻 S_k、1 周期 P、経過時間から表示するフレーム。
// どれも整数の演算で求め、浮動小数の誤差で 0.5 ちょうどの値がずれないようにする。タイムライン・再生・書き出し・モーションのタブが共有する。
// 入力の直し(L の decimal の読みと round、停止時間の 100 ms 刻みへの round)は担当 timeline の TimeInput が行い、ここは直したあとの値を受ける。
namespace GlintLoop.Core.Session;

public static class TimeModel
{
    /// <summary>L10(ループの長さの 10 倍の整数)の下限・上限・既定(FR-58: 0.5〜10.0 秒、既定 2.0)。</summary>
    public const int MinLength10 = 5;
    public const int MaxLength10 = 100;
    public const int DefaultLength10 = 20;

    /// <summary>既定の fps(FR-58)。</summary>
    public const int DefaultFps = 20;

    /// <summary>停止時間の下限・上限・刻み(FR-61: 0〜5000 ms、100 ms 刻み)。</summary>
    public const long MinHoldMs = 0;
    public const long MaxHoldMs = 5000;
    public const long HoldStepMs = 100;

    /// <summary>N の下限と上限(FR-58: 3 以上 500 以下)。</summary>
    public const int MinFrames = 3;
    public const int MaxFrames = 500;

    /// <summary>fps の選択肢 11 通り(FR-58。DocumentJson.FpsChoices と同じもの)。</summary>
    public static IReadOnlyList<int> FpsChoices => DocumentJson.FpsChoices;

    /// <summary>フレーム数 N=(L10×F+5)÷10(整数の除算。FR-58)。</summary>
    public static int FrameCount(int length10, int fps) => (length10 * fps + 5) / 10;

    /// <summary>時間の設定のフレーム数(モーションの有無は見ない。有無を見るのは MotionInfo.FrameCount)。</summary>
    public static int FrameCount(TimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return FrameCount(time.Length10, time.Fps);
    }

    /// <summary>位相 t=k÷N(FR-58。N が 0 以下なら 0)。</summary>
    public static double Phase(int k, int n) => n > 0 ? (double)k / n : 0.0;

    /// <summary>フレーム k の開始時刻 S_k=10×round(100×k÷F) ms(整数では 10×((200×k+F)÷(2×F))。FR-60・AC-35 の (3))。</summary>
    public static long StartMs(int k, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        return 10L * ((200L * k + fps) / (2L * fps));
    }

    /// <summary>書き出しの全体の長さ S_N(停止時間を含まない)。</summary>
    public static long TotalMs(TimeSettings time) => StartMs(FrameCount(time), time.Fps);

    /// <summary>1 周期 P=S_N+停止時間(ms。FR-60)。</summary>
    public static long PeriodMs(TimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return TotalMs(time) + time.HoldMs;
    }

    /// <summary>
    /// フレーム k(0〜N−1)の表示の長さ(ms)。S_(k+1)−S_k に、最後のフレームだけ停止時間を足したもの。GIF・APNG の遅延 d_k の 10 倍と同じ(§9.8 の 7)。
    /// </summary>
    public static long DurationMs(int k, TimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var n = FrameCount(time);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(k, n);
        var d = StartMs(k + 1, time.Fps) - StartMs(k, time.Fps);
        return k == n - 1 ? d + time.HoldMs : d;
    }

    /// <summary>
    /// 再生を始めてからの経過 elapsedMs で表示するフレーム(FR-60)。無限は r=経過 mod P として S_k≤r を満たす最大の k(N−1 まで)。
    /// 1 回は、経過が P 以上なら最後のフレーム(N−1)のまま(終わったかは PlaybackEnded)。経過が負なら 0。
    /// </summary>
    public static int FrameAt(long elapsedMs, TimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var n = FrameCount(time);
        if (n <= 1 || elapsedMs <= 0) return 0;
        var p = PeriodMs(time);
        if (time.Loop == LoopMode.Once && elapsedMs >= p) return n - 1;
        var r = p > 0 ? elapsedMs % p : 0;
        // S_k ≤ r の最大の k。S_k=10×floor((200k+F)÷(2F)) は k について単調なので、近い k から直す
        var f = time.Fps;
        var k = (int)Math.Min(n - 1, r * f / 1000);
        while (k + 1 <= n - 1 && StartMs(k + 1, f) <= r) k++;
        while (k > 0 && StartMs(k, f) > r) k--;
        return k;
    }

    /// <summary>ループが「1 回」で、経過が 1 周期 P に達したか(FR-60: そこで再生を終える)。無限なら常に false。</summary>
    public static bool PlaybackEnded(long elapsedMs, TimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return time.Loop == LoopMode.Once && elapsedMs >= PeriodMs(time);
    }
}
