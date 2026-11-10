// 動きのモーション 7 個(bounce・hover・pulse・shake・wobble・jelly・coinflip)のテスト(WARP): AC-34 の (1)(2)(3)、AC-35 の (5)(6)、
// AC-36 の pulse の (1)(2)(4)(9) と shake の (5)、§9.5 の定義の式(この file に書き写した逆写像で、像の内と外の α を全画素で調べる)、FR-28 の amount 0。
// 見本は MotionRenderAc36Tests(P0)。文書は CompositeKit、エフェクト 1 つの描画は MotionTestKit で作る。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Motions;

public sealed class MoveEffectsTests
{
    private const int N = 40;
    private const int K = 10;

    private static readonly string[] Ids = ["bounce", "hover", "pulse", "shake", "wobble", "jelly", "coinflip"];

    // ---- AC-35 の (5)(6): 共通パラメータとカタログ(§9.5 の動きの表) ----

    private sealed record P(string Key, ParamType Type, double? Min, double? Max, ParamValue Default, string Unit, string[]? Options = null);

    private sealed record Row(string Id, string Name, bool HasWave, AmountMode Mode, string[] ScaleParams, P[] Params);

    private static readonly Row[] Table =
    [
        new("bounce", "バウンス", false, AmountMode.Scale, ["height", "squash"],
        [
            new("height", ParamType.Float, 0, 300, new NumberValue(60), "‰"),
            new("squash", ParamType.Float, 0, 100, new NumberValue(30), "%"),
        ]),
        new("hover", "ふわふわ", true, AmountMode.Scale, ["amp", "tilt"],
        [
            new("amp", ParamType.Float, 0, 100, new NumberValue(20), "‰"),
            new("tilt", ParamType.Float, 0, 15, new NumberValue(2), "deg"),
        ]),
        new("pulse", "ぷくぷく", false, AmountMode.Scale, ["amp"],
        [
            new("shape", ParamType.Enum, null, null, new StringValue("sine"), "", ["sine", "tri", "ease", "saw", "heartbeat"]),
            new("amp", ParamType.Float, 0, 50, new NumberValue(10), "%"),
            new("pivot", ParamType.Point, null, null, new PointValue(0.5, 0.5), ""),
        ]),
        new("shake", "ブルブル", false, AmountMode.Scale, ["amp", "rot"],
        [
            new("amp", ParamType.Float, 0, 100, new NumberValue(15), "‰"),
            new("rot", ParamType.Float, 0, 20, new NumberValue(3), "deg"),
            new("rate", ParamType.Int, 2, 24, new NumberValue(8), ""),
            new("smooth", ParamType.Bool, null, null, new BoolValue(false), ""),
        ]),
        new("wobble", "ゆらゆら", true, AmountMode.Scale, ["angle"],
        [
            new("angle", ParamType.Float, 0, 45, new NumberValue(8), "deg"),
            new("pivot", ParamType.Point, null, null, new PointValue(0.5, 0.9), ""),
        ]),
        new("jelly", "ぷるぷる", true, AmountMode.Scale, ["amp", "wobble"],
        [
            new("amp", ParamType.Float, 0, 50, new NumberValue(12), "%"),
            new("wobble", ParamType.Float, 0, 100, new NumberValue(40), "%"),
        ]),
        new("coinflip", "コイン", false, AmountMode.None, [],
        [
            new("axis", ParamType.Enum, null, null, new StringValue("y"), "", ["y", "x"]),
        ]),
    ];

    private static IEffectCatalog Catalog() => BuiltInEffects.Load(GlintLoop.Core.Diagnostics.NullLogSink.Instance);

    [Test]
    public void AC35_5_動きの7個はcyclesとphaseを持ちwaveは波形ありのものだけ()
    {
        var catalog = Catalog();
        foreach (var row in Table)
        {
            var def = catalog.Find(row.Id) ?? throw new CheckFailedException(row.Id + " がカタログに無い");
            Check.True(def.TimeDependent, $"{row.Id} の timeDependent");
            Check.Equal(!row.HasWave, def.NoWave, $"{row.Id} の noWave");
            Check.True(def.Params.All(p => p.Key is not ("cycles" or "phase" or "wave")), $"{row.Id} の JSON に共通パラメータを書かない");
            var all = EffectSchema.AllParams(def);
            var cycles = all[0];
            Check.Equal("cycles", cycles.Key, $"{row.Id} の 1 番目");
            Check.Equal(ParamType.Int, cycles.Type, $"{row.Id}.cycles の型");
            Check.Equal(1.0, cycles.Min, $"{row.Id}.cycles の min");
            Check.Equal(8.0, cycles.Max, $"{row.Id}.cycles の max");
            Check.Equal<ParamValue>(new NumberValue(1), cycles.Default, $"{row.Id}.cycles の既定");
            var phase = all[1];
            Check.Equal("phase", phase.Key, $"{row.Id} の 2 番目");
            Check.Equal(0.0, phase.Min, $"{row.Id}.phase の min");
            Check.Equal(100.0, phase.Max, $"{row.Id}.phase の max");
            Check.Equal("%", phase.Unit, $"{row.Id}.phase の単位");
            Check.Equal<ParamValue>(new NumberValue(0), phase.Default, $"{row.Id}.phase の既定");
            var wave = EffectSchema.FindParam(def, "wave");
            Check.Equal(row.HasWave, wave is not null, $"{row.Id} の wave の有無");
            if (wave is not null)
            {
                Check.Equal("wave", all[2].Key, $"{row.Id} の 3 番目");
                Check.SequenceEqual(new[] { "sine", "tri", "ease", "saw" }, wave.Options!.Select(o => o.Value).ToArray(), $"{row.Id}.wave の選択肢");
                Check.Equal<ParamValue>(new StringValue("sine"), wave.Default, $"{row.Id}.wave の既定");
            }
        }
    }

    [Test]
    public void AC35_6_動きの7個が表のidと名前とgroupとパラメータで読み込まれる()
    {
        var catalog = Catalog();
        foreach (var row in Table)
        {
            var def = catalog.Find(row.Id) ?? throw new CheckFailedException(row.Id + " がカタログに無い");
            Check.Equal(row.Name, def.Name, $"{row.Id} の名前");
            Check.Equal("動き", def.Group, $"{row.Id} の group");
            Check.Equal(EffectStage.Motion, def.Stage, $"{row.Id} の stage");
            Check.Equal(SizeRule.Same, def.SizeRule, $"{row.Id} の sizeRule");
            Check.Equal(row.Mode, def.AmountMode, $"{row.Id} の amountMode");
            Check.SequenceEqual(row.ScaleParams, def.ScaleParams.ToArray(), $"{row.Id} の scaleParams");
            Check.SequenceEqual(row.Params.Select(p => p.Key).ToArray(), def.Params.Select(p => p.Key).ToArray(), $"{row.Id} のパラメータの並び");
            foreach (var p in row.Params)
            {
                var d = def.FindParam(p.Key)!;
                var what = $"{row.Id}.{p.Key}";
                Check.Equal(p.Type, d.Type, $"{what} の型");
                Check.Equal(p.Min, d.Min, $"{what} の min");
                Check.Equal(p.Max, d.Max, $"{what} の max");
                Check.Equal(p.Default, d.Default, $"{what} の既定値");
                Check.Equal(p.Unit, d.Unit, $"{what} の単位");
                Check.True(d.Neutral is null, $"{what} に neutral がある(範囲の 0 を使う)");
                if (p.Options is not null) Check.SequenceEqual(p.Options, d.Options!.Select(o => o.Value).ToArray(), $"{what} の選択肢");
            }
        }
        foreach (var id in Ids) Check.True(Table.Any(r => r.Id == id), id + " が表に無い(テストの誤り)");
    }

    // ---- AC-34 の (1)(2)(3)(id ごと。組み合わせが多いので Slow) ----

    /// <summary>同じ (k, n) を描き直さないように覚える(閉じ方・連続・周期が同じフレームを使う)。</summary>
    private static FrameRender Memo(FrameRender render)
    {
        var cache = new Dictionary<(int, int), byte[]>();
        return (k, n) =>
        {
            if (!cache.TryGetValue((k, n), out var px)) cache[(k, n)] = px = render(k, n);
            return px;
        };
    }

    private static void Seams(string id, bool periodic)
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        foreach (var img in MotionTestKit.Ac34Images())
        foreach (var inv in MotionTestKit.Ac34Variants(rs.Effects, id))
        {
            var what = $"{id} cycles={inv.Cycles} phase={inv.Phase} wave={inv.Wave}";
            var render = Memo(MotionTestKit.Render(rs, img, inv));
            MotionTestKit.Pass(MotionTestKit.CheckClosure(render), what);
            if (inv.Wave != WaveKind.Saw) MotionTestKit.Pass(MotionTestKit.CheckContinuity(render), what);
            if (periodic && inv.Cycles == 2) MotionTestKit.Pass(MotionTestKit.CheckPeriod(render), what);
        }
    }

    [Test(Slow = true)]
    public void AC34_1_2_3_bounceの継ぎ目() => Seams("bounce", periodic: true);

    [Test(Slow = true)]
    public void AC34_1_2_3_hoverの継ぎ目() => Seams("hover", periodic: true);

    [Test(Slow = true)]
    public void AC34_1_2_3_pulseの継ぎ目() => Seams("pulse", periodic: true);

    /// <summary>shake は乱数の格子を使うので (3) の対象外(AC-34 の (3))。</summary>
    [Test(Slow = true)]
    public void AC34_1_2_shakeの継ぎ目() => Seams("shake", periodic: false);

    [Test(Slow = true)]
    public void AC34_1_2_3_wobbleの継ぎ目() => Seams("wobble", periodic: true);

    [Test(Slow = true)]
    public void AC34_1_2_3_jellyの継ぎ目() => Seams("jelly", periodic: true);

    [Test(Slow = true)]
    public void AC34_1_2_3_coinflipの継ぎ目() => Seams("coinflip", periodic: true);

    // ---- §9.5 の定義の式: 逆写像 xs=M⁻¹(x) を書き写し、像の内と外の α を全画素で調べる ----

    /// <summary>M=T(pivot+offset)×R(θ)×Sh×Sc(sx, sy)×T(−pivot) の各成分(位置は画素、θ はラジアンで時計回りが正)。</summary>
    private readonly record struct Affine(double Px, double Py, double Ox, double Oy, double Theta, double Shear, double Sx, double Sy)
    {
        /// <summary>xs=M⁻¹(x)。Sh は x'=x+shear×(pivot.y−y)、R は y が下向きの画面で時計回り。</summary>
        public (double X, double Y) Inverse(double x, double y)
        {
            var a = x - Px - Ox;
            var b = y - Py - Oy;
            var c = Math.Cos(Theta);
            var s = Math.Sin(Theta);
            var ra = a * c + b * s; // R(−θ)
            var rb = -a * s + b * c;
            ra += Shear * rb; // Sh⁻¹
            return (Px + ra / Sx, Py + rb / Sy);
        }
    }

    private const int Side = 128;
    private const double S0 = Side;

    // 不透明な矩形(x が 20〜99、y が 28〜109)。外は透明。中心からずらして、基準点と向きの誤りが α に出るようにする。
    private const int X0 = 20, X1 = 100, Y0 = 28, Y1 = 110;

    private static CompositeImg OpaqueRect() => CompositeImg.Make(Side, Side, (x, y) =>
        x is >= X0 and < X1 && y is >= Y0 and < Y1 ? (40 + x, 200 - y, 120, 255) : (0, 0, 0, 0));

    private static double Fract(double v) => v - Math.Floor(v);

    private static double SmoothS(double x) => x * x * (3 - 2 * x);

    /// <summary>波形 W(ψ)(§9.5 の表)。</summary>
    private static double W(double psi, WaveKind wave)
    {
        double Tri(double p) => 4 * Math.Abs(Fract(p - 0.25) - 0.5) - 1;
        return wave switch
        {
            WaveKind.Tri => Tri(psi),
            WaveKind.Ease => 2 * SmoothS((Tri(psi) + 1) / 2) - 1,
            WaveKind.Saw => 2 * Fract(psi) - 1,
            _ => Math.Sin(2 * Math.PI * psi),
        };
    }

    private static double Px(double permille) => permille * S0 / 1000;

    private static double Rad(double deg) => deg * Math.PI / 180;

    /// <summary>1 つの確かめ: 依頼の値と、ψ から定義の式で M を作る関数。</summary>
    private sealed record GeoCase(string Id, (string Key, ParamValue Value)[] Values, int Cycles, double Phase, WaveKind? Wave, Func<double, Affine> Map);

    private static readonly double Mid = Side / 2.0;

    private static IEnumerable<GeoCase> GeoCases()
    {
        // bounce: h=4u(1−u)、offset=(0, −px(height)×h)、q=squash÷100×(1−smoothstep(0, 0.2, h))、sx=1+0.5q、sy=max(1−q, 0.02)、pivot (0.5, 1.0)
        foreach (var (height, squash) in new[] { (60.0, 30.0), (250.0, 100.0) })
        {
            yield return new("bounce", [("height", new NumberValue(height)), ("squash", new NumberValue(squash))], 1, 0, null, psi =>
            {
                var u = Fract(psi);
                var h = 4 * u * (1 - u);
                var q = squash / 100 * (1 - SmoothS(Math.Clamp(h / 0.2, 0, 1))); // smoothstep(0, 0.2, h)
                return new Affine(Mid, Side, 0, -Px(height) * h, 0, 0, 1 + 0.5 * q, Math.Max(1 - q, 0.02));
            });
        }
        // hover: offset=(0, −px(amp)×W(ψ))、θ=tilt×W(ψ+0.25) 度、中心
        foreach (var wave in MotionTestKit.Waves)
        {
            yield return new("hover", [("amp", new NumberValue(100)), ("tilt", new NumberValue(15))], 2, 37, wave, psi =>
                new Affine(Mid, Mid, 0, -Px(100) * W(psi, wave), Rad(15 * W(psi + 0.25, wave)), 0, 1, 1));
        }
        // pulse: s=1+amp÷100×W(ψ)、heartbeat は g(u)=b(u−0.2)+0.6×b(u−0.4)、b(x)=exp(−(x÷0.05)²)
        foreach (var shape in new[] { "sine", "tri", "ease", "saw", "heartbeat" })
        {
            yield return new("pulse", [("shape", new StringValue(shape)), ("amp", new NumberValue(50)), ("pivot", new PointValue(0.3, 0.6))], 1, 0, null, psi =>
            {
                double B(double x) => Math.Exp(-(x / 0.05) * (x / 0.05));
                var g = shape switch
                {
                    "heartbeat" => B(Fract(psi) - 0.2) + 0.6 * B(Fract(psi) - 0.4),
                    "tri" => W(psi, WaveKind.Tri),
                    "ease" => W(psi, WaveKind.Ease),
                    "saw" => W(psi, WaveKind.Saw),
                    _ => W(psi, WaveKind.Sine),
                };
                var s = 1 + 0.5 * g;
                return new Affine(0.3 * Side, 0.6 * Side, 0, 0, 0, 0, s, s);
            });
        }
        // shake: K=rate、k0=H(seed, salt)、rx_j=2×u01(H3(k0,11,j))−1(ry は 12、rr は 13)、smooth は格子の補間
        foreach (var (smooth, cycles, phase, rate) in new[] { (false, 1, 0.0, 5), (false, 2, 37.0, 7), (true, 1, 0.0, 5), (true, 3, 37.0, 4) })
        {
            yield return new("shake", [("amp", new NumberValue(100)), ("rot", new NumberValue(20)), ("rate", new NumberValue(rate)), ("smooth", new BoolValue(smooth))],
                cycles, phase, null, psi =>
                {
                    var k0 = Hash.H(MotionTestKit.Seed, 1);
                    var period = (uint)(rate * cycles);
                    double V(uint use, uint j) => 2 * Hash.U01(Hash.H3(k0, use, j)) - 1;
                    double rx, ry, rr;
                    var x = psi * rate;
                    var i = (uint)Math.Floor(x) % period;
                    if (!smooth)
                    {
                        (rx, ry, rr) = (V(11, i), V(12, i), V(13, i));
                    }
                    else
                    {
                        var next = (i + 1) % period;
                        var f = SmoothS(Fract(x));
                        double Mix(uint use) => V(use, i) + (V(use, next) - V(use, i)) * f;
                        (rx, ry, rr) = (Mix(11), Mix(12), Mix(13));
                    }
                    return new Affine(Mid, Mid, Px(100) * rx, Px(100) * ry, Rad(20 * rr), 0, 1, 1);
                });
        }
        // wobble: θ=angle×W(ψ) 度、pivot
        foreach (var wave in MotionTestKit.Waves)
        {
            yield return new("wobble", [("angle", new NumberValue(45)), ("pivot", new PointValue(0.3, 0.8))], 1, 37, wave, psi =>
                new Affine(0.3 * Side, 0.8 * Side, 0, 0, Rad(45 * W(psi, wave)), 0, 1, 1));
        }
        // jelly: sy=1+amp÷100×W(ψ)、sx=1−0.8×amp÷100×W(ψ)、sh=wobble÷100×0.15×W(ψ+0.25)、pivot (0.5, 1.0)
        foreach (var wave in MotionTestKit.Waves)
        {
            yield return new("jelly", [("amp", new NumberValue(50)), ("wobble", new NumberValue(100))], 2, 0, wave, psi =>
            {
                var w = W(psi, wave);
                return new Affine(Mid, Side, 0, 0, 0, 0.15 * W(psi + 0.25, wave), 1 - 0.8 * 0.5 * w, 1 + 0.5 * w);
            });
        }
        // coinflip: c=cos(360°×ψ)(abs が 0.003 未満は符号を保って 0.003)、axis=y は x の拡大率、axis=x は y の拡大率、中心
        foreach (var axis in new[] { "y", "x" })
        {
            yield return new("coinflip", [("axis", new StringValue(axis))], 1, 0, null, psi =>
            {
                var c = Math.Cos(2 * Math.PI * psi);
                if (Math.Abs(c) < 0.003) c = c < 0 ? -0.003 : 0.003;
                return axis == "y" ? new Affine(Mid, Mid, 0, 0, 0, 0, c, 1) : new Affine(Mid, Mid, 0, 0, 0, 0, 1, c);
            });
        }
    }

    /// <summary>
    /// 出力の画素の中心 x の読む位置 M⁻¹(x) が、矩形の内側に 2 画素以上入るなら α は 255、外側に 2 画素以上離れるなら 0(キャッチマル・ロムの 4 タップが
    /// すべて矩形の中、またはすべて外)。その間の画素は調べない。調べた画素の数(内, 外)を返す。
    /// </summary>
    private static (int Inside, int Outside) CheckCoverage(byte[] px, Affine m, string what)
    {
        int inside = 0, outside = 0;
        for (var j = 0; j < Side; j++)
        for (var i = 0; i < Side; i++)
        {
            var (x, y) = m.Inverse(i + 0.5, j + 0.5);
            var a = px[(j * Side + i) * 4 + 3];
            if (x >= X0 + 2 && x <= X1 - 2 && y >= Y0 + 2 && y <= Y1 - 2)
            {
                Check.True(a >= 254, $"{what}: ({i}, {j}) は像の内側(読む位置 {x:0.##}, {y:0.##})なのに α={a}");
                inside++;
            }
            else if (x < X0 - 2 || x > X1 + 2 || y < Y0 - 2 || y > Y1 + 2)
            {
                Check.True(a <= 1, $"{what}: ({i}, {j}) は像の外側(読む位置 {x:0.##}, {y:0.##})なのに α={a}");
                outside++;
            }
        }
        return (inside, outside);
    }

    [Test]
    public void FR63_動きの7個は9_5の定義の式どおりに像を動かす()
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        var img = OpaqueRect();
        int[] frames = [0, 3, 8, 13, 16, 22, 30, 37];
        var seen = new HashSet<string>();
        foreach (var c in GeoCases())
        {
            var inv = MotionTestKit.Invocation(rs.Effects, c.Id, c.Cycles, c.Phase, c.Wave, 1, c.Values);
            var render = MotionTestKit.Render(rs, img, inv);
            var label = $"{c.Id} {string.Join(",", c.Values.Select(v => $"{v.Key}={v.Value}"))} cycles={c.Cycles} phase={c.Phase} wave={c.Wave}";
            int inside = 0, outside = 0;
            foreach (var k in frames)
            {
                var psi = c.Cycles * (double)k / N + c.Phase / 100;
                var (i, o) = CheckCoverage(render(k, N), c.Map(psi), $"{label} k={k}");
                inside += i;
                outside += o;
            }
            // つぶれ 100 の着地や coinflip の c≈0 では像が細くなるので、数はフレームの合計で問う
            Check.True(inside >= 300 * frames.Length, $"{label}: 内側として調べた画素が少ない({inside})");
            Check.True(outside >= 300 * frames.Length, $"{label}: 外側として調べた画素が少ない({outside})");
            seen.Add(c.Id);
        }
        Check.Equal(Ids.Length, seen.Count, "調べた id の数");
    }

    [Test]
    public void FR63_coinflipは半周で裏返った像になる()
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        var img = MotionTestKit.SmoothOpaque();
        foreach (var axis in new[] { "y", "x" })
        {
            var inv = MotionTestKit.Invocation(rs.Effects, "coinflip", values: ("axis", new StringValue(axis)));
            var px = MotionTestKit.Render(rs, img, inv)(N / 2, N); // ψ=0.5、c=−1
            for (var y = 0; y < Side; y++)
            for (var x = 0; x < Side; x++)
            {
                var (sx, sy) = axis == "y" ? (Side - 1 - x, y) : (x, Side - 1 - y);
                var e = img.At(sx, sy);
                var o = (y * Side + x) * 4;
                var d = Math.Max(Math.Max(Math.Abs(px[o] - e.R), Math.Abs(px[o + 1] - e.G)), Math.Max(Math.Abs(px[o + 2] - e.B), Math.Abs(px[o + 3] - e.A)));
                Check.True(d <= 1, $"axis={axis}: ({x}, {y}) が裏返った像の ({sx}, {sy}) と {d} 違う");
            }
        }
    }

    [Test]
    public void FR28_scaleの動きはamountが0なら入力をそのまま返す()
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        var img = MotionTestKit.SmoothOpaque();
        var still = img.Px;
        foreach (var id in Ids.Where(i => i != "coinflip"))
        {
            var inv = MotionTestKit.Invocation(rs.Effects, id, cycles: 2, phase: 37) with { Amount = 0 };
            var px = MotionTestKit.Render(rs, img, inv)(13, N);
            Check.True(MotionTestKit.MaxDiff(still, px) <= 1, $"{id}: amount 0 の結果と入力の差 {MotionTestKit.MaxDiff(still, px)}");
        }
    }

    // ---- AC-36 の pulse の分(1)(2)(4)(9)と shake の (5) ----

    /// <summary>100×50 の不透明な画像(左半分が赤、右半分が青。上の 10 行は少し明るい)。P0 の MotionRenderAc36Tests と同じ絵。</summary>
    private static CompositeImg LeftRight() => CompositeImg.Make(100, 50, (x, y) =>
        x < 50 ? (255, y < 10 ? 120 : 0, 0, 255) : (0, y < 10 ? 120 : 0, 255, 255));

    private static DocumentImage RenderAt(CompositeKit kit, SessionDocument doc, int lout, int k, int n = N) =>
        kit.Renderer.RenderAsync(doc, lout, DocumentPixelFormat.StraightRgba8, k, n).GetAwaiter().GetResult();

    /// <summary>AC-36 の (1) の文書: 土台 200×200 の透明で表示がオフ、2 枚目 100×50 を (40, −30, 100) に置き(枠は x 90〜189、y 45〜94)、2 枚目のモーションに id。</summary>
    private static void BuildFramed(CompositeKit kit, string id, params (string Key, ParamValue Value)[] values)
    {
        kit.Import(CompositeImg.Solid(200, 200, 0, 0, 0, 0), LeftRight());
        kit.Place(1, 40, -30, 100);
        Check.True(kit.Session.SetLayerVisible(0, false).Changed, "土台の表示をオフ");
        kit.AddEffect(Target.OfLayer(1), id, values);
    }

    /// <summary>pulse(shape=sine、cycles=1、amp=50)。shape と cycles は既定のまま(既定と同じ値は SetEffectParam が変更にしない)。</summary>
    private static readonly (string, ParamValue)[] Pulse50 = [("amp", new NumberValue(50))];

    [Test]
    public void AC36_1_pulseは枠の中心で拡大し参照の文書と枠の中で一致する()
    {
        using var kit = new CompositeKit();
        BuildFramed(kit, "pulse", Pulse50);
        var im = RenderAt(kit, kit.Doc, 200, K);
        using var refKit = new CompositeKit();
        refKit.Import(LeftRight());
        refKit.AddEffect(Target.OfLayer(0), "pulse", Pulse50);
        var reference = RenderAt(refKit, refKit.Doc, 100, K);
        CompositeCheck.Size(reference, 100, 50, "参照");
        for (var y = 0; y < 50; y++)
        for (var x = 0; x < 100; x++)
            CompositeCheck.Pixel(im, 90 + x, 45 + y, CompositeCheck.At(reference, x, y), 3, "pulse の枠の中");
    }

    [Test]
    public void AC36_2_pulseの拡大した像は枠の外に出る()
    {
        using var kit = new CompositeKit();
        BuildFramed(kit, "pulse", Pulse50);
        var im = RenderAt(kit, kit.Doc, 200, K); // t=0.25 で s=1.5。像は x が 65〜214、y が 33〜106
        Check.Equal(255, CompositeCheck.At(im, 80, 70).A, "pulse: 枠の外の点 (80, 70) の α");
        Check.Equal(255, CompositeCheck.At(im, 140, 36).A, "pulse: 枠の上の外の点 (140, 36) の α");
        Check.Equal(0, CompositeCheck.At(im, 60, 70).A, "pulse: 像の左の外 (60, 70) の α");
        Check.Equal(0, CompositeCheck.At(im, 140, 28).A, "pulse: 像の上の外 (140, 28) の α");
    }

    [Test]
    public void AC36_9_pulseの枠を基準にした動きの継ぎ目はt1とt0が1以内()
    {
        using var kit = new CompositeKit();
        BuildFramed(kit, "pulse", Pulse50);
        var t0 = RenderAt(kit, kit.Doc, 200, 0);
        var t1 = RenderAt(kit, kit.Doc, 200, N);
        var d = MotionTestKit.MaxDiff(t0.Pixels, t1.Pixels);
        Check.True(d <= 1, $"pulse: t=1 と t=0 の差の最大 {d}");
    }

    [Test]
    public void AC36_4_2枚目の表示がオフでも全体のpulseがあればフレーム数は40()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(200, 200, 30, 60, 90), LeftRight());
        kit.Place(1, 40, -30, 100);
        kit.AddEffect(Target.OfLayer(1), "spin");
        Check.Equal(TimeSettings.Default, kit.Doc.Time, "L=2.0・F=20(既定)");
        Check.Equal(40, MotionInfo.FrameCount(kit.Doc, kit.Rs.Effects), "2 枚目に spin");
        Check.True(kit.Session.SetLayerVisible(1, false).Changed, "2 枚目の表示をオフ");
        Check.Equal(1, MotionInfo.FrameCount(kit.Doc, kit.Rs.Effects), "2 枚目の表示がオフ");
        var row = kit.AddEffect(Target.All, "pulse");
        Check.Equal(40, MotionInfo.FrameCount(kit.Doc, kit.Rs.Effects), "全体にオンの pulse");
        Check.True(kit.Session.SetEffectEnabled(Target.All, row, false).Changed, "全体の pulse の目をオフ");
        Check.Equal(1, MotionInfo.FrameCount(kit.Doc, kit.Rs.Effects), "全体の pulse の目がオフ");
    }

    /// <summary>AC-36 の (5) の文書: 土台 200×200 の表示がオフ、2 枚目 100×50 に shake、それを複製した 3 枚目(同じ配置)。</summary>
    private static void BuildShake(CompositeKit kit)
    {
        kit.Import(CompositeImg.Solid(200, 200, 0, 0, 0, 0), LeftRight());
        kit.Place(1, 40, -30, 100);
        Check.True(kit.Session.SetLayerVisible(0, false).Changed, "土台の表示をオフ");
        kit.AddEffect(Target.OfLayer(1), "shake");
        Check.True(kit.Session.DuplicateLayer(1).Changed, "2 枚目を複製");
    }

    private static SessionDocument Only(SessionDocument doc, int index) =>
        doc with { Layers = doc.Layers.Select((l, i) => i == 0 ? l : l with { Visible = i == index }).ToArray() };

    private static byte[][] Frames(CompositeKit kit, SessionDocument doc) =>
        Enumerable.Range(0, N).Select(k => RenderAt(kit, doc, 200, k).Pixels).ToArray();

    private static int DifferingFrames(byte[][] a, byte[][] b) =>
        Enumerable.Range(0, a.Length).Count(k => MotionTestKit.MaxDiff(a[k], b[k]) >= 1);

    [Test]
    public void AC36_5_shakeを複製した行はsaltが違い結果が違う()
    {
        using var kit = new CompositeKit();
        BuildShake(kit);
        var doc = kit.Doc;
        Check.Equal(3, doc.Layers.Count, "レイヤーの数");
        Check.Equal(doc.Layers[1].Place, doc.Layers[2].Place, "複製は元と同じ配置");
        Check.True(doc.Layers[1].Stack[0].Salt != doc.Layers[2].Stack[0].Salt, "複製の行は新しい salt");
        var second = Frames(kit, Only(doc, 1));
        var third = Frames(kit, Only(doc, 2));
        var differ = DifferingFrames(second, third);
        Check.True(differ >= 10, $"2 枚目だけと 3 枚目だけで違うフレームの数 {differ}(10 以上)");

        // 同じ文書を別の描画で 2 回描くと完全に一致する
        using var again = new CompositeKit();
        BuildShake(again);
        var secondAgain = Frames(again, Only(again.Doc, 1));
        for (var k = 0; k < N; k++) Check.SequenceEqual(second[k], secondAgain[k], $"2 回描いたフレーム {k}");

        // seed を 54321 にすると違う
        var other = Frames(kit, Only(doc with { Seed = 54321 }, 1));
        var seedDiffer = DifferingFrames(second, other);
        Check.True(seedDiffer >= 10, $"seed=54321 と 12345 で違うフレームの数 {seedDiffer}(10 以上)");
    }
}
