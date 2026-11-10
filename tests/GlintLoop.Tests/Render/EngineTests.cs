// 描画の土台のテスト(WARP だけ): AC-12 の往復、最後の変換の式(§9.7 の規則 7)、汎用のエフェクト実行(passes・repeat・when・scale・mix・切り)、
// exposure の式(AC-14 の (1) の土台)、INV-5(RenderContext は専用スレッドの外から使えない)。
// 画像はすべてコードで作る(§8.5)。期待値は仕様書の式を C# の double で書き直して求める。
using GlintLoop.Core.Dither;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Render;

public sealed class EngineTests
{
    private static RenderService Warp() => RenderService.Create(GpuAdapterRequest.Warp);

    private static readonly IReadOnlyList<EffectInvocation> Empty = [];

    // ---- 画像 ----

    /// <summary>256×256、α=255。R=x、G=y、B=x xor y(どのチャンネルも 0〜255 の全値を含み、並べ方が違う)。</summary>
    private static byte[] AllValues256()
    {
        var px = new byte[256 * 256 * 4];
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
        {
            var o = (y * 256 + x) * 4;
            px[o] = (byte)x;
            px[o + 1] = (byte)y;
            px[o + 2] = (byte)(x ^ y);
            px[o + 3] = 255;
        }
        return px;
    }

    /// <summary>size×size、α=255、RGB は hash32(画素の番号) の下位 3 バイト。</summary>
    private static byte[] HashImage(int size)
    {
        var px = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var h = Hash.Hash32((uint)i);
            px[i * 4] = (byte)h;
            px[i * 4 + 1] = (byte)(h >> 8);
            px[i * 4 + 2] = (byte)(h >> 16);
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    private static readonly byte[] PartialAlphas = [1, 2, 17, 64, 128, 200, 254];

    /// <summary>256×8。行 0〜6 は α が PartialAlphas、行 7 は α=0。R=x、G=255−x、B=(37x+11) mod 256(どれも全 256 値)。</summary>
    private static byte[] PartialAlphaImage()
    {
        var px = new byte[256 * 8 * 4];
        for (var row = 0; row < 8; row++)
        for (var x = 0; x < 256; x++)
        {
            var o = (row * 256 + x) * 4;
            px[o] = (byte)x;
            px[o + 1] = (byte)(255 - x);
            px[o + 2] = (byte)((37 * x + 11) & 255);
            px[o + 3] = row < 7 ? PartialAlphas[row] : (byte)0;
        }
        return px;
    }

    private static void CheckExact(byte[] expected, byte[] actual, int width, string what)
    {
        Check.Equal(expected.Length, actual.Length, $"{what}: バイト数");
        var bad = 0;
        var first = "";
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i]) continue;
            if (bad++ == 0)
                first = $"画素 ({i / 4 % width}, {i / 4 / width}) の成分 {"RGBA"[i % 4]}: 期待 {expected[i]}、実際 {actual[i]}";
        }
        Check.True(bad == 0, $"{what}: 一致しない成分が {bad} 個。最初は {first}");
    }

    // ---- AC-12 ----

    [Test(Slow = true)]
    public void AC12_1_α255の全256値とhash32の1024の画像が書き出しで完全に一致する()
    {
        using var rs = Warp();
        var a = AllValues256();
        CheckExact(a, rs.RenderStraightRgba8Async(a, 256, 256, Empty, FrameContext.Still).GetAwaiter().GetResult(), 256, "全 256 値");
        var h = HashImage(1024);
        CheckExact(h, rs.RenderStraightRgba8Async(h, 1024, 1024, Empty, FrameContext.Still).GetAwaiter().GetResult(), 1024, "hash32 の 1024×1024");
    }

    [Test]
    public void AC12_2_半透明は差1以内でα0は全部0()
    {
        using var rs = Warp();
        var src = PartialAlphaImage();
        var output = rs.RenderStraightRgba8Async(src, 256, 8, Empty, FrameContext.Still).GetAwaiter().GetResult();
        var maxDiff = 0;
        for (var row = 0; row < 7; row++)
        for (var x = 0; x < 256; x++)
        for (var c = 0; c < 4; c++)
        {
            var o = (row * 256 + x) * 4 + c;
            var d = Math.Abs(src[o] - output[o]);
            maxDiff = Math.Max(maxDiff, d);
            Check.True(d <= 1, $"α={PartialAlphas[row]} の画素 x={x} の成分 {"RGBA"[c]}: 入力 {src[o]}、出力 {output[o]}");
        }
        for (var x = 0; x < 256; x++)
        for (var c = 0; c < 4; c++)
            Check.Equal((byte)0, output[(7 * 256 + x) * 4 + c], $"α=0 の画素 x={x} の成分 {"RGBA"[c]}");
    }

    [Test(Slow = true)]
    public void AC12_3_表示用のPbgra32でもα255の値が書き出しと一致する()
    {
        using var rs = Warp();
        foreach (var (img, w) in new[] { (AllValues256(), 256), (HashImage(1024), 1024) })
        {
            var straight = rs.RenderStraightRgba8Async(img, w, w, Empty, FrameContext.Still).GetAwaiter().GetResult();
            var pbgra = rs.RenderPbgra32Async(img, w, w, Empty, FrameContext.Still).GetAwaiter().GetResult();
            var asBgra = new byte[straight.Length];
            for (var i = 0; i < straight.Length; i += 4)
            {
                asBgra[i] = straight[i + 2];
                asBgra[i + 1] = straight[i + 1];
                asBgra[i + 2] = straight[i];
                asBgra[i + 3] = straight[i + 3];
            }
            CheckExact(asBgra, pbgra, w, $"{w}×{w} の Pbgra32");
        }
    }

    // ---- 規則 7 の式 ----

    /// <summary>§9.7 の規則 7 の量子化を double で書き直したもの(P は読み戻した RGBA16F の値)。境目に近い(1e-3 以内)ものは null。</summary>
    private static byte? Quantize(double c, int i, int j)
    {
        var n = ((BlueNoise.At(i, j) + 0.5) / 4096.0 - 0.5) * 0.9;
        var v = 255 * Srgb.Enc(MathX.Sat(c)) + 0.5 + n;
        if (Math.Abs(v - Math.Round(v)) < 1e-3 && Math.Abs(v - Math.Floor(v)) < 1e-3) return null;
        return (byte)Math.Clamp(Math.Floor(v), 0, 255);
    }

    [Test]
    public void 規則7_最後の変換は青色雑音の量子化とアルファの丸めの式どおり()
    {
        // 130×70(64 で割った余りが 0 以外の大きさ)を 1 色で塗り、画素ごとの雑音で値が変わるのを式と比べる
        const int w = 130, h = 70;
        var colors = new[]
        {
            new System.Numerics.Vector4(0.2f, 0.5f, 0.8f, 1f),
            new System.Numerics.Vector4(0.1f * 0.4f, 0.37f * 0.4f, 0.004f * 0.4f, 0.4f),
            new System.Numerics.Vector4(3f, 0.0001f, 0.9f, 0.9f), // straight で 1 を超える色は 255 に切れる
            new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 0.00005f), // α≤0.0001 は全部 0
        };
        using var rs = Warp();
        foreach (var color in colors)
        {
            var (half, straight, pbgra) = rs.InvokeAsync(c =>
            {
                using var t = c.CreateWorking(w, h);
                c.Passes.Fill(t, color);
                return (c.ReadRgba16F(t), c.ReadStraightRgba8(t), c.ReadPbgra32(t));
            }).GetAwaiter().GetResult();
            var checkedCount = 0;
            for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                var o = (j * w + i) * 4;
                double a = half[o + 3];
                if (a <= 0.0001)
                {
                    for (var k = 0; k < 4; k++)
                    {
                        Check.Equal((byte)0, straight[o + k], $"{color} ({i},{j}) straight[{k}]");
                        Check.Equal((byte)0, pbgra[o + k], $"{color} ({i},{j}) pbgra[{k}]");
                    }
                    continue;
                }
                var qa = (byte)Math.Floor(255 * MathX.Sat(a) + 0.5);
                Check.Equal(qa, straight[o + 3], $"{color} ({i},{j}) の α");
                Check.Equal(qa, pbgra[o + 3], $"{color} ({i},{j}) の Pbgra32 の α");
                for (var k = 0; k < 3; k++)
                {
                    var q = Quantize(half[o + k] / a, i, j);
                    if (q is null) continue;
                    checkedCount++;
                    Check.Equal(q.Value, straight[o + k], $"{color} ({i},{j}) の成分 {k}");
                    var pm = (byte)Math.Floor(q.Value * qa / 255.0 + 0.5);
                    Check.Equal(pm, pbgra[o + 2 - k], $"{color} ({i},{j}) の Pbgra32 の成分 {k}");
                }
            }
            if (color.W > 0.001f) Check.True(checkedCount > w * h * 3 * 9 / 10, $"{color}: 比べた成分が少ない({checkedCount})");
        }
    }

    // ---- 汎用のエフェクト実行(exposure のシェーダーを使って組み立てた宣言で確かめる) ----

    private static EffectDef ExposureLike(RenderService rs) => rs.Effects.Find("exposure") ?? throw new CheckFailedException("exposure が読めていない");

    /// <summary>1×1 の sRGB 8 ビットの画素を取り込み、effects を掛けて RGBA16F で読む。</summary>
    private static float[] RunOnPixel(RenderService rs, byte r, byte g, byte b, byte a, params EffectInvocation[] effects) =>
        rs.RenderRgba16FAsync([r, g, b, a], 1, 1, effects, FrameContext.Still).GetAwaiter().GetResult();

    private static EffectInvocation Ev(EffectDef def, double ev, double amount = 1) =>
        new(def) { Values = new Dictionary<string, ParamValue> { ["ev"] = new NumberValue(ev) }, Amount = amount };

    [Test]
    public void AC14_exposureの結果が式Cかける2のev乗と8ビットで3以内()
    {
        using var rs = Warp();
        var def = ExposureLike(rs);
        string[] colors = ["000000", "FFFFFF", "808080", "FF0000", "C8A080", "203040", "F0D8E8"];
        double[] evs = [-3, -1.5, -0.25, 0, 0.5, 1, 3];
        var w = colors.Length;
        var src = new byte[w * 4];
        for (var i = 0; i < w; i++)
        {
            var v = Convert.ToUInt32(colors[i], 16);
            src[i * 4] = (byte)(v >> 16);
            src[i * 4 + 1] = (byte)(v >> 8);
            src[i * 4 + 2] = (byte)v;
            src[i * 4 + 3] = 255;
        }
        foreach (var ev in evs)
        foreach (var amount in new[] { 1.0, 0.5 })
        {
            var output = rs.RenderStraightRgba8Async(src, w, 1, [Ev(def, ev, amount)], FrameContext.Still).GetAwaiter().GetResult();
            for (var i = 0; i < w; i++)
            for (var k = 0; k < 3; k++)
            {
                var c = Srgb.Lin(src[i * 4 + k] / 255.0);
                var fx = c * Math.Pow(2, ev);
                var mixed = c + amount * (fx - c);
                var expected = Math.Floor(255 * Srgb.Enc(MathX.Sat(mixed)) + 0.5);
                Check.True(Math.Abs(expected - output[i * 4 + k]) <= 3,
                    $"#{colors[i]} ev={ev} amount={amount} の成分 {k}: 期待 {expected}、実際 {output[i * 4 + k]}");
            }
        }
    }

    [Test]
    public void 規則4_出力はRGBを64までに切りNaNを0にしαは変えない()
    {
        using var rs = Warp();
        var def = ExposureLike(rs);
        // 範囲の直しは呼び手の仕事なので、ここでは範囲外の値をそのまま渡して、共通の切りだけを確かめる
        var big = RunOnPixel(rs, 255, 128, 0, 255, Ev(def, 8));
        Check.Near(64, big[0], 0, "R(1×256 は 64 に切る)");
        Check.Near(Srgb.Lin(128 / 255.0) * 256, big[1], 0.1, "G(約 55 で 64 未満はそのまま)");
        Check.Equal(0f, big[2], "B");
        Check.Equal(1f, big[3], "α");
        var nan = RunOnPixel(rs, 200, 100, 50, 255, Ev(def, double.NaN));
        Check.SequenceEqual(new[] { 0f, 0f, 0f, 1f }, nan, "NaN は 0");
        var negative = RunOnPixel(rs, 200, 100, 50, 255, Ev(def, double.NegativeInfinity));
        Check.SequenceEqual(new[] { 0f, 0f, 0f, 1f }, negative, "2^−∞ は 0");
    }

    [Test]
    public void 規則4_mixのamountが0なら入力のままで1なら効果のまま()
    {
        using var rs = Warp();
        var def = ExposureLike(rs);
        var input = RunOnPixel(rs, 200, 100, 50, 128);
        Check.SequenceEqual(input, RunOnPixel(rs, 200, 100, 50, 128, Ev(def, 2, amount: 0)), "amount=0");
        var full = RunOnPixel(rs, 200, 100, 50, 128, Ev(def, 2, amount: 1));
        for (var k = 0; k < 3; k++) Check.Near(input[k] * 4, full[k], 0.002 * input[k] * 4 + 1e-6, $"amount=1 の成分 {k}");
        Check.Equal(input[3], full[3], "α");
    }

    [Test]
    public void 汎用実行_passのつなぎとwhenとrepeatとscaleモード()
    {
        using var rs = Warp();
        var baseDef = ExposureLike(rs);
        var ev = baseDef.Params[0];
        var kind = new ParamDef("kind", "種類", ParamType.Enum, null, null, null, new StringValue("a"), "",
            [new EnumOption("a", "甲"), new EnumOption("b", "乙")], null, false, true, null, null);
        var times = new ParamDef("times", "回数", ParamType.Int, 0, 8, 1, new NumberValue(3), "", null, null, false, true, null, null);
        PassDef P(string id, string input, ParamCondition? when = null, PassRepeat? repeat = null, string source = "ev") =>
            new(id, "exposure", [input], 1, repeat ?? PassRepeat.Once, when, [new PassBinding("ev", source), new PassBinding("amount", "$amount")]);

        // 宣言の順と逆に参照するパス: b は pass:a を読む。実行は a、b の順で、出力は実行の順で最後のパス(b)
        var chain = baseDef with { Params = [ev, kind, times], Passes = [P("b", "pass:a"), P("a", "src")] };
        var v = new Dictionary<string, ParamValue> { ["ev"] = new NumberValue(1) };
        var input = RunOnPixel(rs, 100, 100, 100, 255);
        var r = RunOnPixel(rs, 100, 100, 100, 255, new EffectInvocation(chain) { Values = v });
        Check.Near(input[0] * 4, r[0], 0.002 * 4 * input[0], "a のあとに b(×2×2)");

        // when: kind=a なら x だけ、kind=b なら x と y(y は pass:x を読む)
        var when = baseDef with
        {
            Params = [ev, kind, times],
            Passes = [P("x", "src"), P("y", "pass:x", new ParamCondition("kind", ["b"]))],
        };
        var ra = RunOnPixel(rs, 100, 100, 100, 255, new EffectInvocation(when) { Values = v });
        var rb = RunOnPixel(rs, 100, 100, 100, 255,
            new EffectInvocation(when) { Values = new Dictionary<string, ParamValue>(v) { ["kind"] = new StringValue("b") } });
        Check.Near(input[0] * 2, ra[0], 0.002 * 2 * input[0], "kind=a は 1 回");
        Check.Near(input[0] * 4, rb[0], 0.002 * 4 * input[0], "kind=b は 2 回");

        // repeat: 整数 3 回と、パラメータ times(既定 3、0 なら入力のまま)
        var rep = baseDef with { Params = [ev, kind, times], Passes = [P("m", "src", repeat: new PassRepeat(3, null))] };
        Check.Near(input[0] * 8, RunOnPixel(rs, 100, 100, 100, 255, new EffectInvocation(rep) { Values = v })[0], 0.004 * 8 * input[0], "repeat 3");
        var repParam = baseDef with { Params = [ev, kind, times], Passes = [P("m", "src", repeat: new PassRepeat(0, "times"))] };
        Check.Near(input[0] * 8, RunOnPixel(rs, 100, 100, 100, 255, new EffectInvocation(repParam) { Values = v })[0], 0.004 * 8 * input[0], "repeat times=3");
        var zero = new Dictionary<string, ParamValue>(v) { ["times"] = new NumberValue(0) };
        Check.SequenceEqual(input, RunOnPixel(rs, 100, 100, 100, 255, new EffectInvocation(repParam) { Values = zero }), "repeat times=0");

        // scale モード: ev の実効値 = 0 + (2 − 0) × 0.5 = 1(×2)。mix の混ぜはしない
        var scale = baseDef with { AmountMode = AmountMode.Scale, ScaleParams = ["ev"] };
        var rs2 = RunOnPixel(rs, 100, 100, 100, 255,
            new EffectInvocation(scale) { Values = new Dictionary<string, ParamValue> { ["ev"] = new NumberValue(2) }, Amount = 0.5 });
        Check.Near(input[0] * 2, rs2[0], 0.002 * 2 * input[0], "scale モードの実効値");

        // パスの scale 0.5 は出力の辺を切り上げで半分にする(none のエフェクトだけが大きさを変えられる)
        var half = baseDef with { AmountMode = AmountMode.None, Passes = [P("m", "src") with { Scale = 0.5 }] };
        var size = rs.InvokeAsync(c =>
        {
            using var t = c.IngestSrgb8(new byte[5 * 3 * 4], 5, 3, 20);
            using var o = c.RunEffect(new EffectInvocation(half), t, FrameContext.Still);
            return (o.Width, o.Height);
        }).GetAwaiter().GetResult();
        Check.Equal((3, 2), size, "scale 0.5 の大きさ");
    }

    [Test]
    public void 定数バッファはbindの順に16バイトずつ型ごとの形で詰める()
    {
        var color = new ParamDef("tint", "色", ParamType.Color, null, null, null, new StringValue("#FF800040"), "", null, null, false, true, null, null);
        var on = new ParamDef("on", "入", ParamType.Bool, null, null, null, new BoolValue(true), "", null, null, false, true, null, null);
        var kind = new ParamDef("kind", "種類", ParamType.Enum, null, null, null, new StringValue("b"), "",
            [new EnumOption("a", "甲"), new EnumOption("b", "乙")], null, false, true, null, null);
        var pt = new ParamDef("center", "中心", ParamType.Point, null, null, null, new PointValue(0.25, 0.75), "", null, null, false, true, null, null);
        var r = new ParamDef("pad", "余白", ParamType.Rect, 0, 100, 1, new RectValue(1, 2, 3, 4), "‰", null, null, false, true, null, null);
        var def = new EffectDef(1, "t", "t", EffectStage.Look, "g", [], AmountMode.Mix, [], true, false, WaveKind.Ease, SizeRule.Same,
            AuxKind.None, [color, on, kind, pt, r], []);
        var pass = new PassDef("m", "exposure", ["src"], 1, PassRepeat.Once, null,
        [
            new("a", "tint"), new("b", "on"), new("c", "kind"), new("d", "center"), new("e", "pad"),
            new("f", "$texel"), new("g", "$shortSide"), new("h", "$seed"), new("i", "$salt"), new("j", "$wave"), new("k", "$t"), new("l", "$amount"),
        ]);
        var inv = new EffectInvocation(def) { Amount = 0.25, Salt = 0xDEADBEEF };
        var bytes = EffectConstants.Pack(inv, pass, EffectConstants.ResolveValues(inv), 40, 30, new FrameContext(0x89ABCDEF, 3, 12));
        Check.Equal(12 * 16, bytes.Length, "バイト数");
        float F(int slot, int c) => BitConverter.ToSingle(bytes, slot * 16 + c * 4);
        uint U(int slot, int c) => BitConverter.ToUInt32(bytes, slot * 16 + c * 4);
        Check.Near(1.0, F(0, 0), 1e-6, "色の R(lin(255))");
        Check.Near(Srgb.Lin(0x80 / 255.0), F(0, 1), 1e-6, "色の G");
        Check.Equal(0f, F(0, 2), "色の B");
        Check.Near(0x40 / 255.0, F(0, 3), 1e-6, "色の不透明度");
        Check.Equal(1u, U(1, 0), "bool");
        Check.Equal(1u, U(2, 0), "enum の番号");
        Check.SequenceEqual(new[] { 0.25f, 0.75f, 0f, 0f }, new[] { F(3, 0), F(3, 1), F(3, 2), F(3, 3) }, "点");
        Check.SequenceEqual(new[] { 1f, 2f, 3f, 4f }, new[] { F(4, 0), F(4, 1), F(4, 2), F(4, 3) }, "矩形");
        Check.SequenceEqual(new[] { 1f / 40, 1f / 30, 40f, 30f }, new[] { F(5, 0), F(5, 1), F(5, 2), F(5, 3) }, "$texel");
        Check.Equal(30f, F(6, 0), "$shortSide");
        Check.Equal(0x89ABCDEFu, U(7, 0), "$seed");
        Check.Equal(0xDEADBEEFu, U(8, 0), "$salt");
        Check.Equal(2u, U(9, 0), "$wave(ease)");
        Check.Equal(0.25f, F(10, 0), "$t");
        Check.Equal(0.25f, F(11, 0), "$amount");
    }

    // ---- INV-5 と RenderService ----

    [Test]
    public void INV5_RenderContextは専用スレッドの外から使うと例外になる()
    {
        using var rs = Warp();
        var (context, threadId) = rs.InvokeAsync(c => (c, Environment.CurrentManagedThreadId)).GetAwaiter().GetResult();
        Check.True(threadId != Environment.CurrentManagedThreadId, "依頼がテストのスレッドで実行された");
        Check.Throws<InvalidOperationException>(() => context.CreateWorking(4, 4), "別スレッドから CreateWorking");
        Check.Throws<InvalidOperationException>(() => context.IngestSrgb8(new byte[4], 1, 1, 4), "別スレッドから IngestSrgb8");
        Check.Throws<InvalidOperationException>(() => context.RunStack(Empty, null!, FrameContext.Still), "別スレッドから RunStack");
        Check.True(rs.IsWarp && rs.FallbackReason is null, "WARP を要求して WARP になっていない");
        Check.Equal(16384, rs.MaxTextureDimension, "機能レベル 11_0 以上のテクスチャの最大寸法");
        Check.Equal(8192, RenderContext.MaxTextureDimensionOf(Vortice.Direct3D.FeatureLevel.Level_10_1), "10_x の最大寸法");
    }
}
