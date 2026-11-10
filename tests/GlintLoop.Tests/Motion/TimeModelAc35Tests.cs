// 時間のモデル(TimeModel)とモーションの有無(MotionInfo)のテスト: AC-35 の (1) フレーム数と (3) 経過時間から表示するフレームの式、
// S_k の差が GIF の遅延(GifEncoder.FrameDelays)と一致すること、FR-67・AC-36 の (4) のフレーム数、規則 14 のモーションの行の外し方。
// GPU を使わない(文書はコードで組み立てる)。タイムラインの画面と再生の制御の検査は担当 timeline が足す。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Session;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Motions;

public sealed class TimeModelAc35Tests
{
    [Test]
    public void AC35_1_1056通りのNが整数の式と一致し3以上500以下()
    {
        var count = 0;
        for (var l10 = 5; l10 <= 100; l10++)
        foreach (var f in TimeModel.FpsChoices)
        {
            var n = TimeModel.FrameCount(l10, f);
            // N=round(L×F)(0.5 は大きい方へ)を decimal で求めたものと同じ
            var expected = (int)Math.Floor(l10 / 10m * f + 0.5m);
            Check.Equal(expected, n, $"L10={l10} F={f}");
            Check.InRange(3, 500, n, $"L10={l10} F={f} の N");
            count++;
        }
        Check.Equal(1056, count, "組み合わせの数");
        Check.Equal(11, TimeModel.FpsChoices.Count, "F の選択肢");
        Check.Equal(3, TimeModel.FrameCount(5, 5), "(0.5, 5)");
        Check.Equal(4, TimeModel.FrameCount(7, 5), "(0.7, 5)");
        Check.Equal(8, TimeModel.FrameCount(15, 5), "(1.5, 5)");
        Check.Equal(40, TimeModel.FrameCount(20, 20), "(2.0, 20)");
        Check.Equal(500, TimeModel.FrameCount(100, 50), "(10.0, 50)");
        Check.Equal(0.25, TimeModel.Phase(10, 40), "t=k÷N");
    }

    [Test]
    public void AC35_3_開始時刻は整数の式で例の値になる()
    {
        Check.Equal(1200L, TimeModel.StartMs(24, 20), "F=20 の S_24");
        Check.Equal(2000L, TimeModel.StartMs(40, 20), "F=20 の S_40");
        Check.Equal(800L, TimeModel.StartMs(4, 5), "F=5 の S_4");
        Check.SequenceEqual(new long[] { 0, 30, 50, 80, 100 }, Enumerable.Range(0, 5).Select(k => TimeModel.StartMs(k, 40)).ToArray(), "F=40 の S_0〜S_4");
        for (var k = 0; k <= 500; k++)
        foreach (var f in TimeModel.FpsChoices)
            Check.Equal((long)(10 * Math.Floor(100m * k / f + 0.5m)), TimeModel.StartMs(k, f), $"S_{k}(F={f})");
    }

    private static TimeSettings T(int l10, int f, long hold = 0, LoopMode loop = LoopMode.Infinite) => new(l10, f, loop, hold);

    [Test]
    public void AC35_3_経過時間から表示するフレームが例のとおり()
    {
        var a = T(20, 20);
        Check.Equal(2000L, TimeModel.PeriodMs(a), "P");
        Check.Equal(24, TimeModel.FrameAt(1234, a), "1234 ms");
        Check.Equal(0, TimeModel.FrameAt(2000, a), "2000 ms");
        Check.Equal(20, TimeModel.FrameAt(3000, a), "3000 ms");
        Check.Equal(19, TimeModel.FrameAt(4990, a), "4990 ms");

        var h = T(20, 20, 500);
        Check.Equal(2500L, TimeModel.PeriodMs(h), "停止 500 の P");
        foreach (var ms in new long[] { 1990, 2100, 2499 }) Check.Equal(39, TimeModel.FrameAt(ms, h), $"停止 500 の {ms} ms");
        Check.Equal(0, TimeModel.FrameAt(2500, h), "停止 500 の 2500 ms");

        var b = T(7, 5);
        Check.Equal(800L, TimeModel.PeriodMs(b), "L=0.7 F=5 の P");
        Check.Equal(2, TimeModel.FrameAt(590, b), "590 ms");
        Check.Equal(3, TimeModel.FrameAt(600, b), "600 ms");
        Check.Equal(3, TimeModel.FrameAt(799, b), "799 ms");
        Check.Equal(0, TimeModel.FrameAt(800, b), "800 ms");

        var c = T(5, 40);
        Check.Equal(20, TimeModel.FrameCount(c), "L=0.5 F=40 の N");
        foreach (var (ms, k) in new[] { (29L, 0), (30L, 1), (49L, 1), (50L, 2), (79L, 2), (80L, 3) })
            Check.Equal(k, TimeModel.FrameAt(ms, c), $"L=0.5 F=40 の {ms} ms");

        var once = T(20, 20, 0, LoopMode.Once);
        Check.Equal(39, TimeModel.FrameAt(1990, once), "1 回の 1990 ms");
        Check.True(!(TimeModel.PlaybackEnded(1990, once)), "1 回の 1990 ms はまだ終わらない");
        Check.Equal(39, TimeModel.FrameAt(2000, once), "1 回の 2000 ms");
        Check.True(TimeModel.PlaybackEnded(2000, once), "1 回は P で終わる");
        Check.Equal(39, TimeModel.FrameAt(9999, once), "1 回は終わったあとも最後のフレーム");
        Check.True(!(TimeModel.PlaybackEnded(99999, a)), "無限は終わらない");
    }

    [Test]
    public void AC35_4_開始時刻の差がGIFの遅延の10倍と一致し最後に停止時間が足される()
    {
        foreach (var hold in new long[] { 0, 500, 5000 })
        for (var l10 = 5; l10 <= 100; l10 += 5)
        foreach (var f in TimeModel.FpsChoices)
        {
            var t = T(l10, f, hold);
            var n = TimeModel.FrameCount(t);
            var delays = GifEncoder.FrameDelays(n, f, (int)hold);
            long sum = 0;
            for (var k = 0; k < n; k++)
            {
                Check.Equal(delays[k] * 10L, TimeModel.DurationMs(k, t), $"L10={l10} F={f} 停止 {hold} の d_{k}");
                sum += TimeModel.DurationMs(k, t);
            }
            Check.Equal(TimeModel.PeriodMs(t), sum, $"L10={l10} F={f} 停止 {hold} の P");
        }
        Check.Equal(55L, TimeModel.DurationMs(39, T(20, 20, 500)) / 10, "F=20 の最後の遅延 5 に 50 が足されて 55");
    }

    // ---- FR-67・AC-36 の (4): モーションの有無とフレーム数 ----

    private static readonly IEffectCatalog Catalog = BuiltInEffects.Load(NullLogSink.Instance);

    private static StackEntry Entry(string id, bool enabled = true) =>
        new(id, enabled, EffectOrigin.User, 1, 100, EffectSchema.DefaultParams(Catalog.Find(id)!));

    private static Layer LayerWith(params StackEntry[] stack) =>
        new("l", LayerKind.Empty, null, new EmptySize(100, 50), "#FF0000FF", true, 100, BlendMode.Normal, Placement.Identity, 100, stack);

    private static SessionDocument Doc(Layer[] layers, params StackEntry[] all) =>
        new(12345, 10, TimeSettings.Default, Target.All, [], layers, new GlobalStack(100, all), OutputSettings.Default);

    [Test]
    public void FR67_AC36_4_モーションの有無とフレーム数()
    {
        var baseLayer = LayerWith() with { Size = new EmptySize(200, 200) };
        var spin = Entry("spin");
        var onDoc = Doc([baseLayer, LayerWith(spin)]);
        Check.Equal(40, MotionInfo.FrameCount(onDoc, Catalog), "2 枚目にオンの spin");
        var hidden = onDoc with { Layers = [baseLayer, onDoc.Layers[1] with { Visible = false }] };
        Check.Equal(1, MotionInfo.FrameCount(hidden, Catalog), "2 枚目の表示がオフ");
        var eyeOff = Doc([baseLayer, LayerWith(Entry("spin", enabled: false))]);
        Check.Equal(1, MotionInfo.FrameCount(eyeOff, Catalog), "spin の目がオフ");
        var global = hidden with { All = new GlobalStack(100, [Entry("slide")]) };
        Check.Equal(40, MotionInfo.FrameCount(global, Catalog), "表示がオフでも全体のモーションがオン");
        Check.True(!(MotionInfo.HasLayerMotion(global, Catalog)), "全体だけのときレイヤーのモーションは無い");
        var still = Doc([baseLayer, LayerWith(Entry("blur"))]);
        Check.Equal(1, MotionInfo.FrameCount(still, Catalog), "モーションが無い(AC-35 の (10))");
        Check.Equal(4, MotionInfo.FrameCount(onDoc with { Time = T(7, 5) }, Catalog), "N は時間の設定から");
    }

    [Test]
    public void 規則14_単体の描画の文書からモーションの行を外す()
    {
        var baseLayer = LayerWith(Entry("blur"), Entry("spin")) with { Size = new EmptySize(200, 200) };
        var doc = Doc([baseLayer, LayerWith(Entry("spin", enabled: false), Entry("slide"))], Entry("slide"));
        var single = SingleLayerView.Of(doc, 1, Catalog);
        Check.Equal(0, single.Layers[0].Stack.Count, "行のサムネイルの文書(オフのモーションも外す)");
        Check.Equal(2, SingleLayerView.Of(doc, 1).Layers[0].Stack.Count, "catalog を渡さない形はスタックをそのまま");
        Check.SequenceEqual(new[] { "blur" }, SingleLayerView.Of(doc, 0, Catalog).Layers[0].Stack.Select(e => e.Id).ToArray(), "土台の行");

        var all = LookStacks.Preview(doc, Target.All, [], Catalog);
        Check.True(all.Layers.All(l => l.Stack.All(e => !MotionInfo.IsMotion(e, Catalog))), "全体のギャラリーは各レイヤーのモーションを通さない");
        Check.Equal(0, all.All.Stack.Count, "全体のギャラリーの全体のスタックは形ステージだけ");
        var layer = LookStacks.Preview(doc, Target.OfLayer(0), [], Catalog);
        Check.Equal(0, layer.Layers[0].Stack.Count(e => MotionInfo.IsMotion(e, Catalog)), "レイヤーのギャラリー");
        var noLayerMotion = MotionInfo.WithoutLayerMotion(doc, Catalog);
        Check.SequenceEqual(new[] { "slide" }, noLayerMotion.All.Stack.Select(e => e.Id).ToArray(), "全体のスタックは残す");
        Check.SequenceEqual(new[] { "blur" }, noLayerMotion.Layers[0].Stack.Select(e => e.Id).ToArray(), "レイヤーのモーションを外す");
        Check.Equal(2, doc.Layers[0].Stack.Count, "元の文書の行の数");
    }
}
