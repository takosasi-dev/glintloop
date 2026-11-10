// 再生の制御(PlaybackController)のテスト: 偽の時計で経過時間を進め、表示するフレームが FR-60 の式(AC-35 の (3) の例の値)になること、
// 「1 回」は P で止まり再生ボタンで先頭から始まること、描画に 3 フレーム分かかる偽の描画でも番号は経過時間の式のとおりで飛ばすこと、ほかの手で止められたらタイマーを外すこと。
// 画面を作らない(FrameCursor と偽の時計だけ)。画面とショートカットの配線は TimelineUiAc35Tests が調べる。
using GlintLoop.App.Playback;
using GlintLoop.App.Timelines;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Timelines;

public sealed class PlaybackAc35Tests
{
    private sealed class Rig : IDisposable
    {
        public Rig(TimeSettings time)
        {
            Time = time;
            Frames.SetFrameCount(TimeModel.FrameCount(time));
            Playback = new PlaybackController(Frames, Clock, Clock, () => Time);
        }

        public FakeClock Clock { get; } = new();

        public FrameCursor Frames { get; } = new();

        public TimeSettings? Time { get; set; }

        public PlaybackController Playback { get; }

        /// <summary>再生を始めてから ms の時点まで時計を進める。</summary>
        public void At(long ms) => Clock.Advance(TimeSpan.FromMilliseconds(ms - Playback.ElapsedMs));

        public void Dispose() => Playback.Dispose();
    }

    private static TimeSettings T(int l10, int fps, long hold = 0, LoopMode loop = LoopMode.Infinite) => new(l10, fps, loop, hold);

    private static void Expect(TimeSettings time, params (long Ms, int K)[] points)
    {
        using var rig = new Rig(time);
        rig.Playback.Play();
        Check.Equal(0, rig.Frames.K, "再生を始めたときのフレーム");
        Check.True(rig.Frames.IsPlaying, "再生中");
        foreach (var (ms, k) in points)
        {
            rig.At(ms);
            Check.Equal(k, rig.Frames.K, $"L10={time.Length10} F={time.Fps} 停止 {time.HoldMs}: 経過 {ms} ms のフレーム");
        }
    }

    [Test]
    public void AC35_3_FR60_偽の時計で表示するフレームが例の値になる()
    {
        Expect(T(20, 20), (1234, 24), (2000, 0), (3000, 20), (4990, 19));
        Expect(T(20, 20, 500), (1990, 39), (2100, 39), (2499, 39), (2500, 0));
        Expect(T(7, 5), (590, 2), (600, 3), (799, 3), (800, 0));
        Expect(T(5, 40), (29, 0), (30, 1), (49, 1), (50, 2), (79, 2), (80, 3));
    }

    [Test]
    public void AC35_3_FR60_各時点のフレームがFrameAtの式と一致しタイマーは次のフレームの開始で鳴る()
    {
        foreach (var t in new[] { T(20, 20), T(20, 20, 500), T(7, 5), T(5, 40), T(100, 50, 5000), T(5, 5, 100) })
        {
            using var rig = new Rig(t);
            var changes = new List<(long Ms, int K)>();
            rig.Frames.Changed += () => changes.Add((rig.Playback.ElapsedMs, rig.Frames.K));
            rig.Playback.Play();
            var p = TimeModel.PeriodMs(t);
            for (long ms = 0; ms <= 2 * p + 7; ms += 7)
            {
                rig.At(ms);
                Check.Equal(TimeModel.FrameAt(ms, t), rig.Frames.K, $"L10={t.Length10} F={t.Fps} 停止 {t.HoldMs}: 経過 {ms} ms");
            }
            // 表示が変わった時刻は、どれもフレームの開始時刻(周期の中の S_k)ちょうど
            foreach (var (ms, k) in changes.Skip(1))
                Check.Equal(TimeModel.StartMs(k, t.Fps), ms % p, $"L10={t.Length10} F={t.Fps}: フレーム {k} に変わった時刻 {ms}");
        }
    }

    [Test]
    public void AC35_3_FR60_ループが1回なら周期で止まり再生ボタンで先頭から始まる()
    {
        using var rig = new Rig(T(20, 20, loop: LoopMode.Once));
        rig.Playback.Play();
        rig.At(1990);
        Check.Equal(39, rig.Frames.K, "1990 ms");
        Check.True(rig.Frames.IsPlaying, "1990 ms は再生中");
        rig.At(2000);
        Check.Equal(39, rig.Frames.K, "2000 ms で最後のフレームを表示したまま");
        Check.True(!rig.Frames.IsPlaying, "2000 ms で再生が終わる");
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Check.Equal(39, rig.Frames.K, "終わったあとは動かない");
        rig.Playback.Toggle();
        Check.True(rig.Frames.IsPlaying, "もう一度押すと再生");
        Check.Equal(0, rig.Frames.K, "先頭から始まる");
        rig.At(1234);
        Check.Equal(24, rig.Frames.K, "2 回目の 1234 ms");

        // 停止時間 500 の「1 回」は、P=2500 で止まる(停止時間は 1 回でも働く)
        using var hold = new Rig(T(20, 20, 500, LoopMode.Once));
        hold.Playback.Play();
        hold.At(2499);
        Check.True(hold.Frames.IsPlaying, "2499 ms はまだ再生中");
        hold.At(2500);
        Check.True(!hold.Frames.IsPlaying, "2500 ms で終わる");
        Check.Equal(39, hold.Frames.K, "最後のフレーム");
    }

    [Test]
    public void AC35_3_FR60_描画に3フレーム分かかる偽の描画でも番号は経過時間の式のとおりで飛ばす()
    {
        var t = T(20, 20);
        using var rig = new Rig(t);
        var frameMs = TimeModel.StartMs(1, t.Fps);
        var shown = new List<(long Ms, int K)>();
        // 偽の描画: 表示のフレームが変わるたびに、3 フレーム分(150 ms)の時間を使う(時計を直接進める。タイマーは鳴らさない)
        rig.Frames.Changed += () =>
        {
            if (!rig.Frames.IsPlaying) return;
            shown.Add((rig.Playback.ElapsedMs, rig.Frames.K));
            rig.Clock.UtcNow += TimeSpan.FromMilliseconds(3 * frameMs);
        };
        rig.Playback.Play();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(TimeModel.PeriodMs(t) * 2));
        Check.True(shown.Count >= 8, $"表示したフレームが少ない({shown.Count})");
        foreach (var (ms, k) in shown) Check.Equal(TimeModel.FrameAt(ms, t), k, $"経過 {ms} ms に入れたフレーム");
        // 間のフレームを飛ばしている(1 つずつ進めていない)
        var steps = shown.Zip(shown.Skip(1), (a, b) => (b.K - a.K + 40) % 40).ToList();
        Check.True(steps.All(s => s >= 3), $"フレームを飛ばしていない: {string.Join(",", steps)}");
        // 遅れない: 経過時間 2 周期の時点の表示は、式のフレームから 3 フレーム分より後ろにずれない
        var elapsed = rig.Playback.ElapsedMs;
        var lag = (TimeModel.FrameAt(elapsed, t) - rig.Frames.K + 40) % 40;
        Check.True(lag <= 3, $"表示が遅れている(経過 {elapsed} ms で式は {TimeModel.FrameAt(elapsed, t)}、表示は {rig.Frames.K})");
    }

    [Test]
    public void FR60_ほかの手で止められたらタイマーを外し時間の設定が変われば決め直す()
    {
        using var rig = new Rig(T(20, 20));
        rig.Playback.Play();
        rig.At(500);
        Check.Equal(10, rig.Frames.K, "500 ms");
        // 文書が入れ替わったとき(AppShell が Reset する)
        rig.Frames.Reset();
        Check.True(!rig.Playback.IsPlaying, "Reset で止まる");
        rig.Clock.Advance(TimeSpan.FromSeconds(3));
        Check.Equal(0, rig.Frames.K, "止まったあとはタイマーで動かない");

        // 再生中に F を変えると、同じ経過時間を新しい設定で読み直す
        rig.Playback.Play();
        rig.At(500);
        Check.Equal(10, rig.Frames.K, "F=20 の 500 ms");
        rig.Time = T(20, 10);
        rig.Frames.SetFrameCount(TimeModel.FrameCount(rig.Time));
        rig.Playback.TimeChanged();
        Check.Equal(5, rig.Frames.K, "F=10 の 500 ms");
        rig.At(1000);
        Check.Equal(10, rig.Frames.K, "F=10 の 1000 ms");

        // 文書が無くなったら止める
        rig.Time = null;
        rig.Playback.Tick();
        Check.True(!rig.Playback.IsPlaying, "文書が無いと止まる");
        rig.Playback.Play();
        Check.True(!rig.Playback.IsPlaying, "文書が無いと始まらない");
    }

    [Test]
    public void FR60_次に表示が変わる時刻は次のフレームの開始か周期の終わり()
    {
        var t = T(20, 20, 500);
        Check.Equal(50L, PlaybackController.NextChangeMs(0, t), "0 ms の次");
        Check.Equal(100L, PlaybackController.NextChangeMs(50, t), "50 ms の次");
        Check.Equal(2500L, PlaybackController.NextChangeMs(1950, t), "最後のフレームは周期の終わりまで");
        Check.Equal(2500L, PlaybackController.NextChangeMs(2499, t), "停止時間の中");
        Check.Equal(2550L, PlaybackController.NextChangeMs(2500, t), "2 周目");
        var odd = T(5, 40);
        Check.Equal(30L, PlaybackController.NextChangeMs(0, odd), "遅延 3・2 の 1 つ目");
        Check.Equal(50L, PlaybackController.NextChangeMs(30, odd), "遅延 3・2 の 2 つ目");
    }
}
