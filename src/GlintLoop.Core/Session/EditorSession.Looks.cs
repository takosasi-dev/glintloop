// EditorSession のルックの操作(FR-34・FR-35・FR-36)。適用は選んでいる対象のスタックの origin=look の行を置き換える Undo の 1 件で、
// 合計が 64 個を超えるときは取り除く分を引いて数え、適用の全体を取りやめる(E-22)。強さ(SetLookStrength)は EditorSession.cs にある。
// 行を入れる規則とサムネイルの文書は Looks.LookStacks にまとめ、ここは文書への反映だけを行う。
using GlintLoop.Core.Looks;
using GlintLoop.Core.Notices;

namespace GlintLoop.Core.Session;

public sealed partial class EditorSession
{
    /// <summary>
    /// ルックの適用(FR-34)。target のスタックの origin=look の行をすべて取り除き、effects を origin=look で
    /// ルックステージと仕上げステージのそれぞれの先頭へレシピの順に入れる。effects が空なら取り除くだけ(「ルックなし」)。
    /// origin=user の行は残る。64 個を超えるときは何も変えずに stack.full を返す。
    /// </summary>
    public EditOutcome ApplyLook(Target target, IReadOnlyList<LookEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        using var _ = Track();
        var doc = RequireDoc();
        CheckTarget(doc, target);
        var counter = doc.SaltCounter;
        var list = LookStacks.Apply(doc.StackOf(target), effects, _catalog, ref counter);
        if (list is null) return EditOutcome.Rejected(NoticeKeys.StackFull);
        // 何も変わらないとき(ルックの行が無いスタックへの「ルックなし」)は、Commit が JSON を比べて履歴に積まない
        return Commit(WithStack(doc with { SaltCounter = counter }, target, list));
    }

    /// <summary>「ルックなし」(FR-34): target のスタックの origin=look の行を取り除くだけ。</summary>
    public EditOutcome RemoveLook(Target target) => ApplyLook(target, Array.Empty<LookEffect>());

    /// <summary>ユーザーのルックとして保存する中身(FR-36): target のルックステージと仕上げステージの行のすべて(origin を問わない)。</summary>
    public IReadOnlyList<LookEffect> CaptureLook(Target target)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        return LookStacks.Capture(doc.StackOf(target), _catalog);
    }

    /// <summary>target のルックの強さ(FR-35。0〜100)。</summary>
    public int LookStrengthOf(Target target)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        return target.IsAll ? doc.All.LookStrength : doc.Layers[target.LayerIndex].LookStrength;
    }

    /// <summary>ギャラリーのタイルのサムネイルの文書(FR-33・§9.7 の規則 14)。effects が空なら「ルックなし」。</summary>
    public SessionDocument LookPreview(Target target, IReadOnlyList<LookEffect> effects)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        return LookStacks.Preview(doc, target, effects, _catalog);
    }
}
