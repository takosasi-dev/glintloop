// 乱れと粒のモーション 5 個(glitch・ripple・sparkle・particles・grainmove。§9.5)を WARP で調べる: AC-35 の (5)(6) の宣言、
// AC-34 の (1) 閉じ方・(2) 連続・(3) 周期(乱数の格子を使う glitch・grainmove は (3) の対象外)、§9.7 の規則 12 の (d)(e) と枠を基準にした読み替え。
// 依頼と検査は MotionTestKit、文書は CompositeKit。AC-34 は組み合わせが多いので Slow にする。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Motions;

public sealed class NoiseEffectsTests
{
    private const int N = 40;
    private const int K = 10;

    /// <summary>この担当の 5 個の id・名前・group(§9.5 の表)。</summary>
    private static readonly (string Id, string Name, string Group)[] Effects =
    [
        ("glitch", "グリッチ", "乱れ"),
        ("ripple", "波紋", "乱れ"),
        ("sparkle", "キラキラ", "粒"),
        ("particles", "粒子", "粒"),
        ("grainmove", "動くグレイン", "粒"),
    ];

    /// <summary>§9.5 の表のパラメータ(キー・型・最小・最大・既定)。enum は options の value の並び、color と point は既定だけ。</summary>
    private static readonly Dictionary<string, (string Key, ParamType Type, double? Min, double? Max, object Default)[]> Declared = new()
    {
        ["glitch"] =
        [
            ("intensity", ParamType.Float, 0, 100, 50.0), ("steps", ParamType.Int, 2, 24, 10.0),
            ("bands", ParamType.Int, 1, 8, 4.0), ("rgb", ParamType.Float, 0, 100, 50.0),
        ],
        ["ripple"] =
        [
            ("amp", ParamType.Float, 0, 40, 8.0), ("len", ParamType.Float, 30, 500, 120.0),
            ("center", ParamType.Point, null, null, new PointValue(0.5, 0.5)), ("fall", ParamType.Float, 0, 100, 50.0),
        ],
        ["sparkle"] =
        [
            ("count", ParamType.Int, 1, 40, 12.0), ("size", ParamType.Float, 10, 200, 60.0),
            ("color", ParamType.Color, null, null, "#FFFFFFFF"), ("intensity", ParamType.Float, 0, 300, 100.0),
        ],
        ["particles"] =
        [
            ("kind", ParamType.Enum, null, null, "snow|dots|bubbles|stars|confetti"), ("dir", ParamType.Enum, null, null, "down|up"),
            ("count", ParamType.Int, 5, 120, 40.0), ("size", ParamType.Float, 5, 60, 18.0),
            ("tone", ParamType.Enum, null, null, "white|rainbow|custom"), ("color", ParamType.Color, null, null, "#FFFFFFFF"),
            ("opacity", ParamType.Float, 0, 100, 85.0),
        ],
        ["grainmove"] =
        [
            ("strength", ParamType.Float, 0, 100, 25.0), ("size", ParamType.Float, 0.5, 20, 2.0),
            ("mono", ParamType.Bool, null, null, true), ("rate", ParamType.Int, 1, 24, 6.0),
        ],
    };

    /// <summary>amount の列(§9.5): どれも scale で、scaleParams は 1 つ。</summary>
    private static readonly Dictionary<string, string> ScaleParam = new()
    {
        ["glitch"] = "intensity", ["ripple"] = "amp", ["sparkle"] = "intensity", ["particles"] = "opacity", ["grainmove"] = "strength",
    };

    [Test]
    public void AC35_5_6_乱れと粒の5個は表のidと名前とgroupで読み込まれcyclesとphaseがあり波形が無い()
    {
        using var kit = new CompositeKit();
        foreach (var (id, name, group) in Effects)
        {
            var def = kit.Rs.Effects.Find(id);
            Check.True(def is not null, $"{id} がカタログに無い");
            Check.Equal(name, def!.Name, $"{id} の名前");
            Check.Equal(group, def.Group, $"{id} の group");
            Check.Equal(EffectStage.Motion, def.Stage, $"{id} のステージ");
            Check.True(def.TimeDependent, $"{id} の timeDependent");
            Check.True(def.NoWave, $"{id} は波形なし");
            Check.Equal(AmountMode.Scale, def.AmountMode, $"{id} の amountMode");
            Check.SequenceEqual([ScaleParam[id]], def.ScaleParams, $"{id} の scaleParams");

            var all = EffectSchema.AllParams(def);
            var expected = Declared[id];
            Check.SequenceEqual(["cycles", "phase", .. expected.Select(e => e.Key)], all.Select(p => p.Key).ToArray(), $"{id} のパラメータの並び");
            Check.Equal(ParamType.Int, all[0].Type, $"{id} の cycles の型");
            Check.Equal((double?)1, all[0].Min, $"{id} の cycles の最小");
            Check.Equal((double?)8, all[0].Max, $"{id} の cycles の最大");
            Check.Equal<ParamValue>(new NumberValue(1), all[0].Default, $"{id} の cycles の既定");
            Check.Equal((double?)0, all[1].Min, $"{id} の phase の最小");
            Check.Equal((double?)100, all[1].Max, $"{id} の phase の最大");
            Check.Equal<ParamValue>(new NumberValue(0), all[1].Default, $"{id} の phase の既定");
            for (var i = 0; i < expected.Length; i++)
            {
                var (key, type, min, max, dflt) = expected[i];
                var p = all[i + 2];
                var what = $"{id} の {key}";
                Check.Equal(type, p.Type, what + " の型");
                switch (dflt)
                {
                    case double d:
                        Check.Equal(min, p.Min, what + " の最小");
                        Check.Equal(max, p.Max, what + " の最大");
                        Check.Equal<ParamValue>(new NumberValue(d), p.Default, what + " の既定");
                        break;
                    case bool b:
                        Check.Equal<ParamValue>(new BoolValue(b), p.Default, what + " の既定");
                        break;
                    case PointValue pt:
                        Check.Equal<ParamValue>(pt, p.Default, what + " の既定");
                        break;
                    case string s when type == ParamType.Enum:
                        var values = s.Split('|');
                        Check.SequenceEqual(values, p.Options!.Select(o => o.Value).ToArray(), what + " の options");
                        Check.Equal<ParamValue>(new StringValue(values[0]), p.Default, what + " の既定");
                        break;
                    case string s:
                        Check.Equal<ParamValue>(new StringValue(s), p.Default, what + " の既定");
                        break;
                }
            }
        }
        // particles の color は tone=custom のときだけ表示する(§9.5 の表)
        var color = kit.Rs.Effects.Find("particles")!.FindParam("color")!;
        Check.True(color.VisibleWhen is not null, "particles の color の visibleWhen");
    }

    // ---- AC-34 の (1)(2)(3) ----

    private static void SeamChecks(string id, bool periodic)
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        var images = MotionTestKit.Ac34Images();
        for (var m = 0; m < images.Count; m++)
        foreach (var inv in MotionTestKit.Ac34Variants(rs.Effects, id))
        {
            var img = images[m];
            var what = $"{id} 画像 {m + 1} cycles={inv.Cycles} phase={inv.Phase}";
            var render = MotionTestKit.Render(rs, img, inv);
            MotionTestKit.Pass(MotionTestKit.CheckClosure(render), what);
            // 5 個とも波形なし(saw の設定が無い)ので、連続の検査はすべての組み合わせで行う
            MotionTestKit.Pass(MotionTestKit.CheckContinuity(render), what);
            if (periodic && inv.Cycles == 2) MotionTestKit.Pass(MotionTestKit.CheckPeriod(render), what);
        }
    }

    [Test(Slow = true)]
    public void AC34_1_2_glitchの継ぎ目() => SeamChecks("glitch", periodic: false);

    [Test(Slow = true)]
    public void AC34_1_2_3_rippleの継ぎ目() => SeamChecks("ripple", periodic: true);

    [Test(Slow = true)]
    public void AC34_1_2_3_sparkleの継ぎ目() => SeamChecks("sparkle", periodic: true);

    [Test(Slow = true)]
    public void AC34_1_2_3_particlesの継ぎ目()
    {
        SeamChecks("particles", periodic: true);
        // 形と向きと色の選択肢も、閉じ方と周期を確かめる(既定の snow・down・white のほか)
        using var kit = new CompositeKit();
        var img = MotionTestKit.SmoothOpaque();
        foreach (var kind in new[] { "dots", "bubbles", "stars", "confetti" })
        foreach (var (dir, tone) in new[] { ("down", "rainbow"), ("up", "custom") })
        {
            var inv = MotionTestKit.Invocation(kit.Rs.Effects, "particles", 2, 37, null, 1,
                ("kind", new StringValue(kind)), ("dir", new StringValue(dir)), ("tone", new StringValue(tone)),
                ("color", new StringValue("#3366CCB0")), ("size", new NumberValue(60)));
            var render = MotionTestKit.Render(kit.Rs, img, inv);
            var what = $"particles kind={kind} dir={dir} tone={tone}";
            MotionTestKit.Pass(MotionTestKit.CheckClosure(render), what);
            MotionTestKit.Pass(MotionTestKit.CheckPeriod(render), what);
        }
    }

    [Test(Slow = true)]
    public void AC34_1_2_grainmoveの継ぎ目() => SeamChecks("grainmove", periodic: false);

    // ---- §9.7 の規則 12 ----

    /// <summary>100×50 の不透明な画像(横と縦の階調。縞で横ずれが見える)。</summary>
    private static CompositeImg Gradient() => CompositeImg.Make(100, 50, (x, y) =>
        ((x * 5) % 256, y * 5, (x / 10) % 2 == 0 ? 60 : 200, 255));

    private static DocumentImage RenderAt(CompositeKit kit, int lout, int k) =>
        kit.Renderer.RenderAsync(kit.Doc, lout, DocumentPixelFormat.StraightRgba8, k, N).GetAwaiter().GetResult();

    /// <summary>AC-36 の (1) と同じ文書: 土台 200×200 の透明で表示がオフ、2 枚目 100×50 を (40, −30, 100) に置く(枠は (90, 45) から 100×50)。</summary>
    private static (DocumentImage Framed, DocumentImage Reference, long FramedSalt, long ReferenceSalt) FramedAndReference(
        string id, int k, params (string Key, ParamValue Value)[] values)
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(200, 200, 0, 0, 0, 0), Gradient());
        kit.Place(1, 40, -30, 100);
        Check.True(kit.Session.SetLayerVisible(0, false).Changed, "土台の表示をオフ");
        var row = kit.AddEffect(Target.OfLayer(1), id, values);
        var framed = RenderAt(kit, 200, k);
        var framedSalt = kit.Doc.Layers[1].Stack[row].Salt;

        using var refKit = new CompositeKit();
        refKit.Import(Gradient());
        var refRow = refKit.AddEffect(Target.OfLayer(0), id, values);
        var reference = RenderAt(refKit, 100, k);
        return (framed, reference, framedSalt, refKit.Doc.Layers[0].Stack[refRow].Salt);
    }

    private static readonly (string Id, (string, ParamValue)[] Values)[] FramedCases =
    [
        ("glitch", [("intensity", new NumberValue(100)), ("bands", new NumberValue(8))]),
        ("ripple", [("amp", new NumberValue(40))]),
        ("sparkle", [("size", new NumberValue(200)), ("count", new NumberValue(40))]),
        ("particles", [("kind", new StringValue("dots")), ("size", new NumberValue(60)), ("count", new NumberValue(120))]),
        ("grainmove", [("strength", new NumberValue(100)), ("size", new NumberValue(20))]),
    ];

    [Test]
    public void 規則12_乱れと粒は枠を基準にして参照の文書と枠の中で一致する()
    {
        var source = Gradient();
        foreach (var (id, values) in FramedCases)
        {
            var (framed, reference, fs, rsalt) = FramedAndReference(id, K, values);
            Check.Equal(rsalt, fs, $"{id} の salt(2 つの文書でそろえる)");
            CompositeCheck.Size(reference, 100, 50, "参照");
            var changed = false;
            for (var y = 0; y < 50 && !changed; y++)
            for (var x = 0; x < 100 && !changed; x++)
            {
                var a = CompositeCheck.At(reference, x, y);
                var i = (y * 100 + x) * 4;
                changed = Math.Abs(a.R - source.Px[i]) > 2 || Math.Abs(a.G - source.Px[i + 1]) > 2 || Math.Abs(a.B - source.Px[i + 2]) > 2;
            }
            Check.True(changed, $"{id} が参照の画像を変えていない(検査の前提)");
            // ripple は作業面の外を clamp で読む(規則 12 の (c))ので、枠の縁の 2 画素は枠の外の透明を読みうる。縁を除いて比べる
            var margin = id == "ripple" ? 2 : 0;
            for (var y = margin; y < 50 - margin; y++)
            for (var x = margin; x < 100 - margin; x++)
                CompositeCheck.Pixel(framed, 90 + x, 45 + y, CompositeCheck.At(reference, x, y), 3, $"{id} の枠の中 ({x}, {y})");
        }
    }

    private static bool InFrame(int x, int y) => x is >= 90 and < 190 && y is >= 45 and < 95;

    [Test]
    public void 規則12d_glitchのwrapは枠の内側で枠の外の画素を変えない()
    {
        var (framed, _, _, _) = FramedAndReference("glitch", K, ("intensity", new NumberValue(100)), ("bands", new NumberValue(8)));
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
        {
            if (InFrame(x, y)) continue;
            Check.Equal(0, CompositeCheck.At(framed, x, y).A, $"glitch: 枠の外 ({x}, {y}) の α");
        }
        // 枠の中は不透明のまま(wrap が枠の外の透明を読まない)
        for (var y = 45; y < 95; y++)
        for (var x = 90; x < 190; x++)
            Check.Equal(255, CompositeCheck.At(framed, x, y).A, $"glitch: 枠の中 ({x}, {y}) の α");
    }

    [Test]
    public void 規則12e_particlesの粒は枠の外の透明な部分にも乗る()
    {
        var outside = 0;
        foreach (var k in new[] { 0, 7, K, 23 })
        {
            var (framed, _, _, _) = FramedAndReference("particles", k,
                ("kind", new StringValue("dots")), ("size", new NumberValue(60)), ("count", new NumberValue(120)));
            for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
                if (!InFrame(x, y) && CompositeCheck.At(framed, x, y).A > 0) outside++;
        }
        Check.True(outside > 0, "粒が枠の外に 1 画素も乗らない");
    }

    [Test]
    public void 規則12_sparkleは透明な部分に乗らずαを変えない()
    {
        var (framed, _, _, _) = FramedAndReference("sparkle", K, ("size", new NumberValue(200)), ("count", new NumberValue(40)));
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
            Check.Equal(InFrame(x, y) ? 255 : 0, CompositeCheck.At(framed, x, y).A, $"sparkle: ({x}, {y}) の α");
    }

    [Test]
    public void FR28_amountが0のときは入力をそのまま返す()
    {
        using var kit = new CompositeKit();
        var img = MotionTestKit.Translucent();
        foreach (var (id, _, _) in Effects)
        {
            var inv = MotionTestKit.Invocation(kit.Rs.Effects, id, 3, 37) with { Amount = 0 };
            var render = MotionTestKit.Render(kit.Rs, img, inv);
            var plain = MotionTestKit.Render(kit.Rs, img, []);
            foreach (var k in new[] { 0, 13, 29 })
                Check.True(MotionTestKit.MaxDiff(plain(k, N), render(k, N)) <= 1, $"{id}: amount 0 のフレーム {k}");
        }
    }
}
