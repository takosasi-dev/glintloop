// EditorSession の出力設定の操作(書き出し帯。FR-68〜FR-77・§9.3・§9.11 の 3 の output)。どれも Undo の 1 件で、変わると SessionChange.Output が上がる。
// 値は OutputSettingsJson.Normalize で範囲の端に直してから入れる。APNG のループと組み込みの容量上限のプリセットのループは、時間の設定の time.loop も変える(§9.9 の 2)。
// 書き出しプリセット(FR-77)を選んだときは、そのプリセットの出力設定(形式・サイズ・形式ごとのオプション・容量上限)を一度だけ写す。
using GlintLoop.Core.Export;

namespace GlintLoop.Core.Session;

public sealed partial class EditorSession
{
    /// <summary>形式(png・jpeg・gif・webp・apng)。知らない形式は ArgumentException。</summary>
    public EditOutcome SetOutputFormat(string format)
    {
        if (!ExportFormat.All.Contains(format)) throw new ArgumentException("知らない形式: " + format, nameof(format));
        return UpdateOutput(o => o with { Format = format });
    }

    /// <summary>サイズの指定(mode は auto・long・width・height・scale。auto のときは value を持たない)。</summary>
    public EditOutcome SetOutputSize(string mode, double? value)
    {
        if (!OutputSettingsJson.SizeModes.Contains(mode)) throw new ArgumentException("知らないサイズの指定: " + mode, nameof(mode));
        return UpdateOutput(o => o with { Size = mode == "auto" ? new OutputSize("auto", null) : new OutputSize(mode, value) });
    }

    /// <summary>容量上限(none・preset・bytes)。組み込みのプリセットの形式・長辺・ループも写すときは ApplyLimitPreset を使う。</summary>
    public EditOutcome SetOutputLimit(OutputLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return UpdateOutput(o => o with { Limit = limit });
    }

    /// <summary>
    /// 組み込みの容量上限のプリセットを選ぶ(§9.9 の 2)。limit を preset にし、format・longSide・loop のうち null でないものを一度だけ写す(null は「変えない」)。
    /// ループの「無限」は time.loop を infinite に、「1 回」は time.loop を once にして apng.loops を 1 にする。key が none なら上限なしにする。
    /// </summary>
    public EditOutcome ApplyLimitPreset(string key, string? format, int? longSide, LoopMode? loop)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var _ = Track();
        var doc = RequireDoc();
        var o = doc.Output with { Limit = key == "none" ? new OutputLimit("none", null, null) : new OutputLimit("preset", key, null) };
        if (format is not null) o = o with { Format = format };
        if (longSide is int l) o = o with { Size = new OutputSize("long", l) };
        var time = doc.Time;
        if (loop is LoopMode.Infinite) time = time with { Loop = LoopMode.Infinite };
        else if (loop is LoopMode.Once)
        {
            time = time with { Loop = LoopMode.Once };
            o = o with { Apng = o.Apng with { Loops = 1 } };
        }
        return Commit(doc with { Output = OutputSettingsJson.Normalize(o), Time = time });
    }

    /// <summary>書き出しプリセット(FR-77)の出力設定を、いまの出力設定に一度だけ写す(時間の設定は変えない)。</summary>
    public EditOutcome ApplyOutputPreset(OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return UpdateOutput(_ => output);
    }

    public EditOutcome SetGifOptions(GifOptions gif)
    {
        ArgumentNullException.ThrowIfNull(gif);
        return UpdateOutput(o => o with { Gif = gif });
    }

    public EditOutcome SetJpegOptions(JpegOptions jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        return UpdateOutput(o => o with { Jpeg = jpeg });
    }

    public EditOutcome SetWebpOptions(WebpOptions webp)
    {
        ArgumentNullException.ThrowIfNull(webp);
        return UpdateOutput(o => o with { Webp = webp });
    }

    /// <summary>APNG の色(32 ビットかパレットか)。</summary>
    public EditOutcome SetApngPalette(bool palette) => UpdateOutput(o => o with { Apng = o.Apng with { Palette = palette } });

    /// <summary>
    /// APNG のループ(§9.3 のフライアウトの「無限・1・2・3・4 回」)。loops が 0 なら time.loop を infinite に、1〜4 なら time.loop を once にして apng.loops に入れる。
    /// </summary>
    public EditOutcome SetApngLoops(int loops)
    {
        if (loops is < 0 or > OutputSettingsJson.MaxApngLoops) throw new ArgumentOutOfRangeException(nameof(loops));
        using var _ = Track();
        var doc = RequireDoc();
        if (loops == 0) return Commit(doc with { Time = doc.Time with { Loop = LoopMode.Infinite } });
        return Commit(doc with
        {
            Time = doc.Time with { Loop = LoopMode.Once },
            Output = doc.Output with { Apng = doc.Output.Apng with { Loops = loops } },
        });
    }

    private EditOutcome UpdateOutput(Func<OutputSettings, OutputSettings> change)
    {
        using var _ = Track();
        var doc = RequireDoc();
        return Commit(doc with { Output = OutputSettingsJson.Normalize(change(doc.Output)) });
    }
}
