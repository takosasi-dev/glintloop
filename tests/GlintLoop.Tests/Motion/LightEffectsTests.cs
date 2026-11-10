// モーションの色と光の 5 個(blink・huecycle・glowpulse・shine・leaks)と rgbsplit のテスト(WARP だけ): EffectDef が §9.5 の表のとおりか(AC-35 の (5)(6))、
// ループの継ぎ目(AC-34 の (1)(2)(3))、定義の式(同じ計算の既存のエフェクトとの一致と、C# の double の参照)、枠を基準にした読み替え(§9.7 の規則 12)を確かめる。
// 依頼の cycles・phase・wave は EffectInvocation の欄に入れる(MotionTestKit)。名前空間は GlintLoop.Tests.Motions(Theme.Motion とぶつけない)。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Motions;

public sealed class LightEffectsTests
{
    private const int N = 40;

    // ==== 仕様書 §9.5 の表(テストの中の写し) ====

    private sealed record PSpec(string Key, ParamType Type, double? Min, double? Max, ParamValue Default, string Unit,
        string[]? Options = null, string? VisibleKey = null, string[]? VisibleValues = null);

    private sealed record ESpec(string Id, string Name, string Group, bool HasWave, AmountMode Mode, string[] ScaleParams, PSpec[] Params);

    private static PSpec F(string key, double min, double max, double def, string unit, string? visKey = null, string[]? visValues = null) =>
        new(key, ParamType.Float, min, max, new NumberValue(def), unit, VisibleKey: visKey, VisibleValues: visValues);

    private static PSpec A(string key, double min, double max, double def) => new(key, ParamType.Angle, min, max, new NumberValue(def), "deg");

    private static PSpec E(string key, string def, params string[] values) => new(key, ParamType.Enum, null, null, new StringValue(def), "", values);

    private static PSpec Col(string key, string def) => new(key, ParamType.Color, null, null, new StringValue(def), "");

    private static readonly ESpec[] Specs =
    [
        new("blink", "ピカピカ", "色と光", false, AmountMode.Scale, ["depth"],
        [
            E("mode", "opacity", "opacity", "dark", "flash"),
            E("shape", "square", "square", "sine", "tri"),
            F("duty", 10, 90, 50, "%", "shape", ["square"]),
            F("depth", 0, 100, 100, "%"),
        ]),
        new("huecycle", "虹色", "色と光", false, AmountMode.Mix, [],
            [E("mode", "full", "full", "swing"), F("range", 10, 180, 60, "deg", "mode", ["swing"])]),
        new("glowpulse", "ネオン", "色と光", true, AmountMode.Scale, ["intensity"],
            [F("threshold", 0, 100, 60, "%"), F("radius", 0, 150, 40, "‰"), F("intensity", 0, 300, 150, "%"), Col("color", "#FFFFFFFF")]),
        new("shine", "キラッ", "色と光", false, AmountMode.Scale, ["intensity"],
        [
            A("angle", -90, 90, 25), F("width", 20, 500, 120, "‰"), F("soft", 0, 100, 60, "%"), F("intensity", 0, 300, 100, "%"),
            Col("color", "#FFFFFFFF"), F("pause", 0, 80, 30, "%"),
        ]),
        new("leaks", "ライトリーク", "色と光", false, AmountMode.Scale, ["intensity"],
            [E("palette", "warm", "warm", "cool", "sunset", "rainbow"), F("size", 100, 1000, 450, "‰"), F("intensity", 0, 200, 70, "%")]),
        new("rgbsplit", "RGBずれ", "乱れ", true, AmountMode.Scale, ["amount"],
            [F("amount", 0, 60, 15, "‰"), A("angle", -180, 180, 0)]),
    ];

    private static readonly string[] Ids = Specs.Select(s => s.Id).ToArray();

    // ==== 小さな道具 ====

    private static NumberValue Num(double v) => new(v);
    private static StringValue Str(string v) => new(v);

    private static EffectInvocation Inv(RenderService rs, string id, int cycles = 1, double phase = 0, WaveKind? wave = null,
        params (string Key, ParamValue Value)[] values) => MotionTestKit.Invocation(rs.Effects, id, cycles, phase, wave, 1, values);

    /// <summary>仕上げ・ルックのエフェクトの依頼(amount=1、salt=1)。</summary>
    private static EffectInvocation Plain(RenderService rs, string id, params (string Key, ParamValue Value)[] values) =>
        new(rs.Effects.Find(id) ?? throw new CheckFailedException(id + " が無い"))
        {
            Values = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal), Amount = 1, Salt = 1,
        };

    /// <summary>img に stack を掛けた RGBA16F(線形・プリマルチプライ。共通の後処理のあと)。stack が空なら取り込んだ値。</summary>
    private static float[] F16(RenderService rs, CompositeImg img, int k, params EffectInvocation[] stack) =>
        rs.RenderRgba16FAsync(img.Px, img.W, img.H, stack, new FrameContext(MotionTestKit.Seed, k, N)).GetAwaiter().GetResult();

    /// <summary>2 つの RGBA16F が各成分で |a−b| ≤ abs+rel×|b| に収まる。</summary>
    private static void Near(float[] expected, float[] actual, double abs, double rel, string what)
    {
        Check.Equal(expected.Length, actual.Length, what + ": 長さ");
        for (var i = 0; i < expected.Length; i++)
        {
            var e = expected[i];
            var a = actual[i];
            if (!(Math.Abs(a - e) <= abs + rel * Math.Abs(e)))
                throw new CheckFailedException($"{what}: 画素 {i / 4} のチャンネル {i % 4} が {a}(期待 {e})");
        }
    }

    /// <summary>入力の各画素から期待値を作る(f は画素の番号 i・位置 (x, y)・入力の RGBA を受けて出力の RGBA を返す)。</summary>
    private static float[] Expect(float[] input, int w, Func<int, double, double, double[], double[]> f)
    {
        var o = new float[input.Length];
        for (var i = 0; i < input.Length / 4; i++)
        {
            var p = new double[] { input[i * 4], input[i * 4 + 1], input[i * 4 + 2], input[i * 4 + 3] };
            var r = f(i, i % w + 0.5, i / w + 0.5, p);
            for (var c = 0; c < 4; c++) o[i * 4 + c] = (float)r[c];
        }
        return o;
    }

    // ==== AC-35 の (5)(6): EffectDef が表のとおり ====

    [Test]
    public void AC35_5_6_色と光とrgbsplitのEffectDefが表のidと名前とgroupと共通パラメータを持つ()
    {
        var catalog = BuiltInEffects.Load(new MemoryLogSink());
        Check.True(catalog.Rejected.Count == 0, "無効な EffectDef: " + string.Join(" / ", catalog.Rejected.Select(r => $"{r.Id}: {r.Detail}")));
        foreach (var s in Specs)
        {
            var def = catalog.Find(s.Id) ?? throw new CheckFailedException(s.Id + " が読めていない");
            Check.Equal(s.Name, def.Name, s.Id + " の名前");
            Check.Equal(s.Group, def.Group, s.Id + " の group");
            Check.Equal(EffectStage.Motion, def.Stage, s.Id + " のステージ");
            Check.True(def.TimeDependent, s.Id + " が timeDependent でない");
            Check.Equal(!s.HasWave, def.NoWave, s.Id + " の noWave");
            Check.Equal(WaveKind.Sine, def.WaveDefault, s.Id + " の波形の既定");
            Check.Equal(SizeRule.Same, def.SizeRule, s.Id + " の sizeRule");
            Check.Equal(s.Mode, def.AmountMode, s.Id + " の amountMode");
            Check.SequenceEqual(s.ScaleParams, def.ScaleParams.ToArray(), s.Id + " の scaleParams");
            foreach (var p in new[] { "cycles", "phase", "wave" })
                Check.True(def.FindParam(p) is null, $"{s.Id} の JSON に {p} を書いている(EffectSchema が足す)");

            // (5) 共通パラメータは強さの次(cycles・phase、波形ありは wave)に自動で足される
            var all = EffectSchema.AllParams(def);
            var cycles = all[0];
            Check.Equal("cycles", cycles.Key, s.Id + " の 1 番目");
            Check.Equal(ParamType.Int, cycles.Type, s.Id + " の cycles の型");
            Check.Equal(1.0, cycles.Min, s.Id + " の cycles の最小");
            Check.Equal(8.0, cycles.Max, s.Id + " の cycles の最大");
            Check.Equal<ParamValue>(new NumberValue(1), cycles.Default, s.Id + " の cycles の既定");
            var phase = all[1];
            Check.Equal("phase", phase.Key, s.Id + " の 2 番目");
            Check.Equal(0.0, phase.Min, s.Id + " の phase の最小");
            Check.Equal(100.0, phase.Max, s.Id + " の phase の最大");
            Check.Equal("%", phase.Unit, s.Id + " の phase の単位");
            Check.Equal<ParamValue>(new NumberValue(0), phase.Default, s.Id + " の phase の既定");
            var rest = all.Skip(2).ToList();
            if (s.HasWave)
            {
                Check.Equal("wave", rest[0].Key, s.Id + " の 3 番目");
                Check.Equal<ParamValue>(new StringValue("sine"), rest[0].Default, s.Id + " の wave の既定");
                Check.SequenceEqual(new[] { "sine", "tri", "ease", "saw" }, rest[0].Options!.Select(o => o.Value).ToArray(), s.Id + " の wave の選択肢");
                rest = rest.Skip(1).ToList();
            }
            else
            {
                Check.True(all.All(p => p.Key != "wave"), s.Id + " に wave がある(波形なし)");
            }

            // 表のパラメータ(並び・型・範囲・既定・単位・選択肢・表示の条件)
            Check.SequenceEqual(s.Params.Select(p => p.Key).ToArray(), rest.Select(p => p.Key).ToArray(), s.Id + " のパラメータの並び");
            foreach (var (ps, pd) in s.Params.Zip(rest))
            {
                var what = $"{s.Id}.{ps.Key}";
                Check.Equal(ps.Type, pd.Type, what + " の型");
                Check.Equal(ps.Min, pd.Min, what + " の最小");
                Check.Equal(ps.Max, pd.Max, what + " の最大");
                Check.Equal(ps.Default, pd.Default, what + " の既定");
                Check.Equal(ps.Unit, pd.Unit, what + " の単位");
                if (ps.Options is not null)
                    Check.SequenceEqual(ps.Options, pd.Options!.Select(o => o.Value).ToArray(), what + " の選択肢");
                Check.Equal(ps.VisibleKey, pd.VisibleWhen?.Key, what + " の表示の条件のキー");
                if (ps.VisibleValues is not null)
                    Check.SequenceEqual(ps.VisibleValues, pd.VisibleWhen!.Values.ToArray(), what + " の表示の条件の値");
            }
        }
    }

    // ==== AC-34 の (1)(2)(3): ループの継ぎ目 ====

    private static void Seams(string id)
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        foreach (var img in MotionTestKit.Ac34Images())
        {
            foreach (var inv in MotionTestKit.Ac34Variants(rs.Effects, id))
            {
                var what = $"{id} cycles={inv.Cycles} phase={inv.Phase} wave={inv.Wave}";
                var render = MotionTestKit.Render(rs, img, inv);
                MotionTestKit.Pass(MotionTestKit.CheckClosure(render), what);
                if (inv.Wave != WaveKind.Saw) MotionTestKit.Pass(MotionTestKit.CheckContinuity(render), what);
                // (3) 乱数の格子を使わない 17 個に入る(leaks の乱数は seed と salt だけで、時間の段を持たない)
                if (inv.Cycles == 2) MotionTestKit.Pass(MotionTestKit.CheckPeriod(render), what);
            }
        }
    }

    [Test(Slow = true)]
    public void AC34_1_2_3_blinkの継ぎ目() => Seams("blink");

    [Test(Slow = true)]
    public void AC34_1_2_3_huecycleの継ぎ目() => Seams("huecycle");

    [Test(Slow = true)]
    public void AC34_1_2_3_glowpulseの継ぎ目() => Seams("glowpulse");

    [Test(Slow = true)]
    public void AC34_1_2_3_shineの継ぎ目() => Seams("shine");

    [Test(Slow = true)]
    public void AC34_1_2_3_leaksの継ぎ目() => Seams("leaks");

    [Test(Slow = true)]
    public void AC34_1_2_3_rgbsplitの継ぎ目() => Seams("rgbsplit");

    [Test]
    public void AC34_継ぎ目の検査は動くものだけを通すので6個とも時間で結果が変わる()
    {
        // 検査が「どのフレームも同じ」で通ってしまっていないことの確かめ: 既定の設定で、フレーム 0 と 10・20・30 のどれかが 1 より大きく違う
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        var img = MotionTestKit.SmoothOpaque();
        foreach (var id in Ids)
        {
            var render = MotionTestKit.Render(rs, img, MotionTestKit.Invocation(rs.Effects, id));
            var f0 = render(0, N);
            var moved = new[] { 10, 20, 30 }.Max(k => MotionTestKit.MaxDiff(f0, render(k, N)));
            Check.True(moved > 1, $"{id}: フレームで結果が変わらない(差の最大 {moved})");
        }
    }

    // ==== 定義の式 ====

    [Test]
    public void 定義_blinkの3つの効き方と3つの形()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var img = MotionTestKit.Translucent();
        var input = F16(rs, img, 0);
        var depth = ("depth", (ParamValue)Num(40));
        // square・duty 50: k=0(u=0)は v=1、k=30(u=0.75)は v=0。m=1−0.4×(1−v)
        Near(input, F16(rs, img, 0, Inv(rs, "blink", values: [depth])), 2e-3, 2e-3, "opacity・v=1 は入力のまま");
        Near(Expect(input, img.W, (_, _, _, p) => [p[0] * 0.6, p[1] * 0.6, p[2] * 0.6, p[3] * 0.6]),
            F16(rs, img, 30, Inv(rs, "blink", values: [depth])), 2e-3, 2e-3, "opacity・v=0 は P×0.6");
        Near(Expect(input, img.W, (_, _, _, p) => [p[0] * 0.6, p[1] * 0.6, p[2] * 0.6, p[3]]),
            F16(rs, img, 30, Inv(rs, "blink", values: [depth, ("mode", Str("dark"))])), 2e-3, 2e-3, "dark・v=0 は RGB×0.6、α はそのまま");
        Near(Expect(input, img.W, (_, _, _, p) => [p[0] + 0.4 * p[3], p[1] + 0.4 * p[3], p[2] + 0.4 * p[3], p[3]]),
            F16(rs, img, 0, Inv(rs, "blink", values: [depth, ("mode", Str("flash"))])), 2e-3, 2e-3, "flash・v=1 は RGB+0.4×α");
        Near(input, F16(rs, img, 30, Inv(rs, "blink", values: [depth, ("mode", Str("flash"))])), 2e-3, 2e-3, "flash・v=0 は入力のまま");
        // duty 25: k=12(u=0.3)は v=0、k=8(u=0.2)は v=1
        Near(input, F16(rs, img, 8, Inv(rs, "blink", values: [depth, ("duty", Num(25))])), 2e-3, 2e-3, "duty 25・u=0.2");
        Near(Expect(input, img.W, (_, _, _, p) => [p[0] * 0.6, p[1] * 0.6, p[2] * 0.6, p[3] * 0.6]),
            F16(rs, img, 12, Inv(rs, "blink", values: [depth, ("duty", Num(25))])), 2e-3, 2e-3, "duty 25・u=0.3");
        // sine: k=20(ψ=0.5)は W01=0.5 で m=0.8。tri: k=30(ψ=0.75)は W01=0 で m=0.6、k=5(ψ=0.125)は W01=0.75 で m=0.9
        Near(Expect(input, img.W, (_, _, _, p) => [p[0] * 0.8, p[1] * 0.8, p[2] * 0.8, p[3] * 0.8]),
            F16(rs, img, 20, Inv(rs, "blink", values: [depth, ("shape", Str("sine"))])), 2e-3, 2e-3, "sine・ψ=0.5");
        Near(Expect(input, img.W, (_, _, _, p) => [p[0] * 0.6, p[1] * 0.6, p[2] * 0.6, p[3] * 0.6]),
            F16(rs, img, 30, Inv(rs, "blink", values: [depth, ("shape", Str("tri"))])), 2e-3, 2e-3, "tri・ψ=0.75");
        Near(Expect(input, img.W, (_, _, _, p) => [p[0] * 0.9, p[1] * 0.9, p[2] * 0.9, p[3] * 0.9]),
            F16(rs, img, 5, Inv(rs, "blink", values: [depth, ("shape", Str("tri"))])), 2e-3, 2e-3, "tri・ψ=0.125");
    }

    [Test]
    public void 定義_huecycleはルックの色相を時間で回したものと一致する()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var img = MotionTestKit.Translucent();
        // full: δ=360°×ψ。k=10 で 90°、k=30 で 270°(=−90°)。phase 37 の k=0 は 133.2°
        Near(F16(rs, img, 0, Plain(rs, "hue", ("shift", Num(90)))), F16(rs, img, 10, Inv(rs, "huecycle")), 2e-3, 2e-3, "full・ψ=0.25");
        Near(F16(rs, img, 0, Plain(rs, "hue", ("shift", Num(-90)))), F16(rs, img, 30, Inv(rs, "huecycle")), 2e-3, 2e-3, "full・ψ=0.75");
        Near(F16(rs, img, 0, Plain(rs, "hue", ("shift", Num(133.2)))), F16(rs, img, 0, Inv(rs, "huecycle", phase: 37)), 2e-3, 2e-3, "full・phase 37");
        // swing: δ=range×sin(2πψ)。k=10 で +range、k=30 で −range、k=20 で 0
        var swing = ("mode", (ParamValue)Str("swing"));
        Near(F16(rs, img, 0, Plain(rs, "hue", ("shift", Num(60)))), F16(rs, img, 10, Inv(rs, "huecycle", values: [swing])), 2e-3, 2e-3, "swing・ψ=0.25");
        Near(F16(rs, img, 0, Plain(rs, "hue", ("shift", Num(-150)))),
            F16(rs, img, 30, Inv(rs, "huecycle", values: [swing, ("range", Num(150))])), 2e-3, 2e-3, "swing・range 150・ψ=0.75");
        Near(F16(rs, img, 0), F16(rs, img, 20, Inv(rs, "huecycle", values: [swing])), 2e-3, 2e-3, "swing・ψ=0.5 は入力のまま");
    }

    [Test]
    public void 定義_glowpulseは仕上げのglowのintensityにW01を掛けたものと一致する()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var img = MotionTestKit.SmoothOpaque();
        var glow150 = F16(rs, img, 0, Plain(rs, "glow", ("intensity", Num(150))));
        Near(glow150, F16(rs, img, 10, Inv(rs, "glowpulse")), 2e-3, 2e-3, "sine・ψ=0.25(W01=1)");
        Near(F16(rs, img, 0), F16(rs, img, 30, Inv(rs, "glowpulse")), 2e-3, 2e-3, "sine・ψ=0.75(W01=0)は入力のまま");
        Near(F16(rs, img, 0, Plain(rs, "glow", ("intensity", Num(75)))), F16(rs, img, 20, Inv(rs, "glowpulse")), 2e-3, 2e-3, "sine・ψ=0.5(W01=0.5)");
        // saw: W01=fract(ψ)。k=10 で 0.25
        Near(F16(rs, img, 0, Plain(rs, "glow", ("intensity", Num(37.5)))), F16(rs, img, 10, Inv(rs, "glowpulse", wave: WaveKind.Saw)), 2e-3, 2e-3,
            "saw・ψ=0.25");
        // ほかのパラメータは glow と同じに効く
        (string, ParamValue)[] other = [("threshold", Num(20)), ("radius", Num(90)), ("color", Str("#FF8040C0"))];
        Near(F16(rs, img, 0, Plain(rs, "glow", [.. other, ("intensity", Num(150))])), F16(rs, img, 10, Inv(rs, "glowpulse", values: other)),
            2e-3, 2e-3, "しきい値・広がり・色を変えたとき");
    }

    [Test]
    public void 定義_rgbsplitは仕上げのaberrationのlinearにW_ψを掛けたものと一致する()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var img = MotionTestKit.Translucent();
        // o=px(amount)×W(ψ)×(cos angle, sin angle)。sine の k=10 で W=1、k=30 で W=−1(向きを 180° 回したのと同じ)、k=0 で 0
        var angle = ("angle", (ParamValue)Num(30));
        Near(F16(rs, img, 0, Plain(rs, "aberration", ("mode", Str("linear")), ("amount", Num(15)), angle)),
            F16(rs, img, 10, Inv(rs, "rgbsplit", values: [angle])), 2e-3, 2e-3, "sine・ψ=0.25");
        Near(F16(rs, img, 0, Plain(rs, "aberration", ("mode", Str("linear")), ("amount", Num(15)), ("angle", Num(-150)))),
            F16(rs, img, 30, Inv(rs, "rgbsplit", values: [angle])), 2e-3, 2e-3, "sine・ψ=0.75");
        Near(F16(rs, img, 0), F16(rs, img, 0, Inv(rs, "rgbsplit", values: [angle])), 2e-3, 2e-3, "sine・ψ=0 は入力のまま");
        // tri の k=5(ψ=0.125)で W=0.5: amount 40 の半分の 20 は aberration の範囲(30 まで)に入る
        Near(F16(rs, img, 0, Plain(rs, "aberration", ("mode", Str("linear")), ("amount", Num(20)), angle)),
            F16(rs, img, 5, Inv(rs, "rgbsplit", wave: WaveKind.Tri, values: [angle, ("amount", Num(40))])), 2e-3, 2e-3, "tri・ψ=0.125");
    }

    /// <summary>shine の参照(double): 画素の位置 (x, y)(枠は画像の全体)と u から帯の強さ b を求める。</summary>
    private static double ShineBand(double x, double y, int w, int h, double u, double angleDeg, double width, double soft, double pause)
    {
        var th = angleDeg * Math.PI / 180;
        var (nx, ny) = (Math.Cos(th), Math.Sin(th));
        double[] corners = [0, w * nx, h * ny, w * nx + h * ny];
        var pmin = corners.Min();
        var pmax = corners.Max();
        var hw = width * Math.Min(w, h) / 1000 / 2;
        var a = MathX.Sat(u / (1 - pause / 100));
        var sa = a * a * (3 - 2 * a);
        var pc = pmin - hw + (pmax + hw - (pmin - hw)) * sa;
        var d = Math.Abs(x * nx + y * ny - pc);
        var e0 = hw * (1 - soft / 100);
        double ss;
        if (e0 >= hw) ss = d >= hw ? 1 : 0;
        else
        {
            var t = MathX.Sat((d - e0) / (hw - e0));
            ss = t * t * (3 - 2 * t);
        }
        return 1 - ss;
    }

    [Test]
    public void 定義_shineは帯が枠の外から外へ進み色を足す()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var img = MotionTestKit.Translucent();
        var input = F16(rs, img, 0);
        // u=0 と u≥1−pause(k=28 以降は u=0.7 以上)では帯が画面の外にある
        Near(input, F16(rs, img, 0, Inv(rs, "shine")), 2e-3, 2e-3, "u=0");
        Near(input, F16(rs, img, 30, Inv(rs, "shine")), 2e-3, 2e-3, "u=0.75");
        var cases = new (int K, (string, ParamValue)[] Values, double Angle, double Width, double Soft, double Intensity, double Pause, double[] Color)[]
        {
            (7, [], 25, 120, 60, 100, 30, [1, 1, 1]),
            (14, [], 25, 120, 60, 100, 30, [1, 1, 1]),
            (10, [("angle", Num(-70)), ("width", Num(300)), ("soft", Num(0)), ("intensity", Num(250)), ("pause", Num(0)), ("color", Str("#FF804080"))],
                -70, 300, 0, 250, 0, [1, Srgb.Lin(0x80 / 255.0), Srgb.Lin(0x40 / 255.0)]),
        };
        foreach (var c in cases)
        {
            var u = (double)c.K / N;
            var opacity = c.Values.Length > 0 ? 0x80 / 255.0 : 1.0;
            var expected = Expect(input, img.W, (_, x, y, p) =>
            {
                var b = ShineBand(x, y, img.W, img.H, u, c.Angle, c.Width, c.Soft, c.Pause);
                var k = c.Intensity / 100 * b * opacity * p[3];
                return [p[0] + k * c.Color[0], p[1] + k * c.Color[1], p[2] + k * c.Color[2], p[3]];
            });
            var actual = F16(rs, img, c.K, Inv(rs, "shine", values: c.Values));
            Near(expected, actual, 4e-3, 2e-3, $"k={c.K}・angle {c.Angle}");
            Check.True(MotionTestKit.MaxDiff(MotionTestKit.Render(rs, img, Inv(rs, "shine", values: c.Values))(0, N),
                MotionTestKit.Render(rs, img, Inv(rs, "shine", values: c.Values))(c.K, N)) > 10, $"k={c.K}: 帯が見えない");
        }
    }

    /// <summary>leaks の参照(double): 画素の位置 (x, y)(枠は画像の全体)の Σ(lin(色i)×bi)。</summary>
    private static double[] LeaksSum(double x, double y, int w, int h, double psi, uint seed, uint salt, int palette, double size)
    {
        uint[][] colors =
        [
            [0xFF8A3D, 0xFF4F6B, 0xFFD166],
            [0x3DA5FF, 0x7A5CFF, 0x3DFFD4],
            [0xFF5E3A, 0xFF3D81, 0xFFB347],
            [0xFF4D4D, 0x4DFF88, 0x4D7BFF],
        ];
        var k0 = Hash.H(seed, salt);
        var r = size * Math.Min(w, h) / 1000;
        var sum = new double[3];
        for (uint i = 0; i < 3; i++)
        {
            var fi = 1 + Hash.H3(k0, 23, i) % 2;
            var gi = 1 + Hash.H3(k0, 24, i) % 2;
            var phi = Hash.U01(Hash.H3(k0, 25, i));
            var chi = Hash.U01(Hash.H3(k0, 26, i));
            var cx = w * (0.15 + 0.7 * Hash.U01(Hash.H3(k0, 21, i))) + 0.25 * w * Math.Sin(2 * Math.PI * (fi * psi + phi));
            var cy = h * (0.15 + 0.7 * Hash.U01(Hash.H3(k0, 22, i))) + 0.25 * h * Math.Cos(2 * Math.PI * (gi * psi + chi));
            var si = 0.6 + 0.4 * Math.Sin(2 * Math.PI * (psi + Hash.U01(Hash.H3(k0, 27, i))));
            var q = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / r;
            var bi = si * Math.Exp(-(q * q));
            var c = colors[palette][i];
            sum[0] += Srgb.Lin((c >> 16 & 0xFF) / 255.0) * bi;
            sum[1] += Srgb.Lin((c >> 8 & 0xFF) / 255.0) * bi;
            sum[2] += Srgb.Lin((c & 0xFF) / 255.0) * bi;
        }
        return sum;
    }

    [Test]
    public void 定義_leaksは3つの光の塊をseedとsaltの位置に足す()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var img = MotionTestKit.Translucent();
        var input = F16(rs, img, 0);
        string[] palettes = ["warm", "cool", "sunset", "rainbow"];
        var cases = new (int K, int Cycles, double Phase, int Palette, double Size, double Intensity)[]
        {
            (0, 1, 0, 0, 450, 70),
            (13, 1, 0, 1, 450, 70),
            (27, 3, 37, 2, 200, 150),
            (35, 2, 0, 3, 1000, 200),
        };
        foreach (var c in cases)
        {
            var psi = c.Cycles * ((double)c.K / N) + c.Phase / 100;
            var expected = Expect(input, img.W, (_, x, y, p) =>
            {
                var s = LeaksSum(x, y, img.W, img.H, psi, MotionTestKit.Seed, 1, c.Palette, c.Size);
                var k = p[3] * c.Intensity / 100 * 0.8;
                return [p[0] + k * s[0], p[1] + k * s[1], p[2] + k * s[2], p[3]];
            });
            var inv = Inv(rs, "leaks", c.Cycles, c.Phase, values: [("palette", Str(palettes[c.Palette])), ("size", Num(c.Size)), ("intensity", Num(c.Intensity))]);
            Near(expected, F16(rs, img, c.K, inv), 4e-3, 3e-3, $"k={c.K}・{palettes[c.Palette]}");
        }
        // salt が違えば塊の位置が変わる
        var salt1 = F16(rs, img, 5, Inv(rs, "leaks"));
        var salt2 = F16(rs, img, 5, MotionTestKit.Invocation(rs.Effects, "leaks", salt: 2));
        Check.True(salt1.Zip(salt2).Max(z => Math.Abs(z.First - z.Second)) > 0.05, "salt を変えても結果が変わらない");
    }

    // ==== §9.7 の規則 12: レイヤーのモーションでは枠を基準に読み替える ====

    /// <summary>100×50 の不透明な画像(横の階調と縦の階調。左半分は赤み、右半分は青み)。</summary>
    private static CompositeImg Framed100x50() => CompositeImg.Make(100, 50, (x, y) =>
        x < 50 ? (200 + x, 40 + y * 2, 30, 255) : (30, 40 + y * 2, 150 + x, 255));

    private static DocumentImage RenderAt(CompositeKit kit, int lout, int k) =>
        kit.Renderer.RenderAsync(kit.Doc, lout, DocumentPixelFormat.StraightRgba8, k, N).GetAwaiter().GetResult();

    /// <summary>
    /// 土台 200×200 の透明(表示はオフ)の上に 100×50 を (40, −30, 100) に置き(枠 Q は (90, 45, 100, 50))、2 枚目のモーションに id を入れた文書と、
    /// 同じ画像だけを土台にした参照の文書を、フレーム k で描いて、枠の中の margin より内側を ±3 で比べる。
    /// </summary>
    private static void SameInFrame(string id, int k, int margin, params (string Key, ParamValue Value)[] values)
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(200, 200, 0, 0, 0, 0), Framed100x50());
        kit.Place(1, 40, -30, 100);
        Check.True(kit.Session.SetLayerVisible(0, false).Changed, "土台の表示をオフ");
        kit.AddEffect(Target.OfLayer(1), id, values);
        var im = RenderAt(kit, 200, k);

        using var refKit = new CompositeKit();
        refKit.Import(Framed100x50());
        refKit.AddEffect(Target.OfLayer(0), id, values);
        var reference = RenderAt(refKit, 100, k);
        CompositeCheck.Size(reference, 100, 50, "参照");

        // 動いていること(参照のフレーム 0 と違う)も確かめる
        var still = refKit.Renderer.RenderAsync(refKit.Doc, 100, DocumentPixelFormat.StraightRgba8, 0, N).GetAwaiter().GetResult();
        Check.True(MotionTestKit.MaxDiff(still.Pixels, reference.Pixels) > 10, $"{id}: フレーム {k} が動いていない");
        for (var y = margin; y < 50 - margin; y++)
        for (var x = margin; x < 100 - margin; x++)
            CompositeCheck.Pixel(im, 90 + x, 45 + y, CompositeCheck.At(reference, x, y), 3, $"{id} の枠の中 ({x}, {y})");
    }

    [Test]
    public void 規則12_shineの隅と幅は枠を基準にする()
    {
        SameInFrame("shine", 9, 0);
        SameInFrame("shine", 14, 0, ("angle", Num(-60)), ("soft", Num(0)));
    }

    [Test]
    public void 規則12_leaksの中心と大きさは枠を基準にする() => SameInFrame("leaks", 11, 0, ("size", Num(200)), ("intensity", Num(150)));

    [Test]
    public void 規則12_glowpulseのぼかしの広がりは枠の短辺を基準にする() =>
        // 枠の短辺 50 で σ1=1、3σ1=3(半幅 9)。外側の 10 画素は、枠の外の透明と参照の端の延ばしで違うので比べない
        SameInFrame("glowpulse", 10, 10, ("threshold", Num(0))); // radius は既定の 40

    [Test]
    public void 規則12_rgbsplitのずれの量は枠の短辺を基準にする() =>
        // amount 60‰ × 50 = 3 画素。外側の 4 画素は、枠の外の透明と参照の clamp で違うので比べない
        SameInFrame("rgbsplit", 10, 4, ("amount", Num(60)));
}
