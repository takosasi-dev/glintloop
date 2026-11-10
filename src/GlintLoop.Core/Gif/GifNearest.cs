// パレットの色への対応(仕様書 §9.8 の 4)。パレットの 8 ビット色を Oklab にし、二乗ユークリッド距離が最小の番号を選ぶ。
// 同じ距離なら番号の小さい方。探索は L で並べて近い順に調べ、ΔL² が今の最小を超えたら打ち切る(結果は全探索と同じ)。
// 透明インデックスは探索の対象に含めない。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Gif;

internal sealed class GifNearest
{
    private readonly Lab[] _lab;      // 番号の順
    private readonly int[] _byL;      // L の昇順(同じなら番号の昇順)に並べた番号
    private readonly double[] _sortedL;

    internal GifNearest(ReadOnlySpan<GifColor> colors)
    {
        if (colors.Length == 0) throw new ArgumentException("パレットの色が無い", nameof(colors));
        _lab = new Lab[colors.Length];
        for (var i = 0; i < colors.Length; i++) _lab[i] = GifPixel.ToLab(colors[i]);
        _byL = new int[colors.Length];
        for (var i = 0; i < _byL.Length; i++) _byL[i] = i;
        var lab = _lab;
        Array.Sort(_byL, (x, y) =>
        {
            var c = lab[x].L.CompareTo(lab[y].L);
            return c != 0 ? c : x.CompareTo(y);
        });
        _sortedL = new double[_byL.Length];
        for (var i = 0; i < _byL.Length; i++) _sortedL[i] = _lab[_byL[i]].L;
    }

    internal int Count => _lab.Length;

    /// <summary>番号 i の色の Oklab(8 ビットの色から変換した値)。</summary>
    internal Lab this[int i] => _lab[i];

    /// <summary>二乗距離。項の順(L,a,b)を固定する。</summary>
    internal static double Dist2(in Lab p, in Lab c)
    {
        var dl = p.L - c.L;
        var da = p.A - c.A;
        var db = p.B - c.B;
        return dl * dl + da * da + db * db;
    }

    /// <summary>p に最も近い番号(同じ距離なら小さい番号)。</summary>
    internal int Find(in Lab p) => Find(p, -1);

    /// <summary>番号 exclude を除いて p に最も近い番号。候補が無ければ −1。</summary>
    internal int Find(in Lab p, int exclude)
    {
        var n = _byL.Length;
        // p.L 以上になる最初の位置
        int lo = 0, hi = n;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_sortedL[mid] < p.L) lo = mid + 1;
            else hi = mid;
        }
        var up = lo;          // 上向きに調べる次の位置
        var down = lo - 1;    // 下向きに調べる次の位置
        var best = -1;
        var bestD = double.PositiveInfinity;
        while (up < n || down >= 0)
        {
            // ΔL の小さい方から調べる
            bool takeUp;
            if (up >= n) takeUp = false;
            else if (down < 0) takeUp = true;
            else takeUp = _sortedL[up] - p.L <= p.L - _sortedL[down];

            var pos = takeUp ? up : down;
            var dl = _sortedL[pos] - p.L;
            if (best >= 0 && dl * dl > bestD)
            {
                // この向きの残りは ΔL² だけで今の最小を超える
                if (takeUp) up = n;
                else down = -1;
                continue;
            }
            if (takeUp) up++;
            else down--;

            var idx = _byL[pos];
            if (idx == exclude) continue;
            var d = Dist2(p, _lab[idx]);
            if (best < 0 || d < bestD || (d == bestD && idx < best))
            {
                best = idx;
                bestD = d;
            }
        }
        return best;
    }
}
