// モーションのテストの道具(P0-9。担当 move・light・noise・motiontab が共有する): AC-34 の 2 枚の 128×128 と AC-35 の (8) の画像、
// エフェクト 1 つをフレーム (k, N) で描く関数、継ぎ目の検査 AC-34 の (1) 閉じ方・(2) 連続・(3) 周期、組み合わせ(cycles・phase・波形)の列。
// 検査は失敗の文(合えば null)を返すので AC-34 の (5) も書ける。描画は WARP。名前空間は GlintLoop.Tests.Motions(…Tests.Motion は既存のテストの Theme.Motion とぶつかる)。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Motions;

/// <summary>フレーム k(N のうち。k=N で t=1)を描いた 8 ビット straight RGBA(幅×高さ×4 バイト)を返す関数。</summary>
internal delegate byte[] FrameRender(int k, int n);

internal static class MotionTestKit
{
    /// <summary>AC-34 の画像の辺・seed・N。</summary>
    public const int Size = 128;
    public const uint Seed = 12345;
    public const int N = 40;

    /// <summary>AC-34 の cycles と phase の組み合わせ。</summary>
    public static IReadOnlyList<int> Cycles { get; } = [1, 2, 3, 8];
    public static IReadOnlyList<double> Phases { get; } = [0, 37];
    public static IReadOnlyList<WaveKind> Waves { get; } = [WaveKind.Sine, WaveKind.Tri, WaveKind.Ease, WaveKind.Saw];

    /// <summary>AC-34 の 1 枚目: 滑らかな色の変化と図形(円・矩形・斜めの線)を描いた不透明な画像。</summary>
    public static CompositeImg SmoothOpaque() => CompositeImg.Make(Size, Size, (x, y) =>
    {
        var r = x * 2;
        var g = y * 2;
        var b = 64 + (x + y) / 2;
        var dx = x - 44;
        var dy = y - 52;
        if (dx * dx + dy * dy <= 22 * 22) return (250, 240, 40, 255);
        if (x is >= 76 and < 112 && y is >= 70 and < 100) return (20, 30, 160, 255);
        if (Math.Abs(x - y) <= 1) return (255, 255, 255, 255);
        return (Math.Min(255, r), Math.Min(255, g), Math.Min(255, b), 255);
    });

    /// <summary>AC-34 の 2 枚目: 半透明の領域(α の階調・半透明の円)と完全に透明な隅を含む画像。</summary>
    public static CompositeImg Translucent() => CompositeImg.Make(Size, Size, (x, y) =>
    {
        if (x < 24 && y < 24) return (0, 0, 0, 0);
        var dx = x - 80;
        var dy = y - 40;
        if (dx * dx + dy * dy <= 26 * 26) return (255, 60, 90, 128);
        var a = Math.Clamp(40 + x + y / 2, 0, 255);
        return (Math.Min(255, 30 + y * 2), 180, Math.Min(255, x * 2), a);
    });

    /// <summary>AC-34 の 2 枚。</summary>
    public static IReadOnlyList<CompositeImg> Ac34Images() => [SmoothOpaque(), Translucent()];

    /// <summary>AC-35 の (8)(AC-35 の (9) も使う)の画像: R=(2x) mod 256、G=(2y) mod 256、B=128、x が 70〜109 かつ y が 20〜49 は白。不透明。</summary>
    public static CompositeImg Ac35Image() => CompositeImg.Make(Size, Size, (x, y) =>
        x is >= 70 and <= 109 && y is >= 20 and <= 49 ? (255, 255, 255, 255) : ((2 * x) % 256, (2 * y) % 256, 128, 255));

    /// <summary>
    /// id のエフェクトの依頼。cycles・phase・wave は依頼の欄に入れ(Values には入れない。EffectConstants が宣言に無いキーを拒むため)、
    /// values はほかのパラメータ(無いキーは既定値)。salt の既定は 1。
    /// </summary>
    public static EffectInvocation Invocation(IEffectCatalog catalog, string id, int cycles = 1, double phase = 0, WaveKind? wave = null,
        uint salt = 1, params (string Key, ParamValue Value)[] values)
    {
        var def = catalog.Find(id) ?? throw new KeyNotFoundException("カタログに無い id: " + id);
        return new EffectInvocation(def)
        {
            Values = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal),
            Cycles = cycles,
            Phase = phase,
            Wave = wave,
            Salt = salt,
        };
    }

    /// <summary>
    /// AC-34 の (1) の組み合わせ: cycles 1・2・3・8 × phase 0・37 × 波形(波形のあるエフェクトは 4 つ、無いものは宣言の既定 1 つ)。ほかのパラメータは既定値。
    /// </summary>
    public static IEnumerable<EffectInvocation> Ac34Variants(IEffectCatalog catalog, string id)
    {
        var def = catalog.Find(id) ?? throw new KeyNotFoundException("カタログに無い id: " + id);
        IReadOnlyList<WaveKind?> waves = def.NoWave ? [null] : Waves.Select(w => (WaveKind?)w).ToArray();
        foreach (var c in Cycles)
        foreach (var p in Phases)
        foreach (var w in waves)
            yield return Invocation(catalog, id, c, p, w);
    }

    /// <summary>エフェクトの列を、img を入力にしてフレーム (k, n) で描く関数(seed は既定 12345)。依頼の枠(MotionFrame)は無い: $frame は画像の全体。</summary>
    public static FrameRender Render(RenderService rs, CompositeImg img, IReadOnlyList<EffectInvocation> stack, uint seed = Seed) =>
        (k, n) => rs.RenderStraightRgba8Async(img.Px, img.W, img.H, stack, new FrameContext(seed, k, n)).GetAwaiter().GetResult();

    public static FrameRender Render(RenderService rs, CompositeImg img, EffectInvocation inv, uint seed = Seed) => Render(rs, img, [inv], seed);

    /// <summary>
    /// AC-34 の (5) の偽のエフェクト: 周期 1 の正しいエフェクト(cycles=1)を、ψ=1.5×t で動くように描く(フレーム (3k, 2n) を描く。t'=1.5×t)。
    /// 製品に試験用のシェーダーを足さずに、周期が整数でない式を作る(仮の決め)。
    /// </summary>
    public static FrameRender NonIntegerPeriod(FrameRender cycles1) => (k, n) => cycles1(3 * k, 2 * n);

    /// <summary>2 つの RGBA の各チャンネルの差の最大。</summary>
    public static int MaxDiff(byte[] a, byte[] b)
    {
        Check.Equal(a.Length, b.Length, "画素の数");
        var m = 0;
        for (var i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    /// <summary>RGBA の絶対差の平均(AC-34 の (2) の d_k)。</summary>
    public static double MeanAbsDiff(byte[] a, byte[] b)
    {
        Check.Equal(a.Length, b.Length, "画素の数");
        long s = 0;
        for (var i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return (double)s / a.Length;
    }

    /// <summary>RGB だけの絶対差の平均(AC-35 の (8))。</summary>
    public static double MeanAbsDiffRgb(byte[] a, byte[] b)
    {
        Check.Equal(a.Length, b.Length, "画素の数");
        long s = 0;
        for (var i = 0; i < a.Length; i += 4)
            s += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return (double)s / (a.Length / 4 * 3);
    }

    /// <summary>AC-34 の (1) 閉じ方: t=1(k=N)を直接指定した結果が、t=0(k=0)の結果と 8 ビットで 1 以内。合っていれば null、違えば失敗の文。</summary>
    public static string? CheckClosure(FrameRender render, int n = N)
    {
        var d = MaxDiff(render(0, n), render(n, n));
        return d <= 1 ? null : $"閉じ方: t=1 と t=0 の差の最大が {d}(1 以内であること)";
    }

    /// <summary>
    /// AC-34 の (2) 連続: d_k をフレーム k と k+1(k+1=N は 0)の RGBA の絶対差の平均として、d_(N−1) ≤ max(d_0〜d_(N−2))×1.25+1。
    /// 波形が saw の設定には使わない(呼ぶ側が外す)。
    /// </summary>
    public static string? CheckContinuity(FrameRender render, int n = N)
    {
        var frames = new byte[n][];
        for (var k = 0; k < n; k++) frames[k] = render(k, n);
        var max = 0.0;
        for (var k = 0; k < n - 1; k++) max = Math.Max(max, MeanAbsDiff(frames[k], frames[k + 1]));
        var seam = MeanAbsDiff(frames[n - 1], frames[0]);
        return seam <= max * 1.25 + 1 ? null : $"連続: 継ぎ目の差 {seam:0.###} が ほかの最大 {max:0.###}×1.25+1 を超える";
    }

    /// <summary>AC-34 の (3) 周期: cycles=2 の描画で、フレーム k と k+N÷2 が 1 以内(呼ぶ側が cycles=2 の依頼を渡す)。</summary>
    public static string? CheckPeriod(FrameRender render, int n = N)
    {
        var half = n / 2;
        for (var k = 0; k < half; k++)
        {
            var d = MaxDiff(render(k, n), render(k + half, n));
            if (d > 1) return $"周期: フレーム {k} と {k + half} の差の最大が {d}(1 以内であること)";
        }
        return null;
    }

    /// <summary>失敗の文が null であることを確かめる。</summary>
    public static void Pass(string? failure, string what)
    {
        if (failure is not null) throw new CheckFailedException(what + ": " + failure);
    }
}
