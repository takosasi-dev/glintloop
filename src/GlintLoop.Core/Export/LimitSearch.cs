// 容量上限の探索(§9.9 の 4、D-28)。はしごの段 r_0〜r_R について、固定の順序で fits(i) を調べ、使う段の番号を決める純粋な手順。
// fits(0) が真なら r_0、R=0 か fits(R) が偽なら r_R で size_fail(FR-76)、それ以外は lo=0・hi=R の二分で r_hi。同じ段は 2 回調べない。
// 試行の回数は R≥1 で 2+ceil(log2(R)) 以下、R=0 で 1 回。size_fail の作り直しは呼ぶ側が行い、ここでは数えない。
namespace GlintLoop.Core.Export;

/// <summary>探索の結果。Chosen は使う段の番号、SizeFail は最後の段でも収まらなかったこと、Tried は fits を調べた段の番号の順。</summary>
public sealed record LimitSearchResult(int Chosen, bool SizeFail, IReadOnlyList<int> Tried);

public static class LimitSearch
{
    /// <summary>r_0〜r_R の探索。fits(i) は「r_i で符号化した結果が上限以下」(同じ i では 1 回しか呼ばない)。</summary>
    public static LimitSearchResult Run(int lastStep, Func<int, bool> fits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastStep);
        ArgumentNullException.ThrowIfNull(fits);
        var tried = new List<int>();
        var memo = new Dictionary<int, bool>();
        bool Fits(int i)
        {
            if (memo.TryGetValue(i, out var v)) return v;
            tried.Add(i);
            v = fits(i);
            memo[i] = v;
            return v;
        }

        if (Fits(0)) return new LimitSearchResult(0, false, tried);
        if (lastStep == 0 || !Fits(lastStep)) return new LimitSearchResult(lastStep, true, tried);
        int lo = 0, hi = lastStep;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (Fits(mid)) hi = mid;
            else lo = mid;
        }
        return new LimitSearchResult(hi, false, tried);
    }

    /// <summary>R≥1 のときの試行の回数の上限 2+ceil(log2(R))(R=0 は 1)。</summary>
    public static int MaxTrials(int lastStep)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastStep);
        if (lastStep == 0) return 1;
        var c = 0;
        while ((1 << c) < lastStep) c++;
        return 2 + c;
    }
}
