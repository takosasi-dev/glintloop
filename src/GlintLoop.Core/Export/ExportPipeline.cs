// 書き出しの中身(出力の解決・はしご・探索・フレーム・エンコーダー)を、自動の書き出し(AutoExport)から呼ぶ口。本物は App の RenderExportPipeline。
// band はキャッシュのファイル(.tmp のストリーム)と取り消しと進みの受け手を用意して Run を呼び、Run は選んだ段の結果をストリームに書いて値を返す。
// Run は ExportRun.Scheduler のスレッドで呼ばれる。取り消しはフレームの切れ目で OperationCanceledException を投げて止める(§9.9 の 8)。
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

/// <summary>
/// 1 回の書き出しの依頼。Document は始めた時点の文書のスナップショット、FrameK は静止形式で描くフレーム(スクラブバーの位置。動く形式とモーションの無い文書は 0)、
/// TimelineFrames は t=k÷N の N(MotionInfo.FrameCount)。
/// </summary>
public sealed record ExportRequest(SessionDocument Document, int FrameK, int TimelineFrames)
{
    public string Format => Document.Output.Format;
}

/// <summary>
/// 書き終えた結果(§9.9 の 7 の json と FR-79 の完了の表示)。Width・Height は出力の画素、Frames は書いたフレーム数、FpsEff ははしごで下げたあとの fps、
/// Steps は調整の内容の文字(調整なしは null)、SizeFail は上限に収まらなかった(FR-76)、Condition は export.condition の {内容}(無ければ null)。
/// </summary>
public sealed record ExportPipelineResult(int Width, int Height, int Frames, double FpsEff, string? Steps, bool SizeFail, string? Condition)
{
    /// <summary>呼び手が完成のファイルを置いた(リネームが成功した)直後に 1 回呼ぶ(本物の中身はここで export_done を書く。§9.11)。無ければ null。</summary>
    public Action? Placed { get; init; }

    /// <summary>中身が返したあと、置く前に取り消されて結果を捨てるときに 1 回呼ぶ(本物の中身はここで export_cancel を書く。§9.11)。無ければ null。</summary>
    public Action? Abandoned { get; init; }

    /// <summary>中身が返したあと、呼び手が完成のファイルを置けなかった(.tmp のリネームの失敗など)ときに 1 回呼ぶ(本物の中身は測定の記録の export_end を書く。§6.8)。無ければ null。</summary>
    public Action? Failed { get; init; }

    /// <summary>選んだ段の描画の上限で縮めたレイヤーの番号(layer.render_shrunk の候補。E-19)。</summary>
    public IReadOnlyList<int> ShrunkLayers { get; init; } = Array.Empty<int>();

    /// <summary>呼び手が、中身が返した直後に呼ぶ。取り消しが立っていれば Abandoned を呼んで OperationCanceledException を投げる。</summary>
    public void ThrowIfCancelled(CancellationToken cancel)
    {
        if (!cancel.IsCancellationRequested) return;
        Abandoned?.Invoke();
        cancel.ThrowIfCancellationRequested();
    }
}

/// <summary>キャッシュを使い回したときに、文書だけから決め直す値(容量上限のバイト数と、制約の警告の {内容})。</summary>
public sealed record ExportCheck(long? LimitBytes, string? Condition)
{
    /// <summary>書いた段の大きさで描画の上限で縮めるレイヤーの番号(キャッシュを使い回した完了でも通知できるように。E-19)。</summary>
    public IReadOnlyList<int> ShrunkLayers { get; init; } = Array.Empty<int>();
}

public interface IExportPipeline
{
    /// <summary>
    /// job を書き出して output に書く。容量上限の探索で試した段の中から選んだものを最後に output へ書く(output に書いたものがキャッシュのファイルになる)。
    /// 進みは run.Progress に、取り消しは run.Cancel を見てフレームの切れ目で OperationCanceledException を投げる。
    /// </summary>
    ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run);

    /// <summary>キャッシュのファイルを使い回すときに、上限のバイト数(上限なしは null)と、書いた段の値(written)での制約の警告を求める(重い計算をしない)。</summary>
    ExportCheck Check(ExportRequest job, ExportWritten written);
}
