// モーションの有無とフレーム数(FR-67)、モーションの行の除き方(§9.7 の規則 14)の純粋な関数。
// 数えるのは、表示がオンのレイヤーと全体のスタックの、目がオンでモーションステージの行(表示がオフのレイヤーのモーションは数えない)。
// 単体の描画(行のサムネイル・ルックのギャラリー・モーションのタイル)の文書は、描画を分けずに(INV-6)ここで文書の側から行を外して作る。
using GlintLoop.Core.Effects;

namespace GlintLoop.Core.Session;

public static class MotionInfo
{
    /// <summary>行がモーションステージのエフェクトか(目のオン・オフは見ない。カタログに無い id は false)。</summary>
    public static bool IsMotion(StackEntry entry, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(entry.Id) is { Stage: EffectStage.Motion };
    }

    /// <summary>スタックに、目がオンのモーションの行が 1 つでもあるか。</summary>
    public static bool HasEnabledMotion(IReadOnlyList<StackEntry> stack, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(stack);
        foreach (var e in stack)
        {
            if (e.Enabled && IsMotion(e, catalog)) return true;
        }
        return false;
    }

    /// <summary>表示がオンのどれかのレイヤーのモーションステージに、目がオンの行があるか(規則 5 の「t を加える」条件)。</summary>
    public static bool HasLayerMotion(SessionDocument doc, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var layer in doc.Layers)
        {
            if (layer.Visible && HasEnabledMotion(layer.Stack, catalog)) return true;
        }
        return false;
    }

    /// <summary>出力がアニメーションになるか(FR-67)。表示がオンのレイヤーか全体のモーションステージに、目がオンの行がある。</summary>
    public static bool HasMotion(SessionDocument doc, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return HasLayerMotion(doc, catalog) || HasEnabledMotion(doc.All.Stack, catalog);
    }

    /// <summary>出力のフレーム数。モーションが無ければ 1(静止画)、あれば TimeModel.FrameCount(doc.Time)(FR-58・FR-67)。</summary>
    public static int FrameCount(SessionDocument doc, IEffectCatalog catalog) =>
        HasMotion(doc, catalog) ? TimeModel.FrameCount(doc.Time) : 1;

    /// <summary>stack からモーションステージの行(目のオン・オフを問わない)を除いた並び。行の順は変えない。</summary>
    public static IReadOnlyList<StackEntry> WithoutMotion(IReadOnlyList<StackEntry> stack, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(catalog);
        var any = false;
        foreach (var e in stack)
        {
            if (IsMotion(e, catalog))
            {
                any = true;
                break;
            }
        }
        return any ? stack.Where(e => !IsMotion(e, catalog)).ToArray() : stack;
    }

    /// <summary>すべてのレイヤーのモーションステージの行を除いた文書(全体のスタックはそのまま)。規則 14 の「各レイヤーのモーションステージは通さない」。</summary>
    public static SessionDocument WithoutLayerMotion(SessionDocument doc, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalog);
        var changed = false;
        var layers = new Layer[doc.Layers.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            var l = doc.Layers[i];
            var stack = WithoutMotion(l.Stack, catalog);
            changed |= !ReferenceEquals(stack, l.Stack);
            layers[i] = ReferenceEquals(stack, l.Stack) ? l : l with { Stack = stack };
        }
        return changed ? doc with { Layers = layers } : doc;
    }
}
