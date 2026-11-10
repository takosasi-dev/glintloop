// EditorSession の時間の設定の操作(FR-58・FR-61)と seed の「振り直し」(FR-66)。どれも Undo の 1 件で、変わったときだけ Changed を上げる。
// 時間の変更は SessionChange.Time、seed の変更は SessionChange.Other が立つ(EditorSession.Changes.cs の比べ方による)。
// 入力の直し(L の round と端、停止時間の 100 ms 刻みと端)は TimeInput の規則で、ここでも同じ直しをかける。
namespace GlintLoop.Core.Session;

public sealed partial class EditorSession
{
    /// <summary>「振り直し」で、いまの seed と違う値が出るまで引き直す回数の上限(仮の決め)。</summary>
    private const int RerollAttempts = 16;

    /// <summary>いまの文書の時間の設定(文書が無ければ null)。</summary>
    public TimeSettings? Time => Document?.Time;

    /// <summary>ループの長さを L10(0.1 秒単位の整数)で決める。範囲外は 5〜100 の端に直す(FR-58)。</summary>
    public EditOutcome SetLength10(int length10)
    {
        using var _ = Track();
        var doc = RequireDoc();
        var v = Math.Clamp(length10, TimeModel.MinLength10, TimeModel.MaxLength10);
        return Commit(doc with { Time = doc.Time with { Length10 = v } });
    }

    /// <summary>フレームレートを決める。11 通り(TimeModel.FpsChoices)に無い値は何も変えない(FR-58)。</summary>
    public EditOutcome SetFps(int fps)
    {
        using var _ = Track();
        var doc = RequireDoc();
        if (!TimeModel.FpsChoices.Contains(fps)) return EditOutcome.Unchanged;
        return Commit(doc with { Time = doc.Time with { Fps = fps } });
    }

    /// <summary>ループ(無限・1 回)を決める(FR-61)。</summary>
    public EditOutcome SetLoop(LoopMode loop)
    {
        using var _ = Track();
        var doc = RequireDoc();
        if (!Enum.IsDefined(loop)) throw new ArgumentOutOfRangeException(nameof(loop));
        return Commit(doc with { Time = doc.Time with { Loop = loop } });
    }

    /// <summary>最後のフレームの停止時間(ms)を決める。100 ms 刻みへ round し、0〜5000 の端に直す(FR-61)。</summary>
    public EditOutcome SetHoldMs(decimal holdMs)
    {
        using var _ = Track();
        var doc = RequireDoc();
        return Commit(doc with { Time = doc.Time with { HoldMs = TimeInput.HoldMsOf(holdMs) } });
    }

    /// <summary>
    /// 「振り直し」(FR-66): seed を 0〜999999 の新しい値にする。値は seed の出所(製品はシステムの乱数)から引き、
    /// いまの値と同じなら引き直す(RerollAttempts 回まで。出所がいつも同じ値を返すときは何も変えない)。
    /// </summary>
    public EditOutcome RerollSeed()
    {
        using var _ = Track();
        var doc = RequireDoc();
        for (var i = 0; i < RerollAttempts; i++)
        {
            var seed = Math.Clamp(_seeds.NextSeed(), 0, DocumentRules.MaxSeed);
            if (seed != doc.Seed) return Commit(doc with { Seed = seed });
        }
        return EditOutcome.Unchanged;
    }
}
