// EditorSession のモーションのプリセットの操作(FR-64)。適用は、選んでいる対象のスタックのモーションステージの行をすべて取り除いてから
// プリセットの行を origin=user・新しい salt で入れる Undo の 1 件で、合計が 64 個を超えるときは何も変えずに stack.full を返す(ルックの適用と同じ形)。
// 行を入れる規則とタイルの絵の文書は Motion.MotionStacks にまとめ、ここは文書への反映だけを行う。
using GlintLoop.Core.Motion;
using GlintLoop.Core.Notices;

namespace GlintLoop.Core.Session;

public sealed partial class EditorSession
{
    /// <summary>
    /// プリセットの適用(FR-64)。target のスタックのモーションステージの行(origin・目を問わない)をすべて取り除き、effects を
    /// origin=user・目がオン・amount 100 で、モーションステージへ表の順に入れる。effects が空なら取り除くだけ(「モーションなし」)。
    /// ほかの対象のスタックは変えない。64 個を超えるときは何も変えずに stack.full を返す。
    /// </summary>
    public EditOutcome ApplyMotionPreset(Target target, IReadOnlyList<MotionPresetEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        using var _ = Track();
        var doc = RequireDoc();
        CheckTarget(doc, target);
        var counter = doc.SaltCounter;
        var list = MotionStacks.Apply(doc.StackOf(target), effects, _catalog, ref counter);
        if (list is null) return EditOutcome.Rejected(NoticeKeys.StackFull);
        // 何も変わらないとき(モーションの行が無いスタックへの「モーションなし」)は、Commit が JSON を比べて履歴に積まない
        return Commit(WithStack(doc with { SaltCounter = counter }, target, list));
    }

    /// <summary>「モーションなし」(FR-64): target のスタックのモーションステージの行を取り除くだけ。</summary>
    public EditOutcome RemoveMotion(Target target) => ApplyMotionPreset(target, Array.Empty<MotionPresetEffect>());

    /// <summary>モーションのタイルの絵の文書(FR-64・§9.7 の規則 14)。effects が空なら「モーションなし」。</summary>
    public SessionDocument MotionPreview(Target target, IReadOnlyList<MotionPresetEffect> effects)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        return MotionStacks.Preview(doc, target, effects, _catalog);
    }
}
