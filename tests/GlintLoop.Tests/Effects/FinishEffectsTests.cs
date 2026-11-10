// 仕上げステージの 14 個(text を除く)のテスト(WARP だけ)。EffectDef が §9.4 の表のとおりか、catalog_points の性質の表の行(AC-14 の (2))、
// 定義の式を C# の double で書き直した参照と 8 ビットで 3 以内に一致するか(§9.4 の passes の約束)を確かめる。
// 参照の入力は取り込みと同じ(lin×α を half に丸めた値)、参照の 8 ビットは §9.7 の規則 7 の量子化(画素の位置の青色雑音を含む)。
using GlintLoop.Core.Dither;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Effects;

public sealed class FinishEffectsTests
{
    // ==== 仕様書 §9.4 の表(テストの中の写し。EffectDef と参照実装の既定値の出所) ====

    private sealed record PSpec(string Key, ParamType Type, double? Min, double? Max, ParamValue Default, string Unit, double? Step,
        string[]? Options = null, string[]? OptionLabels = null, bool Handle = true, string? VisibleKey = null, string[]? VisibleValues = null);

    private sealed record ESpec(string Id, string Name, string Group, AmountMode Mode, string[] ScaleParams, PSpec[] Params);

    private static PSpec F(string key, double min, double max, double def, string unit, double step) =>
        new(key, ParamType.Float, min, max, new NumberValue(def), unit, step);

    private static PSpec A(string key, double min, double max, double def, bool handle = true, string? visKey = null, string[]? visValues = null) =>
        new(key, ParamType.Angle, min, max, new NumberValue(def), "deg", 1, Handle: handle, VisibleKey: visKey, VisibleValues: visValues);

    private static PSpec E(string key, string def, string[] values, string[] labels) =>
        new(key, ParamType.Enum, null, null, new StringValue(def), "", null, values, labels);

    private static PSpec Col(string key, string def, string? visKey = null, string[]? visValues = null) =>
        new(key, ParamType.Color, null, null, new StringValue(def), "", null, VisibleKey: visKey, VisibleValues: visValues);

    private static PSpec Pt(string key, double x, double y) => new(key, ParamType.Point, null, null, new PointValue(x, y), "", null);

    private static readonly ESpec[] Specs =
    [
        new("blur", "ぼかし", "ぼかし", AmountMode.Mix, [],
        [
            E("kind", "gaussian", ["gaussian", "directional"], ["ガウス", "方向"]),
            F("radius", 0, 100, 10, "‰", 0.1),
            A("angle", -180, 180, 0, visKey: "kind", visValues: ["directional"]),
        ]),
        new("zoomblur", "放射ぼかし", "ぼかし", AmountMode.Mix, [],
            [E("kind", "zoom", ["zoom", "spin"], ["ズーム", "回転"]), Pt("center", 0.5, 0.5), F("strength", 0, 100, 25, "%", 1)]),
        new("tiltshift", "ミニチュア", "ぼかし", AmountMode.Mix, [],
            [A("angle", -90, 90, 0), F("pos", 0, 100, 50, "%", 1), F("width", 0, 80, 20, "%", 0.1), F("blur", 0, 100, 30, "‰", 1)]),
        new("sharpen", "シャープ", "ぼかし", AmountMode.Scale, ["strength"],
            [F("radius", 0, 20, 3, "‰", 0.1), F("strength", 0, 300, 100, "%", 1), F("threshold", 0, 50, 0, "", 0.1)]),
        new("glow", "グロー", "光", AmountMode.Scale, ["intensity"],
        [
            F("threshold", 0, 100, 60, "%", 1), F("radius", 0, 150, 40, "‰", 1), F("intensity", 0, 300, 80, "%", 1),
            Col("color", "#FFFFFFFF"),
        ]),
        new("vignette", "周辺光量", "光", AmountMode.Scale, ["strength"],
        [
            F("strength", 0, 100, 40, "%", 1), F("size", 0, 100, 50, "%", 1), F("soft", 0, 100, 60, "%", 1), F("round", 0, 100, 0, "%", 1),
            Col("color", "#000000FF"),
        ]),
        new("grain", "グレイン", "光", AmountMode.Scale, ["strength"],
            [F("strength", 0, 100, 25, "%", 1), F("size", 0.5, 20, 2, "‰", 0.1), new("mono", ParamType.Bool, null, null, new BoolValue(true), "", null)]),
        new("aberration", "色ずれ", "光", AmountMode.Scale, ["amount"],
        [
            E("mode", "radial", ["radial", "linear"], ["放射", "直線"]),
            F("amount", 0, 30, 6, "‰", 0.1),
            A("angle", -180, 180, 0, visKey: "mode", visValues: ["linear"]),
        ]),
        new("emboss", "エンボス", "光", AmountMode.Mix, [],
            [E("mode", "gray", ["gray", "color"], ["灰色", "カラー"]), A("angle", 0, 360, 135), F("depth", 0, 40, 6, "‰", 0.1), F("soft", 0, 10, 1, "‰", 0.1)]),
        new("halftone", "ハーフトーン", "ひずみ", AmountMode.Mix, [],
        [
            E("mode", "mono", ["mono", "cmy"], ["単色", "三原色"]),
            F("size", 5, 100, 25, "‰", 0.1),
            A("angle", 0, 90, 45),
            Col("ink", "#14121FFF", "mode", ["mono"]),
            Col("paper", "#F2F0E6FF", "mode", ["mono"]),
        ]),
        new("waves", "波打ち", "ひずみ", AmountMode.Mix, [],
        [
            E("dir", "h", ["h", "v", "both"], ["横にゆれる", "縦にゆれる", "両方"]),
            F("amp", 0, 60, 8, "‰", 0.1), F("len", 10, 500, 100, "‰", 1), A("phase", 0, 360, 0, handle: false),
        ]),
        new("warp", "ひずみ", "ひずみ", AmountMode.Mix, [],
        [
            E("kind", "bulge", ["bulge", "twirl"], ["ふくらみ", "渦"]), Pt("center", 0.5, 0.5),
            F("radius", 50, 1000, 400, "‰", 1), F("strength", -100, 100, 50, "%", 1),
        ]),
        new("outline", "縁取り", "縁と文字", AmountMode.Mix, [],
        [
            F("width", 0, 80, 12, "‰", 0.1),
            E("pos", "outside", ["outside", "inside", "center"], ["外側", "内側", "中央"]),
            Col("color", "#FFFFFFFF"),
            F("soft", 0, 30, 0, "‰", 0.1),
        ]),
        new("shadow", "影", "縁と文字", AmountMode.Mix, [],
            [F("dx", -100, 100, 0, "‰", 1), F("dy", -100, 100, 20, "‰", 1), F("blur", 0, 100, 20, "‰", 1), Col("color", "#000000B0")]),
    ];

    private static ESpec SpecOf(string id) => Specs.First(s => s.Id == id);

    // ==== 小さな道具 ====

    private static RenderService Warp() => RenderService.Create(GpuAdapterRequest.Warp);

    private static EffectDef Def(RenderService rs, string id) => rs.Effects.Find(id) ?? throw new CheckFailedException($"{id} が読めていない");

    private static EffectInvocation Inv(RenderService rs, string id, params (string Key, ParamValue Value)[] values) =>
        new(Def(rs, id)) { Values = values.ToDictionary(v => v.Key, v => v.Value), Amount = 1, Salt = 1 };

    private static readonly FrameContext Still = new(12345, 0, 1);

    private static float[] F16(RenderService rs, byte[] img, int w, int h, params EffectInvocation[] stack) =>
        rs.RenderRgba16FAsync(img, w, h, stack, Still).GetAwaiter().GetResult();

    private static byte[] Rgba8(RenderService rs, byte[] img, int w, int h, params EffectInvocation[] stack) =>
        rs.RenderStraightRgba8Async(img, w, h, stack, Still).GetAwaiter().GetResult();

    private static byte[] Rgba8(RenderService rs, byte[] img, int w, int h, FrameContext frame, EffectInvocation inv) =>
        rs.RenderStraightRgba8Async(img, w, h, [inv], frame).GetAwaiter().GetResult();

    private static NumberValue N(double v) => new(v);
    private static StringValue S(string v) => new(v);

    private static void Put(byte[] px, int i, uint rgba)
    {
        px[i * 4] = (byte)(rgba >> 24);
        px[i * 4 + 1] = (byte)(rgba >> 16);
        px[i * 4 + 2] = (byte)(rgba >> 8);
        px[i * 4 + 3] = (byte)rgba;
    }

    /// <summary>w×h の 1 色の画像(sRGB の straight)。</summary>
    private static byte[] Solid(int w, int h, uint rgba)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) Put(px, i, rgba);
        return px;
    }

    private const uint Uniform = 0x6496C8FF; // 表の「一様な画像」の色

    /// <summary>透明な 200×200 の中央(x、y とも 50〜149)に不透明な 100×100 の正方形。</summary>
    private static byte[] CenterSquare(uint rgba)
    {
        var px = new byte[200 * 200 * 4];
        for (var y = 50; y < 150; y++)
        for (var x = 50; x < 150; x++) Put(px, y * 200 + x, rgba);
        return px;
    }

    /// <summary>エフェクトを通さずに同じ最後の変換で 8 ビットにしたもの(「入力」と比べるときの基準)。</summary>
    private static byte[] Input8(RenderService rs, byte[] img, int w, int h) => Rgba8(rs, img, w, h);

    /// <summary>全画素の全チャンネルが tol 以内で一致する。</summary>
    private static void AllWithin(byte[] expected, byte[] actual, int tol, string what)
    {
        Check.Equal(expected.Length, actual.Length, what + " の長さ");
        for (var i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) > tol)
                throw new CheckFailedException($"{what}: 画素 {i / 4} のチャンネル {i % 4} 期待 {expected[i]}±{tol}、実際 {actual[i]}");
        }
    }

    private static void PixelWithin(byte[] expected, byte[] actual, int index, int tol, string what)
    {
        for (var c = 0; c < 4; c++)
        {
            var e = expected[index * 4 + c];
            var a = actual[index * 4 + c];
            if (Math.Abs(e - a) > tol) throw new CheckFailedException($"{what}: 画素 {index} のチャンネル {c} 期待 {e}±{tol}、実際 {a}");
        }
    }

    private static void PixelIs(byte[] actual, int index, (int R, int G, int B, int A) expected, int tol, string what)
    {
        int[] e = [expected.R, expected.G, expected.B, expected.A];
        for (var c = 0; c < 4; c++)
        {
            var a = actual[index * 4 + c];
            if (Math.Abs(e[c] - a) > tol) throw new CheckFailedException($"{what}: 画素 {index} のチャンネル {c} 期待 {e[c]}±{tol}、実際 {a}");
        }
    }

    // ==== EffectDef が表のとおり ====

    [Test]
    public void AC14_仕上げの14個のEffectDefが表のとおり()
    {
        using var rs = Warp();
        foreach (var spec in Specs)
        {
            var def = Def(rs, spec.Id);
            Check.Equal(spec.Name, def.Name, $"{spec.Id} の name");
            Check.Equal(EffectStage.Finish, def.Stage, $"{spec.Id} の stage");
            Check.Equal(spec.Group, def.Group, $"{spec.Id} の group");
            Check.Equal(spec.Mode, def.AmountMode, $"{spec.Id} の amountMode");
            Check.SequenceEqual(spec.ScaleParams, def.ScaleParams.ToArray(), $"{spec.Id} の scaleParams");
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
                Check.True(d.Neutral is null, $"{what} に neutral がある(省略して範囲の 0 を使う)");
                if (p.VisibleKey is null)
                {
                    Check.True(d.VisibleWhen is null, $"{what} に visibleWhen がある");
                }
                else
                {
                    Check.True(d.VisibleWhen is not null, $"{what} に visibleWhen が無い");
                    Check.Equal(p.VisibleKey, d.VisibleWhen!.Key, $"{what} の visibleWhen のキー");
                    Check.SequenceEqual(p.VisibleValues!, d.VisibleWhen.Values.ToArray(), $"{what} の visibleWhen の値");
                }
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

    [Test]
    public void AC14_3_sharpenのstrength100にamount50パーセントを掛けると実効のstrengthが50()
    {
        using var rs = Warp();
        var def = Def(rs, "sharpen");
        var a = EffectAmount.Effective(50, 100, fromLook: false, def.AmountMode);
        var values = EffectConstants.ResolveValues(new EffectInvocation(def) { Values = new Dictionary<string, ParamValue> { ["strength"] = N(100) }, Amount = a });
        Check.Near(50, ((NumberValue)values["strength"]).Value, 1e-12, "実効の strength");
        Check.Near(3, ((NumberValue)values["radius"]).Value, 1e-12, "scaleParams に無い radius は変わらない");
    }

    // ================================================================ 参照の道具(double)

    /// <summary>線形光・プリマルチプライの RGBA(double)の画像。</summary>
    private sealed class Img(int w, int h)
    {
        public int W { get; } = w;
        public int H { get; } = h;
        public double[] P { get; } = new double[w * h * 4];
        public double S0 => Math.Min(W, H);
        public int O(int i, int j) => (j * W + i) * 4;

        public Img Copy()
        {
            var c = new Img(W, H);
            P.CopyTo(c.P, 0);
            return c;
        }

        public double[] At(int i, int j)
        {
            var o = O(i, j);
            return [P[o], P[o + 1], P[o + 2], P[o + 3]];
        }

        public void Set(int i, int j, double r, double g, double b, double a)
        {
            var o = O(i, j);
            P[o] = r;
            P[o + 1] = g;
            P[o + 2] = b;
            P[o + 3] = a;
        }
    }

    private static double Lin(double c) => Srgb.Lin(c);
    private static double Enc(double v) => Srgb.Enc(MathX.Sat(v));
    private static double Y(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
    private static double Ev(double r, double g, double b) => Enc(MathX.Sat(Y(r, g, b)));
    private static double Px(double permille, double s0) => permille * s0 / 1000;

    private static double SmoothStep(double e0, double e1, double x)
    {
        if (e0 >= e1) return x >= e1 ? 1 : 0;
        var t = MathX.Sat((x - e0) / (e1 - e0));
        return t * t * (3 - 2 * t);
    }

    private static (double X, double Y) Turn(double th, double x, double y) =>
        (x * Math.Cos(th) - y * Math.Sin(th), x * Math.Sin(th) + y * Math.Cos(th));

    /// <summary>P から straight の C(α が 0.0001 以下なら 0)。</summary>
    private static (double R, double G, double B) Straight(double[] p) =>
        p[3] > 0.0001 ? (p[0] / p[3], p[1] / p[3], p[2] / p[3]) : (0, 0, 0);

    /// <summary>取り込み(§9.7 の規則 2)の結果: lin(c)×α と α を half に丸めた値。</summary>
    private static Img Ingest(byte[] px, int w, int h)
    {
        var img = new Img(w, h);
        for (var k = 0; k < w * h; k++)
        {
            var a = px[k * 4 + 3] / 255.0;
            for (var c = 0; c < 3; c++) img.P[k * 4 + c] = (double)(Half)(Srgb.Lin8(px[k * 4 + c]) * a);
            img.P[k * 4 + 3] = (double)(Half)a;
        }
        return img;
    }

    /// <summary>共通の後処理の切り(RGB 0〜64、α 0〜1)と、§9.7 の規則 7 の量子化(青色雑音を含む straight の 8 ビット)。</summary>
    private static byte[] Encode8(Img img)
    {
        var dst = new byte[img.W * img.H * 4];
        for (var j = 0; j < img.H; j++)
        for (var i = 0; i < img.W; i++)
        {
            var o = img.O(i, j);
            var a = Math.Clamp(img.P[o + 3], 0, 1);
            if (!(a > 0.0001)) continue;
            var n = ((BlueNoise.At(i, j) + 0.5) / 4096.0 - 0.5) * 0.9;
            for (var c = 0; c < 3; c++)
            {
                var v = Math.Clamp(img.P[o + c], 0, 64);
                dst[o + c] = (byte)Math.Clamp(Math.Floor(255 * Enc(MathX.Sat(v / a)) + 0.5 + n), 0, 255);
            }
            dst[o + 3] = (byte)Math.Clamp(Math.Floor(255 * a + 0.5), 0, 255);
        }
        return dst;
    }

    /// <summary>位置 u(画素の中心が i+0.5)の双線形補間。zero が真ならキャンバスの外のタップは 0、偽なら端を延ばす。</summary>
    private static double[] Bil(Img img, double ux, double uy, bool zero = false)
    {
        var tx = ux - 0.5;
        var ty = uy - 0.5;
        var fx = Math.Floor(tx);
        var fy = Math.Floor(ty);
        var ax = tx - fx;
        var ay = ty - fy;
        var i0 = (int)fx;
        var j0 = (int)fy;
        var r = new double[4];
        for (var k = 0; k < 4; k++)
        {
            var i = i0 + (k & 1);
            var j = j0 + (k >> 1);
            var wgt = ((k & 1) == 1 ? ax : 1 - ax) * ((k >> 1) == 1 ? ay : 1 - ay);
            if (zero && (i < 0 || j < 0 || i >= img.W || j >= img.H)) continue;
            var o = img.O(Math.Clamp(i, 0, img.W - 1), Math.Clamp(j, 0, img.H - 1));
            for (var c = 0; c < 4; c++) r[c] += wgt * img.P[o + c];
        }
        return r;
    }

    /// <summary>1 次元ガウスの重み(半幅 ceil(3σ)、合計 1 に正規化)。</summary>
    private static double[] Kernel(double sigma, out int r)
    {
        r = (int)Math.Ceiling(3 * sigma);
        var w = new double[2 * r + 1];
        double sum = 0;
        for (var d = -r; d <= r; d++) sum += w[d + r] = Math.Exp(-d * d / (2 * sigma * sigma));
        for (var k = 0; k < w.Length; k++) w[k] /= sum;
        return w;
    }

    /// <summary>Blur(X, σ)(§9.4 の仕上げの規則 2)。zero が真ならキャンバスの外は 0(shadow だけ)、偽なら端を延ばす。</summary>
    private static Img Blur(Img src, double sigma, bool zero = false)
    {
        if (!(sigma >= 0.05)) return src.Copy();
        var w = Kernel(sigma, out var r);
        Img Pass(Img a, bool horizontal)
        {
            var b = new Img(a.W, a.H);
            for (var j = 0; j < a.H; j++)
            for (var i = 0; i < a.W; i++)
            {
                var o = b.O(i, j);
                for (var d = -r; d <= r; d++)
                {
                    var x = horizontal ? i + d : i;
                    var y = horizontal ? j : j + d;
                    if (zero && (x < 0 || y < 0 || x >= a.W || y >= a.H)) continue;
                    var q = a.O(Math.Clamp(x, 0, a.W - 1), Math.Clamp(y, 0, a.H - 1));
                    for (var c = 0; c < 4; c++) b.P[o + c] += w[d + r] * a.P[q + c];
                }
            }
            return b;
        }
        return Pass(Pass(src, true), false);
    }

    /// <summary>色のパラメータの線形 RGB と不透明度。</summary>
    private static (double R, double G, double B, double A) ColorLin(ParamValue v)
    {
        var hex = ((StringValue)v).Value;
        var x = Convert.ToUInt32(hex[1..], 16);
        return (Srgb.Lin8((byte)(x >> 24)), Srgb.Lin8((byte)(x >> 16)), Srgb.Lin8((byte)(x >> 8)), (x & 0xFF) / 255.0);
    }

    private static double Num(IReadOnlyDictionary<string, ParamValue> v, string key) => ((NumberValue)v[key]).Value;
    private static string Str(IReadOnlyDictionary<string, ParamValue> v, string key) => ((StringValue)v[key]).Value;
    private static double Rad(double deg) => deg * Math.PI / 180;

    /// <summary>参照の値: 表の既定値に、渡した値を上書きしたもの(EffectDef の既定値は使わない)。</summary>
    private static Dictionary<string, ParamValue> RefValues(string id, IReadOnlyDictionary<string, ParamValue> given)
    {
        var v = SpecOf(id).Params.ToDictionary(p => p.Key, p => p.Default);
        foreach (var (k, x) in given) v[k] = x;
        return v;
    }

    /// <summary>画素ごとに f(i, j, その画素の P) で作った画像。</summary>
    private static Img Map(Img src, Func<int, int, double[], double[]> f)
    {
        var dst = new Img(src.W, src.H);
        for (var j = 0; j < src.H; j++)
        for (var i = 0; i < src.W; i++)
        {
            var q = f(i, j, src.At(i, j));
            dst.Set(i, j, q[0], q[1], q[2], q[3]);
        }
        return dst;
    }

    /// <summary>sRGB で y を作ったあと、lin して元の α でプリマルチプライする(「sRGB で」の約束)。</summary>
    private static double[] FromSrgb(double r, double g, double b, double a) => [Lin(r) * a, Lin(g) * a, Lin(b) * a, a];

    // ================================================================ 参照実装(§9.4 の定義の式)

    private static Img RefBlur(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var sigma = Px(Num(v, "radius"), p.S0) / 2;
        if (Str(v, "kind") == "gaussian") return Blur(p, sigma);
        if (!(sigma >= 0.05)) return p.Copy();
        var th = Rad(Num(v, "angle"));
        var (dx, dy) = (Math.Cos(th), Math.Sin(th));
        var w = Kernel(sigma, out var r);
        return Map(p, (i, j, _) =>
        {
            var s = new double[4];
            for (var d = -r; d <= r; d++)
            {
                var q = Bil(p, i + 0.5 + dx * d, j + 0.5 + dy * d);
                for (var c = 0; c < 4; c++) s[c] += w[d + r] * q[c];
            }
            return s;
        });
    }

    private static Img RefZoomBlur(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var c = (PointValue)v["center"];
        var (cx, cy) = (c.X * p.W, c.Y * p.H);
        var s = Num(v, "strength") / 100;
        var spin = Str(v, "kind") == "spin";
        return Map(p, (i, j, _) =>
        {
            var sum = new double[4];
            var (vx, vy) = (i + 0.5 - cx, j + 0.5 - cy);
            for (var k = 0; k < 32; k++)
            {
                var t = k / 31.0;
                var (qx, qy) = spin ? Turn((t - 0.5) * s * (Math.PI / 3), vx, vy) : (vx * (1 - s * 0.5 * t), vy * (1 - s * 0.5 * t));
                var q = Bil(p, cx + qx, cy + qy);
                for (var ch = 0; ch < 4; ch++) sum[ch] += q[ch] / 32;
            }
            return sum;
        });
    }

    /// <summary>tiltshift の画素の中心 (x, y) の σ(x)。</summary>
    private static double TiltSigma(Img p, IReadOnlyDictionary<string, ParamValue> v, double x, double y)
    {
        var th = Rad(Num(v, "angle"));
        var (nx, ny) = (-Math.Sin(th), Math.Cos(th));
        var en = p.W * Math.Abs(Math.Sin(th)) + p.H * Math.Abs(Math.Cos(th));
        var off = (Num(v, "pos") - 50) / 100 * en;
        var (bx, by) = (p.W / 2.0 + nx * off, p.H / 2.0 + ny * off);
        var d = (x - bx) * nx + (y - by) * ny;
        var wh = Num(v, "width") / 100 * en / 2;
        return Px(Num(v, "blur"), p.S0) / 2 * SmoothStep(0, 0.25 * en, Math.Abs(d) - wh);
    }

    private static Img RefTiltShift(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var smax = Px(Num(v, "blur"), p.S0) / 2;
        if (!(smax >= 0.05)) return p.Copy();
        double[] sig = [0, smax / 8, smax / 4, smax / 2, smax];
        var levels = sig.Select((s, k) => k == 0 ? p : Blur(p, s)).ToArray();
        return Map(p, (i, j, _) =>
        {
            var s = TiltSigma(p, v, i + 0.5, j + 0.5);
            var k = 0;
            while (k < 3 && s > sig[k + 1]) k++;
            var w = MathX.Sat((s - sig[k]) / Math.Max(sig[k + 1] - sig[k], 1e-6));
            var a = levels[k].At(i, j);
            var b = levels[k + 1].At(i, j);
            return [a[0] + (b[0] - a[0]) * w, a[1] + (b[1] - a[1]) * w, a[2] + (b[2] - a[2]) * w, a[3] + (b[3] - a[3]) * w];
        });
    }

    private static Img RefSharpen(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var blurred = Blur(p, Px(Num(v, "radius"), p.S0) / 2);
        var th = Num(v, "threshold") / 255;
        var k = Num(v, "strength") / 100;
        return Map(p, (i, j, q) =>
        {
            var (r, g, b) = Straight(q);
            var (br, bg, bb) = Straight(blurred.At(i, j));
            double[] x = [Enc(r), Enc(g), Enc(b)];
            double[] bx = [Enc(br), Enc(bg), Enc(bb)];
            var y = new double[3];
            for (var c = 0; c < 3; c++)
            {
                var d = x[c] - bx[c];
                if (th > 0) d *= SmoothStep(th, 2 * th, Math.Abs(d));
                y[c] = x[c] + k * d;
            }
            return FromSrgb(y[0], y[1], y[2], q[3]);
        });
    }

    private static Img RefGlow(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var t = Num(v, "threshold") / 100;
        var bright = Map(p, (_, _, q) =>
        {
            var (r, g, b) = Straight(q);
            var w = SmoothStep(t - 0.1, t + 0.1, Ev(r, g, b));
            return [r * w * q[3], g * w * q[3], b * w * q[3], q[3] * w];
        });
        var s1 = Px(Num(v, "radius"), p.S0) / 2;
        var g1 = Blur(bright, s1);
        var g3 = Blur(bright, 3 * s1);
        var col = ColorLin(v["color"]);
        var k = Num(v, "intensity") / 100 * col.A;
        return Map(p, (i, j, q) =>
        {
            var a1 = g1.At(i, j);
            var a3 = g3.At(i, j);
            double[] g = [0.6 * a1[0] + 0.4 * a3[0], 0.6 * a1[1] + 0.4 * a3[1], 0.6 * a1[2] + 0.4 * a3[2], 0.6 * a1[3] + 0.4 * a3[3]];
            return [q[0] + g[0] * col.R * k, q[1] + g[1] * col.G * k, q[2] + g[2] * col.B * k, MathX.Sat(q[3] + g[3] * k * (1 - q[3]))];
        });
    }

    private static Img RefVignette(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        double w = p.W, h = p.H;
        var m0 = Math.Max(w, h);
        var rd = Num(v, "round") / 100;
        var ax = (w + (m0 - w) * rd) / 2;
        var ay = (h + (m0 - h) * rd) / 2;
        var s0 = Num(v, "size") / 100 * 1.2;
        var s1 = s0 + Math.Max(Num(v, "soft") / 100 * 1.2, 0.01);
        var col = ColorLin(v["color"]);
        var st = Num(v, "strength") / 100;
        return Map(p, (i, j, q) =>
        {
            var dx = (i + 0.5 - w / 2) / ax;
            var dy = (j + 0.5 - h / 2) / ay;
            var m = st * SmoothStep(s0, s1, Math.Sqrt(dx * dx + dy * dy)) * col.A;
            return [q[0] * (1 - m) + col.R * q[3] * m, q[1] * (1 - m) + col.G * q[3] * m, q[2] * (1 - m) + col.B * q[3] * m, q[3]];
        });
    }

    private static Img RefGrain(Img p, IReadOnlyDictionary<string, ParamValue> v, uint seed, uint salt)
    {
        var k0 = Hash.H(seed, salt);
        var cell = Math.Max(1, MathX.RoundHalfUpToInt(Px(Num(v, "size"), p.S0)));
        var mono = ((BoolValue)v["mono"]).Value;
        var st = Num(v, "strength") / 100;
        return Map(p, (i, j, q) =>
        {
            var idx = unchecked((uint)((j / cell) * 16384 + i / cell));
            var n = new double[3];
            for (var c = 0; c < 3; c++)
            {
                var cc = mono ? 0u : (uint)c;
                n[c] = Hash.U01(Hash.H3(k0, 100 + cc, idx)) - Hash.U01(Hash.H3(k0, 200 + cc, idx));
            }
            var (r, g, b) = Straight(q);
            var e = 2 * Ev(r, g, b) - 1;
            var m = 1 - 0.7 * e * e;
            return FromSrgb(Enc(r) + st * 0.35 * n[0] * m, Enc(g) + st * 0.35 * n[1] * m, Enc(b) + st * 0.35 * n[2] * m, q[3]);
        });
    }

    private static Img RefAberration(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var k = Px(Num(v, "amount"), p.S0);
        var linear = Str(v, "mode") == "linear";
        var th = Rad(Num(v, "angle"));
        return Map(p, (i, j, q) =>
        {
            var (x, y) = (i + 0.5, j + 0.5);
            var (ox, oy) = linear ? (k * Math.Cos(th), k * Math.Sin(th)) : (k * (x - p.W / 2.0) / (p.S0 / 2), k * (y - p.H / 2.0) / (p.S0 / 2));
            var pr = Bil(p, x + ox, y + oy);
            var pb = Bil(p, x - ox, y - oy);
            return [pr[0], q[1], pb[2], (pr[3] + q[3] + pb[3]) / 3];
        });
    }

    private static Img RefEmboss(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var e = Map(p, (_, _, q) =>
        {
            var (r, g, b) = Straight(q);
            var x = Ev(r, g, b);
            return [x, x, x, x];
        });
        var h = Blur(e, Px(Num(v, "soft"), p.S0) / 2);
        var th = Rad(Num(v, "angle"));
        var dd = Px(Num(v, "depth"), p.S0);
        var (ox, oy) = (Math.Cos(th) * dd, Math.Sin(th) * dd);
        var color = Str(v, "mode") == "color";
        return Map(p, (i, j, q) =>
        {
            var (x, y) = (i + 0.5, j + 0.5);
            var d = Bil(h, x + ox, y + oy)[0] - Bil(h, x - ox, y - oy)[0];
            if (!color)
            {
                var gg = MathX.Sat(0.5 + 2 * d);
                return FromSrgb(gg, gg, gg, q[3]);
            }
            var (r, g, b) = Straight(q);
            return FromSrgb(MathX.Sat(Enc(r) + d), MathX.Sat(Enc(g) + d), MathX.Sat(Enc(b) + d), q[3]);
        });
    }

    /// <summary>
    /// halftone の参照。一様な画像だけに使う(升目の平均 A は、一様な画像ではどの近似でも入力の C に一致する)。
    /// 実装は A を half へ丸めて RGBA16F に置く(halftone_h・halftone_v)ので、WARP の切り捨ての幅は持たせない。
    /// </summary>
    private static Img RefHalftoneUniform(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var s = Math.Max(2, Px(Num(v, "size"), p.S0));
        var (c0x, c0y) = (p.W / 2.0, p.H / 2.0);
        var ink = ColorLin(v["ink"]);
        var paper = ColorLin(v["paper"]);
        var cmy = Str(v, "mode") == "cmy";
        var baseDeg = Num(v, "angle");
        double Cov(double x, double y, double deg, double d)
        {
            var th = Rad(deg);
            var (rx, ry) = Turn(-th, x - c0x, y - c0y);
            var (qx, qy) = Turn(th, MathX.RoundHalfUp(rx / s) * s, MathX.RoundHalfUp(ry / s) * s);
            var dist = Math.Sqrt((x - c0x - qx) * (x - c0x - qx) + (y - c0y - qy) * (y - c0y - qy));
            return MathX.Sat(0.5 + 0.7071 * s * Math.Sqrt(Math.Max(d, 0)) - dist);
        }
        return Map(p, (i, j, q) =>
        {
            var (x, y) = (i + 0.5, j + 0.5);
            var (r, g, b) = Straight(q);
            if (!cmy)
            {
                var cov = Cov(x, y, baseDeg, 1 - Ev(r, g, b));
                return [(paper.R + (ink.R - paper.R) * cov) * q[3], (paper.G + (ink.G - paper.G) * cov) * q[3], (paper.B + (ink.B - paper.B) * cov) * q[3], q[3]];
            }
            var o0 = 1 - Cov(x, y, baseDeg + 15, 1 - Enc(r));
            var o1 = 1 - Cov(x, y, baseDeg + 75, 1 - Enc(g));
            var o2 = 1 - Cov(x, y, baseDeg, 1 - Enc(b));
            return [Lin(o0) * q[3], Lin(o1) * q[3], Lin(o2) * q[3], q[3]];
        });
    }

    private static Img RefWaves(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var amp = Px(Num(v, "amp"), p.S0);
        var len = Math.Max(Px(Num(v, "len"), p.S0), 2);
        var ph = Rad(Num(v, "phase"));
        var dir = Str(v, "dir");
        return Map(p, (i, j, _) =>
        {
            var (x, y) = (i + 0.5, j + 0.5);
            var qx = dir != "v" ? x + amp * Math.Sin(2 * Math.PI * y / len + ph) : x;
            var qy = dir != "h" ? y + amp * Math.Sin(2 * Math.PI * x / len + ph) : y;
            return Bil(p, qx, qy);
        });
    }

    private static Img RefWarp(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var c = (PointValue)v["center"];
        var (cx, cy) = (c.X * p.W, c.Y * p.H);
        var rr = Px(Num(v, "radius"), p.S0);
        var s = Num(v, "strength") / 100;
        var twirl = Str(v, "kind") == "twirl";
        return Map(p, (i, j, q) =>
        {
            var (vx, vy) = (i + 0.5 - cx, j + 0.5 - cy);
            var rn = Math.Sqrt(vx * vx + vy * vy) / rr;
            if (rn >= 1) return q;
            if (twirl)
            {
                var (tx, ty) = Turn(s * 2 * Math.PI * (1 - rn) * (1 - rn), vx, vy);
                return Bil(p, cx + tx, cy + ty);
            }
            var u = 1 - rn * rn;
            var f = 1 - 0.8 * s * u * u;
            return Bil(p, cx + vx * f, cy + vy * f);
        });
    }

    /// <summary>sdα(§9.4 の記法)を総当たりで求める。キャンバスの外は B。</summary>
    private static double[] SdAlpha(Img p)
    {
        var sd = new double[p.W * p.H];
        var isA = new bool[p.W * p.H];
        for (var k = 0; k < isA.Length; k++) isA[k] = p.P[k * 4 + 3] >= 0.5;
        var anyA = isA.Any(a => a);
        for (var j = 0; j < p.H; j++)
        for (var i = 0; i < p.W; i++)
        {
            var here = isA[j * p.W + i];
            double best;
            if (here)
            {
                best = Math.Min(Math.Min(i + 1, p.W - i), Math.Min(j + 1, p.H - j)); // キャンバスの外の画素まで
                best *= best;
            }
            else
            {
                best = double.MaxValue;
            }
            for (var y = 0; y < p.H; y++)
            for (var x = 0; x < p.W; x++)
            {
                if (isA[y * p.W + x] == here) continue;
                double d2 = (x - i) * (x - i) + (y - j) * (y - j);
                if (d2 < best) best = d2;
            }
            sd[j * p.W + i] = here ? -Math.Sqrt(best) : anyA ? Math.Sqrt(best) : 1e4;
        }
        return sd;
    }

    private static Img RefOutline(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var sd = SdAlpha(p);
        var r = Px(Num(v, "width"), p.S0);
        var aa = Math.Max(1, Px(Num(v, "soft"), p.S0) * 2);
        var pos = Str(v, "pos");
        var (lo, hi) = pos switch { "outside" => (0.0, r), "inside" => (-r, 0.0), _ => (-r / 2, r / 2) };
        var col = ColorLin(v["color"]);
        return Map(p, (i, j, q) =>
        {
            var s = sd[j * p.W + i];
            var cov = MathX.Sat((s - lo) / aa + 0.5) * MathX.Sat((hi - s) / aa + 0.5);
            var a = col.A * cov;
            double[] l = [col.R * a, col.G * a, col.B * a, a];
            if (pos == "outside") return [q[0] + (1 - q[3]) * l[0], q[1] + (1 - q[3]) * l[1], q[2] + (1 - q[3]) * l[2], q[3] + (1 - q[3]) * l[3]];
            return [l[0] + (1 - a) * q[0], l[1] + (1 - a) * q[1], l[2] + (1 - a) * q[2], l[3] + (1 - a) * q[3]];
        });
    }

    private static Img RefShadow(Img p, IReadOnlyDictionary<string, ParamValue> v)
    {
        var alpha = Map(p, (_, _, q) => [q[3], q[3], q[3], q[3]]);
        var blurred = Blur(alpha, Px(Num(v, "blur"), p.S0) / 2, zero: true);
        var (dx, dy) = (Px(Num(v, "dx"), p.S0), Px(Num(v, "dy"), p.S0));
        var col = ColorLin(v["color"]);
        return Map(p, (i, j, q) =>
        {
            var a = col.A * Bil(blurred, i + 0.5 - dx, j + 0.5 - dy, zero: true)[0];
            var k = 1 - q[3];
            return [q[0] + k * col.R * a, q[1] + k * col.G * a, q[2] + k * col.B * a, q[3] + k * a];
        });
    }

    // ================================================================ 参照との比較(8 ビットで 3 以内)

    /// <summary>参照の画像(96×72)。滑らかな変化に、縁の硬い円と矩形と画素ごとの小さな雑音を重ねる。translucent なら α を楕円の内側 255・輪 128・外 0 にする。</summary>
    private static byte[] Varied(int w, int h, bool translucent)
    {
        var px = new byte[w * h * 4];
        for (var j = 0; j < h; j++)
        for (var i = 0; i < w; i++)
        {
            var o = (j * w + i) * 4;
            var n = (int)(Hash.Hash32((uint)(j * w + i) ^ 0x7A11u) % 25) - 12;
            var r = 40 + 150.0 * i / w + 30 * Math.Sin(j / 5.0) + n;
            var g = 200 - 140.0 * j / h + n;
            var b = 128 + 100 * Math.Sin((i + j) / 9.0) + n;
            var dx = i - w * 0.3;
            var dy = j - h * 0.55;
            if (dx * dx + dy * dy < 14 * 14) (r, g, b) = (240, 230, 60);
            if (i >= w * 0.62 && i < w * 0.84 && j >= h * 0.14 && j < h * 0.4) (r, g, b) = (20, 20, 30);
            var a = 255;
            if (translucent)
            {
                var ex = (i - w / 2.0) / (w * 0.36);
                var ey = (j - h / 2.0) / (h * 0.36);
                var e = Math.Sqrt(ex * ex + ey * ey);
                a = e < 0.8 ? 255 : e < 1.0 ? 128 : 0;
            }
            px[o] = (byte)Math.Clamp((int)r, 0, 255);
            px[o + 1] = (byte)Math.Clamp((int)g, 0, 255);
            px[o + 2] = (byte)Math.Clamp((int)b, 0, 255);
            px[o + 3] = (byte)a;
        }
        return px;
    }

    /// <summary>試す値の組: 全部既定の 1 組、数値のパラメータを 1 つずつ最小・最大・中間にした組(ほかは既定)、enum の全部の選択肢、bool の両方、extra の組。</summary>
    private static List<Dictionary<string, ParamValue>> Cases(string id, params Dictionary<string, ParamValue>[] extra)
    {
        var list = new List<Dictionary<string, ParamValue>> { new() };
        foreach (var p in SpecOf(id).Params)
        {
            IEnumerable<ParamValue> values = p.Type switch
            {
                ParamType.Float or ParamType.Angle => new[] { p.Min!.Value, p.Max!.Value, (p.Min.Value + p.Max.Value) / 2 }.Select(x => (ParamValue)N(x)),
                ParamType.Enum => p.Options!.Select(o => (ParamValue)S(o)),
                ParamType.Bool => [new BoolValue(true), new BoolValue(false)],
                _ => [],
            };
            foreach (var x in values) list.Add(new Dictionary<string, ParamValue> { [p.Key] = x });
        }
        list.AddRange(extra);
        return list;
    }

    private static Dictionary<string, ParamValue> Vals(params (string Key, ParamValue Value)[] kv) => kv.ToDictionary(x => x.Key, x => x.Value);

    private static string Describe(IReadOnlyDictionary<string, ParamValue> v) =>
        v.Count == 0 ? "既定" : string.Join(",", v.Select(kv => $"{kv.Key}={kv.Value switch { NumberValue n => n.Value.ToString(), StringValue s => s.Value, BoolValue b => b.Value.ToString(), PointValue pt => $"({pt.X},{pt.Y})", _ => kv.Value.ToString() }}"));

    /// <summary>
    /// 画像に値の組ごとにエフェクトを掛けて参照と比べる。α は全画素で 3 以内、RGB は出力の α が minAlpha 以上の画素で 3 以内。
    /// 比べた RGB の成分の数を返す。reference2 があるときは、2 つの参照の間(両端から 3 以内)を許す。
    /// </summary>
    private static int CompareRef(RenderService rs, string id, byte[] src, int w, int h, Func<Img, IReadOnlyDictionary<string, ParamValue>, Img> reference,
        List<Dictionary<string, ParamValue>> cases, int minAlpha = 0, Func<Img, IReadOnlyDictionary<string, ParamValue>, Img>? reference2 = null)
    {
        var def = Def(rs, id);
        var input = Ingest(src, w, h);
        var compared = 0;
        foreach (var given in cases)
        {
            var actual = Rgba8(rs, src, w, h, Still, new EffectInvocation(def) { Values = given, Amount = 1, Salt = 1 });
            var e1 = Encode8(reference(input, RefValues(id, given)));
            var e2 = reference2 is null ? e1 : Encode8(reference2(input, RefValues(id, given)));
            for (var k = 0; k < w * h; k++)
            {
                var what = $"{id} {Describe(given)} の ({k % w}, {k / w})";
                for (var c = 0; c < 4; c++)
                {
                    var o = k * 4 + c;
                    if (c < 3 && (Math.Min(e1[k * 4 + 3], e2[k * 4 + 3]) < Math.Max(minAlpha, 1) || actual[k * 4 + 3] < minAlpha)) continue;
                    var lo = Math.Min(e1[o], e2[o]) - 3;
                    var hi = Math.Max(e1[o], e2[o]) + 3;
                    Check.True(actual[o] >= lo && actual[o] <= hi,
                        $"{what} の {"RGBA"[c]}: 参照 {e1[o]}{(reference2 is null ? "" : $"〜{e2[o]}")}、実際 {actual[o]}");
                    if (c < 3) compared++;
                }
            }
        }
        return compared;
    }

    private const int RW = 96, RH = 72;

    [Test]
    public void AC14_blurが参照と3以内()
    {
        using var rs = Warp();
        var img = Varied(RW, RH, false);
        var n = CompareRef(rs, "blur", img, RW, RH, RefBlur, Cases("blur",
            Vals(("kind", S("directional")), ("angle", N(30))),
            Vals(("kind", S("directional")), ("radius", N(100)), ("angle", N(-120))),
            Vals(("kind", S("directional")), ("radius", N(0.5)))));
        Check.True(n > 0, "blur: 比べた成分が無い");
        CompareRef(rs, "blur", Varied(RW, RH, true), RW, RH, RefBlur,
            [Vals(("radius", N(60))), Vals(("kind", S("directional")), ("radius", N(60)), ("angle", N(45)))], minAlpha: 128);
    }

    [Test]
    public void AC14_zoomblurが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "zoomblur", Varied(RW, RH, false), RW, RH, RefZoomBlur, Cases("zoomblur",
            Vals(("kind", S("spin")), ("strength", N(100))),
            Vals(("center", new PointValue(0.2, 0.7)), ("strength", N(80))),
            Vals(("kind", S("spin")), ("center", new PointValue(1, 0)))));
    }

    [Test]
    public void AC14_tiltshiftが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "tiltshift", Varied(RW, RH, false), RW, RH, RefTiltShift, Cases("tiltshift",
            Vals(("angle", N(30)), ("pos", N(30)), ("width", N(10)), ("blur", N(100))),
            Vals(("angle", N(-75)), ("width", N(0)), ("blur", N(80)))));
    }

    [Test]
    public void AC14_sharpenが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "sharpen", Varied(RW, RH, false), RW, RH, RefSharpen, Cases("sharpen",
            Vals(("radius", N(20)), ("strength", N(300)), ("threshold", N(10))),
            Vals(("threshold", N(4)), ("strength", N(200)))));
        CompareRef(rs, "sharpen", Varied(RW, RH, true), RW, RH, RefSharpen, [Vals(("radius", N(10)))], minAlpha: 128);
    }

    [Test]
    public void AC14_glowが参照と3以内()
    {
        using var rs = Warp();
        var extra = new[]
        {
            Vals(("threshold", N(30)), ("intensity", N(300)), ("color", S("#FF804080"))),
            Vals(("threshold", N(50)), ("radius", N(150)), ("color", S("#40C0FFFF"))),
        };
        CompareRef(rs, "glow", Varied(RW, RH, false), RW, RH, RefGlow, Cases("glow", extra));
        CompareRef(rs, "glow", Varied(RW, RH, true), RW, RH, RefGlow, [Vals(("threshold", N(30)), ("intensity", N(200)))], minAlpha: 128);
    }

    [Test]
    public void AC14_vignetteが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "vignette", Varied(RW, RH, false), RW, RH, RefVignette, Cases("vignette",
            Vals(("strength", N(100)), ("size", N(20)), ("soft", N(0)), ("color", S("#FFE0A080"))),
            Vals(("strength", N(100)), ("round", N(100)), ("color", S("#2040FFFF")))));
        CompareRef(rs, "vignette", Varied(RW, RH, true), RW, RH, RefVignette, [Vals(("strength", N(100)))], minAlpha: 128);
    }

    [Test]
    public void AC14_grainが参照と3以内()
    {
        using var rs = Warp();
        // 一辺 1 の粒(小さい画像)と、一辺 4 の粒(256×200 は S0=200 なので size=20 → px=4.0 → cell=4)の両方で比べる
        CompareRef(rs, "grain", Varied(RW, RH, false), RW, RH, (p, v) => RefGrain(p, v, Still.Seed, 1), Cases("grain",
            Vals(("strength", N(100)), ("mono", new BoolValue(false)))));
        CompareRef(rs, "grain", Varied(256, 200, false), 256, 200, (p, v) => RefGrain(p, v, Still.Seed, 1),
            [Vals(("strength", N(100)), ("size", N(20))), Vals(("strength", N(80)), ("size", N(9)), ("mono", new BoolValue(false)))]);
    }

    [Test]
    public void AC14_aberrationが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "aberration", Varied(RW, RH, false), RW, RH, RefAberration, Cases("aberration",
            Vals(("mode", S("linear")), ("amount", N(30)), ("angle", N(135))),
            Vals(("mode", S("linear")), ("amount", N(12.5)), ("angle", N(-60)))));
        CompareRef(rs, "aberration", Varied(RW, RH, true), RW, RH, RefAberration, [Vals(("amount", N(30)))], minAlpha: 128);
    }

    [Test]
    public void AC14_embossが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "emboss", Varied(RW, RH, false), RW, RH, RefEmboss, Cases("emboss",
            Vals(("mode", S("color")), ("depth", N(40)), ("soft", N(0))),
            Vals(("mode", S("color")), ("angle", N(20)), ("soft", N(10)))));
    }

    [Test]
    public void AC14_halftoneが一様な画像で参照と3以内()
    {
        using var rs = Warp();
        // 升目の平均 A は近似してよい(§9.4)ので、近似の差が出ない一様な画像で、点の形(格子・半径・被覆)を比べる
        foreach (var color in new uint[] { 0x808080FF, 0x203040FF, 0xC8A080FF, 0xFFFFFFFF, 0x000000FF, 0xF0D8E8FF })
        {
            CompareRef(rs, "halftone", Solid(RW, RH, color), RW, RH, RefHalftoneUniform, Cases("halftone",
                Vals(("mode", S("cmy")), ("size", N(100)), ("angle", N(10))),
                Vals(("mode", S("cmy")), ("size", N(5))),
                Vals(("ink", S("#C03020FF")), ("paper", S("#20304080")), ("angle", N(0)))));
        }
    }

    [Test]
    public void AC14_wavesが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "waves", Varied(RW, RH, false), RW, RH, RefWaves, Cases("waves",
            Vals(("dir", S("both")), ("amp", N(60)), ("len", N(30)), ("phase", N(90))),
            Vals(("dir", S("v")), ("amp", N(25)), ("phase", N(200)))));
    }

    [Test]
    public void AC14_warpが参照と3以内()
    {
        using var rs = Warp();
        CompareRef(rs, "warp", Varied(RW, RH, false), RW, RH, RefWarp, Cases("warp",
            Vals(("kind", S("twirl")), ("strength", N(-100))),
            Vals(("kind", S("twirl")), ("strength", N(100)), ("center", new PointValue(0.3, 0.6)), ("radius", N(600))),
            Vals(("strength", N(-100)), ("center", new PointValue(0.8, 0.2)))));
    }

    [Test]
    public void AC14_outlineが参照と3以内()
    {
        using var rs = Warp();
        var img = Varied(RW, RH, true);
        var extra = new[]
        {
            Vals(("pos", S("inside")), ("width", N(80)), ("soft", N(10)), ("color", S("#FF2010C0"))),
            Vals(("pos", S("center")), ("width", N(40)), ("soft", N(30))),
            Vals(("width", N(80)), ("soft", N(5)), ("color", S("#3060FF80"))),
        };
        CompareRef(rs, "outline", img, RW, RH, RefOutline, Cases("outline", extra), minAlpha: 128);
        // 不透明な画像: outside は何も描かず、inside と center はキャンバスの縁に沿って描く
        CompareRef(rs, "outline", Varied(RW, RH, false), RW, RH, RefOutline,
            [Vals(("pos", S("inside")), ("width", N(60))), Vals(("pos", S("center")), ("width", N(60))), Vals(("width", N(60)))]);
    }

    [Test]
    public void AC14_shadowが参照と3以内()
    {
        using var rs = Warp();
        var extra = new[]
        {
            Vals(("dx", N(-70)), ("dy", N(-30)), ("blur", N(60)), ("color", S("#4020A0FF"))),
            Vals(("dx", N(33.3)), ("dy", N(12.7)), ("blur", N(0))),
        };
        CompareRef(rs, "shadow", Varied(RW, RH, true), RW, RH, RefShadow, Cases("shadow", extra), minAlpha: 128);
    }

    // ================================================================ catalog_points の性質の表(AC-14 の (2))

    [Test]
    public void AC14_blurは一様な画像を変えず1画素の白の合計と対称を保つ()
    {
        using var rs = Warp();
        var uni = Solid(128, 128, Uniform);
        AllWithin(Input8(rs, uni, 128, 128), Rgba8(rs, uni, 128, 128, Inv(rs, "blur")), 1, "一様な画像");

        const int n = 129;
        var img = Solid(n, n, 0x000000FF);
        Put(img, 64 * n + 64, 0xFFFFFFFF);
        var f = F16(rs, img, n, n, Inv(rs, "blur", ("radius", N(20))));
        double sum = 0;
        for (var i = 0; i < n * n; i++) sum += f[i * 4];
        Check.InRange(0.99, 1.01, sum, "線形値の合計");
        var center = f[(64 * n + 64) * 4];
        for (var i = 0; i < n * n; i++) Check.True(f[i * 4] <= center, $"画素 {i} が中央より大きい");
        // 中心からの距離が同じ画素どうしの差が 1% 以内(判定の解釈は報告に書く: 大きい方の 1%、ただし中央の値の 1% より小さい値どうしは比べない)
        var byDistance = new Dictionary<int, List<float>>();
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var d2 = (x - 64) * (x - 64) + (y - 64) * (y - 64);
            if (!byDistance.TryGetValue(d2, out var l)) byDistance[d2] = l = [];
            l.Add(f[(y * n + x) * 4]);
        }
        foreach (var (d2, l) in byDistance)
        {
            var hi = l.Max();
            var lo = l.Min();
            if (hi < 0.01 * center) continue;
            Check.True(hi - lo <= 0.01 * hi, $"距離² {d2} の画素の値が {lo}〜{hi}");
        }
    }

    [Test]
    public void AC14_zoomblurは一様な画像を変えずstrength0で入力と一致する()
    {
        using var rs = Warp();
        var uni = Solid(128, 128, Uniform);
        foreach (var kind in new[] { "zoom", "spin" })
            AllWithin(Input8(rs, uni, 128, 128), Rgba8(rs, uni, 128, 128, Inv(rs, "zoomblur", ("kind", S(kind)), ("strength", N(100)))), 1, $"一様な画像 {kind}");
        var img = Varied(128, 128, true);
        foreach (var kind in new[] { "zoom", "spin" })
            AllWithin(Input8(rs, img, 128, 128), Rgba8(rs, img, 128, 128, Inv(rs, "zoomblur", ("kind", S(kind)), ("strength", N(0)))), 1, $"strength=0 {kind}");
    }

    [Test]
    public void AC14_tiltshiftは一様な画像を変えず帯の内側を変えない()
    {
        using var rs = Warp();
        var uni = Solid(128, 128, Uniform);
        AllWithin(Input8(rs, uni, 128, 128), Rgba8(rs, uni, 128, 128, Inv(rs, "tiltshift", ("blur", N(100)))), 1, "一様な画像");
        var img = Varied(128, 128, false);
        var input = Input8(rs, img, 128, 128);
        foreach (var (angle, pos, width) in new[] { (0.0, 50.0, 20.0), (30.0, 35.0, 30.0), (-60.0, 70.0, 10.0) })
        {
            var got = Rgba8(rs, img, 128, 128, Inv(rs, "tiltshift", ("angle", N(angle)), ("pos", N(pos)), ("width", N(width)), ("blur", N(100))));
            // 帯の内側は abs(v)≤wh(v は基準点からの法線の向きの距離。基準点は中心を法線の向きに (pos−50)÷100×En 動かした点)
            var th = Rad(angle);
            var en = 128 * Math.Abs(Math.Sin(th)) + 128 * Math.Abs(Math.Cos(th));
            var wh = width / 100 * en / 2;
            var (nx, ny) = (-Math.Sin(th), Math.Cos(th));
            var off = (pos - 50) / 100 * en;
            var inside = 0;
            for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
            {
                var v = (x + 0.5 - 64 - nx * off) * nx + (y + 0.5 - 64 - ny * off) * ny;
                if (Math.Abs(v) > wh) continue;
                PixelWithin(input, got, y * 128 + x, 1, $"angle={angle} の帯の内側 ({x}, {y})");
                inside++;
            }
            Check.True(inside > 128, $"angle={angle}: 帯の内側の画素が少ない({inside})");
        }
    }

    [Test]
    public void AC14_sharpenは段差の縁を強めstrength0と一様な画像を変えない()
    {
        using var rs = Warp();
        var img = new byte[128 * 128 * 4];
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 128; x++) Put(img, y * 128 + x, x < 64 ? 0x404040FFu : 0xC0C0C0FFu);
        var input = Input8(rs, img, 128, 128);
        var got = Rgba8(rs, img, 128, 128, Inv(rs, "sharpen", ("radius", N(10)), ("strength", N(100))));
        var row = 64 * 128;
        Check.Near(23, got[(row + 63) * 4], 3, "行 64 の x=63 の R");
        Check.Near(207, got[(row + 64) * 4], 3, "行 64 の x=64 の R");
        for (var x = 0; x < 128; x++)
        {
            if (x <= 60 || x >= 67) PixelWithin(input, got, row + x, 1, $"行 64 の x={x}");
        }
        AllWithin(input, Rgba8(rs, img, 128, 128, Inv(rs, "sharpen", ("radius", N(10)), ("strength", N(0)))), 1, "strength=0");
        var uni = Solid(128, 128, Uniform);
        AllWithin(Input8(rs, uni, 128, 128), Rgba8(rs, uni, 128, 128, Inv(rs, "sharpen", ("strength", N(300)))), 1, "一様な画像");
    }

    [Test]
    public void AC14_glowはintensity0と黒を変えず白い1画素の隣を明るくする()
    {
        using var rs = Warp();
        var img = Varied(128, 128, true);
        AllWithin(Input8(rs, img, 128, 128), Rgba8(rs, img, 128, 128, Inv(rs, "glow", ("intensity", N(0)), ("threshold", N(0)))), 1, "intensity=0");
        var black = Solid(128, 128, 0x000000FF);
        AllWithin(Input8(rs, black, 128, 128), Rgba8(rs, black, 128, 128, Inv(rs, "glow", ("threshold", N(0)), ("intensity", N(300)))), 1, "黒い画像");
        const int n = 129;
        var dot = Solid(n, n, 0x000000FF);
        Put(dot, 64 * n + 64, 0xFFFFFFFF);
        var before = F16(rs, dot, n, n);
        var after = F16(rs, dot, n, n, Inv(rs, "glow", ("threshold", N(50)), ("intensity", N(100))));
        foreach (var (x, y) in new[] { (63, 64), (65, 64), (64, 63), (64, 65) })
        {
            var i = (y * n + x) * 4;
            Check.True(after[i] > before[i] && after[i + 1] > before[i + 1] && after[i + 2] > before[i + 2], $"隣の画素 ({x}, {y}) が明るくならない");
        }
    }

    [Test]
    public void AC14_vignetteはstrength0で入力と一致し最大で四隅を黒くする()
    {
        using var rs = Warp();
        var img = Varied(128, 128, true);
        AllWithin(Input8(rs, img, 128, 128), Rgba8(rs, img, 128, 128, Inv(rs, "vignette", ("strength", N(0)), ("size", N(0)))), 1, "strength=0");
        var uni = Solid(128, 128, Uniform);
        var input = Input8(rs, uni, 128, 128);
        var got = Rgba8(rs, uni, 128, 128,
            Inv(rs, "vignette", ("strength", N(100)), ("size", N(0)), ("soft", N(60)), ("color", S("#000000FF"))));
        foreach (var (x, y) in new[] { (0, 0), (127, 0), (0, 127), (127, 127) }) PixelIs(got, y * 128 + x, (0, 0, 0, 255), 1, $"隅 ({x}, {y})");
        PixelWithin(input, got, 64 * 128 + 64, 1, "中心に最も近い画素");
        PixelWithin(input, got, 63 * 128 + 63, 1, "中心に最も近い画素");
    }

    [Test]
    public void AC14_grainはstrength0で入力と一致し平均と散らばりとseedの差が表のとおり()
    {
        using var rs = Warp();
        var img = Varied(128, 128, true);
        AllWithin(Input8(rs, img, 128, 128), Rgba8(rs, img, 128, 128, Inv(rs, "grain", ("strength", N(0)))), 1, "strength=0");

        var gray = Solid(128, 128, 0x808080FF);
        var inv = Inv(rs, "grain", ("strength", N(50)), ("size", N(2)));
        var a = Rgba8(rs, gray, 128, 128, new FrameContext(12345, 0, 1), inv);
        var b = Rgba8(rs, gray, 128, 128, new FrameContext(12345, 0, 1), inv);
        var c = Rgba8(rs, gray, 128, 128, new FrameContext(54321, 0, 1), inv);
        Check.True(a.AsSpan().SequenceEqual(b), "同じ seed と salt で出力が違う");
        double mean = 0, sq = 0, diff = 0;
        var count = 128 * 128;
        for (var i = 0; i < count; i++)
        {
            mean += a[i * 4];
            sq += a[i * 4] * (double)a[i * 4];
            diff += Math.Abs(a[i * 4] - c[i * 4]);
            Check.Equal((byte)255, a[i * 4 + 3], $"画素 {i} の α");
            Check.True(a[i * 4] == a[i * 4 + 1] && a[i * 4] == a[i * 4 + 2], $"画素 {i}: mono で 3 チャンネルが違う");
        }
        mean /= count;
        var sd = Math.Sqrt(sq / count - mean * mean);
        Console.WriteLine($"    grain: 平均 {mean:F2}、標準偏差 {sd:F2}、seed を変えた平均絶対差 {diff / count:F2}");
        Check.Near(128, mean, 2, "出力の平均");
        Check.True(sd >= 5, $"標準偏差 {sd}");
        Check.True(diff / count >= 3, $"seed を変えた平均絶対差 {diff / count}");
        // salt を変えても変わる(行ごとに別の雑音)
        var d = Rgba8(rs, gray, 128, 128, new FrameContext(12345, 0, 1), inv with { Salt = 2 });
        Check.True(!a.AsSpan().SequenceEqual(d), "salt を変えても出力が同じ");
        // 時間に依存しない(フレームの番号では変わらない)
        var e = Rgba8(rs, gray, 128, 128, new FrameContext(12345, 7, 40), inv);
        Check.True(a.AsSpan().SequenceEqual(e), "フレームの番号で出力が変わった");
    }

    [Test]
    public void AC14_aberrationはamount0で入力と一致し白い縦線を赤緑青にずらす()
    {
        using var rs = Warp();
        var img = Varied(128, 128, true);
        AllWithin(Input8(rs, img, 128, 128), Rgba8(rs, img, 128, 128, Inv(rs, "aberration", ("amount", N(0)))), 1, "amount=0");
        var line = Solid(200, 200, 0x000000FF);
        for (var y = 0; y < 200; y++) Put(line, y * 200 + 100, 0xFFFFFFFF);
        var got = Rgba8(rs, line, 200, 200, Inv(rs, "aberration", ("mode", S("linear")), ("angle", N(0)), ("amount", N(30))));
        foreach (var y in new[] { 0, 50, 100, 199 })
        {
            for (var c = 0; c < 3; c++)
            {
                var best = 0;
                for (var x = 0; x < 200; x++) if (got[(y * 200 + x) * 4 + c] > got[(y * 200 + best) * 4 + c]) best = x;
                var expected = c == 0 ? 94 : c == 1 ? 100 : 106;
                Check.InRange(expected - 1, expected + 1, best, $"行 {y} の {"RGB"[c]} の線の位置");
                Check.True(got[(y * 200 + best) * 4 + c] >= 250, $"行 {y} の {"RGB"[c]} の線の明るさ");
            }
        }
    }

    [Test]
    public void AC14_embossは一様な画像のgrayで128になりαを変えない()
    {
        using var rs = Warp();
        var uni = Solid(128, 128, Uniform);
        var got = Rgba8(rs, uni, 128, 128, Inv(rs, "emboss", ("mode", S("gray"))));
        for (var i = 0; i < 128 * 128; i++) PixelIs(got, i, (128, 128, 128, 255), 3, "一様な画像");
        var img = Varied(128, 128, true);
        var input = Input8(rs, img, 128, 128);
        foreach (var mode in new[] { "gray", "color" })
        {
            var e = Rgba8(rs, img, 128, 128, Inv(rs, "emboss", ("mode", S(mode)), ("depth", N(40))));
            for (var i = 0; i < 128 * 128; i++) Check.Equal(input[i * 4 + 3], e[i * 4 + 3], $"{mode}: 画素 {i} の α");
        }
    }

    [Test]
    public void AC14_halftoneは白一色で紙の色に黒一色でインクの色になる()
    {
        using var rs = Warp();
        foreach (var (color, expected, name) in new[] { (0xFFFFFFFFu, 0xF2F0E6u, "白一色→紙"), (0x000000FFu, 0x14121Fu, "黒一色→インク") })
        {
            var got = Rgba8(rs, Solid(200, 200, color), 200, 200, Inv(rs, "halftone", ("mode", S("mono")), ("size", N(100))));
            for (var c = 0; c < 3; c++)
            {
                double mean = 0;
                for (var i = 0; i < 200 * 200; i++) mean += got[i * 4 + c];
                mean /= 200 * 200;
                Check.Near((expected >> (16 - 8 * c)) & 0xFF, mean, 3, $"{name} の {"RGB"[c]} の平均");
            }
        }
    }

    [Test]
    public void AC14_wavesはamp0と行ごとに一様な画像のhで入力と一致する()
    {
        using var rs = Warp();
        var img = Varied(128, 128, true);
        foreach (var dir in new[] { "h", "v", "both" })
            AllWithin(Input8(rs, img, 128, 128), Rgba8(rs, img, 128, 128, Inv(rs, "waves", ("dir", S(dir)), ("amp", N(0)))), 1, $"amp=0 {dir}");
        var rows = new byte[128 * 128 * 4];
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 128; x++) Put(rows, y * 128 + x, ((uint)(y * 2) << 24) | ((uint)(255 - y) << 16) | ((uint)((y * 7) & 255) << 8) | 0xFF);
        AllWithin(Input8(rs, rows, 128, 128), Rgba8(rs, rows, 128, 128, Inv(rs, "waves", ("dir", S("h")), ("amp", N(60)))), 1, "行ごとに一様な画像の h");
    }

    [Test]
    public void AC14_warpはstrength0で入力と一致し半径の外の四隅を変えない()
    {
        using var rs = Warp();
        var img = Varied(200, 200, false);
        var input = Input8(rs, img, 200, 200);
        foreach (var kind in new[] { "bulge", "twirl" })
        {
            AllWithin(input, Rgba8(rs, img, 200, 200, Inv(rs, "warp", ("kind", S(kind)), ("strength", N(0)))), 1, $"strength=0 {kind}");
            var got = Rgba8(rs, img, 200, 200, Inv(rs, "warp", ("kind", S(kind)), ("center", new PointValue(0.5, 0.5)), ("radius", N(200)), ("strength", N(100))));
            foreach (var (x, y) in new[] { (0, 0), (199, 0), (0, 199), (199, 199) }) PixelWithin(input, got, y * 200 + x, 1, $"{kind} の隅 ({x}, {y})");
        }
    }

    [Test]
    public void AC14_outlineは外側の1から2画素目を色にし7画素目から透明で内側を変えない()
    {
        using var rs = Warp();
        var img = CenterSquare(Uniform);
        var input = Input8(rs, img, 200, 200);
        var got = Rgba8(rs, img, 200, 200, Inv(rs, "outline", ("width", N(20)), ("pos", S("outside")), ("color", S("#FF0000FF")), ("soft", N(0))));
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
        {
            var i = y * 200 + x;
            var outside = Math.Max(Math.Max(50 - x, x - 149), Math.Max(50 - y, y - 149)); // 辺のすぐ外の画素が 1
            var alongEdge = (x >= 50 && x < 150) || (y >= 50 && y < 150); // 辺に垂直な向きにだけ離れた画素
            if (outside <= 0) PixelWithin(input, got, i, 1, $"正方形の内側 ({x}, {y})");
            else if (outside <= 2 && alongEdge) PixelIs(got, i, (255, 0, 0, 255), 3, $"辺から {outside} 画素目 ({x}, {y})");
            else if (outside >= 7) Check.Equal((byte)0, got[i * 4 + 3], $"辺から {outside} 画素目 ({x}, {y}) の α");
        }
    }

    [Test]
    public void AC14_shadowは下辺の外の8行を黒くしその下を透明にしdx0dy0で入力と一致する()
    {
        using var rs = Warp();
        var img = CenterSquare(0xFFFFFFFF);
        var input = Input8(rs, img, 200, 200);
        var got = Rgba8(rs, img, 200, 200, Inv(rs, "shadow", ("dx", N(0)), ("dy", N(40)), ("blur", N(0)), ("color", S("#000000FF"))));
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
        {
            var i = y * 200 + x;
            var inSquare = x >= 50 && x < 150 && y >= 50 && y < 150;
            if (inSquare) PixelWithin(input, got, i, 1, $"正方形の内側 ({x}, {y})");
            else if (x >= 50 && x < 150 && y >= 150 && y < 158) PixelIs(got, i, (0, 0, 0, 255), 3, $"下辺の外の行 ({x}, {y})");
            else if (y >= 158) Check.Equal((byte)0, got[i * 4 + 3], $"影の下 ({x}, {y}) の α");
        }
        // 表の「dx=0・dy=0 のとき」は、前の条件(blur=0・color=#000000FF)の dy だけを 0 にしたものと読む(既定の blur=20 では影がにじみ出る)
        AllWithin(input, Rgba8(rs, img, 200, 200, Inv(rs, "shadow", ("dx", N(0)), ("dy", N(0)), ("blur", N(0)), ("color", S("#000000FF")))), 1, "dx=0・dy=0");
    }
}
