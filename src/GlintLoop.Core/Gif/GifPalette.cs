// 全フレームで共有する 1 つのパレット(仕様書 §9.8 の 3、D-21・D-22)。Oklab の集計から分散最小化の分割(Wu 法)で色を作る。
// 箱の選び方・切り方・同点の規則・番号の振り方は §9.8 の 3 の 2 のとおり。色は箱の平均 (L,a,b) の昇順に並べる。
// 丸める前の各箱の平均は BoxMeans に残す(AC-5 の (2) の検査がこれを使う)。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Gif;

internal sealed class GifPalette
{
    private GifPalette(GifColor[] colors, Lab[] boxMeans)
    {
        Colors = colors;
        BoxMeans = boxMeans;
    }

    /// <summary>インデックスの順の色(nc' 個)。</summary>
    internal GifColor[] Colors { get; }

    /// <summary>Colors[i] を作った箱(またはセル)の平均 (L,a,b)。8 ビットに丸める前の値。</summary>
    internal Lab[] BoxMeans { get; }

    internal int Count => Colors.Length;

    /// <summary>集計から最大 nc 色を作る。不透明な画素が無ければ黒 1 色。</summary>
    internal static GifPalette Build(GifHistogram h, int nc)
    {
        if (nc < 1) throw new ArgumentOutOfRangeException(nameof(nc));
        var occupied = 0;
        for (var c = 0; c < GifHistogram.CellCount; c++)
        {
            if (h.Count[c] > 0) occupied++;
        }
        if (occupied == 0) return new GifPalette([new GifColor(0, 0, 0)], [new Lab(0, 0, 0)]);

        // 規則 4: セルの種類が nc 未満なら、各セルを 1 色にする(セルの番号の順に並べてから並べ替える)。
        var means = occupied < nc ? CellMeans(h) : WuMeans(h, nc);
        return FromMeans(means);
    }

    /// <summary>平均の列から、昇順(L、同じなら a、b。それでも同じなら元の番号)に並べた色を作る。</summary>
    private static GifPalette FromMeans(List<Lab> means)
    {
        var order = new int[means.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            var p = means[x];
            var q = means[y];
            var c = p.L.CompareTo(q.L);
            if (c != 0) return c;
            c = p.A.CompareTo(q.A);
            if (c != 0) return c;
            c = p.B.CompareTo(q.B);
            return c != 0 ? c : x.CompareTo(y);
        });
        var colors = new GifColor[order.Length];
        var sorted = new Lab[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            sorted[i] = means[order[i]];
            colors[i] = ToColor(sorted[i]);
        }
        return new GifPalette(colors, sorted);
    }

    /// <summary>平均の (L,a,b) を fromOk で線形 RGB にし、0〜1 に切って enc し、8 ビットに round する。</summary>
    internal static GifColor ToColor(Lab mean)
    {
        var rgb = Oklab.FromOk(mean);
        return new GifColor(To8(rgb.R), To8(rgb.G), To8(rgb.B));
    }

    private static byte To8(double linear) => (byte)MathX.RoundHalfUpToInt(Srgb.Enc(MathX.Sat(linear)) * 255.0);

    private static List<Lab> CellMeans(GifHistogram h)
    {
        var list = new List<Lab>();
        for (var c = 0; c < GifHistogram.CellCount; c++)
        {
            var w = h.Count[c];
            if (w > 0) list.Add(new Lab(h.SumL[c] / w, h.SumA[c] / w, h.SumB[c] / w));
        }
        return list;
    }

    // ---- 分散最小化の分割 ----

    private const int S = GifHistogram.Bins + 1; // 累積の各軸の大きさ(0 は空の番兵)

    private static int Idx(int l, int a, int b) => ((l * S) + a) * S + b;

    /// <summary>累積の配列(各軸 65)。W は個数、Occ は w&gt;0 のセルの数、L・A・B・Q は座標の和と二乗の和。</summary>
    private sealed class Moments
    {
        internal readonly long[] W = new long[S * S * S];
        internal readonly long[] Occ = new long[S * S * S];
        internal readonly double[] L = new double[S * S * S];
        internal readonly double[] A = new double[S * S * S];
        internal readonly double[] B = new double[S * S * S];
        internal readonly double[] Q = new double[S * S * S];
    }

    /// <summary>箱 (l0,l1]×(a0,a1]×(b0,b1](累積の番号で、下は含まず上は含む)。</summary>
    private readonly record struct Box(int L0, int L1, int A0, int A1, int B0, int B1)
    {
        internal int Lo(int axis) => axis switch { 0 => L0, 1 => A0, _ => B0 };

        internal int Hi(int axis) => axis switch { 0 => L1, 1 => A1, _ => B1 };

        internal Box WithHi(int axis, int v) => axis switch
        {
            0 => this with { L1 = v },
            1 => this with { A1 = v },
            _ => this with { B1 = v },
        };

        internal Box WithLo(int axis, int v) => axis switch
        {
            0 => this with { L0 = v },
            1 => this with { A0 = v },
            _ => this with { B0 = v },
        };
    }

    private readonly record struct Sum(long W, long Occ, double L, double A, double B, double Q)
    {
        /// <summary>|Σ座標|²÷w。</summary>
        internal double Strength => (L * L + A * A + B * B) / W;

        /// <summary>Σ(L²+a²+b²)−|Σ座標|²÷w。</summary>
        internal double Variance => Q - Strength;
    }

    private static Moments BuildMoments(GifHistogram h)
    {
        var m = new Moments();
        for (var l = 0; l < GifHistogram.Bins; l++)
        {
            for (var a = 0; a < GifHistogram.Bins; a++)
            {
                for (var b = 0; b < GifHistogram.Bins; b++)
                {
                    var c = ((l * GifHistogram.Bins) + a) * GifHistogram.Bins + b;
                    var w = h.Count[c];
                    if (w == 0) continue;
                    var i = Idx(l + 1, a + 1, b + 1);
                    m.W[i] = w;
                    m.Occ[i] = 1;
                    m.L[i] = h.SumL[c];
                    m.A[i] = h.SumA[c];
                    m.B[i] = h.SumB[c];
                    m.Q[i] = h.SumSq[c];
                }
            }
        }
        // 累積を b 軸、a 軸、L 軸の順に作る(順序は固定)。
        foreach (var stride in new[] { 1, S, S * S })
        {
            for (var l = 1; l < S; l++)
            {
                for (var a = 1; a < S; a++)
                {
                    for (var b = 1; b < S; b++)
                    {
                        var i = Idx(l, a, b);
                        var j = i - stride;
                        m.W[i] += m.W[j];
                        m.Occ[i] += m.Occ[j];
                        m.L[i] += m.L[j];
                        m.A[i] += m.A[j];
                        m.B[i] += m.B[j];
                        m.Q[i] += m.Q[j];
                    }
                }
            }
        }
        return m;
    }

    private static long Vol(long[] t, in Box x) =>
        t[Idx(x.L1, x.A1, x.B1)] - t[Idx(x.L1, x.A1, x.B0)] - t[Idx(x.L1, x.A0, x.B1)] + t[Idx(x.L1, x.A0, x.B0)]
        - t[Idx(x.L0, x.A1, x.B1)] + t[Idx(x.L0, x.A1, x.B0)] + t[Idx(x.L0, x.A0, x.B1)] - t[Idx(x.L0, x.A0, x.B0)];

    private static double Vol(double[] t, in Box x) =>
        t[Idx(x.L1, x.A1, x.B1)] - t[Idx(x.L1, x.A1, x.B0)] - t[Idx(x.L1, x.A0, x.B1)] + t[Idx(x.L1, x.A0, x.B0)]
        - t[Idx(x.L0, x.A1, x.B1)] + t[Idx(x.L0, x.A1, x.B0)] + t[Idx(x.L0, x.A0, x.B1)] - t[Idx(x.L0, x.A0, x.B0)];

    private static Sum SumOf(Moments m, in Box x) =>
        new(Vol(m.W, x), Vol(m.Occ, x), Vol(m.L, x), Vol(m.A, x), Vol(m.B, x), Vol(m.Q, x));

    private static List<Lab> WuMeans(GifHistogram h, int nc)
    {
        var m = BuildMoments(h);
        var boxes = new List<Box> { new(0, GifHistogram.Bins, 0, GifHistogram.Bins, 0, GifHistogram.Bins) };
        var sums = new List<Sum> { SumOf(m, boxes[0]) };

        while (boxes.Count < nc)
        {
            // (a) w>0 のセルを 2 つ以上含む箱のうち、分散が最大の箱(同点なら番号の小さい方)。
            var pick = -1;
            var bestVar = double.NegativeInfinity;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (sums[i].Occ < 2) continue;
                var v = sums[i].Variance;
                if (pick < 0 || v > bestVar)
                {
                    pick = i;
                    bestVar = v;
                }
            }
            if (pick < 0) break;

            // (b) 両側に w>0 のセルが残る切り方のうち、両側の |Σ座標|²÷w の和が最大の位置(同点は軸 L,a,b の順、位置の小さい方)。
            var box = boxes[pick];
            var bestAxis = -1;
            var bestCut = 0;
            var bestScore = double.NegativeInfinity;
            Sum bestLow = default, bestHigh = default;
            for (var axis = 0; axis < 3; axis++)
            {
                for (var cut = box.Lo(axis) + 1; cut < box.Hi(axis); cut++)
                {
                    var low = SumOf(m, box.WithHi(axis, cut));
                    if (low.W == 0) continue;
                    var high = SumOf(m, box.WithLo(axis, cut));
                    if (high.W == 0) continue;
                    var score = low.Strength + high.Strength;
                    if (bestAxis < 0 || score > bestScore)
                    {
                        bestAxis = axis;
                        bestCut = cut;
                        bestScore = score;
                        bestLow = low;
                        bestHigh = high;
                    }
                }
            }
            if (bestAxis < 0) throw new InvalidOperationException("w>0 のセルが 2 つ以上ある箱を切れなかった");

            // 値が小さい側が元の番号を引き継ぎ、大きい側に新しい番号(割る前の箱の数)を振る。
            boxes[pick] = box.WithHi(bestAxis, bestCut);
            sums[pick] = bestLow;
            boxes.Add(box.WithLo(bestAxis, bestCut));
            sums.Add(bestHigh);
        }

        var means = new List<Lab>(boxes.Count);
        foreach (var s in sums) means.Add(new Lab(s.L / s.W, s.A / s.W, s.B / s.W));
        return means;
    }
}
