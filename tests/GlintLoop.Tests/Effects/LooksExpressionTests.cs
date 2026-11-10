// ルックの「表現」と「重ね」の 8 個(posterize・threshold・palette・pixelate・bleach・crossproc・tint・gradient)のテスト(WARP だけ)。
// AC-14 の (1) は定義の式を C# の double で書き直した参照と、(2) は pixelate の性質の表の行と比べる。mix の amount=0 と範囲外の値も確かめる。
// 参照の入力は取り込みと同じ lin を half に丸めた値、参照の 8 ビットは §9.7 の規則 7 の量子化(画素の位置の青色雑音を含む)。
using GlintLoop.Core.Dither;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Effects;

public sealed class LooksExpressionTests
{
    // ==== 仕様書 §9.4 の表(テストの中の写し。EffectDef と参照実装の既定値の出所) ====

    private sealed record PSpec(string Key, ParamType Type, double? Min, double? Max, ParamValue Default, string Unit, double? Step,
        string[]? Options = null, string[]? OptionLabels = null, bool Handle = true);

    private sealed record ESpec(string Id, string Name, string Group, PSpec[] Params);

    private static readonly string[] ModeValues = ["normal", "multiply", "screen", "overlay", "softlight"];
    private static readonly string[] ModeLabels = ["通常", "乗算", "スクリーン", "オーバーレイ", "ソフトライト"];

    private static PSpec F(string key, double min, double max, double def, string unit, double step) =>
        new(key, ParamType.Float, min, max, new NumberValue(def), unit, step);

    private static PSpec Col(string key, string def) => new(key, ParamType.Color, null, null, new StringValue(def), "", null);

    private static readonly ESpec[] Specs =
    [
        new("posterize", "ポスタリゼーション", "表現", [new("levels", ParamType.Int, 2, 16, new NumberValue(5), "", 1)]),
        new("threshold", "しきい値", "表現",
            [F("t", 0, 100, 50, "%", 1), F("soft", 0, 30, 2, "%", 0.1), Col("dark", "#000000FF"), Col("light", "#FFFFFFFF")]),
        new("palette", "パレット減色", "表現",
        [
            new("kind", ParamType.Enum, null, null, new StringValue("p16"), "", null,
                ["p16", "g4", "bw", "sepia4"], ["16色", "4階調の緑", "白黒", "4階調のセピア"]),
            F("dither", 0, 100, 50, "%", 1),
        ]),
        new("pixelate", "モザイク", "表現", [F("size", 5, 200, 30, "‰", 1)]),
        new("bleach", "銀残し", "表現", [F("contrast", 0, 100, 40, "%", 1)]),
        new("crossproc", "クロスプロセス", "表現", []),
        new("tint", "色の重ね", "重ね",
        [
            Col("color", "#FF8040FF"),
            new("mode", ParamType.Enum, null, null, new StringValue("softlight"), "", null, ModeValues, ModeLabels),
            F("opacity", 0, 100, 50, "%", 1),
        ]),
        new("gradient", "グラデーション重ね", "重ね",
        [
            Col("c0", "#FF5FA2FF"),
            Col("c1", "#5F7BFFFF"),
            new("angle", ParamType.Angle, -180, 180, new NumberValue(90), "deg", 1),
            new("mode", ParamType.Enum, null, null, new StringValue("softlight"), "", null, ModeValues, ModeLabels),
            F("opacity", 0, 100, 60, "%", 1),
        ]),
    ];

    private static ESpec SpecOf(string id) => Specs.First(s => s.Id == id);

    /// <summary>§9.4 のパレットの色の並び(番号 0 から)。</summary>
    private static readonly Dictionary<string, string[]> Palettes = new()
    {
        ["p16"] =
        [
            "14121F", "3A3A5C", "8C8FAE", "F2F0E6", "7A1F3D", "D6334A", "F28C28", "F7D154",
            "7CBF3F", "1F8A5B", "2BB5B0", "3F8FE0", "2C3EA8", "7C4DD6", "E06BC0", "8A5A3C",
        ],
        ["g4"] = ["0B1F10", "2F5D3A", "86B36B", "E4F2C8"],
        ["bw"] = ["000000", "FFFFFF"],
        ["sepia4"] = ["1E1410", "5C4030", "B08A66", "F1E2C6"],
    };

    /// <summary>ベイヤー行列 bm。[j, i] が bm(i, j)(i が列、j が行)。</summary>
    private static readonly int[,] Bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };

    /// <summary>AC-14 の (1) の代表の 12 色(sRGB、α=255)。</summary>
    private static readonly string[] TwelveColors =
        ["000000", "FFFFFF", "808080", "FF0000", "00FF00", "0000FF", "FFFF00", "00FFFF", "FF00FF", "C8A080", "203040", "F0D8E8"];

    // ==== 小さな道具 ====

    private readonly record struct V3(double R, double G, double B)
    {
        public double this[int k] => k == 0 ? R : k == 1 ? G : B;
        public static V3 Map(V3 v, Func<double, double> f) => new(f(v.R), f(v.G), f(v.B));
        public static V3 Mix(V3 a, V3 b, double t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    private static RenderService Warp() => RenderService.Create(GpuAdapterRequest.Warp);

    private static EffectDef Def(RenderService rs, string id) => rs.Effects.Find(id) ?? throw new CheckFailedException($"{id} が読めていない");

    private static (byte R, byte G, byte B, byte A) Hex8(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return hex.TrimStart('#').Length == 8
            ? ((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : ((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)255);
    }

    /// <summary>色のパラメータの sRGB の値(0〜1)と不透明度。</summary>
    private static (V3 Srgb, double A) ColorParam(ParamValue v)
    {
        var (r, g, b, a) = Hex8(((StringValue)v).Value);
        return (new V3(r / 255.0, g / 255.0, b / 255.0), a / 255.0);
    }

    private static double Lin(double c) => Srgb.Lin(c);
    private static double Enc(double v) => Srgb.Enc(MathX.Sat(v));
    private static V3 Lin(V3 c) => V3.Map(c, Lin);
    private static V3 Enc(V3 v) => V3.Map(v, Enc);

    /// <summary>取り込み(§9.7 の規則 2)の結果: 8 ビットの sRGB を lin して half に丸めた線形の値。</summary>
    private static double Ingest(byte c) => (double)(Half)Srgb.Lin8(c);

    /// <summary>§9.7 の規則 7 の量子化。n は画素 (i, j) の青色雑音。</summary>
    private static int Quantize(double c, int i, int j)
    {
        var n = ((BlueNoise.At(i, j) + 0.5) / 4096.0 - 0.5) * 0.9;
        return (int)Math.Clamp(Math.Floor(255 * Enc(c) + 0.5 + n), 0, 255);
    }

    private static double Y(V3 c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
    private static double E(V3 c) => Enc(MathX.Sat(Y(c)));

    private static double SmoothStep(double e0, double e1, double x)
    {
        if (e0 >= e1) return x >= e1 ? 1 : 0;
        var t = MathX.Sat((x - e0) / (e1 - e0));
        return t * t * (3 - 2 * t);
    }

    private static double S(double x) => x * x * (3 - 2 * x);

    private static double Overlay(double cb, double cs) => cb <= 0.5 ? 2 * cb * cs : 1 - 2 * (1 - cb) * (1 - cs);

    private static double SoftLight(double cb, double cs)
    {
        static double D(double x) => x <= 0.25 ? ((16 * x - 12) * x + 4) * x : Math.Sqrt(x);
        return cs <= 0.5 ? cb - (1 - 2 * cs) * cb * (1 - cb) : cb + (2 * cs - 1) * (D(cb) - cb);
    }

    private static double Blend(string mode, double cb, double cs) => mode switch
    {
        "normal" => cs,
        "multiply" => cb * cs,
        "screen" => cb + cs - cb * cs,
        "overlay" => Overlay(cb, cs),
        "softlight" => SoftLight(cb, cs),
        _ => throw new CheckFailedException($"mode {mode} は無い"),
    };

    private static double Num(IReadOnlyDictionary<string, ParamValue> v, string key) => ((NumberValue)v[key]).Value;
    private static string Str(IReadOnlyDictionary<string, ParamValue> v, string key) => ((StringValue)v[key]).Value;

    /// <summary>参照の値: 表の既定値に、渡した値を上書きしたもの(EffectDef の既定値は使わない)。</summary>
    private static Dictionary<string, ParamValue> RefValues(string id, IReadOnlyDictionary<string, ParamValue> given)
    {
        var v = SpecOf(id).Params.ToDictionary(p => p.Key, p => p.Default);
        foreach (var (k, x) in given) v[k] = x;
        return v;
    }

    /// <summary>
    /// 試す値の組: 全部既定の 1 組と、パラメータ 1 つずつを最小・既定・最大・中間にした組(ほかは既定)。enum は全部の選択肢、
    /// 色は既定と extraColors の色。
    /// </summary>
    private static List<Dictionary<string, ParamValue>> Cases(string id, Dictionary<string, string[]>? extraColors = null)
    {
        var list = new List<Dictionary<string, ParamValue>> { new() };
        foreach (var p in SpecOf(id).Params)
        {
            IEnumerable<ParamValue> values = p.Type switch
            {
                ParamType.Float or ParamType.Angle =>
                    new[] { p.Min!.Value, ((NumberValue)p.Default).Value, p.Max!.Value, (p.Min.Value + p.Max.Value) / 2 }.Select(x => (ParamValue)new NumberValue(x)),
                ParamType.Int =>
                    new[] { p.Min!.Value, ((NumberValue)p.Default).Value, p.Max!.Value, MathX.RoundHalfUp((p.Min.Value + p.Max.Value) / 2) }.Select(x => (ParamValue)new NumberValue(x)),
                ParamType.Enum => p.Options!.Select(o => (ParamValue)new StringValue(o)),
                ParamType.Color => new[] { p.Default }.Concat((extraColors?.GetValueOrDefault(p.Key) ?? []).Select(c => (ParamValue)new StringValue(c))),
                _ => throw new CheckFailedException($"{id}.{p.Key}: 試す値を作れない型"),
            };
            foreach (var x in values) list.Add(new Dictionary<string, ParamValue> { [p.Key] = x });
        }
        return list;
    }

    private static string Describe(IReadOnlyDictionary<string, ParamValue> v) =>
        v.Count == 0 ? "既定" : string.Join(",", v.Select(kv => $"{kv.Key}={kv.Value switch { NumberValue n => n.Value.ToString(), StringValue s => s.Value, _ => kv.Value.ToString() }}"));

    /// <summary>1 行の画像(各画素が hex の色、α=255)。</summary>
    private static byte[] RowImage(string[] colors)
    {
        var px = new byte[colors.Length * 4];
        for (var i = 0; i < colors.Length; i++)
        {
            var (r, g, b, _) = Hex8(colors[i]);
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    /// <summary>w×h の多色の画像。α は opaque なら 255、そうでなければ画素ごとに 0〜255 を巡る。</summary>
    private static byte[] VariedImage(int w, int h, bool opaque)
    {
        var px = new byte[w * h * 4];
        for (var j = 0; j < h; j++)
        for (var i = 0; i < w; i++)
        {
            var o = (j * w + i) * 4;
            var hsh = Hash.Hash32((uint)(j * w + i) + 0x1234u);
            px[o] = (byte)(i * 255 / Math.Max(1, w - 1));
            px[o + 1] = (byte)(j * 255 / Math.Max(1, h - 1));
            px[o + 2] = (byte)hsh;
            px[o + 3] = opaque ? (byte)255 : (byte)(hsh >> 8);
        }
        return px;
    }

    private static EffectInvocation Inv(EffectDef def, IReadOnlyDictionary<string, ParamValue> values, double amount = 1) =>
        new(def) { Values = values, Amount = amount };

    private static byte[] Render8(RenderService rs, byte[] src, int w, int h, EffectInvocation inv) =>
        rs.RenderStraightRgba8Async(src, w, h, [inv], FrameContext.Still).GetAwaiter().GetResult();

    // ==== 参照実装(§9.4 の定義の式。入力は straight の線形 C、画素の整数座標 i, j) ====

    private static V3 RefPosterize(V3 c, IReadOnlyDictionary<string, ParamValue> v)
    {
        var n1 = Num(v, "levels") - 1;
        return Lin(V3.Map(Enc(c), x => MathX.RoundHalfUp(x * n1) / n1));
    }

    private static V3 RefThreshold(V3 c, IReadOnlyDictionary<string, ParamValue> v)
    {
        var e = E(c);
        var t = Num(v, "t") / 100;
        var soft = Num(v, "soft");
        var w = soft == 0 ? (e >= t ? 1 : 0) : SmoothStep(t - soft / 200, t + soft / 200, e);
        return V3.Mix(Lin(ColorParam(v["dark"]).Srgb), Lin(ColorParam(v["light"]).Srgb), w);
    }

    /// <summary>palette で選べる色の番号。最も近い色と、距離の差が tieTolerance 以内の色(float と double の差で入れ替わりうるもの)。</summary>
    private static List<int> RefPaletteChoices(V3 c, IReadOnlyDictionary<string, ParamValue> v, int i, int j, double tieTolerance)
    {
        var colors = Palettes[Str(v, "kind")];
        var amp = Num(v, "dither") / 100 * 0.25;
        var bm = Bayer[j & 3, i & 3];
        var x = V3.Map(Enc(c), s => MathX.Sat(s + (bm / 16.0 + 1.0 / 32 - 0.5) * amp));
        var lx = Lin(x);
        var lab = Oklab.ToOk(lx.R, lx.G, lx.B);
        var dist = colors.Select(hex =>
        {
            var (r, g, b, _) = Hex8(hex);
            var q = Oklab.ToOk(Srgb.Lin8(r), Srgb.Lin8(g), Srgb.Lin8(b));
            return (q.L - lab.L) * (q.L - lab.L) + (q.A - lab.A) * (q.A - lab.A) + (q.B - lab.B) * (q.B - lab.B);
        }).ToArray();
        var best = 0;
        for (var k = 1; k < dist.Length; k++) if (dist[k] < dist[best]) best = k;
        var result = new List<int> { best };
        for (var k = 0; k < dist.Length; k++)
            if (k != best && dist[k] - dist[best] <= tieTolerance) result.Add(k);
        return result;
    }

    private static V3 PaletteLin(string kind, int index)
    {
        var (r, g, b, _) = Hex8(Palettes[kind][index]);
        return new V3(Srgb.Lin8(r), Srgb.Lin8(g), Srgb.Lin8(b));
    }

    private static V3 RefBleach(V3 c, IReadOnlyDictionary<string, ParamValue> v)
    {
        var x = Enc(c);
        var g = E(c);
        var k = Num(v, "contrast") / 100;
        return Lin(V3.Map(x, xc =>
        {
            var x2 = xc + (Overlay(xc, g) - xc) * k;
            return g + (x2 - g) * 0.6;
        }));
    }

    private static V3 RefCrossproc(V3 c)
    {
        var x = Enc(c);
        return Lin(new V3(S(x.R), x.G + (S(x.G) - x.G) * 0.5, 0.12 + 0.76 * x.B));
    }

    /// <summary>tint の式。s は重ねる色の sRGB(0〜1)、k は opacity÷100×不透明度。</summary>
    private static V3 RefTintWith(V3 c, string mode, V3 s, double k)
    {
        var x = Enc(c);
        return Lin(new V3(
            x.R + (Blend(mode, x.R, s.R) - x.R) * k,
            x.G + (Blend(mode, x.G, s.G) - x.G) * k,
            x.B + (Blend(mode, x.B, s.B) - x.B) * k));
    }

    private static V3 RefTint(V3 c, IReadOnlyDictionary<string, ParamValue> v)
    {
        var (s, a) = ColorParam(v["color"]);
        return RefTintWith(c, Str(v, "mode"), s, Num(v, "opacity") / 100 * a);
    }

    private static V3 RefGradient(V3 c, IReadOnlyDictionary<string, ParamValue> v, int i, int j, int w, int h)
    {
        var th = Num(v, "angle") * Math.PI / 180;
        var (dx, dy) = (Math.Cos(th), Math.Sin(th));
        double[] corners = [0, w * dx, h * dy, w * dx + h * dy];
        var lo = corners.Min();
        var hi = corners.Max();
        var t = ((i + 0.5) * dx + (j + 0.5) * dy - lo) / (hi - lo);
        var (s0, a0) = ColorParam(v["c0"]);
        var (s1, a1) = ColorParam(v["c1"]);
        var s = V3.Mix(s0, s1, t);
        var a = a0 + (a1 - a0) * t;
        return RefTintWith(c, Str(v, "mode"), s, Num(v, "opacity") / 100 * a);
    }

    // ==== EffectDef が表のとおり ====

    [Test]
    public void AC14_表現と重ねの8個のEffectDefが表のとおり()
    {
        using var rs = Warp();
        foreach (var spec in Specs)
        {
            var def = Def(rs, spec.Id);
            Check.Equal(spec.Name, def.Name, $"{spec.Id} の name");
            Check.Equal(EffectStage.Look, def.Stage, $"{spec.Id} の stage");
            Check.Equal(spec.Group, def.Group, $"{spec.Id} の group");
            Check.Equal(AmountMode.Mix, def.AmountMode, $"{spec.Id} の amountMode");
            Check.Equal(false, def.TimeDependent, $"{spec.Id} の timeDependent");
            Check.Equal(SizeRule.Same, def.SizeRule, $"{spec.Id} の sizeRule");
            Check.Equal(AuxKind.None, def.Aux, $"{spec.Id} の aux");
            Check.SequenceEqual(spec.Params.Select(p => p.Key).ToArray(), def.Params.Select(p => p.Key).ToArray(), $"{spec.Id} のパラメータの並び");
            foreach (var p in spec.Params)
            {
                var d = def.FindParam(p.Key)!;
                var what = $"{spec.Id}.{p.Key}";
                Check.Equal(p.Type, d.Type, $"{what} の型");
                Check.Equal(p.Min, d.Min, $"{what} の min");
                Check.Equal(p.Max, d.Max, $"{what} の max");
                Check.Equal(p.Step, d.Step, $"{what} の step");
                Check.Equal(p.Default, d.Default, $"{what} の既定値");
                Check.Equal(p.Unit, d.Unit, $"{what} の単位");
                Check.Equal(p.Handle, d.Handle, $"{what} のハンドル");
                Check.Equal(false, d.Advanced, $"{what} の advanced");
                Check.True(d.VisibleWhen is null, $"{what} に visibleWhen がある");
                if (p.Options is null)
                {
                    Check.True(d.Options is null, $"{what} に options がある");
                }
                else
                {
                    Check.SequenceEqual(p.Options, d.Options!.Select(o => o.Value).ToArray(), $"{what} の選択肢");
                    Check.SequenceEqual(p.OptionLabels!, d.Options!.Select(o => o.Label).ToArray(), $"{what} の選択肢のラベル");
                }
            }
        }
    }

    // ==== AC-14 の (1): 1 画素で決まる 6 個(palette・gradient は別) ====

    /// <summary>
    /// 12 色の 1 行の画像に、値の組ごとにエフェクトを掛けて参照と比べる。skip が真を返す成分(段の境目に 1e-4 より近いもの)は比べない。
    /// 比べた成分の数を返す。
    /// </summary>
    private static int CompareTwelve(RenderService rs, string id, Func<V3, IReadOnlyDictionary<string, ParamValue>, V3> reference,
        Dictionary<string, string[]>? extraColors = null, Func<V3, IReadOnlyDictionary<string, ParamValue>, int, bool>? skip = null)
    {
        var def = Def(rs, id);
        var src = RowImage(TwelveColors);
        var compared = 0;
        foreach (var given in Cases(id, extraColors))
        {
            var output = Render8(rs, src, TwelveColors.Length, 1, Inv(def, given));
            var v = RefValues(id, given);
            for (var i = 0; i < TwelveColors.Length; i++)
            {
                var c = new V3(Ingest(src[i * 4]), Ingest(src[i * 4 + 1]), Ingest(src[i * 4 + 2]));
                var expected = reference(c, v);
                for (var k = 0; k < 3; k++)
                {
                    if (skip?.Invoke(c, v, k) == true) continue;
                    var q = Quantize(expected[k], i, 0);
                    var actual = output[i * 4 + k];
                    Check.True(Math.Abs(q - actual) <= 3, $"{id} {Describe(given)} の #{TwelveColors[i]} の成分 {"RGB"[k]}: 参照 {q}、実際 {actual}");
                    compared++;
                }
                Check.Equal((byte)255, output[i * 4 + 3], $"{id} {Describe(given)} の #{TwelveColors[i]} の α");
            }
        }
        return compared;
    }

    [Test]
    public void AC14_1_posterizeが代表12色で参照と3以内()
    {
        using var rs = Warp();
        // round の境目(x×(n−1) の小数部が 0.5)に 1e-4 より近い成分は、float と double の差で段が入れ替わりうるので比べない
        var compared = CompareTwelve(rs, "posterize", RefPosterize, skip: (c, v, k) =>
        {
            var y = Enc(c[k]) * (Num(v, "levels") - 1);
            return Math.Abs(y - Math.Floor(y) - 0.5) < 1e-4;
        });
        Check.Equal(12 * 3 * Cases("posterize").Count, compared, "posterize: 比べた成分の数(境目で外したものが無い)");
    }

    [Test]
    public void AC14_1_thresholdが代表12色で参照と3以内で色の不透明度を使わない()
    {
        using var rs = Warp();
        var extra = new Dictionary<string, string[]> { ["dark"] = ["#3366CC40", "#80402000"], ["light"] = ["#E0C0A080", "#20FF6000"] };
        // soft=0 の段で e と t÷100 が等しい成分(白の t=100、黒の t=0)も、e≥t÷100 の側(light)になることまで比べる
        Check.Equal(12 * 3 * Cases("threshold", extra).Count, CompareTwelve(rs, "threshold", RefThreshold, extra), "threshold: 比べた成分の数");
    }

    [Test]
    public void AC14_1_bleachが代表12色で参照と3以内()
    {
        using var rs = Warp();
        Check.Equal(12 * 3 * Cases("bleach").Count, CompareTwelve(rs, "bleach", RefBleach), "bleach: 比べた成分の数");
    }

    [Test]
    public void AC14_1_crossprocが代表12色で参照と3以内()
    {
        using var rs = Warp();
        Check.Equal(12 * 3, CompareTwelve(rs, "crossproc", (c, _) => RefCrossproc(c)), "crossproc: 比べた成分の数");
    }

    [Test]
    public void AC14_1_tintが代表12色と全部のmodeで参照と3以内()
    {
        using var rs = Warp();
        var extra = new Dictionary<string, string[]> { ["color"] = ["#3366CC80", "#FFFFFF00", "#000000FF", "#20E0A0FF"] };
        Check.Equal(12 * 3 * Cases("tint", extra).Count, CompareTwelve(rs, "tint", RefTint, extra), "tint: 比べた成分の数");
    }

    // ==== AC-14 の (1): palette ====

    [Test]
    public void AC14_1_paletteはdither0で代表12色が参照と3以内で全部のkindで比べる()
    {
        using var rs = Warp();
        var def = Def(rs, "palette");
        var src = RowImage(TwelveColors);
        foreach (var kind in SpecOf("palette").Params[0].Options!)
        {
            var given = new Dictionary<string, ParamValue> { ["kind"] = new StringValue(kind), ["dither"] = new NumberValue(0) };
            var output = Render8(rs, src, TwelveColors.Length, 1, Inv(def, given));
            var v = RefValues("palette", given);
            for (var i = 0; i < TwelveColors.Length; i++)
            {
                var c = new V3(Ingest(src[i * 4]), Ingest(src[i * 4 + 1]), Ingest(src[i * 4 + 2]));
                // 距離の差が 1e-5 以内の色は、float と double の差で入れ替わりうるので、どれに一致してもよい
                var choices = RefPaletteChoices(c, v, i, 0, 1e-5);
                var ok = choices.Any(index =>
                {
                    var e = PaletteLin(kind, index);
                    return Enumerable.Range(0, 3).All(k => Math.Abs(Quantize(e[k], i, 0) - output[i * 4 + k]) <= 3);
                });
                Check.True(ok, $"palette kind={kind} dither=0 の #{TwelveColors[i]}: 参照 #{Palettes[kind][choices[0]]}(候補 {choices.Count})、" +
                    $"実際 {output[i * 4]},{output[i * 4 + 1]},{output[i * 4 + 2]}");
            }
        }
    }

    [Test]
    public void AC14_1_paletteはパレットの色そのものをその色に写す()
    {
        // シェーダーの中の色の表とテストの表の写し違いを見つける(dither=0)
        using var rs = Warp();
        var def = Def(rs, "palette");
        foreach (var (kind, colors) in Palettes)
        {
            var given = new Dictionary<string, ParamValue> { ["kind"] = new StringValue(kind), ["dither"] = new NumberValue(0) };
            var output = Render8(rs, RowImage(colors), colors.Length, 1, Inv(def, given));
            for (var i = 0; i < colors.Length; i++)
            {
                var (r, g, b, _) = Hex8(colors[i]);
                Check.True(output[i * 4] == r && output[i * 4 + 1] == g && output[i * 4 + 2] == b,
                    $"palette kind={kind} の色 {i}(#{colors[i]}): 実際 {output[i * 4]:X2}{output[i * 4 + 1]:X2}{output[i * 4 + 2]:X2}");
            }
        }
    }

    [Test]
    public void AC14_1_paletteはditherが正なら出力がパレットの色だけになる()
    {
        using var rs = Warp();
        var def = Def(rs, "palette");
        const int w = 48, h = 40;
        var src = VariedImage(w, h, opaque: true);
        foreach (var kind in Palettes.Keys)
        foreach (var dither in new double?[] { null, 1, 25, 75, 100 }) // null は既定(50)
        {
            var given = new Dictionary<string, ParamValue> { ["kind"] = new StringValue(kind) };
            if (dither is { } d) given["dither"] = new NumberValue(d);
            var what = $"palette kind={kind} dither={dither?.ToString() ?? "既定"}";
            var output = Render8(rs, src, w, h, Inv(def, given));
            var half = rs.RenderRgba16FAsync(src, w, h, [Inv(def, given)], FrameContext.Still).GetAwaiter().GetResult();
            var colors = Palettes[kind].Select(hex => Hex8(hex)).ToArray();
            var lins = Enumerable.Range(0, colors.Length).Select(k => PaletteLin(kind, k)).ToArray();
            var seen = new HashSet<int>();
            for (var p = 0; p < w * h; p++)
            {
                // 線形の値(RGBA16F の読み戻し)が、パレットのどれかの色の lin に half の精度(相対 1e-3)で一致する
                var index = Array.FindIndex(lins, e => Enumerable.Range(0, 3).All(k => Math.Abs(half[p * 4 + k] - e[k]) <= 1e-3 * e[k] + 1e-6));
                Check.True(index >= 0, $"{what} の画素 ({p % w},{p / w}): 線形の値 {half[p * 4]},{half[p * 4 + 1]},{half[p * 4 + 2]} はパレットの色でない");
                seen.Add(index);
                // 8 ビットでは、規則 7 の青色雑音(±0.45)と half の誤差が重なる画素で 1 ずれうるので、各チャンネル 1 以内
                var (r, g, b, _) = colors[index];
                Check.True(Math.Abs(output[p * 4] - r) <= 1 && Math.Abs(output[p * 4 + 1] - g) <= 1 && Math.Abs(output[p * 4 + 2] - b) <= 1,
                    $"{what} の画素 ({p % w},{p / w}): 8 ビット {output[p * 4]},{output[p * 4 + 1]},{output[p * 4 + 2]} がパレットの色 {index} から 1 より離れる");
            }
            Check.True(seen.Count >= Math.Min(2, colors.Length), $"{what}: 出た色が {seen.Count} 色だけ");
        }
        // dither の効き: 平らな灰の画像では、dither=100 のときベイヤーで 2 色以上に分かれ、dither=0 では 1 色
        var flat = Enumerable.Range(0, 16 * 16).SelectMany(_ => new byte[] { 120, 120, 120, 255 }).ToArray();
        int Colors(double d)
        {
            var o = Render8(rs, flat, 16, 16, Inv(def, new Dictionary<string, ParamValue> { ["kind"] = new StringValue("bw"), ["dither"] = new NumberValue(d) }));
            return Enumerable.Range(0, 256).Select(p => o[p * 4]).Distinct().Count();
        }
        Check.Equal(1, Colors(0), "bw の dither=0 の色の数");
        Check.True(Colors(100) >= 2, "bw の dither=100 でベイヤーのディザが効いていない");
    }

    [Test]
    public void AC14_1_paletteのditherが正の値も参照の式どおりに色を選ぶ()
    {
        // ディザの式(bm の位置と振幅)を、画素ごとの参照と比べる(距離の差が 1e-5 以内の色はどれでもよい)
        using var rs = Warp();
        var def = Def(rs, "palette");
        const int w = 48, h = 40;
        var src = VariedImage(w, h, opaque: true);
        foreach (var kind in Palettes.Keys)
        foreach (var dither in new[] { 50.0, 100 })
        {
            var given = new Dictionary<string, ParamValue> { ["kind"] = new StringValue(kind), ["dither"] = new NumberValue(dither) };
            var output = Render8(rs, src, w, h, Inv(def, given));
            var v = RefValues("palette", given);
            for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                var o = (j * w + i) * 4;
                var c = new V3(Ingest(src[o]), Ingest(src[o + 1]), Ingest(src[o + 2]));
                var choices = RefPaletteChoices(c, v, i, j, 1e-5);
                var ok = choices.Any(index =>
                {
                    var e = PaletteLin(kind, index);
                    return Enumerable.Range(0, 3).All(k => Math.Abs(Quantize(e[k], i, j) - output[o + k]) <= 3);
                });
                Check.True(ok, $"palette kind={kind} dither={dither} の画素 ({i},{j}): 参照 #{Palettes[kind][choices[0]]}、実際 {output[o]},{output[o + 1]},{output[o + 2]}");
            }
        }
    }

    // ==== AC-14 の (1): gradient(4 隅と中央の 5 点) ====

    [Test]
    public void AC14_1_gradientが代表12色の4隅と中央で参照と3以内()
    {
        using var rs = Warp();
        var def = Def(rs, "gradient");
        const int w = 37, h = 23;
        (int I, int J)[] points = [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1), (w / 2, h / 2)];
        var extra = new Dictionary<string, string[]> { ["c0"] = ["#20A0E080", "#00000000"], ["c1"] = ["#FFFFFF40", "#10204080"] };
        var cases = Cases("gradient", extra);
        // angle を ±45 度にした組も足す(4 隅の射影の最小と最大が対角になる向き)
        cases.Add(new Dictionary<string, ParamValue> { ["angle"] = new NumberValue(45) });
        cases.Add(new Dictionary<string, ParamValue> { ["angle"] = new NumberValue(-135) });
        foreach (var hex in TwelveColors)
        {
            var (r, g, b, _) = Hex8(hex);
            var src = Enumerable.Range(0, w * h).SelectMany(_ => new[] { r, g, b, (byte)255 }).ToArray();
            var c = new V3(Ingest(r), Ingest(g), Ingest(b));
            foreach (var given in cases)
            {
                var output = Render8(rs, src, w, h, Inv(def, given));
                var v = RefValues("gradient", given);
                foreach (var (i, j) in points)
                {
                    var expected = RefGradient(c, v, i, j, w, h);
                    var o = (j * w + i) * 4;
                    for (var k = 0; k < 3; k++)
                    {
                        var q = Quantize(expected[k], i, j);
                        Check.True(Math.Abs(q - output[o + k]) <= 3,
                            $"gradient {Describe(given)} の #{hex} の点 ({i},{j}) の成分 {"RGB"[k]}: 参照 {q}、実際 {output[o + k]}");
                    }
                }
            }
        }
    }

    // ==== AC-14 の (2): pixelate(catalog_points の性質の表) ====

    [Test]
    public void AC14_2_pixelateは200四方でsize100の升目20の画素が同じ値で入力の平均をencした値に一致する()
    {
        using var rs = Warp();
        var def = Def(rs, "pixelate");
        const int n = 200, cell = 20;
        var src = VariedImage(n, n, opaque: true);
        var output = Render8(rs, src, n, n, Inv(def, new Dictionary<string, ParamValue> { ["size"] = new NumberValue(100) }));
        for (var cy = 0; cy < n / cell; cy++)
        for (var cx = 0; cx < n / cell; cx++)
        {
            for (var k = 0; k < 3; k++)
            {
                double sum = 0;
                int lo = 255, hi = 0;
                for (var y = cy * cell; y < (cy + 1) * cell; y++)
                for (var x = cx * cell; x < (cx + 1) * cell; x++)
                {
                    var o = (y * n + x) * 4 + k;
                    sum += Ingest(src[o]);
                    lo = Math.Min(lo, output[o]);
                    hi = Math.Max(hi, output[o]);
                }
                Check.True(hi - lo <= 1, $"pixelate の升目 ({cx},{cy}) の成分 {"RGB"[k]}: 値が {lo}〜{hi} に散らばる");
                var expected = MathX.RoundHalfUpToInt(255 * Enc(sum / (cell * cell)));
                var first = output[(cy * cell * n + cx * cell) * 4 + k];
                Check.True(Math.Abs(expected - first) <= 3, $"pixelate の升目 ({cx},{cy}) の成分 {"RGB"[k]}: 平均の enc {expected}、実際 {first}");
            }
        }
    }

    [Test]
    public void AC14_2_pixelateは左上からの升目で端の升目が小さく半透明はプリマルチプライで平均する()
    {
        using var rs = Warp();
        var def = Def(rs, "pixelate");
        // 70×45: S0=45、size=200 で px=9 → 升目 9(右端の列の升目は 7 画素、下端の行はちょうど 5 段)。size=110 で px=4.95 → 5
        const int w = 70, h = 45;
        var src = VariedImage(w, h, opaque: false);
        foreach (var (size, cell) in new[] { (200.0, 9), (110.0, 5), (30.0, 1), (5.0, 1) })
        {
            var output = Render8(rs, src, w, h, Inv(def, new Dictionary<string, ParamValue> { ["size"] = new NumberValue(size) }));
            for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                int x0 = i / cell * cell, y0 = j / cell * cell, x1 = Math.Min(x0 + cell, w), y1 = Math.Min(y0 + cell, h);
                double r = 0, g = 0, b = 0, a = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var o = (y * w + x) * 4;
                    var al = src[o + 3] / 255.0;
                    // 取り込みの P=(C×α, α) を half に丸めた値
                    r += (double)(Half)(Srgb.Lin8(src[o]) * al);
                    g += (double)(Half)(Srgb.Lin8(src[o + 1]) * al);
                    b += (double)(Half)(Srgb.Lin8(src[o + 2]) * al);
                    a += (double)(Half)al;
                }
                var count = (x1 - x0) * (y1 - y0);
                var pa = a / count;
                var po = (j * w + i) * 4;
                var qa = MathX.RoundHalfUpToInt(255 * MathX.Sat(pa));
                Check.True(Math.Abs(qa - output[po + 3]) <= 1, $"pixelate size={size} ({i},{j}) の α: 参照 {qa}、実際 {output[po + 3]}");
                if (pa <= 0.0001 || qa < 8) continue; // ほぼ透明な升目は straight の色が不安定なので色を比べない
                var ch = new[] { r, g, b };
                for (var k = 0; k < 3; k++)
                {
                    var q = Quantize(ch[k] / count / pa, i, j);
                    Check.True(Math.Abs(q - output[po + k]) <= 3, $"pixelate size={size} ({i},{j}) の成分 {"RGB"[k]}: 参照 {q}、実際 {output[po + k]}");
                }
            }
        }
    }

    // ==== mix の amount=0 ====

    [Test]
    public void AC14_表現と重ねの8個はmixのamount0で入力と1以内()
    {
        using var rs = Warp();
        const int w = 32, h = 16;
        var src = VariedImage(w, h, opaque: false);
        foreach (var spec in Specs)
        {
            var def = Def(rs, spec.Id);
            var output = Render8(rs, src, w, h, Inv(def, new Dictionary<string, ParamValue>(), amount: 0));
            for (var p = 0; p < w * h; p++)
            {
                if (src[p * 4 + 3] == 0)
                {
                    for (var k = 0; k < 4; k++) Check.Equal((byte)0, output[p * 4 + k], $"{spec.Id} amount=0 の α=0 の画素 {p} の成分 {k}");
                    continue;
                }
                for (var k = 0; k < 4; k++)
                {
                    var d = Math.Abs(src[p * 4 + k] - output[p * 4 + k]);
                    Check.True(d <= 1, $"{spec.Id} amount=0 の画素 ({p % w},{p / w}) の成分 {"RGBA"[k]}: 入力 {src[p * 4 + k]}、出力 {output[p * 4 + k]}");
                }
            }
            // 1 では効いている(amount=0 の検査が素通りでないこと)。pixelate は既定の size=30 だと 32×16 で升目 0.48→1 なので size=200 で見る
            var on = spec.Id == "pixelate" ? new Dictionary<string, ParamValue> { ["size"] = new NumberValue(200) } : new Dictionary<string, ParamValue>();
            var fx = Render8(rs, src, w, h, Inv(def, on, amount: 1));
            Check.True(!fx.SequenceEqual(src), $"{spec.Id}: amount=1 でも入力と同じ");
        }
    }

    // ==== 範囲外の値を直に描画へ渡しても NaN・無限大が出ない ====

    [Test]
    public void AC15_範囲外とNaNの値を直に渡しても表現と重ねの8個はRGBA16FにNaNと無限大を出さない()
    {
        using var rs = Warp();
        const int w = 24, h = 20;
        var src = VariedImage(w, h, opaque: false);
        foreach (var spec in Specs)
        {
            var def = Def(rs, spec.Id);
            var cases = new List<Dictionary<string, ParamValue>> { new() };
            foreach (var p in spec.Params.Where(p => p.Type is ParamType.Float or ParamType.Int or ParamType.Angle))
            {
                foreach (var x in new[] { p.Min!.Value - 1000, p.Max!.Value + 1000, -1e9, 1e9, 0, 1, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                    cases.Add(new Dictionary<string, ParamValue> { [p.Key] = new NumberValue(x) });
            }
            foreach (var given in cases)
            {
                var half = rs.RenderRgba16FAsync(src, w, h, [Inv(def, given)], FrameContext.Still).GetAwaiter().GetResult();
                for (var i = 0; i < half.Length; i++)
                {
                    Check.True(float.IsFinite(half[i]), $"{spec.Id} {Describe(given)} の成分 {i}: {half[i]}");
                    if (i % 4 == 3) Check.InRange(0, 1, half[i], $"{spec.Id} {Describe(given)} の α {i / 4}");
                }
            }
        }
    }
}
