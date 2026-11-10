// 容量上限の一覧に出す組み込みのプリセット(§9.9 の 2 の表の順)。選ぶと形式・長辺・ループを一度だけ写す(null は「変えない」)。
// 表の本体は Core の LimitPresets(制約もそちら)で、ここは一覧の項目の形に写すだけ。
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Export;

/// <summary>組み込みのプリセット 1 行。LimitBytes は上限(none は null)。Format・LongSide・Loop は null なら変えない。</summary>
public sealed record LimitPresetItem(string Key, string Name, string? Format, long? LimitBytes, int? LongSide, LoopMode? Loop);

public static class BandLimitPresets
{
    /// <summary>§9.9 の 2 の表(上から順)。</summary>
    public static IReadOnlyList<LimitPresetItem> Builtin { get; } =
        LimitPresets.All.Select(p => new LimitPresetItem(p.Key, p.Name, p.Format, p.LimitBytes, p.LongSide, p.Loop)).ToArray();
}
