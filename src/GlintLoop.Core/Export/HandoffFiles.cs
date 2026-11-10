// 渡すファイルの扱い(FR-85・§9.10 の 1 と 4・E-36): 名前を決め、キャッシュの完成ファイルを cache\handoff\ の実体や保存先へ .tmp を経由して写し、完成ファイルがまだ完成のままかを確かめる。
// 名前は glint-YYYYMMDD-HHMMSS(時計のローカル時刻)で、handoff に同じ名前があれば拡張子の前に -2・-3 と空いている最小の番号を付ける。
// 実体の最終更新時刻は、リネームのあとに渡した時刻(UTC)にする。ファイルの操作はすべて IFileSystem を通し、UI スレッドの外で呼ぶ(INV-5)。
using System.Globalization;
using GlintLoop.Core.Platform;

namespace GlintLoop.Core.Export;

public sealed class HandoffFiles
{
    private readonly IClock _clock;
    private readonly IFileSystem _fs;

    /// <summary>handoffDir は AppPaths.CacheHandoffDir。</summary>
    public HandoffFiles(string handoffDir, IClock clock, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(handoffDir);
        ArgumentNullException.ThrowIfNull(clock);
        Directory = handoffDir;
        _clock = clock;
        _fs = fileSystem ?? RealFileSystem.Instance;
    }

    public string Directory { get; }

    /// <summary>番号なしの名前(FR-85)。保存ダイアログの既定の名前にも使う。</summary>
    public string FileName(string format) => BaseName() + "." + ExportFormat.Extension(format);

    private string BaseName() =>
        "glint-" + TimeZoneInfo.ConvertTime(_clock.UtcNow, _clock.LocalZone).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>実体の置き場の、まだ使われていないパス(ベース名 → -2 → -3 …)。</summary>
    public string NextPath(string format)
    {
        var name = BaseName();
        var ext = "." + ExportFormat.Extension(format);
        var path = Path.Combine(Directory, name + ext);
        for (var n = 2; _fs.Exists(path); n++)
            path = Path.Combine(Directory, name + "-" + n.ToString(CultureInfo.InvariantCulture) + ext);
        return path;
    }

    /// <summary>完成ファイルを実体にする(§9.10 の 1)。失敗は例外のまま返し、最終的な名前のファイルと .tmp を残さない。</summary>
    public string Materialize(ExportCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = NextPath(entry.Format);
        CopyTo(entry, path);
        _fs.SetLastWriteUtc(path, _clock.UtcNow.UtcDateTime);
        return path;
    }

    /// <summary>完成ファイルを destination へ .tmp を経由して写す(保存。INV-16)。失敗しても destination の既存のファイルは変わらない。</summary>
    public void CopyTo(ExportCacheEntry entry, string destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        AtomicFile.Write(destination, output =>
        {
            using var input = _fs.OpenRead(entry.Path);
            input.CopyTo(output);
        }, _fs);
    }

    /// <summary>完成ファイルがあり、バイト数が &lt;key&gt;.json と合うか(E-36)。読めないときは false。</summary>
    public bool IsIntact(ExportCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            return _fs.Exists(entry.Path) && _fs.GetLength(entry.Path) == entry.Meta.Bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
