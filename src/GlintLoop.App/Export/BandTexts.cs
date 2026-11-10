// 書き出し帯の文言(ui.tsv の band.* と notices.tsv の export.*)。
// 状態欄の 2 行(FR-79・§9.3): 1 行目は状態と進み(計算中は「完了したフレーム数 / N」と「段 n / R」、完了はサイズ・画素・フレーム数・実効 fps)、2 行目は調整の内容と警告。
// 「ソフトウェア描画」(FR-94)は 1 行目の末尾に別に出し、省略しない(帯の側で右に寄せる)。
using System.Globalization;
using GlintLoop.App.Text;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;

namespace GlintLoop.App.Export;

public static class BandTexts
{
    public static string Ui(string key) => NoticeTexts.UiText(key);

    public static string P(string key, params (string Name, string Value)[] args)
    {
        var s = Ui(key);
        foreach (var (n, v) in args) s = s.Replace("{" + n + "}", v, StringComparison.Ordinal);
        return s;
    }

    /// <summary>形式の表示名(band.format.*)。</summary>
    public static string FormatName(string format) => Ui("band.format." + format);

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// 状態欄の 1 行目(「ソフトウェア描画」を除く)。writing は F5・「実出力」を完成前に押したとき(「書き出し中」の進みを出す。FR-19)。
    /// </summary>
    public static string Line1(ExportState state, ExportProgress? progress, ExportOutcome? outcome, bool writing)
    {
        switch (state)
        {
            case ExportState.Running:
            {
                var label = Ui(writing ? "band.writing" : "band.status.running");
                if (progress is not { } p) return label;
                var s = label + " " + P("band.status.progress", ("done", p.FramesDone.ToString(CultureInfo.InvariantCulture)), ("n", p.FrameCount.ToString(CultureInfo.InvariantCulture)));
                if (p.StepCount > 0)
                    s += "・" + P("band.status.step", ("n", p.Step.ToString(CultureInfo.InvariantCulture)), ("r", p.StepCount.ToString(CultureInfo.InvariantCulture)));
                return s;
            }
            case ExportState.Done when outcome is { } o:
            {
                // ファイルサイズは KB(1000 バイト。仮の決め)で小数 1 位
                var kb = (o.Bytes / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
                return string.Join("・",
                    Ui("band.status.done") + " " + P("band.status.kb", ("kb", kb)),
                    o.Width.ToString(CultureInfo.InvariantCulture) + "×" + o.Height.ToString(CultureInfo.InvariantCulture),
                    P("band.status.frames", ("n", o.Frames.ToString(CultureInfo.InvariantCulture))),
                    P("band.status.fps", ("fps", Num(o.FpsEff))));
            }
            case ExportState.Failed:
                return Ui("band.status.failed");
            default:
                // 編集のあとの 600 ms を待っている間に押されたときも「書き出し中」にする
                return Ui(writing ? "band.writing" : "band.status.idle");
        }
    }

    /// <summary>状態欄の 2 行目: 完了は調整の内容・上限に収まらなかった警告(E-30)・制約の警告(E-33)、失敗は export.failed(大きすぎるときは export.gif_too_big。E-31)。無ければ空。</summary>
    public static string Line2(ExportState state, ExportOutcome? outcome, long? limitBytes, Exception? error = null)
    {
        if (state == ExportState.Failed)
        {
            return error is ExportTooBigException big
                ? NoticeTexts.Format(NoticeKeys.ExportGifTooBig, ("fmt", big.FormatName))
                : NoticeTexts.Format(NoticeKeys.ExportFailed);
        }
        if (state != ExportState.Done || outcome is not { } o) return "";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(o.Steps)) parts.Add(o.Steps);
        if (o.SizeFail)
            parts.Add(NoticeTexts.Format(NoticeKeys.ExportSizeFail,
                ("limit", (limitBytes ?? 0).ToString(CultureInfo.InvariantCulture)), ("size", o.Bytes.ToString(CultureInfo.InvariantCulture))));
        if (!string.IsNullOrEmpty(o.Condition)) parts.Add(NoticeTexts.Format(NoticeKeys.ExportCondition, ("内容", o.Condition)));
        return string.Join("  ", parts);
    }
}
