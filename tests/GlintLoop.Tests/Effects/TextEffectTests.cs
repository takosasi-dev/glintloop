// 仕上げのテキスト(text、aux=text)のテスト(WARP だけ): EffectDef が §9.4 の表のとおりか、catalog_points の性質の表の text の行(AC-14 の (2))、
// GPU の重ね(P'=T+(1−T.a)×P)が、同じビットマップから double で計算した参照と 8 ビットで 3 以内に一致するか、塊の置き方(揃え・位置・回転・行の間隔・線)、
// 文字のビットマップを作り直す時点(値とキャンバスの大きさが変わったときだけ)、実際のビットマップを使った AC-15、フォントが無いときの描き方(AC-24 の (2))を確かめる。
using System.Diagnostics;
using GlintLoop.Codec.Text;
using GlintLoop.Core.Dither;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Effects;

public sealed class TextEffectTests
{
    private static readonly FrameContext Still = new(12345, 0, 1);
    private static readonly SkiaTextRasterizer Skia = new();

    private static RenderService Warp() => RenderService.Create(GpuAdapterRequest.Warp);

    private static EffectDef Def(RenderService rs) => rs.Effects.Find("text") ?? throw new CheckFailedException("text が読めていない");

    private static Dictionary<string, ParamValue> V(params (string Key, ParamValue Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    private static NumberValue N(double v) => new(v);
    private static StringValue S(string v) => new(v);

    /// <summary>w×h の 1 色の画像(sRGB の straight)。</summary>
    private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }
        return px;
    }

    /// <summary>グラデーションに半透明の帯を足した画像(重ねの参照との比べに使う)。</summary>
    private static byte[] Pattern(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 4;
            px[o] = (byte)(x * 255 / (w - 1));
            px[o + 1] = (byte)(y * 255 / (h - 1));
            px[o + 2] = (byte)((x + y) * 127 / (w + h));
            px[o + 3] = (byte)(x < w / 3 ? 255 : x < 2 * w / 3 ? 128 : (y * 255 / (h - 1)));
        }
        return px;
    }

    /// <summary>text のエフェクトを 1 回、実際の文字のビットマップで描く(straight の RGBA 8 ビット)。</summary>
    private static byte[] Run(RenderService rs, byte[] img, int w, int h, IReadOnlyDictionary<string, ParamValue> values, double amount = 1,
        ITextRasterizer? rasterizer = null) =>
        rs.InvokeAsync(ctx =>
        {
            var inv = new EffectInvocation(Def(rs)) { Values = values, Amount = amount, Salt = 1 };
            using var input = ctx.IngestSrgb8(img, w, h, w * 4);
            using var aux = TextAux.Build(ctx, TextAux.Plan(rasterizer ?? Skia, inv, w, h));
            using var output = ctx.RunEffect(inv with { Aux = aux }, input, Still);
            return ctx.ReadStraightRgba8(output);
        }).GetAwaiter().GetResult();

    private static TextSpec Spec(RenderService rs, IReadOnlyDictionary<string, ParamValue> values) =>
        TextSpec.From(EffectConstants.ResolveValues(new EffectInvocation(Def(rs)) { Values = values }));

    private static int Luma(byte[] px, int i) =>
        (int)Math.Round(0.2126 * px[i * 4] + 0.7152 * px[i * 4 + 1] + 0.0722 * px[i * 4 + 2]);

    /// <summary>条件に合う画素の外接矩形と重心(x0, y0, x1, y1 は含む画素の端。無ければ null)。</summary>
    private static (int X0, int Y0, int X1, int Y1, double Cx, double Cy, int Count)? Ink(byte[] px, int w, int h, Func<int, bool> hit)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, n = 0;
        double sx = 0, sy = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!hit(y * w + x)) continue;
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
            sx += x + 0.5;
            sy += y + 0.5;
            n++;
        }
        return n == 0 ? null : (x0, y0, x1, y1, sx / n, sy / n, n);
    }

    private static void AllWithin(byte[] expected, byte[] actual, int tol, string what)
    {
        Check.Equal(expected.Length, actual.Length, what + " の長さ");
        for (var i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) > tol)
                throw new CheckFailedException($"{what}: 画素 {i / 4} のチャンネル {i % 4} 期待 {expected[i]}±{tol}、実際 {actual[i]}");
        }
    }

    // ================================================================ EffectDef(§9.4 の表の text の行)

    [Test]
    public void AC13_AC14_textのEffectDefが表のとおり()
    {
        using var rs = Warp();
        var def = Def(rs);
        Check.Equal("テキスト", def.Name, "name");
        Check.Equal(EffectStage.Finish, def.Stage, "stage");
        Check.Equal("縁と文字", def.Group, "group");
        Check.Equal(AmountMode.Mix, def.AmountMode, "amountMode");
        Check.Equal(false, def.TimeDependent, "timeDependent");
        Check.Equal(SizeRule.Same, def.SizeRule, "sizeRule");
        Check.Equal(AuxKind.Text, def.Aux, "aux");
        Check.SequenceEqual(["text", "font", "size", "color", "align", "pos", "rot", "line", "stroke", "stroke_color"],
            def.Params.Select(p => p.Key).ToArray(), "パラメータの並び");
        void P(string key, ParamType type, double? min, double? max, double? step, ParamValue def0, string unit)
        {
            var p = def.FindParam(key)!;
            Check.Equal(type, p.Type, key + " の型");
            Check.Equal(min, p.Min, key + " の min");
            Check.Equal(max, p.Max, key + " の max");
            Check.Equal(step, p.Step, key + " の step");
            Check.Equal(def0, p.Default, key + " の既定値");
            Check.Equal(unit, p.Unit, key + " の単位");
            Check.Equal(false, p.Advanced, key + " の advanced");
            Check.True(p.VisibleWhen is null, key + " に visibleWhen がある");
            Check.Equal(key == "text" ? 200 : null, p.MaxLength, key + " の maxLength(FR-37 の 200 文字。text だけ)");
        }
        P("text", ParamType.Text, null, null, null, S("テキスト"), "");
        P("font", ParamType.Font, null, null, null, S("Yu Gothic UI"), "");
        P("size", ParamType.Float, 10, 500, 1, N(100), "‰");
        P("color", ParamType.Color, null, null, null, S("#FFFFFFFF"), "");
        P("align", ParamType.Enum, null, null, null, S("center"), "");
        P("pos", ParamType.Point, null, null, null, new PointValue(0.5, 0.5), "");
        P("rot", ParamType.Angle, -180, 180, 1, N(0), "deg");
        P("line", ParamType.Float, 80, 200, 1, N(120), "%");
        P("stroke", ParamType.Float, 0, 60, 0.1, N(0), "‰");
        P("stroke_color", ParamType.Color, null, null, null, S("#000000FF"), "");
        var align = def.FindParam("align")!.Options!;
        Check.SequenceEqual(["left", "center", "right"], align.Select(o => o.Value).ToArray(), "align の選択肢");
        Check.SequenceEqual(["左", "中央", "右"], align.Select(o => o.Label).ToArray(), "align の選択肢のラベル");
        var pass = def.Passes.Single();
        Check.SequenceEqual(["src", "aux"], pass.Inputs.ToArray(), "パスの入力");
        Check.Equal(1.0, pass.Scale, "パスの scale");
    }

    // ================================================================ catalog_points の性質の表の text の行(AC-14 の (2))

    [Test]
    public void AC14_2_文字列が空のとき入力と1以内で一致する()
    {
        using var rs = Warp();
        var img = Pattern(120, 90);
        var input = rs.RenderStraightRgba8Async(img, 120, 90, [], Still).GetAwaiter().GetResult();
        AllWithin(input, Run(rs, img, 120, 90, V(("text", S("")))), 1, "空の文字列");
    }

    [Test]
    public void AC14_2_黒い200四方にAを描くと中心の近くが明るく四隅が黒のまま()
    {
        using var rs = Warp();
        var img = Solid(200, 200, 0, 0, 0);
        var values = V(("text", S("A")), ("size", N(200)), ("color", S("#FFFFFFFF")), ("pos", new PointValue(0.5, 0.5)));
        var px = Run(rs, img, 200, 200, values);
        var near = false;
        for (var y = 0; y < 200 && !near; y++)
        for (var x = 0; x < 200; x++)
        {
            var dx = x + 0.5 - 100;
            var dy = y + 0.5 - 100;
            if (dx * dx + dy * dy <= 40 * 40 && Luma(px, y * 200 + x) >= 128)
            {
                near = true;
                break;
            }
        }
        Check.True(near, "中心から 40 画素以内に輝度 128 以上の画素が無い");
        foreach (var (cx, cy) in new[] { (0, 0), (180, 0), (0, 180), (180, 180) })
        {
            for (var y = cy; y < cy + 20; y++)
            for (var x = cx; x < cx + 20; x++)
            {
                var o = (y * 200 + x) * 4;
                Check.True(px[o] == 0 && px[o + 1] == 0 && px[o + 2] == 0 && px[o + 3] == 255, $"隅の画素 ({x}, {y}) が黒でない");
            }
        }
        // 同じ設定で 2 回描いた結果が完全に一致する
        Check.SequenceEqual(px, Run(rs, img, 200, 200, values), "2 回目の出力");
    }

    [Test]
    public void AC14_2_日本語と絵文字と改行を含む文字列でも例外が出ない()
    {
        using var rs = Warp();
        var img = Pattern(160, 120);
        foreach (var t in new[] { "日本語のテキスト", "絵文字\U0001F600と❤️", "一行目\n二行目\r\n三\r四", "混在 ABC あいう 😀\n\n" })
        {
            var px = Run(rs, img, 160, 120, V(("text", S(t)), ("stroke", N(10))));
            Check.Equal(160 * 120 * 4, px.Length, $"「{t}」の出力の長さ");
        }
    }

    [Test]
    public void AC14_2_posが0_25のとき白い画素の重心のxがWの0_25の5パーセント以内()
    {
        using var rs = Warp();
        var img = Solid(200, 200, 0, 0, 0);
        var px = Run(rs, img, 200, 200, V(("text", S("A")), ("size", N(200)), ("pos", new PointValue(0.25, 0.5))));
        var ink = Ink(px, 200, 200, i => Luma(px, i) >= 128) ?? throw new CheckFailedException("白い画素が無い");
        Check.Near(200 * 0.25, ink.Cx, 200 * 0.25 * 0.05, "白い画素の重心の x");
    }

    // ================================================================ 重ねの参照(double)との比べ(AC-14)

    /// <summary>取り込み(§9.7 の規則 2)と同じ: lin(c)×α と α を half に丸めた値。</summary>
    private static double[] Ingest(byte[] px)
    {
        var p = new double[px.Length];
        for (var k = 0; k < px.Length / 4; k++)
        {
            var a = px[k * 4 + 3] / 255.0;
            for (var c = 0; c < 3; c++) p[k * 4 + c] = (double)(Half)(Srgb.Lin8(px[k * 4 + c]) * a);
            p[k * 4 + 3] = (double)(Half)a;
        }
        return p;
    }

    /// <summary>§9.7 の規則 7 の量子化(画素の位置の青色雑音を含む straight の 8 ビット)。</summary>
    private static byte[] Encode8(double[] p, int w, int h)
    {
        var dst = new byte[p.Length];
        for (var j = 0; j < h; j++)
        for (var i = 0; i < w; i++)
        {
            var o = (j * w + i) * 4;
            var a = Math.Clamp(p[o + 3], 0, 1);
            if (!(a > 0.0001)) continue;
            var n = ((BlueNoise.At(i, j) + 0.5) / 4096.0 - 0.5) * 0.9;
            for (var c = 0; c < 3; c++)
            {
                var v = Math.Clamp(p[o + c], 0, 64);
                dst[o + c] = (byte)Math.Clamp(Math.Floor(255 * Srgb.Enc(MathX.Sat(v / a)) + 0.5 + n), 0, 255);
            }
            dst[o + 3] = (byte)Math.Clamp(Math.Floor(255 * a + 0.5), 0, 255);
        }
        return dst;
    }

    /// <summary>参照: ビットマップ T(straight の sRGB 8 ビットを lin と不透明度に)を P に P'=T+(1−T.a)×P で重ね、amount で mix する。</summary>
    private static byte[] Reference(byte[] img, byte[]? t8, int w, int h, double amount)
    {
        var p = Ingest(img);
        var t = t8 is null ? new double[p.Length] : Ingest(t8);
        var r = new double[p.Length];
        for (var k = 0; k < p.Length; k += 4)
        {
            for (var c = 0; c < 4; c++)
            {
                var fx = t[k + c] + (1 - t[k + 3]) * p[k + c];
                r[k + c] = p[k + c] * (1 - amount) + fx * amount;
            }
        }
        return Encode8(r, w, h);
    }

    [Test]
    public void AC14_重ねがビットマップからdoubleで計算した参照と8ビットで3以内に一致する()
    {
        // 許す幅 3 は §9.4 の passes の約束(定義の式の結果と 8 ビットで ±3 以内)。参照は同じ ITextRasterizer のビットマップを使うので、
        // 差は GPU の float の演算と RGBA16F の丸め(8 ビット換算で 0.05 以下)だけで、青色雑音の位置も同じ。実測の最大の差も出力する。
        using var rs = Warp();
        const int w = 240, h = 180;
        var img = Pattern(w, h);
        var cases = new (string Name, Dictionary<string, ParamValue> Values, double Amount)[]
        {
            ("既定", V(), 1),
            ("線と回転と右揃え", V(("text", S("GlintLoop\n文字と線")), ("stroke", N(12)), ("rot", N(-30)), ("align", S("right")), ("pos", new PointValue(0.8, 0.4))), 1),
            ("半透明の色と線", V(("text", S("Aa あ")), ("size", N(300)), ("color", S("#30C0FF80")), ("stroke", N(20)), ("stroke_color", S("#FF000060"))), 1),
            ("amount 40%", V(("text", S("mix")), ("size", N(250)), ("line", N(200)), ("align", S("left"))), 0.4),
        };
        var worst = 0;
        foreach (var (name, values, amount) in cases)
        {
            var actual = Run(rs, img, w, h, values, amount);
            var bitmap = Skia.Rasterize(Spec(rs, values), w, h);
            var expected = Reference(img, bitmap, w, h, amount);
            for (var i = 0; i < expected.Length; i++) worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
            AllWithin(expected, actual, 3, name);
        }
        Console.WriteLine($"    text の重ね: 参照との差の最大は {worst}(8 ビット)");
    }

    // ================================================================ 塊の置き方(§9.4 の text の定義)

    [Test]
    public void 揃えは塊の左端と中央と右端を基準点のxに合わせる()
    {
        using var rs = Warp();
        const int w = 400, h = 200;
        var img = Solid(w, h, 0, 0, 0);
        double Edge(string align, bool left)
        {
            var px = Run(rs, img, w, h, V(("text", S("IIII")), ("size", N(200)), ("align", S(align)), ("pos", new PointValue(0.5, 0.5))));
            var ink = Ink(px, w, h, i => Luma(px, i) >= 128) ?? throw new CheckFailedException(align + ": 白い画素が無い");
            return left ? ink.X0 : ink.X1 + 1;
        }
        var spec = Spec(rs, V(("text", S("IIII")), ("size", N(200))));
        var block = Skia.Measure(spec, w, h);
        var bearing = 200 * 200 / 1000.0 * 0.25; // px(size) の 1/4 まで(字形の左右の余白)
        var l = Edge("left", true);
        Check.True(l >= 200 - 1 && l <= 200 + bearing, $"left: 塊の左端の文字の左端 {l} が基準点 200 の右の {bearing:F1} 画素以内でない");
        var r = Edge("right", false);
        Check.True(r <= 200 + 1 && r >= 200 - bearing, $"right: 塊の右端の文字の右端 {r} が基準点 200 の左の {bearing:F1} 画素以内でない");
        var px = Run(rs, img, w, h, V(("text", S("IIII")), ("size", N(200))));
        var c = Ink(px, w, h, i => Luma(px, i) >= 128)!.Value;
        Check.Near(200, (c.X0 + c.X1 + 1) / 2.0, 2, "center: 文字の左右の中央");
        Check.Near(-block.Width / 2, block.Left, 1e-9, "center の塊の左端");
        Check.True(block.Width > 0 && block.Height > 0, "塊の大きさ");
    }

    [Test]
    public void 行の間隔は2行の距離がpx_size_かけるline割る100になる()
    {
        using var rs = Warp();
        const int w = 300, h = 300;
        var img = Solid(w, h, 0, 0, 0);
        foreach (var line in new[] { 80.0, 120.0, 200.0 })
        {
            var px = Run(rs, img, w, h, V(("text", S("A\nA")), ("size", N(100)), ("line", N(line))));
            var top = Ink(px, w, h, i => i / w < 150 && Luma(px, i) >= 128) ?? throw new CheckFailedException($"line={line}: 上の行が無い");
            var bottom = Ink(px, w, h, i => i / w >= 150 && Luma(px, i) >= 128) ?? throw new CheckFailedException($"line={line}: 下の行が無い");
            var expected = 300 * 100 / 1000.0 * line / 100; // px(size)×line÷100
            Check.Near(expected, bottom.Cy - top.Cy, 1, $"line={line}: 2 行の重心の距離");
            // 塊の高さの中央が基準点の y(2 行の重心の中点が 150 に来る。字形の上下の偏りは 2 行で同じなので打ち消し合わない分だけ px の 1/4 まで許す)
            Check.Near(150, (top.Cy + bottom.Cy) / 2, 30 * 0.25, $"line={line}: 2 行の中点");
        }
    }

    [Test]
    public void 回転は基準点まわりに時計回り()
    {
        using var rs = Warp();
        const int w = 300, h = 300;
        var img = Solid(w, h, 0, 0, 0);
        var flat = Run(rs, img, w, h, V(("text", S("MMMMMM")), ("size", N(100)), ("align", S("left"))));
        var f = Ink(flat, w, h, i => Luma(flat, i) >= 128)!.Value;
        Check.True(f.X1 - f.X0 > 2 * (f.Y1 - f.Y0), "回さないときは横に長い");
        // 90 度: 左揃えの文字は基準点から下へ伸びる(y が下向きなので時計回り)
        var down = Run(rs, img, w, h, V(("text", S("MMMMMM")), ("size", N(100)), ("align", S("left")), ("rot", N(90))));
        var d = Ink(down, w, h, i => Luma(down, i) >= 128)!.Value;
        Check.True(d.Y1 - d.Y0 > 2 * (d.X1 - d.X0), "90 度で縦に長くならない");
        Check.True(d.Y0 >= 150 - 2 && d.Cy > 150, $"90 度で基準点の下へ伸びない(上端 {d.Y0}、重心 {d.Cy:F1})");
        Check.Near(150, d.Cx, 4, "90 度の重心の x");
        // -90 度は上へ
        var up = Run(rs, img, w, h, V(("text", S("MMMMMM")), ("size", N(100)), ("align", S("left")), ("rot", N(-90))));
        var u = Ink(up, w, h, i => Luma(up, i) >= 128)!.Value;
        Check.True(u.Y1 <= 150 + 2 && u.Cy < 150, $"-90 度で基準点の上へ伸びない(下端 {u.Y1}、重心 {u.Cy:F1})");
    }

    [Test]
    public void 線は片側px_stroke_だけ外へ広がり内側は塗りの色()
    {
        using var rs = Warp();
        const int w = 400, h = 400;
        var img = Solid(w, h, 0, 0, 0);
        var plain = Run(rs, img, w, h, V(("text", S("I")), ("size", N(400))));
        var stroked = Run(rs, img, w, h, V(("text", S("I")), ("size", N(400)), ("stroke", N(20)), ("stroke_color", S("#FF0000FF"))));
        var a = Ink(plain, w, h, i => Luma(plain, i) >= 128)!.Value;
        var b = Ink(stroked, w, h, i => stroked[i * 4] >= 128)!.Value; // 線(赤)と塗り(白)のどちらも R が大きい
        var s = 400 * 20 / 1000.0; // px(stroke) = 8
        Check.Near(s, a.X0 - b.X0, 1.5, "左へ広がった幅");
        Check.Near(s, b.X1 - a.X1, 1.5, "右へ広がった幅");
        Check.Near(s, a.Y0 - b.Y0, 1.5, "上へ広がった幅");
        // 塗りの中央は白、塗りの縁の外側 s/2 は赤
        var cx = (a.X0 + a.X1) / 2;
        var cy = (a.Y0 + a.Y1) / 2;
        var o = (cy * w + cx) * 4;
        Check.True(stroked[o] == 255 && stroked[o + 1] == 255 && stroked[o + 2] == 255, "塗りの中央が白でない");
        var ox = a.X0 - (int)(s / 2) - 1;
        o = (cy * w + ox) * 4;
        Check.True(stroked[o] >= 250 && stroked[o + 1] <= 5 && stroked[o + 2] <= 5, $"線の画素 ({ox}, {cy}) が赤でない");
        // 角は丸め: 縦棒の上の左右の角の外側(45 度の向きで s よりわずかに外)は線にならない(角ばった線なら塗られる)
        var corner = ((a.Y0 - (int)(s * 0.85)) * w + a.X0 - (int)(s * 0.85)) * 4;
        Check.True(stroked[corner + 3] == 255 && stroked[corner] <= 64, "角の外側が線で塗られている(角が丸くない)");
    }

    [Test]
    public void 色はlinと不透明度で重なる()
    {
        using var rs = Warp();
        const int w = 200, h = 200;
        var px = Run(rs, Solid(w, h, 0, 0, 0), w, h, V(("text", S("I")), ("size", N(400)), ("color", S("#FF000080"))));
        var ink = Ink(px, w, h, i => px[i * 4] >= 64)!.Value;
        var o = (((ink.Y0 + ink.Y1) / 2) * w + (ink.X0 + ink.X1) / 2) * 4;
        // 黒の上に lin(1)×(128/255) を重ねる: 線形 0.502 → enc で 188 前後
        var expected = 255 * Srgb.Enc(128 / 255.0);
        Check.Near(expected, px[o], 2, "半透明の赤の R");
        Check.True(px[o + 1] <= 1 && px[o + 2] <= 1 && px[o + 3] == 255, "G・B・A");
    }

    // ================================================================ ビットマップを作り直す時点(§9.4 の text の定義の最後の文)

    /// <summary>描いた回数を数える受け口(中身は SkiaTextRasterizer)。</summary>
    private sealed class Counting : ITextRasterizer
    {
        public int Count;

        public byte[]? Rasterize(TextSpec spec, int width, int height)
        {
            Interlocked.Increment(ref Count);
            return Skia.Rasterize(spec, width, height);
        }

        public TextBlockLayout Measure(TextSpec spec, int width, int height) => Skia.Measure(spec, width, height);
    }

    [Test]
    public void ビットマップは文字や大きさが変わったときだけ作り直しamountや上流が変わっても作り直さない()
    {
        using var kit = new CompositeKit();
        var counting = new Counting();
        kit.Renderer.TextRasterizer = counting;
        kit.Import(CompositeImg.Solid(160, 120, 40, 80, 120));
        var layer = Target.OfLayer(0);
        var row = kit.AddEffect(layer, "text", ("text", S("abc")));
        var first = kit.Render(160);
        Check.Equal(1, counting.Count, "最初の描画");
        kit.Render(160);
        Check.Equal(1, counting.Count, "同じ文書をもう一度描いたとき");
        Check.True(kit.Session.SetEffectAmount(layer, row, 50).Changed, "amount を変えられない");
        var half = kit.Render(160);
        Check.Equal(1, counting.Count, "amount を変えたとき");
        Check.True(!first.Pixels.SequenceEqual(half.Pixels), "amount を変えても出力が変わらない");
        kit.AddEffect(layer, "exposure", ("ev", N(1)));
        kit.Render(160);
        Check.Equal(1, counting.Count, "上流のエフェクト(露出)を足したとき");
        row = kit.Doc.Layers[0].Stack.ToList().FindIndex(e => e.Id == "text"); // 足した行で番号がずれることがある
        Check.True(kit.Session.SetEffectParam(layer, row, "text", S("abcd")).Changed, "文字を変えられない");
        kit.Render(160);
        Check.Equal(2, counting.Count, "文字を変えたとき");
        kit.Render(80);
        Check.Equal(3, counting.Count, "キャンバスの大きさを変えたとき");
        foreach (var (key, value) in new (string, ParamValue)[]
                 {
                     ("font", S("Meiryo UI")), ("size", N(150)), ("color", S("#FF00FFFF")), ("align", S("left")), ("pos", new PointValue(0.3, 0.6)),
                     ("rot", N(15)), ("line", N(150)), ("stroke", N(5)), ("stroke_color", S("#00FF00FF")),
                 })
        {
            var before = counting.Count;
            Check.True(kit.Session.SetEffectParam(layer, row, key, value).Changed, key + " を変えられない");
            kit.Render(80);
            Check.Equal(before + 1, counting.Count, key + " を変えたとき");
        }
    }

    [Test]
    public void 受け口が無いときtextの行は描かれず入れると描かれる()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(120, 120, 0, 0, 0));
        var plain = kit.Render(120);
        kit.AddEffect(Target.OfLayer(0), "text", ("text", S("X")), ("size", N(500)));
        var skipped = kit.Render(120);
        Check.SequenceEqual(plain.Pixels, skipped.Pixels, "受け口が無いときの出力");
        kit.Renderer.TextRasterizer = Skia;
        var drawn = kit.Render(120);
        Check.True(drawn.Pixels.Where((b, i) => i % 4 == 0).Any(b => b >= 200), "受け口を入れても文字が描かれない");
        // 全体のスタックの text も描ける(入力は合成。大きさは合成の大きさ)
        kit.Session.RemoveEffect(Target.OfLayer(0), kit.Doc.Layers[0].Stack.Count - 1);
        kit.AddEffect(Target.All, "text", ("text", S("X")), ("size", N(500)));
        var all = kit.Render(120);
        Check.SequenceEqual(drawn.Pixels, all.Pixels, "全体のスタックに置いた text の出力(土台 1 枚ではレイヤーに置いたときと同じ)");
    }

    // ================================================================ AC-15(実際の文字のビットマップで)

    [Test(Slow = true)]
    public void AC15_実際のビットマップで最小最大と選択肢のすべてが有限で範囲内で5秒以内で2回一致する()
    {
        using var rs = Warp();
        const int size = 512;
        var def = Def(rs);
        var images = new (string Name, byte[] Px)[]
        {
            ("一様な灰", Solid(size, size, 128, 128, 128)),
            ("模様", Pattern(size, size)),
            ("半透明", Solid(size, size, 200, 100, 50, 100)),
        };
        var configs = new List<(string Label, Dictionary<string, ParamValue> Values)> { ("既定", V()) };
        foreach (var p in def.Params)
        {
            if (p.Min is double min && p.Max is double max)
            {
                configs.Add(($"{p.Key}=最小", V((p.Key, N(min)))));
                configs.Add(($"{p.Key}=最大", V((p.Key, N(max)))));
            }
            if (p.Options is { } options)
                foreach (var o in options) configs.Add(($"{p.Key}={o.Value}", V((p.Key, S(o.Value)))));
        }
        configs.Add(("最大の線と回転と行の間隔と大きさ", V(("text", S("長い\n文字列の\n行")), ("size", N(500)), ("stroke", N(60)), ("rot", N(180)), ("line", N(200)))));
        configs.Add(("位置の隅", V(("pos", new PointValue(1, 0)))));
        var slowest = 0.0;
        rs.InvokeAsync(ctx =>
        {
            foreach (var (name, img) in images)
            {
                using var input = ctx.IngestSrgb8(img, size, size, size * 4);
                var expected0 = ctx.ReadStraightRgba8(input);
                foreach (var (label, values) in configs)
                {
                    var what = $"text・{name}・{label}";
                    float[] Once(double amount, out double ms, out byte[] rgba8)
                    {
                        var sw = Stopwatch.StartNew();
                        var inv = new EffectInvocation(def) { Values = values, Amount = amount, Salt = 1 };
                        using var aux = TextAux.Build(ctx, TextAux.Plan(Skia, inv, size, size));
                        using var output = ctx.RunEffect(inv with { Aux = aux }, input, Still);
                        var f = ctx.ReadRgba16F(output);
                        sw.Stop();
                        ms = sw.Elapsed.TotalMilliseconds;
                        Check.Equal(size, output.Width, what + ": (7) 幅");
                        Check.Equal(size, output.Height, what + ": (7) 高さ");
                        rgba8 = ctx.ReadStraightRgba8(output);
                        return f;
                    }
                    var a = Once(1, out var ms, out _);
                    slowest = Math.Max(slowest, ms);
                    Check.True(ms <= 5000, $"{what}: (2) 1 回の処理が {ms:F0} ms");
                    for (var i = 0; i < a.Length; i++)
                    {
                        var v = a[i];
                        var hi = i % 4 == 3 ? 1f : 64f;
                        if (!float.IsFinite(v) || v < 0 || v > hi) throw new CheckFailedException($"{what}: (1) 画素 {i / 4} のチャンネル {i % 4} が {v}");
                    }
                    var b = Once(1, out _, out _);
                    for (var i = 0; i < a.Length; i++)
                    {
                        if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                            throw new CheckFailedException($"{what}: (4) 2 回の出力が画素 {i / 4} で違う");
                    }
                    Once(0, out _, out var zero);
                    AllWithin(expected0, zero, 1, what + ": (3) amount=0");
                }
            }
        }).GetAwaiter().GetResult();
        Console.WriteLine($"    text の AC-15: {configs.Count} 組 × 3 枚、最も遅い 1 回は {slowest:F0} ms");
    }

    // ================================================================ AC-24 の (2): フォントが無いとき

    [Test]
    public void AC24_2_この_PCに無いフォントはYuGothicUIで描かれる()
    {
        using var rs = Warp();
        const string fake = "GlintLoop Test No Such Font 架空";
        Check.True(!InstalledFonts.IsInstalled(fake), "架空のフォントがあると判定された");
        Check.True(InstalledFonts.IsInstalled(TextSpec.DefaultFont), "Yu Gothic UI がこの PC に無い");
        var img = Pattern(200, 120);
        var missing = Run(rs, img, 200, 120, V(("text", S("Abcあ")), ("font", S(fake))));
        var yu = Run(rs, img, 200, 120, V(("text", S("Abcあ")), ("font", S(TextSpec.DefaultFont))));
        Check.SequenceEqual(yu, missing, "無いフォントの出力と Yu Gothic UI の出力");
        var other = Run(rs, img, 200, 120, V(("text", S("Abcあ")), ("font", S("Meiryo UI"))));
        Check.True(!other.SequenceEqual(yu), "別のフォント(Meiryo UI)でも同じ出力になる(フォントが効いていない)");
    }

    [Test]
    public void AC24_2_無いフォントの名前は文書の値に保存した名前のまま残る()
    {
        var session = new EditorSession(BuiltInEffects.Load(new GlintLoop.Core.Diagnostics.MemoryLogSink()));
        session.NewEmptyCanvas(64, 64);
        var row = session.AddEffect(Target.All, "text").Row!.Value;
        const string fake = "GlintLoop Test No Such Font 架空";
        Check.True(session.SetEffectParam(Target.All, row, "font", S(fake)).Changed, "架空のフォント名を入れられない");
        var json = session.Json!;
        var read = DocumentJson.Read(json, session.Catalog, new GlintLoop.Core.Diagnostics.MemoryLogSink());
        Check.True(read.IsValid, "保存した文書が読めない");
        Check.Equal<ParamValue?>(S(fake), read.Document!.All.Stack[row].Get("font"), "読み込んだ font の値");
    }
}
