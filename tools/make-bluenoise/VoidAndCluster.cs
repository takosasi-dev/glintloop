// 64×64 の青色雑音の順位の表を void-and-cluster 法(Ulichney, 1993)で決定論的に作る(仕様書 §9.7 の 8)。
// σ=1.5 のガウス、周期 64 のトーラスの距離、hash32 で選んだ密度 10%(409 個)の最初の二値パターン。
// エネルギーが同じときは線形の番号(j×64+i)の小さい方を選ぶ(仕様書に無いので実装で決めた規則)。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Tools.MakeBlueNoise;

public static class VoidAndCluster
{
    public const int Size = 64;
    public const int Count = Size * Size;
    public const double Sigma = 1.5;
    public const int InitialCount = 409;
    public const uint SeedBase = 0x474C494Eu;

    /// <summary>
    /// 順位の表を作る。戻り値は行優先の 4096 個で、[j×64+i] が j 行目・i 列目の順位(0〜4095)。
    /// 呼ぶたびに新しい配列を作る(キャッシュしない)。並列化せず、浮動小数の足し引きの順序は固定。
    /// </summary>
    public static ushort[] Generate()
    {
        var kernel = BuildKernel();
        var prototype = InitialPattern();
        Relax(prototype, kernel);

        var rank = new ushort[Count];
        var ones = CountOnes(prototype);

        // 第 1 段: 最初のパターンの 1 を、最も密な所から順に外して、ones-1 から 0 までの順位を付ける。
        {
            var pattern = (bool[])prototype.Clone();
            var energy = EnergyOf(pattern, true, kernel);
            for (var r = ones - 1; r >= 0; r--)
            {
                var c = TightestCluster(pattern, energy, true);
                pattern[c] = false;
                Apply(energy, c, kernel, -1.0);
                rank[c] = (ushort)r;
            }
        }

        // 第 2 段: 最初のパターンから、最も大きな空きに 1 を足して、ones から Count/2-1 までの順位を付ける。
        var work = (bool[])prototype.Clone();
        {
            var energy = EnergyOf(work, true, kernel);
            for (var r = ones; r < Count / 2; r++)
            {
                var v = LargestVoid(work, energy);
                work[v] = true;
                Apply(energy, v, kernel, +1.0);
                rank[v] = (ushort)r;
            }
        }

        // 第 3 段: 0 が少数派になるので、0 どうしのエネルギー(毎回最初から作る)で最も密な 0 を 1 にして、
        // Count/2 から Count-1 までの順位を付ける(Ulichney の第 3 段をそのまま書いた形)。
        {
            var energy0 = EnergyOf(work, false, kernel);
            for (var r = Count / 2; r < Count; r++)
            {
                var c = TightestCluster(work, energy0, false);
                work[c] = true;
                Apply(energy0, c, kernel, -1.0);
                rank[c] = (ushort)r;
            }
        }

        return rank;
    }

    /// <summary>行優先の uint16 リトルエンディアン(8192 バイト)にする。</summary>
    public static byte[] ToBytes(ReadOnlySpan<ushort> table)
    {
        if (table.Length != Count) throw new ArgumentException($"表の長さが {Count} ではない: {table.Length}", nameof(table));
        var bytes = new byte[Count * 2];
        for (var k = 0; k < Count; k++)
        {
            bytes[2 * k] = (byte)(table[k] & 0xFF);
            bytes[2 * k + 1] = (byte)(table[k] >> 8);
        }
        return bytes;
    }

    /// <summary>
    /// ガウスの重み。kernel[dy×64+dx] は、オフセット (dx, dy)(0〜63)のトーラスの距離 d に対する exp(−d²/(2σ²))。
    /// 打ち切らずに 64×64 の全体を使う。
    /// </summary>
    internal static double[] BuildKernel()
    {
        var k = new double[Count];
        var twoSigma2 = 2.0 * Sigma * Sigma;
        for (var dy = 0; dy < Size; dy++)
        {
            var ty = Math.Min(dy, Size - dy);
            for (var dx = 0; dx < Size; dx++)
            {
                var tx = Math.Min(dx, Size - dx);
                var d2 = (double)(tx * tx + ty * ty);
                k[dy * Size + dx] = Math.Exp(-d2 / twoSigma2);
            }
        }
        return k;
    }

    /// <summary>
    /// 最初の二値パターン。n=0 から順に r=hash32(0x474C494E+n) mod 4096 を線形の番号として
    /// (i=r mod 64、j=r÷64)、すでに 1 の位置は飛ばし、409 個になるまで 1 を置く。
    /// </summary>
    internal static bool[] InitialPattern()
    {
        var p = new bool[Count];
        var placed = 0;
        for (uint n = 0; placed < InitialCount; n++)
        {
            var r = (int)(Hash.Hash32(unchecked(SeedBase + n)) % (uint)Count);
            if (p[r]) continue;
            p[r] = true;
            placed++;
        }
        return p;
    }

    /// <summary>
    /// 最も密な 1 を外し、最も大きな空きに置く、を繰り返す。外した位置と置く位置が同じになったら止める。
    /// 止まらないまま回り続けないように、回数の上限で例外にする。
    /// </summary>
    internal static int Relax(bool[] pattern, double[] kernel)
    {
        var energy = EnergyOf(pattern, true, kernel);
        const int MaxIterations = 1_000_000;
        for (var it = 0; it < MaxIterations; it++)
        {
            var c = TightestCluster(pattern, energy, true);
            pattern[c] = false;
            Apply(energy, c, kernel, -1.0);
            var v = LargestVoid(pattern, energy);
            if (v == c)
            {
                pattern[c] = true;
                Apply(energy, c, kernel, +1.0);
                return it;
            }
            pattern[v] = true;
            Apply(energy, v, kernel, +1.0);
        }
        throw new InvalidOperationException("void-and-cluster の初期パターンの整理が収まらなかった");
    }

    /// <summary>pattern[k]==value の画素を番号の小さい順に足し込んだエネルギーの表を、最初から作る。</summary>
    internal static double[] EnergyOf(bool[] pattern, bool value, double[] kernel)
    {
        var e = new double[Count];
        for (var k = 0; k < Count; k++)
        {
            if (pattern[k] == value) Apply(e, k, kernel, +1.0);
        }
        return e;
    }

    /// <summary>位置 p の画素の寄与を、全画素のエネルギーに sign 倍で足す(番号の小さい順)。</summary>
    internal static void Apply(double[] energy, int p, double[] kernel, double sign)
    {
        var px = p % Size;
        var py = p / Size;
        for (var y = 0; y < Size; y++)
        {
            var row = ((y - py) & (Size - 1)) * Size;
            for (var x = 0; x < Size; x++)
            {
                energy[y * Size + x] += sign * kernel[row + ((x - px) & (Size - 1))];
            }
        }
    }

    /// <summary>pattern[k]==value の画素のうちエネルギーが最大の位置。同点は番号の小さい方(厳密な &gt; で昇順に走査)。</summary>
    internal static int TightestCluster(bool[] pattern, double[] energy, bool value)
    {
        var best = -1;
        var bestE = double.NegativeInfinity;
        for (var k = 0; k < Count; k++)
        {
            if (pattern[k] != value) continue;
            if (best < 0 || energy[k] > bestE)
            {
                best = k;
                bestE = energy[k];
            }
        }
        if (best < 0) throw new InvalidOperationException("対象の画素が無い");
        return best;
    }

    /// <summary>0 の画素のうちエネルギーが最小の位置。同点は番号の小さい方(厳密な &lt; で昇順に走査)。</summary>
    internal static int LargestVoid(bool[] pattern, double[] energy)
    {
        var best = -1;
        var bestE = double.PositiveInfinity;
        for (var k = 0; k < Count; k++)
        {
            if (pattern[k]) continue;
            if (best < 0 || energy[k] < bestE)
            {
                best = k;
                bestE = energy[k];
            }
        }
        if (best < 0) throw new InvalidOperationException("空きの画素が無い");
        return best;
    }

    private static int CountOnes(bool[] pattern)
    {
        var n = 0;
        foreach (var b in pattern) if (b) n++;
        return n;
    }

    /// <summary>8×8 の窓(トーラス、4096 通り)の (v+0.5)÷4096 の平均の最小と最大(AC-3 の (2))。</summary>
    public static (double Min, double Max) WindowMeanRange(ReadOnlySpan<ushort> table)
    {
        if (table.Length != Count) throw new ArgumentException($"表の長さが {Count} ではない: {table.Length}", nameof(table));
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (var y0 = 0; y0 < Size; y0++)
        {
            for (var x0 = 0; x0 < Size; x0++)
            {
                var sum = 0.0;
                for (var dy = 0; dy < 8; dy++)
                {
                    for (var dx = 0; dx < 8; dx++)
                    {
                        var v = table[((y0 + dy) & (Size - 1)) * Size + ((x0 + dx) & (Size - 1))];
                        sum += (v + 0.5) / Count;
                    }
                }
                var mean = sum / 64.0;
                if (mean < min) min = mean;
                if (mean > max) max = mean;
            }
        }
        return (min, max);
    }
}
