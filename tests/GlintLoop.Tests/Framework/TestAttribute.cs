// 自作のテストランナーが拾うテストの印と、判定の小さな道具(Check)。
// テスト名には AC・INV の番号を含める(例: AC2_Hash32の試験値)。
// 外部のテストフレームワークを使わない(依存を増やさないため。仕様書 D-39)。
namespace GlintLoop.Tests.Framework;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    /// <summary>true のとき、--all を付けた実行だけで走らせる(長時間の検査)。</summary>
    public bool Slow { get; init; }

    /// <summary>true のとき、--soak を付けた実行だけで走らせる(--all でも走らせない。AC-61)。</summary>
    public bool Soak { get; init; }
}

public sealed class CheckFailedException(string message) : Exception(message);

/// <summary>判定しなかった検査(合格にも失敗にも数えない。ランナーは終了コード 2)。</summary>
public sealed class NotJudgedException(string message) : Exception(message);

public static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new CheckFailedException(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailedException($"{what}: 期待 {expected}、実際 {actual}");
    }

    public static void Near(double expected, double actual, double tolerance, string what)
    {
        if (!(Math.Abs(expected - actual) <= tolerance))
            throw new CheckFailedException($"{what}: 期待 {expected}±{tolerance}、実際 {actual}");
    }

    public static void InRange(double min, double max, double actual, string what)
    {
        if (!(actual >= min && actual <= max))
            throw new CheckFailedException($"{what}: 期待 {min}〜{max}、実際 {actual}");
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string what)
    {
        if (expected.Count != actual.Count)
            throw new CheckFailedException($"{what}: 長さ 期待 {expected.Count}、実際 {actual.Count}");
        for (var i = 0; i < expected.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
                throw new CheckFailedException($"{what}: [{i}] 期待 {expected[i]}、実際 {actual[i]}");
        }
    }

    public static TException Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException e)
        {
            return e;
        }
        catch (Exception e)
        {
            throw new CheckFailedException($"{what}: {typeof(TException).Name} を期待したが {e.GetType().Name}");
        }
        throw new CheckFailedException($"{what}: {typeof(TException).Name} が投げられなかった");
    }
}
