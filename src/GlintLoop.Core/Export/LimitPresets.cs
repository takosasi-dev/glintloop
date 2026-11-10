// 容量上限の組み込みのプリセット 7 個と制約(§9.9 の 2、FR-75、E-33)。選んだときの設定の写し、フレーム数の上限による fps の自動の下げ、制約の文字を作る。
// 制約の名前と並びは §9.9 の 2 の表のとおりで、export.condition の {内容} に「、」でつないで入れる。値を自動で変えるのはフレーム数の上限の fps だけ。
// セッションの F は書き換えない(この書き出しの設定だけを下げる)。
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

/// <summary>プリセットの制約(並びは §9.9 の 2 の制約の表の順)。</summary>
public enum LimitConstraint
{
    Square,
    Fps,
    FrameMax,
    FrameRange,
    LoopLength,
    LongSide,
    ShortSide,
    LoopCount,
}

/// <summary>
/// 組み込みの容量上限のプリセット 1 つ。Format・LongSide・Loop が null の項目は「変えない」。LimitBytes が null は上限なし。
/// MaxFrames はフレーム数の上限(制約 (1)。fps を下げる)で、無ければ null。
/// </summary>
public sealed record LimitPreset(
    string Key,
    string Name,
    string? Format,
    long? LimitBytes,
    int? LongSide,
    LoopMode? Loop,
    IReadOnlyList<LimitConstraint> Constraints,
    int? MaxFrames);

public static class LimitPresets
{
    public const string NoneKey = "none";

    /// <summary>§9.9 の 2 の表の 7 個(表の順)。</summary>
    public static IReadOnlyList<LimitPreset> All { get; } =
    [
        new(NoneKey, "上限なし", null, null, null, null, [], null),
        new("discord_emoji", "Discord 絵文字", ExportFormat.Gif, 255_000, 128, LoopMode.Infinite, [], null),
        new("discord_sticker", "Discord ステッカー", ExportFormat.Apng, 500_000, 320, LoopMode.Infinite,
            [LimitConstraint.Square, LimitConstraint.Fps], null),
        new("slack_emoji", "Slack 絵文字", ExportFormat.Gif, 120_000, 128, LoopMode.Infinite, [LimitConstraint.FrameMax], 50),
        new("line_stamp", "LINE アニメスタンプ", ExportFormat.Apng, 990_000, 320, LoopMode.Once,
            [LimitConstraint.FrameRange, LimitConstraint.LoopLength, LimitConstraint.LongSide, LimitConstraint.ShortSide, LimitConstraint.LoopCount], null),
        new("chat_small", "チャット(小)", ExportFormat.Gif, 1_000_000, 320, LoopMode.Infinite, [], null),
        new("chat_medium", "チャット(中)", ExportFormat.Gif, 8_000_000, 480, LoopMode.Infinite, [], null),
    ];

    /// <summary>キーのプリセット(無ければ null)。</summary>
    public static LimitPreset? Find(string? key) => key is null ? null : All.FirstOrDefault(p => p.Key == key);

    /// <summary>制約の名前({内容} に入れる文字列。§9.9 の 2)。</summary>
    public static string ConstraintName(LimitConstraint c) => c switch
    {
        LimitConstraint.Square => "320×320 の正方形",
        LimitConstraint.Fps => "fps 50 以下",
        LimitConstraint.FrameMax => "フレーム数 50 以下",
        LimitConstraint.FrameRange => "フレーム数 5〜20",
        LimitConstraint.LoopLength => "ループ長 4.0 秒以下",
        LimitConstraint.LongSide => "長辺 270〜320 px",
        LimitConstraint.ShortSide => "短辺 270 px 以下",
        LimitConstraint.LoopCount => "ループ 1 回",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>
    /// プリセットを選んだときの設定を一度だけ写す(形式・長辺・ループ・容量上限。「変えない」の項目は今の値)。
    /// ループの「1 回」は time.loop を once に、output.options.apng.loops を 1 にする(§9.9 の 2)。
    /// </summary>
    public static (OutputSettings Output, TimeSettings Time) Apply(LimitPreset preset, OutputSettings output, TimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(time);
        var o = output with
        {
            Format = preset.Format ?? output.Format,
            Size = preset.LongSide is int l ? new OutputSize("long", l) : output.Size,
            Limit = preset.Key == NoneKey ? new OutputLimit("none", null, null) : new OutputLimit("preset", preset.Key, null),
        };
        var t = time;
        if (preset.Loop is LoopMode loop)
        {
            t = time with { Loop = loop };
            if (loop == LoopMode.Once) o = o with { Apng = o.Apng with { Loops = 1 } };
        }
        return (o, t);
    }

    /// <summary>
    /// フレーム数の上限 maxFrames に収まる fps(制約 (1))。N=(L10×F+5)÷10 が上限以下ならそのまま。超えるときは上限以下になる最大の F'(FR-58 の選択肢)、
    /// 5 fps でも超えるときは 5 fps で Exceeds=true(警告)。
    /// </summary>
    public static (int Fps, bool Exceeds) FpsForMaxFrames(int length10, int fps, int maxFrames)
    {
        if (TimeModel.FrameCount(length10, fps) <= maxFrames) return (fps, false);
        var choices = TimeModel.FpsChoices;
        for (var i = choices.Count - 1; i >= 0; i--)
        {
            var f = choices[i];
            if (f <= fps && TimeModel.FrameCount(length10, f) <= maxFrames) return (f, false);
        }
        return (choices[0], true);
    }

    /// <summary>settings がプリセットの制約を満たすか。</summary>
    public static bool Satisfies(LimitConstraint c, ExportSettings s) => c switch
    {
        LimitConstraint.Square => s.Width == 320 && s.Height == 320,
        LimitConstraint.Fps => s.Fps <= 50,
        LimitConstraint.FrameMax => s.FrameCount <= 50,
        LimitConstraint.FrameRange => s.FrameCount is >= 5 and <= 20,
        LimitConstraint.LoopLength => s.Length10 <= 40,
        LimitConstraint.LongSide => Math.Max(s.Width, s.Height) is >= 270 and <= 320,
        LimitConstraint.ShortSide => Math.Min(s.Width, s.Height) <= 270,
        LimitConstraint.LoopCount => s.Loop == LoopMode.Once && (s.Format != ExportFormat.Apng || s.ApngLoops == 1),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>満たしていない制約の名前を表の順に「、」でつないだもの(export.condition の {内容})。全部満たせば null。</summary>
    public static string? Condition(LimitPreset? preset, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (preset is null) return null;
        var names = preset.Constraints.Order().Where(c => !Satisfies(c, settings)).Select(ConstraintName).ToList();
        return names.Count == 0 ? null : string.Join("、", names);
    }
}
