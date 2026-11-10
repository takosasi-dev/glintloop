// パレットの Wu 法(仕様書 §9.8 の 3 の 1・2・4)を、累積の配列を使わずに書いたテスト側の別実装(AC-5 の (2) の照合用)。
// 集計は規則 3 の 1 の式でセルに分け、箱の和はセルを直接足し上げる。切り方・箱の選び方・同点の規則・番号は規則 3 の 2 のとおり。
// 同点が実際に起きたかを数えて返す(同点の検査が、規則を本当に通ったかを確かめるため)。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Tests.Gif;

internal static class WuReference
{
    /// <summary>規則 3 の 1 の区間の番号 (iL, ia, ib)。</summary>
    public static (int L, int A, int B) BinOf(Lab p)
    {
        var iL = Math.Min(63, (int)Math.Floor(p.L * 64));
        if (iL < 0) iL = 0;
        var ia = Math.Clamp((int)Math.Floor((p.A + 0.24) / 0.52 * 64), 0, 63);
        var ib = Math.Clamp((int)Math.Floor((p.B + 0.32) / 0.52 * 64), 0, 63);
        return (iL, ia, ib);
    }

    /// <summary>1 つのセルの集計。座標は区間の番号、和は実際の座標の和。</summary>
    public sealed class Cell
    {
        public int[] Pos = new int[3];
        public long W;
        public double L, A, B, Q;
    }

    /// <summary>画素の列をセルに分けて集計する(画素の順に足す)。セルは番号 (iL×64+ia)×64+ib の昇順。</summary>
    public static List<Cell> Histogram(IEnumerable<Lab> pixels)
    {
        var map = new SortedDictionary<int, Cell>();
        foreach (var p in pixels)
        {
            var (l, a, b) = BinOf(p);
            var key = (l * 64 + a) * 64 + b;
            if (!map.TryGetValue(key, out var c))
            {
                c = new Cell { Pos = [l, a, b] };
                map.Add(key, c);
            }
            c.W++;
            c.L += p.L;
            c.A += p.A;
            c.B += p.B;
            c.Q += p.L * p.L + p.A * p.A + p.B * p.B;
        }
        return map.Values.ToList();
    }

    private readonly record struct Sum(long W, int Occ, double L, double A, double B, double Q)
    {
        public double Strength => (L * L + A * A + B * B) / W;

        public double Variance => Q - Strength;
    }

    private static Sum SumOf(IEnumerable<Cell> cells)
    {
        long w = 0;
        var occ = 0;
        double l = 0, a = 0, b = 0, q = 0;
        foreach (var c in cells)
        {
            w += c.W;
            occ++;
            l += c.L;
            a += c.A;
            b += c.B;
            q += c.Q;
        }
        return new Sum(w, occ, l, a, b, q);
    }

    /// <summary>箱。各軸で区間の番号 Lo 以上 Hi 未満のセルを含む。</summary>
    private sealed class Box
    {
        public int[] Lo = [0, 0, 0];
        public int[] Hi = [64, 64, 64];
        public List<Cell> Cells = [];
    }

    /// <summary>照合の結果。Means は昇順に並べた箱の平均、*Ties は同点が起きた回数。</summary>
    public sealed record Result(List<Lab> Means, int PickTies, int AxisTies, int PositionTies);

    /// <summary>セルの集計から最大 nc 色の平均を作り、(L, a, b、それでも同じなら元の番号)の昇順に並べる。</summary>
    public static Result Build(List<Cell> cells, int nc)
    {
        if (cells.Count == 0) return new Result([new Lab(0, 0, 0)], 0, 0, 0);
        List<Lab> means;
        int pickTies = 0, axisTies = 0, positionTies = 0;
        if (cells.Count < nc)
        {
            // 規則 4: 各セルを 1 色にする
            means = cells.Select(c => new Lab(c.L / c.W, c.A / c.W, c.B / c.W)).ToList();
        }
        else
        {
            var boxes = new List<Box> { new() { Cells = cells.ToList() } };
            while (boxes.Count < nc)
            {
                // (a) w>0 のセルを 2 つ以上含む箱のうち、分散が最大の箱。同点なら番号の小さい方。
                var pick = -1;
                var best = 0.0;
                for (var i = 0; i < boxes.Count; i++)
                {
                    var s = SumOf(boxes[i].Cells);
                    if (s.Occ < 2) continue;
                    var v = s.Variance;
                    if (pick < 0 || v > best)
                    {
                        pick = i;
                        best = v;
                    }
                    else if (v == best)
                    {
                        pickTies++;
                    }
                }
                if (pick < 0) break;

                // (b) 軸 L・a・b、位置の小さい順に調べ、両側に w>0 のセルが残る切り方のうち、両側の |Σ|²÷w の和が最大のもの。
                var box = boxes[pick];
                int bestAxis = -1, bestCut = 0;
                var bestScore = 0.0;
                for (var axis = 0; axis < 3; axis++)
                {
                    for (var cut = box.Lo[axis] + 1; cut < box.Hi[axis]; cut++)
                    {
                        var low = SumOf(box.Cells.Where(c => c.Pos[axis] < cut));
                        var high = SumOf(box.Cells.Where(c => c.Pos[axis] >= cut));
                        if (low.W == 0 || high.W == 0) continue;
                        var score = low.Strength + high.Strength;
                        if (bestAxis < 0 || score > bestScore)
                        {
                            bestAxis = axis;
                            bestCut = cut;
                            bestScore = score;
                        }
                        else if (score == bestScore && !SamePartition(box, bestAxis, bestCut, axis, cut))
                        {
                            // 両側のセルの分け方が違う同点だけを数える(空の区間をはさむ位置の違いは数えない)
                            if (axis == bestAxis) positionTies++;
                            else axisTies++;
                        }
                    }
                }
                if (bestAxis < 0) throw new InvalidOperationException("セルが 2 つ以上ある箱を切れなかった");

                // 値の小さい側が元の番号、大きい側が新しい番号(割る前の箱の数)
                var lowBox = new Box { Lo = (int[])box.Lo.Clone(), Hi = (int[])box.Hi.Clone() };
                lowBox.Hi[bestAxis] = bestCut;
                lowBox.Cells = box.Cells.Where(c => c.Pos[bestAxis] < bestCut).ToList();
                var highBox = new Box { Lo = (int[])box.Lo.Clone(), Hi = (int[])box.Hi.Clone() };
                highBox.Lo[bestAxis] = bestCut;
                highBox.Cells = box.Cells.Where(c => c.Pos[bestAxis] >= bestCut).ToList();
                boxes[pick] = lowBox;
                boxes.Add(highBox);
            }
            means = boxes.Select(b =>
            {
                var s = SumOf(b.Cells);
                return new Lab(s.L / s.W, s.A / s.W, s.B / s.W);
            }).ToList();
        }

        var order = Enumerable.Range(0, means.Count).ToList();
        order.Sort((x, y) =>
        {
            var c = means[x].L.CompareTo(means[y].L);
            if (c != 0) return c;
            c = means[x].A.CompareTo(means[y].A);
            if (c != 0) return c;
            c = means[x].B.CompareTo(means[y].B);
            return c != 0 ? c : x.CompareTo(y);
        });
        return new Result(order.Select(i => means[i]).ToList(), pickTies, axisTies, positionTies);
    }

    private static bool SamePartition(Box box, int axis1, int cut1, int axis2, int cut2) =>
        box.Cells.All(c => (c.Pos[axis1] < cut1) == (c.Pos[axis2] < cut2));
}
