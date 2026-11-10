// 文書と出力設定から、書き出しの設定(ユーザーの設定と r_0)を解決する(FR-73・§9.9 の 1・2・4・5、FR-67)。
// 長辺は OutputSizing.LongSide、幅と高さは CompositeGeometry.Output(描画と同じ式)。N は MotionInfo(モーションが無ければ 1 フレーム)。
// r_0 はユーザーの設定に、プリセットのフレーム数の上限による fps の下げ(§9.9 の 2 の制約 (1))を当てはめたもの。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

/// <summary>
/// 解決した書き出しの計画。User はユーザーの設定そのもの(調整の文字の比べる元)、R0 は探索の最初の段、Composite は合成のネイティブ寸法(はしごの長辺の段で使う)。
/// LimitBytes は上限(null は上限なし)、Preset は選んだ組み込みのプリセット(無ければ null)、FramesExceed は 5 fps でもフレーム数の上限を超えたこと。
/// </summary>
public sealed record ExportPlan(
    ExportSettings User,
    ExportSettings R0,
    SizeD Composite,
    long? LimitBytes,
    LimitPreset? Preset,
    bool FramesExceed);

public static class OutputPlan
{
    /// <summary>
    /// 文書の今の出力設定から計画を作る。sourceSize は image のレイヤーのソースの寸法、scrubK はスクラブバーの位置(静止形式で描くフレーム。§9.9 の 5)。
    /// </summary>
    public static ExportPlan Resolve(SessionDocument doc, IEffectCatalog catalog, Func<string, SizeD> sourceSize, int scrubK)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sourceSize);
        var native = CompositeGeometry.CompositeNative(doc, sourceSize);
        var lout = OutputSizing.LongSide(doc, sourceSize);
        var hasMotion = MotionInfo.HasMotion(doc, catalog);
        var user = Settings(doc.Output, doc.Time, native, lout, hasMotion, scrubK);

        var (limit, preset) = Limit(doc.Output.Limit);
        var r0 = user;
        var exceed = false;
        if (preset?.MaxFrames is int max && user.FrameCount > 1)
        {
            var (fps, ex) = LimitPresets.FpsForMaxFrames(user.Length10, user.Fps, max);
            exceed = ex;
            r0 = WithFps(user, fps);
        }
        return new ExportPlan(user, r0, native, limit, preset, exceed);
    }

    /// <summary>
    /// 出力設定と時間の設定から、長辺 lout の設定を作る。アニメの形式(GIF・APNG)はモーションがあれば N フレーム、無ければ 1 フレーム(FR-67・FR-70)。
    /// 静止形式は 1 フレームで、モーションがあれば scrubK(0〜N−1 に切る)を t=k÷N で描く。
    /// </summary>
    public static ExportSettings Settings(OutputSettings output, TimeSettings time, SizeD composite, int lout, bool hasMotion, int scrubK)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(time);
        var dims = CompositeGeometry.Output(composite, lout);
        var n = hasMotion ? TimeModel.FrameCount(time) : 1;
        var animated = ExportFormat.IsAnimated(output.Format);
        var quality = output.Format == ExportFormat.Webp ? output.Webp.Quality : output.Jpeg.Quality;
        return new ExportSettings
        {
            Format = output.Format,
            Width = dims.Wo,
            Height = dims.Ho,
            LongSide = lout,
            FrameCount = animated ? n : 1,
            TimelineFrames = n,
            FrameK = animated || !hasMotion ? 0 : Math.Clamp(scrubK, 0, n - 1),
            Fps = time.Fps,
            Length10 = time.Length10,
            Loop = time.Loop,
            ApngLoops = (int)Math.Clamp(output.Apng.Loops, 1, 4),
            HoldMs = time.HoldMs,
            // パレットの APNG は 256 色から始める(§9.9 の 3 の「パレット(256 色)」。GIF の色数は使わない)
            Colors = output.Format == ExportFormat.Apng ? 256 : (int)Math.Clamp(output.Gif.Colors, 2, 256),
            Dither = output.Gif.Dither switch
            {
                "error" => GifDither.ErrorDiffusion,
                "none" => GifDither.None,
                _ => GifDither.BlueNoise,
            },
            Matte = ParseRgb(output.Gif.Matte),
            DiffOptimization = output.Gif.Diff,
            Quality = (int)Math.Clamp(quality, 1, 100),
            Lossless = output.Webp.Lossless,
            ApngPalette = output.Apng.Palette,
            JpegBackground = output.Jpeg.Background,
        };
    }

    /// <summary>容量上限(null は上限なし)と、選んだ組み込みのプリセット。知らないキーのプリセットは上限なしとして扱う。</summary>
    public static (long? Bytes, LimitPreset? Preset) Limit(OutputLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return limit.Kind switch
        {
            "bytes" when limit.Bytes is long b && b >= 1 => (b, null),
            "preset" when LimitPresets.Find(limit.Key) is { } p => (p.LimitBytes, p),
            _ => (null, null),
        };
    }

    /// <summary>fps を変えた設定(アニメの形式で 2 フレーム以上なら N=(L10×F'+5)÷10 に直す)。</summary>
    public static ExportSettings WithFps(ExportSettings s, int fps)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.FrameCount <= 1) return s with { Fps = fps };
        var n = TimeModel.FrameCount(s.Length10, fps);
        return s with { Fps = fps, FrameCount = n, TimelineFrames = n };
    }

    /// <summary>長辺を変えた設定(幅と高さは描画と同じ式で直す)。</summary>
    public static ExportSettings WithLongSide(ExportSettings s, SizeD composite, int lout)
    {
        ArgumentNullException.ThrowIfNull(s);
        var dims = CompositeGeometry.Output(composite, lout);
        return s with { LongSide = lout, Width = dims.Wo, Height = dims.Ho };
    }

    /// <summary>GIF・APNG の上限(§9.8 の 10。W・H が 1〜4096、N が 1〜500、N×W×H が 160,000,000 以下)の内側か。静止形式はいつも true。</summary>
    public static bool IsWithinLimits(ExportSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return !ExportFormat.IsAnimated(s.Format) || GifLimits.IsWithin(s.Width, s.Height, s.FrameCount);
    }

    /// <summary>上限を超えたときの {fmt}(GIF または APNG)。</summary>
    public static string TooBigFormatName(string format) => format == ExportFormat.Apng ? "APNG" : "GIF";

    /// <summary>「#RRGGBB」の色(読めなければ null)。GIF のマット色と JPEG の背景色に使う。</summary>
    public static GifColor? ParseRgb(string? text)
    {
        if (!ColorText.TryNormalizeRgb(text, out var s)) return null;
        var v = Convert.ToUInt32(s[1..], 16);
        return new GifColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
