// タイムライン(FR-59)の固定の文言を ui.tsv の timeline.* から引く。文言は P0 が仮に足した行で、作者が後で直す。
// スクラブバーの「k+1 / N」は、ui.tsv の「{k} / {n}」の置き換えをここで行う。
using System.Globalization;
using GlintLoop.App.Text;

namespace GlintLoop.App.Timelines;

public static class TimelineTexts
{
    public static string Get(string key) => NoticeTexts.UiText(key);

    public static string Bar => Get("timeline.bar");
    public static string Play => Get("timeline.play");
    public static string Stop => Get("timeline.stop");
    public static string Scrub => Get("timeline.scrub");
    public static string Length => Get("timeline.length");
    public static string LengthUnit => Get("timeline.length.unit");
    public static string Fps => Get("timeline.fps");
    public static string FpsUnit => Get("timeline.fps.unit");
    public static string Loop => Get("timeline.loop");
    public static string LoopInfinite => Get("timeline.loop.infinite");
    public static string LoopOnce => Get("timeline.loop.once");
    public static string Hold => Get("timeline.hold");
    public static string HoldUnit => Get("timeline.hold.unit");
    public static string Reseed => Get("timeline.reseed");

    /// <summary>スクラブバーの表示「k+1 / N」(FR-59。例「3 / 40」)。k は 0 から数えた番号。</summary>
    public static string Frame(int k, int n) => Get("timeline.frame")
        .Replace("{k}", (k + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{n}", n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
