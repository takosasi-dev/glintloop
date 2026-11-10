// ルックの「基本」と「色」の 13 個(exposure・contrast・levels・tone・fade・wb・saturation・hue・splittone・gradmap・mono・invert・channelmix)のテスト(WARP だけ)。
// AC-14 の (1): §9.4 の定義を C# の double で書き直した参照と、代表の 12 色で 8 ビットの差が 3 以内。EffectDef の既定値と範囲は表の値と照合する。
// あわせて amount(mix)の効きと、範囲外の値を描画に直接渡しても NaN・無限大が出ないこと(§9.7 の規則 4 の 4・5)を確かめる。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Dither;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Effects;

public sealed class LooksBasicColorTests
{
    // ==== §9.4 の表(テストの側に持つ写し。EffectDef と食い違えば失敗する) ====

    /// <summary>表の 1 つのパラメータ。OptionLabels は表に書いてあるラベルだけ(書いていないものは null)。Step が null なら §9.4 の冒頭の規則。</summary>
    private sealed record Spec(
        string Key, ParamType Type, double Min, double Max, ParamValue Default, string Unit,
        double? Step = null, string[]? Options = null, string?[]? OptionLabels = null, bool Handle = true,
        string? VisibleKey = null, string[]? VisibleValues = null);

    private readonly record struct Rgb(double R, double G, double B)
    {
        public double this[int k] => k switch { 0 => R, 1 => G, _ => B };
        public Rgb Map(Func<double, double> f) => new(f(R), f(G), f(B));
        public Rgb Scale(double s) => new(R * s, G * s, B * s);
    }

    /// <summary>参照実装が読むパラメータの値(表の既定値に、組の値を重ねたもの)。</summary>
    private sealed class V(IReadOnlyDictionary<string, ParamValue> map)
    {
        public double N(string key) => ((NumberValue)map[key]).Value;
        public string S(string key) => ((StringValue)map[key]).Value;
    }

    private sealed record Fx(string Id, string Name, string Group, Spec[] Params, Func<V, Rgb, Rgb> Reference)
    {
        /// <summary>1 つのパラメータを動かすときの土台の組(gradmap は n=3 も)。</summary>
        public Dictionary<string, ParamValue>[] Bases { get; init; } = [new()];

        /// <summary>1 つずつ動かす組のほかに確かめる組。</summary>
        public Dictionary<string, ParamValue>[] Extras { get; init; } = [];

        /// <summary>amount を確かめる、効きの大きい組。</summary>
        public Dictionary<string, ParamValue> Strong { get; init; } = new();

        /// <summary>範囲外を直接渡すときに足す組(0 除算になりうる組など)。</summary>
        public Dictionary<string, ParamValue>[] OutOfRange { get; init; } = [];
    }

    private static Spec Fl(string key, double min, double max, double def, string unit = "", double? step = null) =>
        new(key, ParamType.Float, min, max, new NumberValue(def), unit, step);

    private static Spec Col(string key, string def) => new(key, ParamType.Color, double.NaN, double.NaN, new StringValue(def), "");

    private static Dictionary<string, ParamValue> Num(params (string Key, double Value)[] items) =>
        items.ToDictionary(x => x.Key, x => (ParamValue)new NumberValue(x.Value));

    private static Dictionary<string, ParamValue> Str(params (string Key, string Value)[] items) =>
        items.ToDictionary(x => x.Key, x => (ParamValue)new StringValue(x.Value));

    private static Dictionary<string, ParamValue> Merge(Dictionary<string, ParamValue> a, Dictionary<string, ParamValue> b)
    {
        var r = new Dictionary<string, ParamValue>(a);
        foreach (var (k, v) in b) r[k] = v;
        return r;
    }

    private static readonly Fx[] Table = BuildTable();

    private static Fx[] BuildTable()
    {
        var n3 = Str(("n", "3"));
        return
        [
            new("exposure", "露出", "基本", [Fl("ev", -3, 3, 0, "EV", 0.01)],
                (v, c) => c.Scale(Math.Pow(2, v.N("ev"))))
            { Strong = Num(("ev", 1.5)) },

            new("contrast", "コントラスト", "基本", [Fl("c", -100, 100, 0, "%")],
                (v, c) =>
                {
                    var k = v.N("c");
                    return InSrgb(c, x => k >= 0 ? x + (Smooth(x) - x) * (k / 50) : x + (0.5 - x) * (-k / 100 * 0.8));
                })
            { Strong = Num(("c", 80)) },

            new("levels", "レベル", "基本", [Fl("black", 0, 90, 0, "%"), Fl("white", 10, 100, 100, "%"), Fl("gamma", 0.2, 5, 1.0)],
                (v, c) =>
                {
                    double b = v.N("black") / 100, w = v.N("white") / 100, g = v.N("gamma");
                    return InSrgb(c, x => Math.Pow(MathX.Sat((x - b) / (w - b)), 1 / g));
                })
            {
                Extras = [Num(("black", 40), ("white", 45)), Num(("black", 20), ("white", 80), ("gamma", 2.5))],
                Strong = Num(("black", 20), ("white", 80), ("gamma", 2)),
                OutOfRange = [Num(("black", 50), ("white", 50)), Num(("black", 80), ("white", 20)), Num(("gamma", 0)), Num(("gamma", -1))],
            },

            new("tone", "影とハイライト", "基本", [Fl("shadows", -100, 100, 0, "%"), Fl("highlights", -100, 100, 0, "%")],
                (v, c) =>
                {
                    var e = E(c);
                    return c.Scale(Math.Pow(2, 1.5 * (v.N("shadows") * (1 - e) * (1 - e) + v.N("highlights") * e * e) / 100));
                })
            { Strong = Num(("shadows", 80), ("highlights", -60)) },

            new("fade", "フェード", "基本", [Fl("lift", 0, 40, 10, "%"), Fl("drop", 0, 40, 0, "%")],
                (v, c) =>
                {
                    double l = v.N("lift") / 100, d = v.N("drop") / 100;
                    return InSrgb(c, x => l + x * (1 - l - d));
                })
            { Strong = Num(("lift", 30), ("drop", 20)) },

            new("wb", "色温度", "色", [Fl("temp", -100, 100, 0), Fl("tint", -100, 100, 0)],
                (v, c) =>
                {
                    double t = v.N("temp") / 100, g = v.N("tint") / 100;
                    return new Rgb(c.R * Math.Pow(2, 0.4 * t + 0.1 * g), c.G * Math.Pow(2, -0.3 * g), c.B * Math.Pow(2, -0.4 * t + 0.1 * g));
                })
            { Strong = Num(("temp", 80), ("tint", -50)) },

            new("saturation", "彩度", "色", [Fl("saturation", -100, 100, 0, "%"), Fl("vibrance", -100, 100, 0, "%")],
                (v, c) =>
                {
                    var lab = Oklab.ToOk(c.R, c.G, c.B);
                    var ch = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
                    var k = Math.Max(0, (1 + v.N("saturation") / 100) * (1 + v.N("vibrance") / 100 * (1 - Math.Min(ch / 0.32, 1))));
                    return FromOk(new Lab(lab.L, lab.A * k, lab.B * k));
                })
            { Strong = Num(("saturation", 80), ("vibrance", 50)) },

            new("hue", "色相", "色", [new Spec("shift", ParamType.Angle, -180, 180, new NumberValue(0), "deg", Handle: false)],
                (v, c) =>
                {
                    var lab = Oklab.ToOk(c.R, c.G, c.B);
                    var s = v.N("shift") * Math.PI / 180;
                    return FromOk(new Lab(lab.L, lab.A * Math.Cos(s) - lab.B * Math.Sin(s), lab.A * Math.Sin(s) + lab.B * Math.Cos(s)));
                })
            { Strong = Num(("shift", 120)) },

            new("splittone", "分割トーン", "色",
                [Col("shadow", "#3A6EA5FF"), Col("highlight", "#FFB070FF"), Fl("balance", -100, 100, 0, "%"), Fl("strength", 0, 100, 50, "%")],
                (v, c) =>
                {
                    var e = E(c);
                    var p = 0.5 + 0.004 * v.N("balance");
                    var wH = SmoothStep(p - 0.5, p + 0.5, e);
                    var wS = 1 - wH;
                    var k = v.N("strength") / 100;
                    var sh = HexSrgb(v.S("shadow"));
                    var hi = HexSrgb(v.S("highlight"));
                    var x = c.Map(X);
                    var y = new double[3];
                    for (var i = 0; i < 3; i++)
                    {
                        var xs = SoftLight(x[i], sh[i]);
                        var xh = SoftLight(x[i], hi[i]);
                        y[i] = x[i] + (xs - x[i]) * wS * k + (xh - x[i]) * wH * k;
                    }
                    return new Rgb(Srgb.Lin(y[0]), Srgb.Lin(y[1]), Srgb.Lin(y[2]));
                })
            {
                Extras = [Merge(Num(("strength", 100), ("balance", -60)), Str(("shadow", "#00FF40FF"), ("highlight", "#8000FFFF")))],
                Strong = Merge(Num(("strength", 100), ("balance", 30)), Str(("shadow", "#0040FFFF"))),
            },

            new("gradmap", "グラデーションマップ", "色",
                [
                    new Spec("n", ParamType.Enum, double.NaN, double.NaN, new StringValue("2"), "", Options: ["2", "3"], OptionLabels: ["2色", "3色"]),
                    Col("c0", "#1B1033FF"),
                    Col("c1", "#FF7AA8FF") with { VisibleKey = "n", VisibleValues = ["3"] },
                    Col("c2", "#FFE9B0FF"),
                    Fl("mid", 10, 90, 50, "%") with { VisibleKey = "n", VisibleValues = ["3"] },
                ],
                (v, c) =>
                {
                    var t = E(c);
                    var ok0 = OkOfHex(v.S("c0"));
                    var ok2 = OkOfHex(v.S("c2"));
                    Lab lab;
                    if (v.S("n") == "3")
                    {
                        var ok1 = OkOfHex(v.S("c1"));
                        var m = v.N("mid") / 100;
                        lab = t <= m ? LerpLab(ok0, ok1, t / m) : LerpLab(ok1, ok2, (t - m) / (1 - m));
                    }
                    else
                    {
                        lab = LerpLab(ok0, ok2, t);
                    }
                    return FromOk(lab);
                })
            {
                Bases = [new(), n3],
                Strong = n3,
                OutOfRange = [Merge(n3, Num(("mid", 0))), Merge(n3, Num(("mid", 100)))],
            },

            new("mono", "白黒", "色", [Fl("rw", 0, 100, 30, "%"), Fl("gw", 0, 100, 59, "%"), Fl("bw", 0, 100, 11, "%")],
                (v, c) =>
                {
                    double rw = v.N("rw"), gw = v.N("gw"), bw = v.N("bw");
                    if (rw + gw + bw == 0) (rw, gw, bw) = (30, 59, 11);
                    var g = (rw * X(c.R) + gw * X(c.G) + bw * X(c.B)) / (rw + gw + bw);
                    var l = Srgb.Lin(g);
                    return new Rgb(l, l, l);
                })
            {
                Extras = [Num(("rw", 0), ("gw", 0), ("bw", 0)), Num(("rw", 100), ("gw", 0), ("bw", 0)), Num(("rw", 0), ("gw", 0), ("bw", 100))],
                Strong = Num(("rw", 80), ("gw", 10), ("bw", 10)),
                OutOfRange = [Num(("rw", -50), ("gw", 50), ("bw", 0))],
            },

            new("invert", "色の反転", "色", [], (v, c) => InSrgb(c, x => 1 - x)),

            new("channelmix", "チャンネル入れ替え", "色",
                [new Spec("order", ParamType.Enum, double.NaN, double.NaN, new StringValue("rgb"), "",
                    Options: ["rgb", "rbg", "grb", "gbr", "brg", "bgr"], OptionLabels: ["そのまま", null, null, null, null, null])],
                (v, c) =>
                {
                    var o = v.S("order");
                    double Pick(char ch) => ch switch { 'r' => c.R, 'g' => c.G, _ => c.B };
                    return new Rgb(Pick(o[0]), Pick(o[1]), Pick(o[2]));
                })
            { Strong = Str(("order", "gbr")) },
        ];
    }

    // ==== §9.4 の記法を double で ====

    /// <summary>「sRGB で」の x=enc(C)(0〜1 に切ってから)。</summary>
    private static double X(double c) => Srgb.Enc(MathX.Sat(c));

    /// <summary>sRGB の値 x に f を掛けて lin で線形に戻す(結果は切らない)。</summary>
    private static Rgb InSrgb(Rgb c, Func<double, double> f) => c.Map(ch => Srgb.Lin(f(X(ch))));

    private static double Smooth(double x) => x * x * (3 - 2 * x);

    /// <summary>E(C)=enc(sat(Y(C)))。</summary>
    private static double E(Rgb c) => X(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);

    private static double SmoothStep(double e0, double e1, double x)
    {
        if (e0 >= e1) return x >= e1 ? 1 : 0;
        var t = MathX.Sat((x - e0) / (e1 - e0));
        return t * t * (3 - 2 * t);
    }

    private static double SoftLight(double cb, double cs)
    {
        static double D(double x) => x <= 0.25 ? ((16 * x - 12) * x + 4) * x : Math.Sqrt(x);
        return cs <= 0.5 ? cb - (1 - 2 * cs) * cb * (1 - cb) : cb + (2 * cs - 1) * (D(cb) - cb);
    }

    /// <summary>#RRGGBBAA の sRGB の値(0〜1)。不透明度は読まない。</summary>
    private static double[] HexSrgb(string hex)
    {
        var v = Convert.ToUInt32(hex[1..], 16);
        return [((v >> 24) & 0xFF) / 255.0, ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0];
    }

    private static Lab OkOfHex(string hex)
    {
        var s = HexSrgb(hex);
        return Oklab.ToOk(Srgb.Lin(s[0]), Srgb.Lin(s[1]), Srgb.Lin(s[2]));
    }

    private static Lab LerpLab(Lab a, Lab b, double t) => new(a.L + (b.L - a.L) * t, a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t);

    private static Rgb FromOk(Lab lab)
    {
        var c = Oklab.FromOk(lab);
        return new Rgb(c.R, c.G, c.B);
    }

    // ==== 画像と量子化 ====

    /// <summary>AC-14 の (1) の代表の 12 色(sRGB)。</summary>
    private static readonly string[] Colors =
        ["000000", "FFFFFF", "808080", "FF0000", "00FF00", "0000FF", "FFFF00", "00FFFF", "FF00FF", "C8A080", "203040", "F0D8E8"];

    /// <summary>12 色を横に並べ、行ごとに α を alphas にした画像(幅 12)。</summary>
    private static byte[] ColorRows(params byte[] alphas)
    {
        var w = Colors.Length;
        var px = new byte[w * alphas.Length * 4];
        for (var j = 0; j < alphas.Length; j++)
        for (var i = 0; i < w; i++)
        {
            var v = Convert.ToUInt32(Colors[i], 16);
            var o = (j * w + i) * 4;
            px[o] = (byte)(v >> 16);
            px[o + 1] = (byte)(v >> 8);
            px[o + 2] = (byte)v;
            px[o + 3] = alphas[j];
        }
        return px;
    }

    /// <summary>§9.7 の規則 7 の量子化を、画素 (i, j) の青色雑音まで含めて double で行う。</summary>
    private static double Quantize(double c, int i, int j)
    {
        var n = ((BlueNoise.At(i, j) + 0.5) / 4096.0 - 0.5) * 0.9;
        return Math.Clamp(Math.Floor(255 * Srgb.Enc(MathX.Sat(c)) + 0.5 + n), 0, 255);
    }

    /// <summary>
    /// straight の RGBA 8 ビットの出力 output を、参照 reference(入力の straight の線形 RGB → 効果後の straight の線形 RGB)と比べる。
    /// 効果後は共通のパスと同じく、プリマルチプライの値を 0〜64 に切ってから amount で混ぜる。α は入力のまま(ルックは α を変えない)。
    /// 返り値は RGB の差の最大。
    /// </summary>
    private static int CompareToReference(byte[] src, byte[] output, int width, Func<Rgb, Rgb> reference, double amount, int tolerance, string what)
    {
        var maxDiff = 0;
        for (var p = 0; p < src.Length / 4; p++)
        {
            int i = p % width, j = p / width;
            var o = p * 4;
            Check.Equal(src[o + 3], output[o + 3], $"{what}: 画素 ({i},{j}) の α");
            var a = src[o + 3] / 255.0;
            if (a == 0) continue;
            var c = new Rgb(Srgb.Lin8(src[o]), Srgb.Lin8(src[o + 1]), Srgb.Lin8(src[o + 2]));
            var fx = reference(c);
            for (var k = 0; k < 3; k++)
            {
                var pin = c[k] * a;
                var pfx = Math.Clamp(fx[k] * a, 0, 64);
                var straight = (pin + amount * (pfx - pin)) / a;
                var expected = Quantize(straight, i, j);
                var d = (int)Math.Abs(expected - output[o + k]);
                maxDiff = Math.Max(maxDiff, d);
                Check.True(d <= tolerance,
                    $"{what}: #{Colors[i % Colors.Length]} α={src[o + 3]} の成分 {"RGB"[k]}: 参照 {expected}(線形 {straight:G6})、実際 {output[o + k]}");
            }
        }
        return maxDiff;
    }

    private static string Describe(IReadOnlyDictionary<string, ParamValue> values) =>
        values.Count == 0 ? "既定" : string.Join(", ", values.Select(kv => $"{kv.Key}={Show(kv.Value)}"));

    private static string Show(ParamValue v) => v switch
    {
        NumberValue n => n.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
        StringValue s => s.Value,
        _ => v.ToString() ?? "",
    };

    private static Dictionary<string, ParamValue> TableDefaults(Fx fx) => fx.Params.ToDictionary(p => p.Key, p => p.Default);

    // ==== 動かす値の組 ====

    private static readonly string[] SampleColors = ["#000000FF", "#FFFFFFFF", "#2080C0FF", "#E0404080"];

    /// <summary>1 つのパラメータに試す値: 数値は最小・既定・最大・中間と四分位、列挙は全部の選択肢、色は既定といくつかの色。</summary>
    private static IEnumerable<ParamValue> SampleValues(Spec p)
    {
        switch (p.Type)
        {
            case ParamType.Float or ParamType.Int or ParamType.Angle:
                var d = ((NumberValue)p.Default).Value;
                double[] xs = [p.Min, d, p.Max, (p.Min + p.Max) / 2, p.Min + (p.Max - p.Min) / 4, p.Min + (p.Max - p.Min) * 3 / 4];
                return xs.Distinct().Select(x => (ParamValue)new NumberValue(x));
            case ParamType.Enum:
                return p.Options!.Select(o => (ParamValue)new StringValue(o));
            case ParamType.Color:
                return new[] { ((StringValue)p.Default).Value }.Concat(SampleColors).Select(s => (ParamValue)new StringValue(s));
            default:
                throw new InvalidOperationException($"{p.Key}: 試す値を決めていない型 {p.Type}");
        }
    }

    /// <summary>土台の組ごとに 1 つのパラメータを動かした組(ほかは既定)と、Extras。値の辞書には動かしたキーだけを入れ、残りは EffectDef の既定値に任せる。</summary>
    private static IEnumerable<Dictionary<string, ParamValue>> Combos(Fx fx)
    {
        yield return new Dictionary<string, ParamValue>();
        foreach (var b in fx.Bases)
        foreach (var p in fx.Params)
        foreach (var v in SampleValues(p))
            yield return Merge(b, new() { [p.Key] = v });
        foreach (var e in fx.Extras) yield return e;
    }

    private static EffectDef Def(RenderService rs, string id) => rs.Effects.Find(id) ?? throw new CheckFailedException($"{id} が読めていない");

    // ==== AC-13・AC-14: 宣言が §9.4 の表のとおり ====

    private static double ExpectedStep(Spec p) => p.Step ?? (p.Type == ParamType.Angle ? 1
        : p.Max - p.Min >= 100 ? 1 : p.Max - p.Min >= 10 ? 0.1 : 0.01);

    [Test]
    public void AC13_AC14_基本と色の13個の宣言が9_4の表のとおり()
    {
        var log = new MemoryLogSink();
        var catalog = BuiltInEffects.Load(log);
        foreach (var fx in Table)
        {
            Check.True(catalog.Rejected.All(r => r.Id != fx.Id), $"{fx.Id} が読み込みで拒否された");
            var def = catalog.Find(fx.Id) ?? throw new CheckFailedException($"{fx.Id} が無い");
            var w = fx.Id;
            Check.Equal(1, def.Schema, $"{w}: schema");
            Check.Equal(fx.Name, def.Name, $"{w}: name");
            Check.Equal(EffectStage.Look, def.Stage, $"{w}: stage");
            Check.Equal(fx.Group, def.Group, $"{w}: group");
            Check.Equal(AmountMode.Mix, def.AmountMode, $"{w}: amountMode");
            Check.Equal(0, def.ScaleParams.Count, $"{w}: scaleParams");
            Check.Equal(false, def.TimeDependent, $"{w}: timeDependent");
            Check.Equal(SizeRule.Same, def.SizeRule, $"{w}: sizeRule");
            Check.Equal(AuxKind.None, def.Aux, $"{w}: aux");
            Check.SequenceEqual(fx.Params.Select(p => p.Key).ToArray(), def.Params.Select(p => p.Key).ToArray(), $"{w}: パラメータの key と並び");
            foreach (var spec in fx.Params)
            {
                var p = def.FindParam(spec.Key)!;
                var pw = $"{w}.{spec.Key}";
                Check.Equal(spec.Type, p.Type, $"{pw}: type");
                Check.Equal(spec.Default, p.Default, $"{pw}: default(表の既定値)");
                Check.Equal(spec.Unit, p.Unit, $"{pw}: unit");
                Check.Equal(spec.Handle, p.Handle, $"{pw}: handle");
                Check.Equal(false, p.Advanced, $"{pw}: advanced");
                Check.True(p.Neutral is null, $"{pw}: neutral は表に無い");
                if (spec.Type is ParamType.Float or ParamType.Int or ParamType.Angle)
                {
                    Check.Equal<double?>(spec.Min, p.Min, $"{pw}: min");
                    Check.Equal<double?>(spec.Max, p.Max, $"{pw}: max");
                    Check.Near(ExpectedStep(spec), p.Step ?? double.NaN, 1e-12, $"{pw}: step");
                }
                else
                {
                    Check.True(p.Min is null && p.Max is null && p.Step is null, $"{pw}: 数値でない型に範囲がある");
                }
                if (spec.Options is { } options)
                {
                    var actual = p.Options ?? [];
                    Check.SequenceEqual(options, actual.Select(o => o.Value).ToArray(), $"{pw}: 選択肢の value と並び");
                    for (var i = 0; i < options.Length; i++)
                    {
                        if (spec.OptionLabels![i] is { } label) Check.Equal(label, actual[i].Label, $"{pw}: 選択肢 {options[i]} の label");
                    }
                }
                else
                {
                    Check.True(p.Options is null, $"{pw}: 列挙でないのに options がある");
                }
                if (spec.VisibleKey is null)
                {
                    Check.True(p.VisibleWhen is null, $"{pw}: 表に無い visibleWhen");
                }
                else
                {
                    Check.Equal(spec.VisibleKey, p.VisibleWhen?.Key, $"{pw}: visibleWhen の key");
                    Check.SequenceEqual(spec.VisibleValues!, p.VisibleWhen!.Values, $"{pw}: visibleWhen の値");
                }
            }
            Check.Equal(1, def.Passes.Count, $"{w}: パスの数");
            Check.True(BuiltInEffects.ShaderExists(def.Passes[0].Shader), $"{w}: シェーダー {def.Passes[0].Shader} が埋め込まれていない");
            Check.True(def.Keywords.Count <= 8, $"{w}: keywords の数");
        }
        Check.Equal(0, log.Events.Count, "ログの件数");
    }

    // ==== AC-14 の (1): 代表の 12 色で定義の式と 8 ビットで 3 以内 ====

    /// <summary>
    /// fx の全部の組を、12 色(α=255 の行と、ルックが α を変えないことを見る α=128 の行)に掛けて参照と比べる。
    /// 参照の 8 ビットは、規則 7 の青色雑音を出力と同じ画素の位置で入れて丸める(許容の 3 は、half の誤差と float の計算の誤差だけに使う)。
    /// </summary>
    private static void CheckAgainstReference(string id)
    {
        var fx = Table.Single(f => f.Id == id);
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var def = Def(rs, id);
        var src = ColorRows(255, 128);
        var w = Colors.Length;
        var defaults = TableDefaults(fx);
        var count = 0;
        foreach (var combo in Combos(fx))
        {
            var inv = new EffectInvocation(def) { Values = combo, Amount = 1 };
            var output = rs.RenderStraightRgba8Async(src, w, 2, [inv], FrameContext.Still).GetAwaiter().GetResult();
            var v = new V(Merge(defaults, combo));
            CompareToReference(src, output, w, c => fx.Reference(v, c), 1, 3, $"{id}({Describe(combo)})");
            count++;
        }
        Check.True(count >= 1 + fx.Params.Length, $"{id}: 比べた組が少ない({count})");
    }

    [Test] public void AC14_1_exposureが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("exposure");
    [Test] public void AC14_1_contrastが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("contrast");
    [Test] public void AC14_1_levelsが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("levels");
    [Test] public void AC14_1_toneが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("tone");
    [Test] public void AC14_1_fadeが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("fade");
    [Test] public void AC14_1_wbが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("wb");
    [Test] public void AC14_1_saturationが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("saturation");
    [Test] public void AC14_1_hueが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("hue");
    [Test] public void AC14_1_splittoneが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("splittone");
    [Test] public void AC14_1_gradmapが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("gradmap");
    [Test] public void AC14_1_monoが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("mono");
    [Test] public void AC14_1_invertが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("invert");
    [Test] public void AC14_1_channelmixが代表の12色で定義の式と8ビットで3以内() => CheckAgainstReference("channelmix");

    // ==== amount(mix): 0 なら入力と 1 以内、0.5 で中間 ====

    [Test]
    public void AC14_基本と色のmixはamount0で入力と1以内で0_5で入力と効果の中間()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var src = ColorRows(255, 128);
        var w = Colors.Length;
        foreach (var fx in Table)
        {
            var def = Def(rs, fx.Id);
            var v = new V(Merge(TableDefaults(fx), fx.Strong));
            var what = $"{fx.Id}({Describe(fx.Strong)})";

            // この組が入力を十分に変えること(変わらない組では中間を確かめられない)
            var changed = 0.0;
            for (var i = 0; i < w; i++)
            {
                var c = new Rgb(Srgb.Lin8(src[i * 4]), Srgb.Lin8(src[i * 4 + 1]), Srgb.Lin8(src[i * 4 + 2]));
                var fxc = fx.Reference(v, c);
                for (var k = 0; k < 3; k++) changed = Math.Max(changed, Math.Abs(255 * (X(fxc[k]) - X(c[k]))));
            }
            Check.True(changed > 20, $"{what}: 効果の大きさが足りない({changed:F1})");

            var zero = rs.RenderStraightRgba8Async(src, w, 2, [new EffectInvocation(def) { Values = fx.Strong, Amount = 0 }], FrameContext.Still)
                .GetAwaiter().GetResult();
            for (var i = 0; i < src.Length; i++)
                Check.True(Math.Abs(src[i] - zero[i]) <= 1, $"{what} amount=0: 画素 {i / 4} の成分 {"RGBA"[i % 4]}: 入力 {src[i]}、出力 {zero[i]}");

            var half = rs.RenderStraightRgba8Async(src, w, 2, [new EffectInvocation(def) { Values = fx.Strong, Amount = 0.5 }], FrameContext.Still)
                .GetAwaiter().GetResult();
            CompareToReference(src, half, w, c => fx.Reference(v, c), 0.5, 3, $"{what} amount=0.5");
        }
    }

    // ==== 範囲の外の値を描画に直接渡しても NaN・無限大が出ない ====

    /// <summary>エフェクトのシェーダーだけを 1 回描いた RGBA16F(共通のパスの切りと NaN の直しを通す前)。</summary>
    private static float[] RawPass(RenderService rs, EffectInvocation inv, byte[] src, int w, int h) =>
        rs.InvokeAsync(c =>
        {
            using var input = c.IngestSrgb8(src, w, h, w * 4);
            var values = EffectConstants.ResolveValues(inv);
            var pass = inv.Def.Passes[0];
            var constants = EffectConstants.Pack(inv, pass, values, w, h, FrameContext.Still);
            using var target = c.CreateWorking(w, h);
            c.Draw(EffectRunner.PixelShaderName(pass.Shader), target, [input], constants);
            return c.ReadRgba16F(target);
        }).GetAwaiter().GetResult();

    [Test]
    public void AC15_2_基本と色に範囲外の値を直接渡してもNaNと無限大が出ない()
    {
        using var rs = RenderService.Create(GpuAdapterRequest.Warp);
        var src = ColorRows(255, 128, 0);
        var w = Colors.Length;
        const int h = 3;
        foreach (var fx in Table)
        {
            var def = Def(rs, fx.Id);
            // (組, シェーダーの生の出力も確かめるか)。±1e4 は 2^x が float を超えうるので、共通のパスを通した結果だけを確かめる
            var cases = new List<(Dictionary<string, ParamValue> Values, bool Raw)>();
            foreach (var b in fx.Bases)
            foreach (var p in fx.Params.Where(p => p.Type is ParamType.Float or ParamType.Int or ParamType.Angle))
            {
                var width = p.Max - p.Min;
                cases.Add((Merge(b, Num((p.Key, p.Min - width))), true));
                cases.Add((Merge(b, Num((p.Key, p.Max + width))), true));
                cases.Add((Merge(b, Num((p.Key, -1e4))), false));
                cases.Add((Merge(b, Num((p.Key, 1e4))), false));
            }
            foreach (var o in fx.OutOfRange) cases.Add((o, true));
            if (cases.Count == 0) cases.Add((new Dictionary<string, ParamValue>(), true)); // パラメータの無いもの(invert)は既定で

            foreach (var (values, raw) in cases)
            {
                var what = $"{fx.Id}({Describe(values)})";
                var inv = new EffectInvocation(def) { Values = values, Amount = 1 };
                var piped = rs.RenderRgba16FAsync(src, w, h, [inv], FrameContext.Still).GetAwaiter().GetResult();
                for (var i = 0; i < piped.Length; i++)
                {
                    var x = piped[i];
                    Check.True(float.IsFinite(x), $"{what}: 共通のパスを通した画素 {i / 4} の成分 {"RGBA"[i % 4]} が {x}");
                    if (i % 4 == 3) Check.InRange(0, 1, x, $"{what}: 画素 {i / 4} の α");
                    else Check.InRange(0, 64, x, $"{what}: 画素 {i / 4} の成分 {"RGB"[i % 4]}");
                }
                if (!raw) continue;
                var direct = RawPass(rs, inv, src, w, h);
                for (var i = 0; i < direct.Length; i++)
                    Check.True(float.IsFinite(direct[i]), $"{what}: シェーダーの生の出力の画素 {i / 4} の成分 {"RGBA"[i % 4]} が {direct[i]}");
            }
        }
    }
}
