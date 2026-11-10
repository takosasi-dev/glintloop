// 書き出しプリセット(FR-77・AC-38 の (8))の保存・読み込み・名前の変更・削除。置き場は AppPaths.PresetsDir の export-<8桁>.json、中身は {"schema":1,"name":…,"output":{…}}。
// <8桁> と名前の規則はユーザーのルックと同じ(LookNames。名前の UTF-8 の SHA-256 の先頭 8 桁、衝突は -2・-3、前後の空白を除いて 1〜40 文字)。書き込みは AtomicFile(INV-16)。
// 組み込みの容量上限のプリセット(§9.9 の 2)はこのストアに入らないので、名前の変更も削除もできない。
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

/// <summary>保存してある書き出しプリセット 1 つ。Path はファイルの絶対パス。</summary>
public sealed record UserExportPreset(string Name, string Path, OutputSettings Output);

public sealed class UserExportPresetStore
{
    public const string FilePrefix = "export-";
    public const string FileExtension = ".json";

    private readonly string _dir;
    private readonly Func<string, string> _hash8;
    private readonly IFileSystem _fs;
    private List<UserExportPreset> _presets = new();

    /// <summary>dir は AppPaths.PresetsDir。hash8 は 8 桁を作る関数(テストで衝突を作るときに差し替える。既定は LookNames.Hash8)。</summary>
    public UserExportPresetStore(string dir, Func<string, string>? hash8 = null, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        _dir = dir;
        _hash8 = hash8 ?? LookNames.Hash8;
        _fs = fileSystem ?? RealFileSystem.Instance;
    }

    public string Directory => _dir;

    /// <summary>保存してあるプリセット(名前の順。序数で比べる)。</summary>
    public IReadOnlyList<UserExportPreset> Presets => _presets;

    /// <summary>保存・名前の変更・削除・読み込みのあとに上がる。</summary>
    public event Action? Changed;

    /// <summary>フォルダの export-*.json をすべて読む。読めないもの・schema が 1 でないもの・名前が不正なもの・同じ名前の 2 つ目は飛ばす。</summary>
    public void Load()
    {
        var list = new List<UserExportPreset>();
        string[] files;
        try
        {
            files = _fs.DirectoryExists(_dir) ? _fs.EnumerateFiles(_dir, FilePrefix + "*" + FileExtension).ToArray() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = [];
        }
        foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (Read(_fs.ReadAllBytes(f), f) is { } p &&!list.Any(l => LookNames.Same(l.Name, p.Name))) list.Add(p);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        _presets = Sorted(list);
        Changed?.Invoke();
    }

    /// <summary>同じ名前のプリセット(前後の空白を除いて比べる)。無ければ null。</summary>
    public UserExportPreset? Find(string name) => _presets.FirstOrDefault(p => LookNames.Same(p.Name, name));

    /// <summary>
    /// 保存する。同じ名前のものがあればそのファイルの中身を置き換える(上書きの確認は呼び出し側が先に出す)。
    /// 無ければ export-<8桁>.json(衝突したら -2、-3)に新しく書く。名前が不正なら ArgumentException。
    /// </summary>
    public UserExportPreset Save(string name, OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!LookNames.IsValid(name)) throw new ArgumentException("名前が 1〜40 文字でない", nameof(name));
        var clean = LookNames.Clean(name);
        var existing = Find(clean);
        var path = existing?.Path ?? NewPath(clean, null);
        var preset = new UserExportPreset(clean, path, OutputSettingsJson.Normalize(output));
        AtomicFile.WriteAllBytes(path, Write(preset), _fs);
        _presets = Sorted(_presets.Where(p => p.Path != path).Append(preset));
        Changed?.Invoke();
        return preset;
    }

    /// <summary>
    /// 名前を変える。別のプリセットと同じ名前にしたときは、その別のものを削除して変更した方を残す(ルックと同じ扱い)。
    /// ファイルは新しい名前の 8 桁の名前に書き直し、古いファイルを消す。
    /// </summary>
    public UserExportPreset Rename(UserExportPreset preset, string newName)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!LookNames.IsValid(newName)) throw new ArgumentException("名前が 1〜40 文字でない", nameof(newName));
        var clean = LookNames.Clean(newName);
        var current = _presets.FirstOrDefault(p => p.Path == preset.Path) ?? throw new InvalidOperationException("プリセットが一覧に無い");
        if (current.Name == clean) return current;
        var list = _presets.ToList();
        var other = list.FirstOrDefault(p => p.Path != current.Path && LookNames.Same(p.Name, clean));
        if (other is not null)
        {
            TryDelete(other.Path);
            list.Remove(other);
        }
        var path = NewPath(clean, current.Path, list);
        var renamed = current with { Name = clean, Path = path };
        AtomicFile.WriteAllBytes(path, Write(renamed), _fs);
        if (path != current.Path) TryDelete(current.Path);
        list.Remove(current);
        list.Add(renamed);
        _presets = Sorted(list);
        Changed?.Invoke();
        return renamed;
    }

    /// <summary>削除する(確認は出さない。§9.3)。</summary>
    public void Delete(UserExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        TryDelete(preset.Path);
        _presets = Sorted(_presets.Where(p => p.Path != preset.Path));
        Changed?.Invoke();
    }

    private string NewPath(string name, string? keep, IReadOnlyList<UserExportPreset>? known = null)
    {
        known ??= _presets;
        var stem = FilePrefix + _hash8(name);
        for (var n = 1; ; n++)
        {
            var file = Path.Combine(_dir, (n == 1 ? stem : stem + "-" + n.ToString(CultureInfo.InvariantCulture)) + FileExtension);
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) return file;
            var taken = _fs.Exists(file) || known.Any(p => string.Equals(p.Path, file, StringComparison.OrdinalIgnoreCase));
            if (!taken) return file;
        }
    }

    private static List<UserExportPreset> Sorted(IEnumerable<UserExportPreset> presets) =>
        presets.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Path, StringComparer.Ordinal).ToList();

    private void TryDelete(string path)
    {
        try
        {
            if (_fs.Exists(path)) _fs.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- ファイルの形

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        IndentCharacter = ' ',
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>{"schema":1,"name":"…","output":{…}} を UTF-8(BOM なし)で書く。</summary>
    public static byte[] Write(UserExportPreset preset)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("name", preset.Name);
            w.WritePropertyName("output");
            OutputSettingsJson.Write(w, preset.Output);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>ファイルの中身を読む。読めないとき・schema が 1 でないとき・名前が不正なとき・output が無いときは null。</summary>
    public static UserExportPreset? Read(byte[] bytes, string path)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schema", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt32(out var sv) || sv != 1) return null;
            if (!root.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return null;
            var name = n.GetString() ?? "";
            if (!LookNames.IsValid(name)) return null;
            if (!root.TryGetProperty("output", out var o) || OutputSettingsJson.Read(o) is not { } output) return null;
            return new UserExportPreset(LookNames.Clean(name), path, output);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
