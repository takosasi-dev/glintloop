// クリップボードとドロップのデータを、WPF の型を使わずに読むための口(仕様書 §9.10 の 5・FR-1・FR-5・INV-18)。
// 実物(WPF の DataObject と Clipboard を包むもの)は Phase 3 で作る。テストと内部の受け渡しは MemoryImportData を使う。
// 形式名は Q-16 で確定した "PNG"・"Format17"(DIBV5)・"DeviceIndependentBitmap"(DIB)・"FileDrop"。
namespace GlintLoop.Codec.Import;

/// <summary>取り込みが読む形式名(WPF の DataFormats の文字列と同じ値)。</summary>
public static class ImportFormatNames
{
    public const string FileDrop = "FileDrop";
    public const string Png = "PNG";
    public const string DibV5 = "Format17";
    public const string Dib = "DeviceIndependentBitmap";
}

/// <summary>クリップボードかドロップのデータ 1 つ。</summary>
public interface IImportData
{
    /// <summary>データが持つ形式名の一覧。</summary>
    IReadOnlyList<string> Formats { get; }

    /// <summary>形式名(PNG・Format17・DeviceIndependentBitmap)のバイト列。無い、または読めなければ null。</summary>
    byte[]? GetBytes(string format);

    /// <summary>FileDrop のファイルの一覧(渡された順)。無ければ null。</summary>
    IReadOnlyList<string>? GetFileList();

    /// <summary>URL か文字列を持つか(画像を持たないドロップを見分けるため。FR-5・E-3)。</summary>
    bool HasUrlOrText { get; }
}

/// <summary>
/// クリップボードを開けなかった(CLIPBRD_E_CANT_OPEN。E-2)。実物の実装が再試行のあとに投げる。
/// 取り込みは clip.failed を出し、ログ clipboard_failed(hr)を書く。
/// </summary>
public sealed class ImportDataUnavailableException(uint hresult) : Exception("クリップボードを開けない")
{
    public uint HResult32 { get; } = hresult;
}

/// <summary>メモリ上のデータ(テストの偽の DataObject と、データを組み立てて渡すとき用)。</summary>
public sealed class MemoryImportData : IImportData
{
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly List<string> _formats = new();
    private List<string>? _files;

    public IReadOnlyList<string> Formats => _formats;

    public bool HasUrlOrText { get; private set; }

    public MemoryImportData With(string format, byte[] bytes)
    {
        if (!_formats.Contains(format)) _formats.Add(format);
        _bytes[format] = bytes;
        return this;
    }

    public MemoryImportData WithFiles(params string[] paths)
    {
        if (!_formats.Contains(ImportFormatNames.FileDrop)) _formats.Add(ImportFormatNames.FileDrop);
        _files = paths.ToList();
        return this;
    }

    /// <summary>URL か文字列を持たせる(形式名は WPF の Text・UnicodeText・UniformResourceLocatorW など。中身は使わない)。</summary>
    public MemoryImportData WithUrlOrText(string format = "UniformResourceLocatorW")
    {
        if (!_formats.Contains(format)) _formats.Add(format);
        HasUrlOrText = true;
        return this;
    }

    public byte[]? GetBytes(string format) => _bytes.TryGetValue(format, out var b) ? b : null;

    public IReadOnlyList<string>? GetFileList() => _files;
}
