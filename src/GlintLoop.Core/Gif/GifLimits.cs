// GIF の 1 回の書き出しの上限(仕様書 §9.8 の 10・E-31)と、上限を超えたときに投げる例外。
// 上限は N×W×H が 160,000,000 画素以下、W と H が 1〜4096、N が 1〜500。超える指定はフレームを 1 枚も読まずに拒否する。
// 画面の側は GifTooBigException.MessageKey(export.gif_too_big)の文言を状態欄に出す。
namespace GlintLoop.Core.Gif;

public static class GifLimits
{
    public const int MaxSide = 4096;
    public const int MaxFrames = 500;
    public const long MaxTotalPixels = 160_000_000;

    /// <summary>幅・高さ・フレーム数が上限の内側にあるか。</summary>
    public static bool IsWithin(int width, int height, int frameCount) =>
        width is >= 1 and <= MaxSide
        && height is >= 1 and <= MaxSide
        && frameCount is >= 1 and <= MaxFrames
        && (long)width * height * frameCount <= MaxTotalPixels;

    /// <summary>上限の外なら GifTooBigException を投げる。</summary>
    public static void Validate(int width, int height, int frameCount)
    {
        if (!IsWithin(width, height, frameCount)) throw new GifTooBigException(width, height, frameCount);
    }
}

/// <summary>GIF の上限(§9.8 の 10)を超える指定を拒否したことを表す。符号化は始まっていない。</summary>
public sealed class GifTooBigException : Exception
{
    /// <summary>状態欄に出す文言のキー(§9.12)。{fmt} には FormatName を入れる。</summary>
    public const string MessageKey = "export.gif_too_big";

    /// <summary>ログ export_fail の reason(E-31)。</summary>
    public const string LogReason = "too_big_gif";

    public GifTooBigException(int width, int height, int frameCount)
        : base($"GIF の上限を超えています: {width}×{height}、{frameCount} フレーム(W と H は 1〜{GifLimits.MaxSide}、N は 1〜{GifLimits.MaxFrames}、N×W×H は {GifLimits.MaxTotalPixels} 以下)")
    {
        Width = width;
        Height = height;
        FrameCount = frameCount;
    }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount { get; }

    /// <summary>{fmt} に入れる形式名。</summary>
    public string FormatName => "GIF";
}
