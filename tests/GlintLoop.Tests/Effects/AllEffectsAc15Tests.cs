// 埋め込まれた全エフェクト(カタログから列挙するので、後から足したものも自動で入る)を、512×512 の画像 3 種で AC-15 の (1)〜(7) にかける。
// 有限性は、共通の後処理(effect_finish。NaN を 0 に、∞ を 64 に切る)の前のパスの出力と、後処理のあとの出力の両方で確かめる。
// 回転・切り抜き・余白は、passes が空で §9.7 の規則 3 の折りたたみで描く(合成の担当の範囲)ので、どの検査からも除く。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Effects;

public sealed class AllEffectsAc15Tests
{
    [Test(Slow = true)]
    public void AC15_1_4_7_既定のパラメータで有限かつ範囲内で2回の出力が一致し大きさが変わらない()
    {
        using var rs = Ac15Kit.Warp();
        rs.InvokeAsync(ctx =>
        {
            using var probe = new Ac15Probe(ctx);
            foreach (var def in Ac15Kit.Targets(rs))
            {
                foreach (var (name, img) in Ac15Kit.Images)
                {
                    var what = $"{def.Id}・{name}・既定";
                    var a = probe.Run(def, img, Ac15Kit.Invocation(def), what);
                    var b = probe.Run(def, img, Ac15Kit.Invocation(def), what + "(2 回目)");
                    Ac15Kit.SameBits(a.Final, b.Final, $"{what}: (4) 2 回の出力");
                }
            }
        }).GetAwaiter().GetResult();
    }

    [Test(Slow = true)]
    public void AC15_2_数値パラメータを最小と最大にしても有限で1回の処理が5秒以内()
    {
        using var rs = Ac15Kit.Warp();
        var slowest = (Ms: 0.0, What: "");
        var runs = 0;
        rs.InvokeAsync(ctx =>
        {
            using var probe = new Ac15Probe(ctx);
            foreach (var def in Ac15Kit.Targets(rs))
            {
                foreach (var (label, values) in Ac15Kit.NumericExtremes(def))
                {
                    foreach (var (name, img) in Ac15Kit.Images)
                    {
                        var what = $"{def.Id}・{name}・{label}";
                        var r = probe.Run(def, img, Ac15Kit.Invocation(def, values), what);
                        Check.True(r.Ms <= 5000, $"{what}: (2) 1 回の処理が {r.Ms:F0} ms(5 秒を超えた)");
                        if (r.Ms > slowest.Ms) slowest = (r.Ms, what);
                        runs++;
                    }
                }
            }
        }).GetAwaiter().GetResult();
        Console.WriteLine($"    AC-15 (2): {runs} 回、最も遅い 1 回は {slowest.What} の {slowest.Ms:F0} ms");
    }

    [Test(Slow = true)]
    public void AC15_3_mixとscaleのエフェクトはamount0で入力と1以内で一致する()
    {
        using var rs = Ac15Kit.Warp();
        rs.InvokeAsync(ctx =>
        {
            foreach (var (name, img) in Ac15Kit.Images)
            {
                // 「入力」は、エフェクトを通さずに同じ最後の変換で 8 ビットにしたもの(α=0 の画素は RGBA がすべて 0 になるため)
                using var input = ctx.IngestSrgb8(img, Ac15Kit.Size, Ac15Kit.Size, Ac15Kit.Size * 4);
                var expected = ctx.ReadStraightRgba8(input);
                foreach (var def in Ac15Kit.Targets(rs).Where(d => d.AmountMode != AmountMode.None))
                {
                    using var aux = Ac15Kit.AuxFor(ctx, def);
                    var inv = Ac15Kit.Invocation(def) with { Amount = 0, Aux = aux };
                    using var output = ctx.RunEffect(inv, input, Ac15Kit.Frame(def));
                    var actual = ctx.ReadStraightRgba8(output);
                    for (var i = 0; i < expected.Length; i++)
                    {
                        if (Math.Abs(expected[i] - actual[i]) > 1)
                            throw new CheckFailedException(
                                $"{def.Id}・{name}: (3) amount=0 の出力が入力と違う(画素 {i / 4} のチャンネル {i % 4}: 入力 {expected[i]}、出力 {actual[i]})");
                    }
                }
            }
        }).GetAwaiter().GetResult();
    }

    [Test(Slow = true)]
    public void AC15_5_列挙型のすべての選択肢で有限かつ範囲内で2回の出力が一致する()
    {
        using var rs = Ac15Kit.Warp();
        rs.InvokeAsync(ctx =>
        {
            using var probe = new Ac15Probe(ctx);
            foreach (var def in Ac15Kit.Targets(rs))
            {
                foreach (var p in EffectSchema.AllParams(def).Where(p => p.Type == ParamType.Enum))
                {
                    foreach (var option in p.Options!)
                    {
                        var values = new Dictionary<string, ParamValue> { [p.Key] = new StringValue(option.Value) };
                        foreach (var (name, img) in Ac15Kit.Images)
                        {
                            var what = $"{def.Id}・{name}・{p.Key}={option.Value}";
                            var a = probe.Run(def, img, Ac15Kit.Invocation(def, values), what);
                            var b = probe.Run(def, img, Ac15Kit.Invocation(def, values), what + "(2 回目)");
                            Ac15Kit.SameBits(a.Final, b.Final, $"{what}: (5) 2 回の出力");
                        }
                    }
                }
            }
        }).GetAwaiter().GetResult();
    }

    [Test]
    public void AC15_6_範囲外の値を読み込むと端か既定値に直りparam_clampedが書かれる()
    {
        var catalog = BuiltInEffects.Load(new MemoryLogSink());
        var targets = Ac15Kit.Targets(catalog);
        var checkedCount = 0;
        foreach (var def in targets)
        {
            foreach (var p in EffectSchema.AllParams(def))
            {
                foreach (var (json, expected) in Ac15Kit.OutOfRangeCases(p))
                {
                    var what = $"{def.Id}.{p.Key}={json}";
                    var log = new MemoryLogSink();
                    var r = DocumentJson.Read(Ac15Kit.DocWith(def.Id, p.Key, json), catalog, log);
                    Check.True(r.IsValid, $"{what}: 文書が読めない({r.InvalidReason})");
                    var entry = r.Document!.Layers[0].Stack.Single();
                    Check.Equal(expected, entry.Get(p.Key), $"{what}: 直した値");
                    var clamps = log.Events.Where(e => e.Name == DocumentJson.ParamClampedEvent)
                        .Select(e => ((string?)e.Get("effect"), (string?)e.Get("key"))).ToList();
                    Check.SequenceEqual([(def.Id, p.Key)], clamps, $"{what}: param_clamped");
                    checkedCount++;
                }
            }
        }
        Check.True(checkedCount > 0, "範囲外を読み込ませたパラメータが 1 つも無い");
    }

    [Test]
    public void AC15_6_NaNの値は既定値に直り範囲外として扱われる()
    {
        // JSON の数には NaN を書けない(JsonDocument が読まない)ので、DocumentJson.Read が値を直すときに使う ParamRules.Normalize に直接渡す。
        // Normalize の outOfRange が真のとき、DocumentJson.Read は param_clamped を書く(上のテストで確かめている)
        var catalog = BuiltInEffects.Load(new MemoryLogSink());
        var count = 0;
        foreach (var def in Ac15Kit.Targets(catalog))
        {
            foreach (var p in EffectSchema.AllParams(def))
            {
                ParamValue? nan = p.Type switch
                {
                    ParamType.Float or ParamType.Int or ParamType.Angle => new NumberValue(double.NaN),
                    ParamType.Point => new PointValue(double.NaN, 0.5),
                    ParamType.Rect => new RectValue(double.NaN, double.NaN, double.NaN, double.NaN),
                    _ => null,
                };
                if (nan is null) continue;
                var v = ParamRules.Normalize(p, nan, out var outOfRange);
                Check.True(outOfRange, $"{def.Id}.{p.Key}: NaN が範囲外として扱われない");
                Check.Equal(p.Default, v, $"{def.Id}.{p.Key}: NaN を直した値");
                count++;
            }
        }
        Check.True(count > 0, "NaN を渡したパラメータが 1 つも無い");
    }
}

/// <summary>AC-15 の検査の材料(対象の列挙・画像・依頼の組み立て・判定)。</summary>
file static class Ac15Kit
{
    public const int Size = 512;

    public static RenderService Warp() => RenderService.Create(GpuAdapterRequest.Warp);

    /// <summary>
    /// 検査するエフェクト。回転・切り抜き・余白(EffectSchema.IsFixedShape)は、passes を持たず §9.7 の規則 3 の折りたたみで
    /// 描くので除く(EffectRunner に渡すと入力をそのまま通すだけで、AC-15 の検査の意味が無い)。
    /// </summary>
    public static IReadOnlyList<EffectDef> Targets(RenderService rs) => Targets(rs.Effects);

    public static IReadOnlyList<EffectDef> Targets(EffectCatalog catalog)
    {
        var list = catalog.Effects.Where(d => !EffectSchema.IsFixedShape(d.Id)).ToList();
        Check.True(list.Count > 0, "検査するエフェクトが 1 つも無い");
        return list;
    }

    public static readonly IReadOnlyList<(string Name, byte[] Pixels)> Images =
    [
        ("一様な灰", UniformGray()),
        ("模様", Pattern(translucent: false)),
        ("半透明", Pattern(translucent: true)),
    ];

    /// <summary>一様な灰(#808080、α=255)。</summary>
    private static byte[] UniformGray()
    {
        var px = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 128;
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    /// <summary>
    /// グラデーション(R=x、G=y、B=斜め)の上に、円・矩形・1 画素の市松・細い縞を描いた画像。
    /// translucent のときは、α を左上から右下への傾き(0〜255)にし、α=0 の帯と α=128 の円を含める。
    /// </summary>
    private static byte[] Pattern(bool translucent)
    {
        var px = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var o = (y * Size + x) * 4;
            byte r = (byte)(x / 2), g = (byte)(y / 2), b = (byte)((x + y) / 4);
            var dx = x - 180;
            var dy = y - 200;
            if (dx * dx + dy * dy < 90 * 90) (r, g, b) = (230, 40, 60); // 円
            if (x >= 300 && x < 460 && y >= 60 && y < 180) (r, g, b) = (20, 160, 90); // 矩形
            if (x >= 320 && x < 448 && y >= 320 && y < 448 && ((x ^ y) & 1) == 0) (r, g, b) = (255, 255, 255); // 1 画素の市松
            if (y >= 470 && (x & 2) == 0) (r, g, b) = (0, 0, 0); // 細い縞
            byte a = 255;
            if (translucent)
            {
                a = (byte)((x + y) * 255 / (2 * Size - 2));
                if (x >= 40 && x < 80) a = 0; // α=0 の帯
                var cx = x - 380;
                var cy = y - 380;
                if (cx * cx + cy * cy < 60 * 60) a = 128;
            }
            px[o] = r;
            px[o + 1] = g;
            px[o + 2] = b;
            px[o + 3] = a;
        }
        return px;
    }

    /// <summary>時間依存のエフェクトは N=40・k=7・seed=12345 のフレーム、そのほかは seed=12345 の静止画で描く。</summary>
    public static FrameContext Frame(EffectDef def) => def.TimeDependent ? new FrameContext(12345, 7, 40) : new FrameContext(12345, 0, 1);

    /// <summary>
    /// 依頼(amount=1、salt=1)。values の cycles・phase・wave が宣言の params に無ければ、時間の共通パラメータとして
    /// EffectInvocation の Cycles・Phase・Wave に入れる(EffectSchema.AllParams が補うもの)。
    /// </summary>
    public static EffectInvocation Invocation(EffectDef def, IReadOnlyDictionary<string, ParamValue>? values = null)
    {
        var own = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
        int cycles = 1;
        double phase = 0;
        WaveKind? wave = null;
        foreach (var (key, value) in values ?? new Dictionary<string, ParamValue>())
        {
            if (def.FindParam(key) is not null) own[key] = value;
            else if (key == "cycles") cycles = MathX.RoundHalfUpToInt(((NumberValue)value).Value);
            else if (key == "phase") phase = ((NumberValue)value).Value;
            else if (key == "wave") wave = Enum.Parse<WaveKind>(((StringValue)value).Value, ignoreCase: true);
            else throw new ArgumentException($"{def.Id}: {key} は無いパラメータ");
        }
        return new EffectInvocation(def) { Values = own, Amount = 1, Salt = 1, Cycles = cycles, Phase = phase, Wave = wave };
    }

    /// <summary>aux が text のエフェクトに渡す補助の入力(模様の画像)。それ以外は null。</summary>
    public static GpuTexture? AuxFor(RenderContext ctx, EffectDef def) =>
        def.Aux == AuxKind.Text ? ctx.IngestSrgb8(Images[1].Pixels, Size, Size, Size * 4) : null;

    /// <summary>(2) の組: 数値の型(float・int・angle)のパラメータを 1 つずつ最小と最大に(ほかは既定)。rect は 4 辺をまとめて最小と最大に。</summary>
    public static IEnumerable<(string Label, IReadOnlyDictionary<string, ParamValue> Values)> NumericExtremes(EffectDef def)
    {
        foreach (var p in EffectSchema.AllParams(def))
        {
            if (p.Min is not double min || p.Max is not double max) continue;
            switch (p.Type)
            {
                case ParamType.Float or ParamType.Int or ParamType.Angle:
                    yield return ($"{p.Key}=最小 {min}", One(p.Key, new NumberValue(min)));
                    yield return ($"{p.Key}=最大 {max}", One(p.Key, new NumberValue(max)));
                    break;
                case ParamType.Rect:
                    yield return ($"{p.Key}=最小 {min}", One(p.Key, new RectValue(min, min, min, min)));
                    yield return ($"{p.Key}=最大 {max}", One(p.Key, new RectValue(max, max, max, max)));
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<string, ParamValue> One(string key, ParamValue v) =>
        new Dictionary<string, ParamValue>(StringComparer.Ordinal) { [key] = v };

    /// <summary>
    /// (6) の組: 文書の JSON に書く範囲外の値と、直ったあとに期待する値。数値は範囲の幅だけ外(と 1e300)、point は 0〜1 の外、
    /// rect は 4 辺とも外、enum は options に無い値。bool・color・text・font は範囲を持たないので組を作らない。
    /// </summary>
    public static IEnumerable<(string Json, ParamValue Expected)> OutOfRangeCases(ParamDef p)
    {
        switch (p.Type)
        {
            case ParamType.Float or ParamType.Int or ParamType.Angle when p.Min is double min && p.Max is double max:
                var span = max - min;
                yield return (Num(min - span - 1), new NumberValue(min));
                yield return (Num(max + span + 1), new NumberValue(max));
                yield return ("-1e300", new NumberValue(min));
                yield return ("1e300", new NumberValue(max));
                yield return ("1e400", new NumberValue(max));
                break;
            case ParamType.Point:
                yield return ("[-0.5, 1.5]", new PointValue(0, 1));
                break;
            case ParamType.Rect when p.Min is double rmin && p.Max is double rmax:
                var lo = Num(rmin - 1);
                var hi = Num(rmax + 1);
                yield return ("{\"l\": " + lo + ", \"t\": " + hi + ", \"r\": " + lo + ", \"b\": " + hi + "}", new RectValue(rmin, rmax, rmin, rmax));
                break;
            case ParamType.Enum:
                yield return ("\"zz_not_an_option\"", p.Default);
                break;
        }
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>レイヤー 1 枚の文書に、エフェクトの行を 1 つだけ置いた JSON(key の値に json をそのまま書く)。</summary>
    public static string DocWith(string id, string key, string json)
    {
        var sha = new string('a', 64);
        var sb = new StringBuilder();
        sb.Append("{\"schema\": 1, \"seed\": 5, \"saltCounter\": 1, ");
        sb.Append("\"assets\": [{\"sha256\": \"").Append(sha).Append("\", \"ext\": \"png\", \"bytes\": 1000}], ");
        sb.Append("\"layers\": [{\"name\": \"a\", \"kind\": \"image\", \"asset\": \"").Append(sha).Append("\", ");
        sb.Append("\"stack\": [{\"id\": \"").Append(id).Append("\", \"salt\": 1, \"params\": {\"").Append(key).Append("\": ").Append(json);
        sb.Append("}}]}]}");
        return sb.ToString();
    }

    /// <summary>(1) の判定: NaN と無限大が無く、α が 0〜1、RGB が 0〜64。</summary>
    public static void FiniteAndInRange(float[] f, string what)
    {
        for (var i = 0; i < f.Length; i++)
        {
            var v = f[i];
            if (!float.IsFinite(v)) throw new CheckFailedException($"{what}: 画素 {i / 4} のチャンネル {i % 4} が {v}");
            var hi = i % 4 == 3 ? 1f : 64f;
            if (v < 0f || v > hi) throw new CheckFailedException($"{what}: 画素 {i / 4} のチャンネル {i % 4} が {v}(0〜{hi} の外)");
        }
    }

    /// <summary>共通の後処理の前の出力の判定: NaN と無限大が無いこと(範囲は後処理が切る約束なので見ない。§9.7 の規則 4 の 4)。</summary>
    public static void Finite(float[] f, string what)
    {
        for (var i = 0; i < f.Length; i++)
        {
            if (!float.IsFinite(f[i])) throw new CheckFailedException($"{what}: 画素 {i / 4} のチャンネル {i % 4} が {f[i]}");
        }
    }

    public static void SameBits(float[] a, float[] b, string what)
    {
        Check.Equal(a.Length, b.Length, $"{what}: 長さ");
        for (var i = 0; i < a.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                throw new CheckFailedException($"{what}: 画素 {i / 4} のチャンネル {i % 4} が {a[i]} と {b[i]}");
        }
    }
}

/// <summary>1 回の処理の結果。Final は後処理のあと、Raw は後処理の前の RGBA16F。Ms は処理と読み戻しの時間。</summary>
file sealed record Ac15Result(float[] Final, float[] Raw, double Ms);

/// <summary>
/// 描画の専用スレッドの上で、エフェクトを 1 回掛けて AC-15 の (1)(2)(7) の判定をする道具。
/// 後処理の前の出力を読むため、EffectRunner.Run の、後処理(Finish)を掛ける前までの手順を写して持つ。
/// 写しが本物とずれていないことは、写しの出力に EffectRunner.Finish を掛けた結果が RenderContext.RunEffect の結果とビットで一致することで毎回確かめる。
/// </summary>
file sealed class Ac15Probe : IDisposable
{
    private readonly RenderContext _ctx;
    private readonly ShaderPassDrawer _drawer;
    private readonly EffectRunner _runner;
    private readonly Dictionary<byte[], GpuTexture> _inputs = new(ReferenceEqualityComparer.Instance);

    public Ac15Probe(RenderContext ctx)
    {
        _ctx = ctx;
        _drawer = new ShaderPassDrawer(ctx.Gpu);
        _runner = new EffectRunner(ctx.Gpu, _drawer);
    }

    private GpuTexture Input(byte[] img)
    {
        if (!_inputs.TryGetValue(img, out var t))
        {
            t = _ctx.IngestSrgb8(img, Ac15Kit.Size, Ac15Kit.Size, Ac15Kit.Size * 4);
            _inputs[img] = t;
        }
        return t;
    }

    public Ac15Result Run(EffectDef def, byte[] img, EffectInvocation inv, string what)
    {
        var input = Input(img);
        using var aux = Ac15Kit.AuxFor(_ctx, def);
        inv = inv with { Aux = aux };
        var frame = Ac15Kit.Frame(def);

        var sw = Stopwatch.StartNew();
        using var output = _ctx.RunEffect(inv, input, frame);
        var final = _ctx.ReadRgba16F(output); // 読み戻しで GPU の処理の終わりを待つ
        sw.Stop();

        if (def.SizeRule == SizeRule.Same)
        {
            Check.Equal(input.Width, output.Width, $"{what}: (7) 出力の幅");
            Check.Equal(input.Height, output.Height, $"{what}: (7) 出力の高さ");
        }
        Ac15Kit.FiniteAndInRange(final, $"{what}: (1) 後処理のあとの出力");

        var owned = new List<GpuTexture>();
        try
        {
            var raw = RunWithoutFinish(inv, input, frame, owned);
            var rawValues = _ctx.ReadRgba16F(raw);
            Ac15Kit.Finite(rawValues, $"{what}: (1) 後処理の前のパスの出力");
            using var again = _runner.Finish(def.AmountMode, inv.Amount, input, raw);
            Ac15Kit.SameBits(final, _ctx.ReadRgba16F(again), $"{what}: 検査の写しが EffectRunner.Run と一致しない");
            return new Ac15Result(final, rawValues, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            foreach (var t in owned) t.Dispose();
        }
    }

    /// <summary>EffectRunner.Run の、Finish の前までの手順の写し(パスの順・when・inputs・scale・repeat・bind)。</summary>
    private GpuTexture RunWithoutFinish(EffectInvocation inv, GpuTexture input, FrameContext frame, List<GpuTexture> owned)
    {
        var def = inv.Def;
        var values = EffectConstants.ResolveValues(inv);
        var outputs = new Dictionary<string, GpuTexture>(StringComparer.Ordinal);
        var last = -1;
        foreach (var index in EffectRunner.ExecutionOrder(def.Passes))
        {
            var pass = def.Passes[index];
            if (!WhenHolds(pass.When, values)) continue;
            var inputs = new GpuTexture?[pass.Inputs.Count];
            for (var i = 0; i < inputs.Length; i++)
            {
                var spec = pass.Inputs[i];
                inputs[i] = spec switch
                {
                    "src" or "orig" => input,
                    "aux" => inv.Aux ?? throw new InvalidOperationException($"{def.Id}: aux が無い"),
                    _ => outputs[spec["pass:".Length..]],
                };
            }
            var width = EffectRunner.ScaledSize(input.Width, pass.Scale);
            var height = EffectRunner.ScaledSize(input.Height, pass.Scale);
            var count = pass.Repeat.ParamKey is null
                ? pass.Repeat.Count
                : Math.Clamp(MathX.RoundHalfUpToInt(((NumberValue)values[pass.Repeat.ParamKey]).Value), 0, 64);
            GpuTexture? result = inputs.Length > 0 ? inputs[0] : input;
            for (var r = 0; r < count; r++)
            {
                if (r > 0 && inputs.Length > 0) inputs[0] = result;
                var first = inputs.Length > 0 ? inputs[0]! : input;
                var constants = EffectConstants.Pack(inv, pass, values, first.Width, first.Height, frame);
                var target = _ctx.CreateWorking(width, height);
                owned.Add(target);
                _drawer.Draw(EffectRunner.PixelShaderName(pass.Shader), target, inputs, constants);
                result = target;
            }
            outputs[pass.Id] = result!;
            last = index;
        }
        return last >= 0 ? outputs[def.Passes[last].Id] : input;
    }

    private static bool WhenHolds(ParamCondition? when, IReadOnlyDictionary<string, ParamValue> values)
    {
        if (when is null) return true;
        var s = values[when.Key] switch
        {
            StringValue sv => sv.Value,
            BoolValue bv => bv.Value ? "true" : "false",
            var other => throw new InvalidOperationException($"when のキー {when.Key} の値 {other} は enum でない"),
        };
        return when.Values.Contains(s, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        foreach (var t in _inputs.Values) t.Dispose();
        _inputs.Clear();
        _drawer.Dispose();
    }
}
