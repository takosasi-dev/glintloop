// 取り込みの上限の判定(FR-10・FR-57・E-7〜E-9・E-12・E-13・INV-11)。ファイルのバイト数とヘッダーの幅・高さだけを見る純粋な関数。
// 調べる順は、ファイルの大きさ、辺、画素数、レイヤーの枚数、文書の合計(§10 の冒頭)で、最初に当てはまった 1 つだけを返す。
// 拒否の理由ごとの通知(§9.12)とログの reason(§9.11)の綴りもここで決める。
using System.Globalization;
using GlintLoop.Core.Notices;

namespace GlintLoop.Codec;

/// <summary>取り込めなかった理由。ログの import_fail の reason と 1 対 1。</summary>
public enum ImportFailReason
{
    Unsupported,
    TooLargeFile,
    TooLargeSide,
    TooManyPixels,
    TooManyLayers,
    OverBudget,
    DecodeError,
    ReadError,
}

/// <summary>取り込めなかった 1 件。Notice はその理由の通知(置き換えの値を入れたもの)。</summary>
public sealed record ImportFailure(ImportFailReason Reason, Notice Notice)
{
    public string LogReason => Reason.LogName();
}

public static class ImportLimits
{
    /// <summary>1 個のファイル(またはクリップボードのデータ)のバイト数の上限。これを超えると拒否。</summary>
    public const long MaxFileBytes = 100_000_000;

    /// <summary>縦・横の上限(px)。</summary>
    public const int MaxSide = 16384;

    /// <summary>画素数の上限(64 メガピクセル)。</summary>
    public const long MaxPixels = 67_108_864;

    /// <summary>文書のレイヤーの枚数の上限。</summary>
    public const int MaxLayers = 32;

    /// <summary>文書の異なる画像のファイルのバイト数の合計の上限。</summary>
    public const long MaxTotalBytes = 300_000_000;

    /// <summary>文書の異なる画像の画素数の合計の上限。</summary>
    public const long MaxTotalPixels = 100_000_000;

    /// <summary>
    /// 1 個のファイルの上限(FR-10)。ファイルの大きさ、辺、画素数の順に調べ、最初に当てはまったものを返す。通れば null。
    /// 幅と高さはヘッダーの値(EXIF の向きを適用する前でよい。辺の上限は縦横で同じ、画素数も変わらない)。
    /// </summary>
    public static ImportFailure? CheckSingle(long fileBytes, long width, long height)
    {
        if (fileBytes > MaxFileBytes) return FileTooLarge(fileBytes);
        if (width > MaxSide || height > MaxSide)
            return Fail(ImportFailReason.TooLargeSide, Notice.Of(NoticeKeys.ImportTooLargeSide, NoticeLevel.Error,
                ("w", width.ToString(CultureInfo.InvariantCulture)), ("h", height.ToString(CultureInfo.InvariantCulture))));
        var pixels = width * height;
        if (pixels > MaxPixels)
            return Fail(ImportFailReason.TooManyPixels, Notice.Of(NoticeKeys.ImportTooManyPixels, NoticeLevel.Error,
                ("mp", NoticeFormat.MegapixelsOneDecimal(pixels))));
        return null;
    }

    /// <summary>ファイルの大きさだけで拒否するとき(ヘッダーを読む前)の失敗。</summary>
    public static ImportFailure FileTooLarge(long fileBytes) =>
        Fail(ImportFailReason.TooLargeFile, Notice.Of(NoticeKeys.ImportTooLargeFile, NoticeLevel.Error,
            ("size", NoticeFormat.MegabytesOneDecimal(fileBytes))));

    /// <summary>
    /// 文書の上限(FR-57)。足す前のレイヤーの枚数と、異なる画像の合計(バイト数・画素数)に、この 1 枚を足したときに超えるか。
    /// <paramref name="alreadyCounted"/> が true(同じ SHA-256 の画像がすでに文書か同じ取り込みの中にある)のときは合計が増えない。
    /// </summary>
    public static ImportFailure? CheckDocument(int layersBefore, long totalBytesBefore, long totalPixelsBefore,
        bool alreadyCounted, long fileBytes, long pixels, long maxTotalBytes = MaxTotalBytes, long maxTotalPixels = MaxTotalPixels)
    {
        if (layersBefore + 1 > MaxLayers) return Fail(ImportFailReason.TooManyLayers, Notice.Of(NoticeKeys.LayerFull, NoticeLevel.Warn));
        if (!alreadyCounted && (totalBytesBefore + fileBytes > maxTotalBytes || totalPixelsBefore + pixels > maxTotalPixels))
            return Fail(ImportFailReason.OverBudget, Notice.Of(NoticeKeys.LayerBudget, NoticeLevel.Warn));
        return null;
    }

    /// <summary>理由だけで決まる失敗(非対応・壊れている・開けない)。置き換えの値を持たない。</summary>
    public static ImportFailure Simple(ImportFailReason reason) => reason switch
    {
        ImportFailReason.Unsupported => Fail(reason, Notice.Of(NoticeKeys.ImportUnsupported, NoticeLevel.Error)),
        ImportFailReason.DecodeError => Fail(reason, Notice.Of(NoticeKeys.ImportDecodeError, NoticeLevel.Error)),
        ImportFailReason.ReadError => Fail(reason, Notice.Of(NoticeKeys.ImportReadError, NoticeLevel.Error)),
        ImportFailReason.TooManyLayers => Fail(reason, Notice.Of(NoticeKeys.LayerFull, NoticeLevel.Warn)),
        ImportFailReason.OverBudget => Fail(reason, Notice.Of(NoticeKeys.LayerBudget, NoticeLevel.Warn)),
        _ => throw new ArgumentException("置き換えの値が要る理由は CheckSingle で作る", nameof(reason)),
    };

    /// <summary>ログの import_fail の reason の綴り(§9.11 の 6)。</summary>
    public static string LogName(this ImportFailReason r) => r switch
    {
        ImportFailReason.Unsupported => "unsupported",
        ImportFailReason.TooLargeFile => "too_large_file",
        ImportFailReason.TooLargeSide => "too_large_side",
        ImportFailReason.TooManyPixels => "too_many_pixels",
        ImportFailReason.TooManyLayers => "too_many_layers",
        ImportFailReason.OverBudget => "over_budget",
        ImportFailReason.DecodeError => "decode_error",
        ImportFailReason.ReadError => "read_error",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    private static ImportFailure Fail(ImportFailReason reason, Notice notice) => new(reason, notice);
}
