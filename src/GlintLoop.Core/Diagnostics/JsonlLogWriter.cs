// ログのファイル(FR-91・§9.11 の 6): logs\glintloop-YYYYMMDD-NN.jsonl に 1 行 1 JSON(UTF-8・BOM なし・LF)で書く。
// 1 ファイル 5,000,000 バイトまで。起動時に 14 日より古いものと、新しい順で 31 番目以降を消す。書けないときは例外にせず捨てる(E-48)。
// 共通の項目は ts(UTC の ISO 8601・ミリ秒)・lvl・event・v。複数のスレッドから呼べる。
using System.Globalization;
using System.Text;
using System.Text.Json;
using GlintLoop.Core.Platform;

namespace GlintLoop.Core.Diagnostics;

public sealed class JsonlLogWriter : ILogSink
{
    public const long DefaultMaxFileBytes = 5_000_000;

    /// <summary>起動時に消す、最終更新時刻の古さ(14 日)。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>起動時に残す個数(新しい順で 31 番目以降を消す)。</summary>
    public const int KeepCount = 30;

    private const string Prefix = "glintloop-";
    private const string Extension = ".jsonl";

    private readonly string _dir;
    private readonly IClock _clock;
    private readonly string _version;
    private readonly long _maxBytes;
    private readonly IFileSystem _fs;
    private readonly object _gate = new();
    private string? _currentPath;
    private long _currentSize;

    public JsonlLogWriter(string logsDir, IClock clock, string version, long maxFileBytes = DefaultMaxFileBytes, IFileSystem? fileSystem = null)
    {
        _dir = logsDir;
        _clock = clock;
        _version = version;
        _maxBytes = maxFileBytes;
        _fs = fileSystem ?? RealFileSystem.Instance;
    }

    /// <summary>いま書いているファイル(まだ 1 行も書いていなければ null)。</summary>
    public string? CurrentPath
    {
        get
        {
            lock (_gate) return _currentPath;
        }
    }

    public void Write(LogEvent e)
    {
        byte[] line;
        try
        {
            line = Format(e);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return;
        }
        lock (_gate)
        {
            try
            {
                if (_currentPath is null) OpenForStart();
                if (_currentSize > 0 && _currentSize + line.Length > _maxBytes) OpenNext();
                using (var s = _fs.OpenAppend(_currentPath!))
                {
                    s.Write(line, 0, line.Length);
                }
                _currentSize += line.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // E-48: そのログを捨てて続ける。次の行でもう一度ファイルを選び直す。
                _currentPath = null;
                _currentSize = 0;
            }
        }
    }

    /// <summary>1 行(末尾の LF を含む)のバイト列。</summary>
    public byte[] Format(LogEvent e)
    {
        var level = LogEvents.LevelOf(e.Name) ?? LogLevel.Info;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("ts", _clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("lvl", LogEvents.LevelName(level));
            w.WriteString("event", e.Name);
            w.WriteString("v", _version);
            foreach (var (key, value) in e.Fields)
            {
                w.WritePropertyName(key);
                WriteValue(w, value);
            }
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter w, object value)
    {
        switch (value)
        {
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case uint u: w.WriteNumberValue(u); break;
            case short s: w.WriteNumberValue(s); break;
            case byte b: w.WriteNumberValue(b); break;
            case bool b: w.WriteBooleanValue(b); break;
            case string s: w.WriteStringValue(s); break;
            case IEnumerable<string> list:
                w.WriteStartArray();
                foreach (var s in list) w.WriteStringValue(s);
                w.WriteEndArray();
                break;
            case Enum en: w.WriteStringValue(en.ToString()); break;
            default: throw new ArgumentException("ログの値の型に対応していない: " + value.GetType().Name);
        }
    }

    /// <summary>今日のファイル名(NN は 1 から)。</summary>
    public string FileNameFor(DateOnly localDate, int nn) =>
        Prefix + localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + nn.ToString("00", CultureInfo.InvariantCulture) + Extension;

    private DateOnly LocalToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.UtcNow, _clock.LocalZone).DateTime);

    /// <summary>起動の最初の 1 行: その日の最後のファイルが上限に達していなければ追記し、達していれば次の NN を作る。</summary>
    private void OpenForStart()
    {
        _fs.CreateDirectory(_dir);
        var today = LocalToday();
        var last = LastNumberOf(today);
        if (last > 0)
        {
            var path = Path.Combine(_dir, FileNameFor(today, last));
            var size = _fs.GetLength(path);
            if (size < _maxBytes)
            {
                _currentPath = path;
                _currentSize = size;
                return;
            }
        }
        _currentPath = Path.Combine(_dir, FileNameFor(today, last + 1));
        _currentSize = _fs.Exists(_currentPath) ? _fs.GetLength(_currentPath) : 0;
    }

    /// <summary>次の 1 行で上限を超えるとき: 今日の次の NN へ移る。</summary>
    private void OpenNext()
    {
        _fs.CreateDirectory(_dir);
        var today = LocalToday();
        _currentPath = Path.Combine(_dir, FileNameFor(today, LastNumberOf(today) + 1));
        _currentSize = _fs.Exists(_currentPath) ? _fs.GetLength(_currentPath) : 0;
    }

    private int LastNumberOf(DateOnly date)
    {
        var head = Prefix + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var max = 0;
        foreach (var f in _fs.EnumerateFiles(_dir, head + "*" + Extension))
        {
            var name = Path.GetFileName(f);
            var mid = name.Substring(head.Length, name.Length - head.Length - Extension.Length);
            if (int.TryParse(mid, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max) max = n;
        }
        return max;
    }

    /// <summary>
    /// 起動時の削除(FR-91): 最終更新時刻が 14 日より前のファイルを消し、残りを最終更新時刻の新しい順(同時刻は名前の降順)に並べて
    /// 31 番目以降を消す。消せないファイルは飛ばす(E-48)。消した個数を返す。
    /// </summary>
    public static int CleanUp(string logsDir, IClock clock, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? RealFileSystem.Instance;
        List<(string Path, DateTime LastWrite)> files;
        try
        {
            if (!fs.DirectoryExists(logsDir)) return 0;
            files = fs.EnumerateFiles(logsDir, Prefix + "*" + Extension).Select(f => (f, fs.GetLastWriteUtc(f))).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        var removed = 0;
        var cutoff = clock.UtcNow.UtcDateTime - MaxAge;
        var kept = new List<(string Path, DateTime LastWrite)>();
        foreach (var f in files)
        {
            if (f.LastWrite < cutoff)
            {
                if (TryDelete(f.Path, fs)) removed++;
            }
            else
            {
                kept.Add(f);
            }
        }
        foreach (var f in kept
                     .OrderByDescending(f => f.LastWrite)
                     .ThenByDescending(f => Path.GetFileName(f.Path), StringComparer.Ordinal)
                     .Skip(KeepCount))
        {
            if (TryDelete(f.Path, fs)) removed++;
        }
        return removed;
    }

    private static bool TryDelete(string path, IFileSystem fs)
    {
        try
        {
            fs.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>テストと診断用: 書いた行を UTF-8 として読む(BOM の有無を見るときは IFileSystem.ReadAllBytes を使う)。</summary>
    public static string[] ReadLines(string path, IFileSystem? fileSystem = null) =>
        Encoding.UTF8.GetString((fileSystem ?? RealFileSystem.Instance).ReadAllBytes(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
