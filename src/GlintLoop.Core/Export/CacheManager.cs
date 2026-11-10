// キャッシュの管理(FR-105・§9.9 の 7・§9.10 の 1): cache\exports\ と cache\handoff\ の合計を上限以下に保ち、最後に使った時刻を更新し、ドラッグ中の実体を守り、設定の「キャッシュの削除」を行う。
// ファイルの列挙・読み書き・削除をするので、Trim・Touch・ClearFromSettings は UI スレッドの外で呼ぶ(INV-5)。3 つは同時に動かない。ProtectFromTrim だけは UI スレッドから呼んでよい。
// 上限はバイト数を返す関数で受ける(製品は設定の cacheLimitMB × 1,000,000。テストは小さな値を直接与える。AC-45)。ログは cache_trim(LogEvents.CacheTrim)を書く。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;

namespace GlintLoop.Core.Export;

public sealed class CacheManager
{
    /// <summary>設定の cacheLimitMB の 1 MB のバイト数(FR-105。1,048,576 でない)。</summary>
    public const long BytesPerMB = 1_000_000;

    /// <summary>渡した実体を、ほかが無いときを除いて消さない時間(FR-105)。</summary>
    public static readonly TimeSpan HandoffProtection = TimeSpan.FromHours(24);

    private readonly IClock _clock;
    private readonly ILogSink _log;
    private readonly IFileSystem _fs;
    private readonly object _workGate = new();
    private readonly object _protectGate = new();
    private readonly Dictionary<string, int> _protected = new(StringComparer.OrdinalIgnoreCase);
    private int _trimCalls;

    /// <summary>exportsDir は AppPaths.CacheExportsDir、handoffDir は AppPaths.CacheHandoffDir。limitBytes は上限のバイト数を返す関数(確認のたびに呼ぶ)。</summary>
    public CacheManager(string exportsDir, string handoffDir, IClock clock, ILogSink log, Func<long> limitBytes, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(exportsDir);
        ArgumentNullException.ThrowIfNull(handoffDir);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(limitBytes);
        ExportsDirectory = exportsDir;
        HandoffDirectory = handoffDir;
        _clock = clock;
        _log = log;
        LimitBytesSource = limitBytes;
        _fs = fileSystem ?? RealFileSystem.Instance;
    }

    /// <summary>確認(Trim)を呼んだ回数(テスト用。AC-45 の (7))。</summary>
    public int TrimCalls => Volatile.Read(ref _trimCalls);

    public string ExportsDirectory { get; }

    public string HandoffDirectory { get; }

    /// <summary>上限のバイト数を返す関数(テストは起動したアプリのものも小さな値に差し替えられる。AC-45)。</summary>
    public Func<long> LimitBytesSource { get; set; }

    /// <summary>いまの上限のバイト数。</summary>
    public long LimitBytes => LimitBytesSource();

    /// <summary>設定の cacheLimitMB を上限のバイト数にする(AC-45 の (1))。</summary>
    public static long LimitBytesFromMB(int cacheLimitMB) => cacheLimitMB * BytesPerMB;

    /// <summary>
    /// 確認して、合計が上限を超えていたら古いものから消す(起動時と書き出しの完了のたび。FR-105)。24 時間以内の実体は、ほかをすべて消してもまだ超えるときだけ消す。
    /// ドラッグ中の実体と書いている途中のファイル(.tmp とその組)は消さないが、合計には数える。消したときだけログを書く。
    /// </summary>
    public void Trim()
    {
        Interlocked.Increment(ref _trimCalls);
        lock (_workGate)
        {
            var exports = ListFiles(ExportsDirectory);
            var handoffs = ListFiles(HandoffDirectory);
            var total = exports.Sum(f => f.Bytes) + handoffs.Sum(f => f.Bytes);
            var limit = LimitBytes;
            if (total <= limit) return;

            var now = _clock.UtcNow.UtcDateTime;
            var items = ExportItems(exports).Concat(HandoffItems(handoffs)).ToArray();
            var order = OldestFirst(items.Where(i => !IsHandedRecently(i, now))).Concat(OldestFirst(items.Where(i => IsHandedRecently(i, now))));

            var removed = 0;
            var bytes = 0L;
            foreach (var item in order)
            {
                if (total <= limit) break;
                foreach (var file in item.Files)
                {
                    if (!TryDelete(file.Path)) continue;
                    removed++;
                    bytes += file.Bytes;
                    total -= file.Bytes;
                }
            }
            if (removed > 0) _log.Write(LogEvents.CacheTrim(removed, bytes));
        }
    }

    /// <summary>書き出しのキャッシュの &lt;key&gt;.json の lastUsed を今の時刻にする(表示・コピー・保存。再利用は ExportCache.TryGet が更新する)。消えた組は作り直さず、書けなくても例外にしない。</summary>
    public void Touch(ExportCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var metaPath = Path.Combine(ExportsDirectory, entry.Key + ExportCache.MetaExtension);
        lock (_workGate)
        {
            try
            {
                if (!_fs.Exists(entry.Path) || !_fs.Exists(metaPath)) return;
                if (ExportCache.ReadMeta(_fs.ReadAllBytes(metaPath)) is not { } meta) return;
                AtomicFile.WriteAllBytes(metaPath, ExportCache.WriteMeta(meta with { LastUsed = _clock.UtcNow }), _fs);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>ドラッグ中の実体を Trim から守る。戻り値を Dispose するまで、その実体を消さない(FR-105)。</summary>
    public IDisposable ProtectFromTrim(string handoffPath)
    {
        var key = Path.GetFullPath(handoffPath);
        lock (_protectGate)
        {
            _protected[key] = _protected.GetValueOrDefault(key) + 1;
        }
        return new Protection(this, key);
    }

    /// <summary>設定の「キャッシュの削除」: cache\exports\ のすべてと、渡してから 24 時間を超えた実体を消す(FR-105)。書いている途中の組とドラッグ中の実体は残す。cache_trim は確認のログなので書かない。</summary>
    public void ClearFromSettings()
    {
        lock (_workGate)
        {
            var now = _clock.UtcNow.UtcDateTime;
            var items = ExportItems(ListFiles(ExportsDirectory)).Concat(HandoffItems(ListFiles(HandoffDirectory)).Where(i => !IsHandedRecently(i, now)));
            foreach (var file in items.SelectMany(i => i.Files).ToArray()) TryDelete(file.Path);
        }
    }

    // ---------------------------------------------------------------- 消す単位

    /// <summary>消す単位 1 つ(書き出しのキャッシュの 1 組、または実体 1 個)。LastUsedUtc は実体では渡した時刻。</summary>
    private sealed record CacheItem(string Name, DateTime LastUsedUtc, bool IsHandoff, IReadOnlyList<CacheFile> Files);

    private readonly record struct CacheFile(string Path, long Bytes);

    /// <summary>渡してから 24 時間以内の実体か(ちょうど 24 時間を含む)。</summary>
    private static bool IsHandedRecently(CacheItem item, DateTime nowUtc) => item.IsHandoff && nowUtc - item.LastUsedUtc <= HandoffProtection;

    private static IEnumerable<CacheItem> OldestFirst(IEnumerable<CacheItem> items) =>
        items.OrderBy(i => i.LastUsedUtc).ThenBy(i => i.Name, StringComparer.Ordinal);

    private static bool IsTemp(string path) => path.EndsWith(AtomicFile.TempExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>フォルダの直下のファイルと大きさ。無いフォルダ・読めないファイルは飛ばす。</summary>
    private List<CacheFile> ListFiles(string directory)
    {
        var files = new List<CacheFile>();
        try
        {
            if (!_fs.DirectoryExists(directory)) return files;
            foreach (var path in _fs.EnumerateFiles(directory, "*").ToArray())
            {
                try
                {
                    files.Add(new CacheFile(path, _fs.GetLength(path)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return files;
    }

    /// <summary>書き出しのキャッシュを &lt;key&gt; ごとの組にする。.tmp のある組は書いている途中なので入れない(.tmp のあいだに json だけが先に置かれる。ExportCache.Write)。</summary>
    private IEnumerable<CacheItem> ExportItems(IEnumerable<CacheFile> files)
    {
        foreach (var group in files.GroupBy(f => KeyOf(f.Path), StringComparer.OrdinalIgnoreCase))
        {
            var pair = group.ToArray();
            if (pair.Any(f => IsTemp(f.Path))) continue;
            yield return new CacheItem(group.Key, LastUsedOf(pair, group.Key), false, pair);
        }
    }

    private IEnumerable<CacheItem> HandoffItems(IEnumerable<CacheFile> files) =>
        files.Where(f => !IsTemp(f.Path)).Select(f => new CacheItem(Path.GetFileName(f.Path), LastWriteUtc(f.Path), true, [f]));

    private static string KeyOf(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>json の lastUsed。json が無い・読めない組は、ファイルの最終更新時刻で代える。</summary>
    private DateTime LastUsedOf(CacheFile[] files, string key)
    {
        var metaPath = Path.Combine(ExportsDirectory, key + ExportCache.MetaExtension);
        try
        {
            if (files.Any(f => string.Equals(f.Path, metaPath, StringComparison.OrdinalIgnoreCase)) && ExportCache.ReadMeta(_fs.ReadAllBytes(metaPath)) is { } meta)
                return meta.LastUsed.UtcDateTime;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return files.Max(f => LastWriteUtc(f.Path));
    }

    private DateTime LastWriteUtc(string path)
    {
        try
        {
            return _fs.GetLastWriteUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return _clock.UtcNow.UtcDateTime;
        }
    }

    /// <summary>消す直前にドラッグ中でないかを見る(ProtectFromTrim が返ったあとは、その実体を消さない)。消せなければ false。</summary>
    private bool TryDelete(string path)
    {
        lock (_protectGate)
        {
            if (_protected.ContainsKey(Path.GetFullPath(path))) return false;
            try
            {
                _fs.Delete(path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private sealed class Protection(CacheManager owner, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._protectGate)
            {
                var left = owner._protected[key] - 1;
                if (left > 0) owner._protected[key] = left;
                else owner._protected.Remove(key);
            }
        }
    }
}
