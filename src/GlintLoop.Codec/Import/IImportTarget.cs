// 取り込みの行き先(文書)の口。文書の担当(EditorSession)がこれを実装し、取り込みは EditorSession の型に直接依存しない。
// 1 回の取り込みで AddImages を 1 回だけ呼ぶ(複数のファイルも 1 回。Undo の 1 件にするため。FR-4・FR-42)。
// 上限の判定(FR-57)に使う合計は、文書の image のレイヤーが参照する異なる画像(SHA-256 で 1 回だけ数える)のもの。
namespace GlintLoop.Codec.Import;

/// <summary>取り込みの操作の種類(ログの import_ok の via。サンプルはログを書かないので Sample は via に出ない)。</summary>
public enum ImportVia
{
    Paste,
    Drop,
    Open,
    Arg,
    Sample,
}

/// <summary>足すレイヤー 1 枚(名前と画像)。同じ取り込みで同じ内容の画像を 2 回足すときは、同じ ImportedImage を共有する。SourcePath は取り込み元のファイルのパス(ファイルからでなければ null。INV-2)。</summary>
public sealed record ImportedLayer(string Name, ImportedImage Image, string? SourcePath = null);

public interface IImportTarget
{
    /// <summary>文書があるか(ウェルカム画面のときは false。false なら最初の 1 枚が土台になる)。</summary>
    bool HasDocument { get; }

    /// <summary>今のレイヤーの枚数(文書が無ければ 0)。</summary>
    int LayerCount { get; }

    /// <summary>文書が参照する異なる画像のファイルのバイト数の合計(ImportedImage.ByteCount の和)。</summary>
    long SourceBytesTotal { get; }

    /// <summary>文書が参照する異なる画像の画素数の合計(ImportedImage.SourcePixels の和)。</summary>
    long SourcePixelsTotal { get; }

    /// <summary>この SHA-256(小文字の 16 進 64 桁)の画像が、すでに文書にあるか。</summary>
    bool ContainsSource(string sha256);

    /// <summary>
    /// 読めた画像を、渡した順にレイヤーとして足す(1 回の取り込みで 1 回だけ呼ばれる。layers は 1 枚以上)。
    /// 文書が無ければ新しい文書を作って最初の 1 枚を土台にする。戻り値は足したあとの文書のレイヤーの枚数。
    /// </summary>
    int AddImages(IReadOnlyList<ImportedLayer> layers, ImportVia via);
}
