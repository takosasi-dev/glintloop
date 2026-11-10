// プロジェクト .glint の読み書き(§9.11 の 4)。ZIP に manifest.json・session.json(Deflate)と assets/<sha256>.bin(無圧縮。元のバイト列)を入れ、.tmp を経由して保存する(INV-16)。
// 開くは §9.11 の 4 の読み込みの規則の順に調べ、エントリーは固定の名前で取り出す(名前をパスとして使わない)。どの失敗も文書に触らず GlintProjectOpenException にする。
// 画面は持たない。呼ぶのは ProjectCoordinator(保存と開く)と担当 robust(クラッシュの退避。AC-50)。ファイルの操作は IFileSystem 越し。
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;

namespace GlintLoop.Codec.Project;

/// <summary>読み込んだプロジェクト。Images は文書の assets の順で、SessionImportTarget.OpenDocument に渡す。</summary>
public sealed record GlintProject(SessionDocument Document, IReadOnlyList<ImportedImage> Images)
{
    /// <summary>文書の読み込みで出す通知(stack.unknown_effect・stack.full)。開いたときに通知バーへ出す。</summary>
    public IReadOnlyList<Notice> Notices { get; init; } = Array.Empty<Notice>();
}

public static class GlintProjectFile
{
    public const string FileExtension = ".glint";
    public const string ManifestEntry = "manifest.json";
    public const string SessionEntry = "session.json";
    public const string FormatName = "glint";
    public const string AppName = "GlintLoop";
    public const int FormatVersion = 1;

    /// <summary>エントリーの数の上限(manifest.json・session.json と assets の最大 32 項目)。</summary>
    public const int MaxEntries = 34;

    /// <summary>manifest.json の展開後の大きさの上限。</summary>
    public const long MaxManifestBytes = 65_536;

    /// <summary>session.json の展開後の大きさの上限。</summary>
    public const long MaxSessionBytes = 4_000_000;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>アセットのエントリーの名前。</summary>
    public static string AssetEntryName(string sha256) => "assets/" + sha256 + ".bin";

    /// <summary>
    /// doc を path に保存する。images は SHA-256 → 画像(SessionImportTarget.Images)で、doc.Assets の全部を引けること。.tmp に書いてからリネームする。
    /// 失敗は例外をそのまま投げ、.tmp を残さない。通知と EditorSession.MarkDelivered は呼び出し側が行う。
    /// </summary>
    public static void Save(string path, SessionDocument doc, IReadOnlyDictionary<string, ImportedImage> images, IClock clock, string appVersion, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(appVersion);
        var manifest = WriteManifest(appVersion, clock.UtcNow);
        var session = Encoding.UTF8.GetBytes(DocumentJson.Write(doc));
        var entryTime = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalZone);
        AtomicFile.Write(path, s =>
        {
            using var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true);
            WriteEntry(zip, ManifestEntry, manifest, CompressionLevel.Optimal, entryTime);
            WriteEntry(zip, SessionEntry, session, CompressionLevel.Optimal, entryTime);
            foreach (var a in doc.Assets)
            {
                if (!images.TryGetValue(a.Sha256, out var image)) throw new InvalidOperationException("文書が参照する画像が無い");
                WriteEntry(zip, AssetEntryName(a.Sha256), image.Encoded, CompressionLevel.NoCompression, entryTime);
            }
        }, fileSystem);
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes, CompressionLevel level, DateTimeOffset time)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = time;
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>{"format":"glint","version":1,"app":"GlintLoop","appVersion":"…","createdAt":"…Z"} を UTF-8(BOM なし)で書く。</summary>
    internal static byte[] WriteManifest(string appVersion, DateTimeOffset createdAt)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatName);
            w.WriteNumber("version", FormatVersion);
            w.WriteString("app", AppName);
            w.WriteString("appVersion", appVersion);
            w.WriteString("createdAt", createdAt.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// path を読んで文書と画像を返す。文書には触らない(置き換えは SessionImportTarget.OpenDocument)。§9.11 の 4 の読み込みの規則の順に調べ、
    /// 開けないときは、どの理由でも GlintProjectOpenException(NoticeKey が project.too_new か project.invalid)を投げる。
    /// maxTextureSide は取り込みと同じ GPU の上限。log は文書の読み込みの param_clamped を書く先。hooks はテストの差し込み口。
    /// </summary>
    public static GlintProject Open(string path, IEffectCatalog catalog, ILogSink? log = null, int maxTextureSide = ImageImporter.DefaultMaxTextureSide, IFileSystem? fileSystem = null, ProjectOpenHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            return OpenChecked(path, catalog, log, maxTextureSide, fileSystem ?? RealFileSystem.Instance, hooks ?? ProjectOpenHooks.Default);
        }
        catch (GlintProjectOpenException)
        {
            throw;
        }
        catch (Exception e)
        {
            // ZIP の壊れ方によって ZipArchive と DeflateStream が投げる例外の型はさまざま。読めない・足りない・デコードできないは、すべて同じ通知にする(E-45)
            throw new GlintProjectOpenException(NoticeKeys.ProjectInvalid, "読めない", e);
        }
    }

    private static GlintProject OpenChecked(string path, IEffectCatalog catalog, ILogSink? log, int maxTextureSide, IFileSystem fs, ProjectOpenHooks hooks)
    {
        using var stream = fs.OpenRead(path);
        // (3) の前段: 末尾の記録の総エントリー数を見てから ZipArchive を作る
        if (ProjectZipTail.ReadTotalEntries(stream) is not { } total) throw Invalid("ZIP の末尾の記録が無い");
        if (total > MaxEntries) throw Invalid("エントリーが多い");
        stream.Position = 0;
        using var zip = hooks.OpenZip(stream);

        CheckManifest(ReadCapped(zip, ManifestEntry, MaxManifestBytes));

        var loaded = DocumentJson.Read(Encoding.UTF8.GetString(ReadCapped(zip, SessionEntry, MaxSessionBytes)), catalog, log);
        if (loaded.Document is not { } doc) throw Invalid("session.json を読めない: " + loaded.InvalidReason);

        // エントリーは manifest.json・session.json と assets の項目ごとの 1 つだけ。アセットを開く前に、名前と数、bytes の合計を調べる
        var expected = new HashSet<string>(StringComparer.Ordinal) { ManifestEntry, SessionEntry };
        long declaredBytes = 0;
        foreach (var a in loaded.DeclaredAssets)
        {
            expected.Add(AssetEntryName(a.Sha256));
            declaredBytes += a.Bytes;
        }
        var names = zip.EntryNames;
        if (names.Count != expected.Count || !expected.SetEquals(names)) throw Invalid("エントリーが決まりと違う");
        if (declaredBytes > ImportLimits.MaxTotalBytes) throw Invalid("bytes の合計が上限を超える");

        // 長さと SHA-256 を照合し、ヘッダーで大きさを調べる。デコードは全部の検査のあとで始める(INV-11)
        var sources = new List<(AssetEntry Asset, byte[] Bytes, ImageFormat Format, long Pixels)>(doc.Assets.Count);
        long pixels = 0;
        foreach (var a in doc.Assets)
        {
            var bytes = ReadCapped(zip, AssetEntryName(a.Sha256), a.Bytes);
            if (bytes.LongLength != a.Bytes) throw Invalid("アセットの長さが bytes と違う");
            if (ImportedImage.ComputeSha256(bytes) != a.Sha256) throw Invalid("アセットの SHA-256 が違う");
            var (format, width, height) = ReadAssetHeader(bytes, a);
            if (ImportLimits.CheckSingle(bytes.LongLength, width, height) is not null) throw Invalid("ソースが単独の上限を超える");
            var count = (long)width * height;
            pixels += count;
            if (pixels > ImportLimits.MaxTotalPixels) throw Invalid("画素の合計が上限を超える");
            sources.Add((a, bytes, format, count));
        }

        var images = new List<ImportedImage>(sources.Count);
        foreach (var s in sources) images.Add(hooks.Decode(s.Bytes, s.Format, s.Asset.Sha256, s.Pixels, maxTextureSide));
        return new GlintProject(doc, images) { Notices = loaded.Notices };
    }

    private static GlintProjectOpenException Invalid(string why) => new(NoticeKeys.ProjectInvalid, why);

    // (2): version が 1 より大きいときは project.too_new。format が glint でない、version が 1 以上の整数でないものは、壊れたものとして扱う
    private static void CheckManifest(byte[] manifest)
    {
        using var doc = JsonDocument.Parse(manifest);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("manifest がオブジェクトでない");
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != FormatName)
            throw Invalid("manifest の format が違う");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt64(out var value) || value < 1)
            throw Invalid("manifest の version が 1 以上の整数でない");
        if (value > FormatVersion) throw new GlintProjectOpenException(NoticeKeys.ProjectTooNew, "新しい版のファイル");
    }

    // (3): 固定の名前のエントリーを、展開後の大きさの上限より 1 バイト多く読むところまで読み、超えたら開かない。ヘッダーの大きさは見ない
    private static byte[] ReadCapped(IProjectZip zip, string name, long limit)
    {
        using var s = zip.OpenEntry(name);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var want = (int)Math.Min(buffer.Length, limit + 1 - ms.Length);
            var n = s.Read(buffer, 0, want);
            if (n == 0) return ms.ToArray();
            ms.Write(buffer, 0, n);
            if (ms.Length > limit) throw Invalid("エントリーが展開後の上限を超える");
        }
    }

    // dib は先頭バイトで判定できないので ext で決める。ほかは取り込みと同じく先頭バイトで判定する
    private static (ImageFormat Format, int Width, int Height) ReadAssetHeader(byte[] bytes, AssetEntry asset)
    {
        if (asset.Ext == ImageFormat.Dib.ExtName())
        {
            if (DibReader.ReadHeader(bytes) is not { } dib) throw Invalid("DIB を読めない");
            return (ImageFormat.Dib, dib.Width, dib.Height);
        }
        if (ImageDecoder.ReadHeader(bytes, out _) is not { } header) throw Invalid("ヘッダーを読めない");
        return (header.Format, header.Width, header.Height);
    }
}
