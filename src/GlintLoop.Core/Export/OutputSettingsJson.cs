// 出力設定(§9.11 の 3 の output)の範囲の直しと、書き出しプリセットのファイル(FR-77)が持つ output の JSON の読み書き。
// 形は文書の output と同じ(format・size・limit・options の gif・png・jpeg・webp・apng)で、読み書きは DocumentJson と同じものを通す。
// プリセットと lastExport には時間の設定が無いので、apng.loops は 1〜4 をそのまま書く(文書の JSON のように time.loop で 0 にしない)。
using System.Text.Json;
using System.Text.Json.Nodes;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

public static class OutputSettingsJson
{
    public const int MinColors = 2;
    public const int MaxColors = 256;
    public const int MinQuality = 1;
    public const int MaxQuality = 100;
    public const int MinApngLoops = 1;
    public const int MaxApngLoops = 4;

    /// <summary>サイズの数値の範囲(長辺・幅・高さは px、倍率は %。仮の決め)。</summary>
    public const double MinSizePx = 1;
    public const double MaxSizePx = 4096;
    public const double MinScale = 1;
    public const double MaxScale = 400;

    /// <summary>容量上限の直接入力の範囲(バイト。仮の決め)。</summary>
    public const long MinLimitBytes = 1;
    public const long MaxLimitBytes = 1_000_000_000_000;

    public static readonly IReadOnlyList<string> SizeModes = ["auto", "long", "width", "height", "scale"];

    public static readonly IReadOnlyList<string> Dithers = ["blue", "error", "none"];

    /// <summary>範囲外を端に、知らない列挙を既定に直す(色は #RRGGBB の大文字、マット色は読めなければ「なし」)。</summary>
    public static OutputSettings Normalize(OutputSettings o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var d = OutputSettings.Default;
        var format = ExportFormat.All.Contains(o.Format) ? o.Format : d.Format;
        var size = NormalizeSize(o.Size);
        var limit = o.Limit.Kind switch
        {
            "preset" when !string.IsNullOrEmpty(o.Limit.Key) => new OutputLimit("preset", o.Limit.Key, null),
            "bytes" when o.Limit.Bytes is long b => new OutputLimit("bytes", null, Math.Clamp(b, MinLimitBytes, MaxLimitBytes)),
            _ => new OutputLimit("none", null, null),
        };
        var gif = new GifOptions(
            Math.Clamp(o.Gif.Colors, MinColors, MaxColors),
            Dithers.Contains(o.Gif.Dither) ? o.Gif.Dither : d.Gif.Dither,
            ColorText.TryNormalizeRgb(o.Gif.Matte, out var matte) ? matte : null,
            o.Gif.Diff);
        var jpeg = new JpegOptions(
            Math.Clamp(o.Jpeg.Quality, MinQuality, MaxQuality),
            ColorText.TryNormalizeRgb(o.Jpeg.Background, out var bg) ? bg : d.Jpeg.Background);
        var webp = new WebpOptions(Math.Clamp(o.Webp.Quality, MinQuality, MaxQuality), o.Webp.Lossless);
        var apng = new ApngOptions(o.Apng.Palette, Math.Clamp(o.Apng.Loops, MinApngLoops, MaxApngLoops));
        return new OutputSettings(format, size, limit, gif, jpeg, webp, apng);
    }

    private static OutputSize NormalizeSize(OutputSize s)
    {
        if (s.Value is not double v || !double.IsFinite(v)) return new OutputSize("auto", null);
        return s.Mode switch
        {
            "long" or "width" or "height" => new OutputSize(s.Mode, Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), MinSizePx, MaxSizePx)),
            "scale" => new OutputSize(s.Mode, Math.Clamp(Math.Round(v, 1, MidpointRounding.AwayFromZero), MinScale, MaxScale)),
            _ => new OutputSize("auto", null),
        };
    }

    /// <summary>output のオブジェクトを書く(呼ぶ側が名前を書いてから呼ぶ)。</summary>
    public static void Write(Utf8JsonWriter w, OutputSettings o) => DocumentJson.WriteOutputObject(w, o, o.Apng.Loops);

    /// <summary>output のオブジェクトを読む(欠けは既定、範囲外は端)。オブジェクトでなければ null。</summary>
    public static OutputSettings? Read(JsonElement o) =>
        o.ValueKind == JsonValueKind.Object ? Normalize(DocumentJson.ReadOutputObject(o)) : null;

    /// <summary>出力設定を settings.json の lastExport の形にする(§9.11 の 2)。</summary>
    public static JsonObject ToJsonObject(OutputSettings o)
    {
        ArgumentNullException.ThrowIfNull(o);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) Write(w, o);
        return (JsonObject)JsonNode.Parse(ms.ToArray())!;
    }

    /// <summary>
    /// 新しい文書の出力設定(§9.11 の 2)。lastExport を読んで範囲を直す。空のときは format が defaultFormat で、ほかは既定(size が auto、limit が none)。
    /// lastExport の format が欠けているか知らない形式のときも defaultFormat にする。
    /// </summary>
    public static OutputSettings ForNewDocument(JsonObject? lastExport, string defaultFormat)
    {
        ArgumentNullException.ThrowIfNull(defaultFormat);
        if (lastExport is null) return Normalize(OutputSettings.Default with { Format = defaultFormat });
        using var json = JsonDocument.Parse(lastExport.ToJsonString());
        var o = Read(json.RootElement)!;
        var known = lastExport["format"] is JsonValue v && v.TryGetValue<string>(out var name) && ExportFormat.All.Contains(name);
        return known ? o : o with { Format = defaultFormat };
    }
}
