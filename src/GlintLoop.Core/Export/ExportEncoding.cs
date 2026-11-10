// エンコーダーが共有する小さな手順: 上限で打ち切る書き込みの包み(Overflowed を返す)と、フレームの列と設定の大きさの照合。
// 打ち切りは CountingStream が投げる ExportLimitExceededException だけを捕まえる(取り消しの OperationCanceledException はそのまま通す)。
// 各形式のエンコーダー(Core の PNG・APNG・GIF、Codec の JPEG・WebP)がここを通る。
namespace GlintLoop.Core.Export;

/// <summary>GIF・APNG の上限(§9.8 の 10・E-31)の外の設定を拒否した。書き出しは始まっていない。状態欄は export.gif_too_big({fmt} は FormatName)。</summary>
public sealed class ExportTooBigException(ExportSettings settings)
    : Exception($"{OutputPlan.TooBigFormatName(settings.Format)} の上限を超えています: {settings.Width}×{settings.Height}、{settings.FrameCount} フレーム")
{
    /// <summary>状態欄の文言のキー(§9.12)。</summary>
    public const string MessageKey = Gif.GifTooBigException.MessageKey;

    /// <summary>ログ export_fail の reason(E-31)。</summary>
    public const string LogReason = Gif.GifTooBigException.LogReason;

    public ExportSettings Settings { get; } = settings;

    /// <summary>{fmt} に入れる形式名(GIF か APNG)。</summary>
    public string FormatName => OutputPlan.TooBigFormatName(Settings.Format);
}

public static class ExportEncoding
{
    /// <summary>output を上限 limitBytes で包んで write を呼ぶ。上限を超えた書き込みで打ち切り、Overflowed=true を返す。</summary>
    public static EncodeResult Run(Stream output, long? limitBytes, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(write);
        var counting = new CountingStream(output, limitBytes);
        try
        {
            write(counting);
            counting.Flush();
            return new EncodeResult(counting.Written, false);
        }
        catch (ExportLimitExceededException)
        {
            return new EncodeResult(counting.Written, true);
        }
    }

    /// <summary>フレームの列の大きさが設定と合うか(合わなければ InvalidOperationException)。frameCount は使うフレームの数。</summary>
    public static void CheckFrames(IExportFrames frames, ExportSettings settings, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(settings);
        if (frames.Width != settings.Width || frames.Height != settings.Height)
            throw new InvalidOperationException($"フレームの大きさ {frames.Width}×{frames.Height} が設定の {settings.Width}×{settings.Height} と違う");
        if (frames.FrameCount < frameCount)
            throw new InvalidOperationException($"フレームの数 {frames.FrameCount} が {frameCount} より少ない");
    }

    /// <summary>フレームの画素を取り出し、大きさ(幅×高さ×4 バイト)を確かめる。</summary>
    public static byte[] Frame(IExportFrames frames, int index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var px = frames.GetFrame(index, ct) ?? throw new InvalidDataException($"フレーム {index} が null");
        if (px.Length != frames.Width * frames.Height * 4)
            throw new InvalidDataException($"フレーム {index} の大きさが {frames.Width}×{frames.Height}×4 バイトではない: {px.Length}");
        return px;
    }
}
