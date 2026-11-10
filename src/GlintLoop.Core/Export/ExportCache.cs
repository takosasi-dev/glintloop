// 書き出しのキャッシュのファイル(§9.9 の 7・INV-16): cache\exports\<key>.<拡張子> と、同じ名前の <key>.json(bytes・width・height・frames・fpsEff・steps・createdAt・lastUsed)。
// 書くときは <key>.<拡張子>.tmp に書き、<key>.json を書いてから .tmp をリネームする。完成の合図はこのリネームの成功だけで、.tmp や json だけがあるものは完成として扱わない。
// 書く途中の例外(取り消しの OperationCanceledException を含む)では .tmp を消して投げ直す(AtomicFile)。キャッシュの上限と削除(FR-105)はここでは扱わない。
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlintLoop.Core.Platform;

namespace GlintLoop.Core.Export;

/// <summary>&lt;key&gt;.json の中身(§9.9 の 7)。Steps は調整の内容の文字(調整なしは null)。時刻は UTC。</summary>
public sealed record ExportCacheMeta(long Bytes, int Width, int Height, int Frames, double FpsEff, string? Steps, DateTimeOffset CreatedAt, DateTimeOffset LastUsed);

/// <summary>完成したキャッシュのファイル 1 つ。</summary>
public sealed record ExportCacheEntry(string Key, string Format, string Path, ExportCacheMeta Meta);

/// <summary>書き出しの中身が書き終えたときに返す、json に入れる値(bytes はファイルの長さ、時刻はキャッシュが入れる)。</summary>
public sealed record ExportWritten(int Width, int Height, int Frames, double FpsEff, string? Steps);

public sealed class ExportCache
{
    public const string MetaExtension = ".json";

    private readonly IClock _clock;
    private readonly IFileSystem _fs;

    /// <summary>dir は AppPaths.CacheExportsDir。</summary>
    public ExportCache(string dir, IClock clock, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(clock);
        Directory = dir;
        _clock = clock;
        _fs = fileSystem ?? RealFileSystem.Instance;
    }

    public string Directory { get; }

    /// <summary>完成したファイルの名前(&lt;key&gt;.&lt;拡張子&gt;)。</summary>
    public string FilePath(string key, string format) => System.IO.Path.Combine(Directory, key + "." + ExportFormat.Extension(format));

    public string MetaPath(string key) => System.IO.Path.Combine(Directory, key + MetaExtension);

    /// <summary>書いている途中のファイルの名前(&lt;key&gt;.&lt;拡張子&gt;.tmp)。</summary>
    public string TempPath(string key, string format) => AtomicFile.TempPathFor(FilePath(key, format));

    /// <summary>
    /// 完成したファイルがあれば返す(無ければ null)。完成は「リネーム後の名前のファイルがある」ことで、json が読めないものも使わない。
    /// 見つけたときは json の lastUsed を今の時刻に書き直す(書けなくても結果は返す)。
    /// </summary>
    public ExportCacheEntry? TryGet(string key, string format)
    {
        var path = FilePath(key, format);
        var metaPath = MetaPath(key);
        try
        {
            if (!_fs.Exists(path) || !_fs.Exists(metaPath)) return null;
            var meta = ReadMeta(_fs.ReadAllBytes(metaPath));
            if (meta is null) return null;
            var length = _fs.GetLength(path);
            if (length != meta.Bytes) return null;
            meta = meta with { LastUsed = _clock.UtcNow };
            try
            {
                AtomicFile.WriteAllBytes(metaPath, WriteMeta(meta), _fs);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            return new ExportCacheEntry(key, format, path, meta);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// write に .tmp のストリームを渡して中身を書かせ、json を書いてから .tmp をリネームする。write が投げたとき(取り消しを含む)は .tmp を消して投げ直す。
    /// </summary>
    public ExportCacheEntry Write(string key, string format, Func<Stream, ExportWritten> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var path = FilePath(key, format);
        var metaPath = MetaPath(key);
        ExportCacheMeta? meta = null;
        AtomicFile.Write(path, s =>
        {
            var written = write(s);
            s.Flush();
            var now = _clock.UtcNow;
            meta = new ExportCacheMeta(s.Length, written.Width, written.Height, written.Frames, written.FpsEff, written.Steps, now, now);
            // json を先に置き、完成の合図(.tmp のリネーム)を最後にする
            AtomicFile.WriteAllBytes(metaPath, WriteMeta(meta), _fs);
        }, _fs);
        return new ExportCacheEntry(key, format, path, meta!);
    }

    // ---------------------------------------------------------------- json の形

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        IndentCharacter = ' ',
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>{"bytes":…,"width":…,"height":…,"frames":…,"fpsEff":…,"steps":…,"createdAt":"…Z","lastUsed":"…Z"} を UTF-8(BOM なし)で書く。</summary>
    public static byte[] WriteMeta(ExportCacheMeta m)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("bytes", m.Bytes);
            w.WriteNumber("width", m.Width);
            w.WriteNumber("height", m.Height);
            w.WriteNumber("frames", m.Frames);
            w.WriteNumber("fpsEff", m.FpsEff);
            if (m.Steps is null) w.WriteNull("steps");
            else w.WriteString("steps", m.Steps);
            w.WriteString("createdAt", m.CreatedAt.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
            w.WriteString("lastUsed", m.LastUsed.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>json を読む。形が違うときは null。</summary>
    public static ExportCacheMeta? ReadMeta(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!Long(r, "bytes", out var b) || !Long(r, "width", out var w) || !Long(r, "height", out var h) || !Long(r, "frames", out var f)) return null;
            if (!r.TryGetProperty("fpsEff", out var fe) || fe.ValueKind != JsonValueKind.Number) return null;
            string? steps = null;
            if (r.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.String) steps = st.GetString();
            if (!Time(r, "createdAt", out var created) || !Time(r, "lastUsed", out var used)) return null;
            return new ExportCacheMeta(b, (int)w, (int)h, (int)f, fe.GetDouble(), steps, created, used);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Long(JsonElement r, string name, out long v)
    {
        v = 0;
        return r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out v) && v >= 0 && v <= int.MaxValue * 1024L;
    }

    private static bool Time(JsonElement r, string name, out DateTimeOffset v)
    {
        v = default;
        return r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out v);
    }
}
