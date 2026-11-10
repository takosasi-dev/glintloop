// パレットのための集計(仕様書 §9.8 の 3 の 1)。Oklab の各軸 64 区間のセルに、個数・L,a,b の和・L²+a²+b² の和を倍精度で持つ。
// フレームごとの集計を作ってからフレームの画素を捨て、全体の集計にはフレーム番号の順に足す(足す順序を固定する)。
// 画素の色は GifPixel が決める(マット色の合成と、α<128 を透明とする規則。D-27)。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Gif;

internal sealed class GifHistogram
{
    internal const int Bins = 64;
    internal const int CellCount = Bins * Bins * Bins;

    internal readonly long[] Count = new long[CellCount];
    internal readonly double[] SumL = new double[CellCount];
    internal readonly double[] SumA = new double[CellCount];
    internal readonly double[] SumB = new double[CellCount];
    internal readonly double[] SumSq = new double[CellCount];

    // フレームごとの集計だけが持つ、個数が 0 から増えたセルの一覧(全体へ足すときと、使い回す前の片付けに使う)。
    private readonly List<int>? _touched;

    private GifHistogram(bool trackTouched)
    {
        _touched = trackTouched ? new List<int>() : null;
    }

    internal static GifHistogram CreateTotal() => new(false);

    internal static GifHistogram CreateFrame() => new(true);

    /// <summary>セルの番号 (iL×64+ia)×64+ib。iL=min(63, floor(L×64))、ia・ib は §9.8 の式で 0〜63 に切る。</summary>
    internal static int CellOf(in Lab p)
    {
        var iL = Bin(p.L * 64);
        var ia = Bin((p.A + 0.24) / 0.52 * 64);
        var ib = Bin((p.B + 0.32) / 0.52 * 64);
        return ((iL * Bins) + ia) * Bins + ib;
    }

    // floor してから 0〜63 に切る。L の下側は規則に無いが、入力が 0〜1 の色なら L≥0 なので結果は変わらない(AC-2 の (4))。
    private static int Bin(double v)
    {
        var f = Math.Floor(v);
        if (f < 0) return 0;
        if (f > Bins - 1) return Bins - 1;
        return (int)f;
    }

    internal void AddPixel(in Lab p)
    {
        var c = CellOf(p);
        if (_touched is not null && Count[c] == 0) _touched.Add(c);
        Count[c]++;
        SumL[c] += p.L;
        SumA[c] += p.A;
        SumB[c] += p.B;
        SumSq[c] += p.L * p.L + p.A * p.A + p.B * p.B;
    }

    /// <summary>1 フレームの不透明な画素を行優先の順に足す。透明(α&lt;128 でマット色なし)の画素があれば true を返す。</summary>
    internal bool AddFrame(ReadOnlySpan<byte> rgba, in GifPixel.Matte matte)
    {
        var anyTransparent = false;
        for (var o = 0; o < rgba.Length; o += 4)
        {
            if (GifPixel.IsTransparent(rgba[o + 3], matte))
            {
                anyTransparent = true;
                continue;
            }
            AddPixel(GifPixel.ToLab(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3], matte));
        }
        return anyTransparent;
    }

    /// <summary>フレームごとの集計を足す(呼ぶ側がフレーム番号の順に呼ぶ)。</summary>
    internal void AddFrameTotals(GifHistogram frame)
    {
        foreach (var c in frame._touched!)
        {
            Count[c] += frame.Count[c];
            SumL[c] += frame.SumL[c];
            SumA[c] += frame.SumA[c];
            SumB[c] += frame.SumB[c];
            SumSq[c] += frame.SumSq[c];
        }
    }

    /// <summary>フレームごとの集計を空に戻す(使い回す前に呼ぶ)。</summary>
    internal void ClearFrame()
    {
        foreach (var c in _touched!)
        {
            Count[c] = 0;
            SumL[c] = 0;
            SumA[c] = 0;
            SumB[c] = 0;
            SumSq[c] = 0;
        }
        _touched.Clear();
    }
}

/// <summary>画素の色の取り出し(D-27・§9.8 の 2)。マット色があれば線形光で合成して不透明にする。</summary>
internal static class GifPixel
{
    /// <summary>α がこの値以上の画素を不透明とする。</summary>
    internal const byte OpaqueAlpha = 128;

    /// <summary>マット色(線形 RGB)。Has が false なら合成しない。</summary>
    internal readonly record struct Matte(bool Has, double R, double G, double B)
    {
        internal static readonly Matte None = new(false, 0, 0, 0);

        internal static Matte From(GifColor? c) =>
            c is { } m ? new Matte(true, Srgb.Lin8(m.R), Srgb.Lin8(m.G), Srgb.Lin8(m.B)) : None;
    }

    internal static bool IsTransparent(byte a, in Matte matte) => !matte.Has && a < OpaqueAlpha;

    /// <summary>不透明として扱う画素の Oklab。マット色があれば C=Cs×α+Cm×(1−α)(線形光)を変換する。</summary>
    internal static Lab ToLab(byte r, byte g, byte b, byte a, in Matte matte)
    {
        if (!matte.Has) return Oklab.ToOk(Srgb.Lin8(r), Srgb.Lin8(g), Srgb.Lin8(b));
        var al = a / 255.0;
        return Oklab.ToOk(
            Srgb.Lin8(r) * al + matte.R * (1 - al),
            Srgb.Lin8(g) * al + matte.G * (1 - al),
            Srgb.Lin8(b) * al + matte.B * (1 - al));
    }

    /// <summary>8 ビットの色の Oklab(パレットの色と、マット色なしの画素)。</summary>
    internal static Lab ToLab(GifColor c) => Oklab.ToOk(Srgb.Lin8(c.R), Srgb.Lin8(c.G), Srgb.Lin8(c.B));
}
