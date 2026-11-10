// ユーザーのルック(FR-36・§9.11 の 1 と 5)の保存・名前の変更・削除・読み込み。置き場は AppPaths.LooksDir の look-<8桁>.json。
// <8桁> は名前の UTF-8 の SHA-256 の最初の 8 桁(16 進の小文字)で、同じ 8 桁のファイルが既にあれば -2、-3 を付ける。書き込みは AtomicFile(INV-16)。
// 名前は前後の空白を除いて持ち、同名の判定は前後の空白を除いた UTF-8 のバイト列で行う(E-29)。中身は UTF-8・BOM なし・インデント 2。
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Looks;

/// <summary>名前の規則(E-29)。文字はコードポイントで数える。</summary>
public static class LookNames
{
    public const int MaxLength = 40;

    /// <summary>入力欄の文字列を直す: 改行を取り除き、先頭の 40 コードポイントで切る(貼り付けも同じ)。</summary>
    public static string LimitInput(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var n = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (r.Value is '\r' or '\n') continue;
            if (n == MaxLength) break;
            sb.Append(r.ToString());
            n++;
        }
        return sb.ToString();
    }

    /// <summary>持つ名前(前後の空白を除いたもの)。</summary>
    public static string Clean(string name) => name.Trim();

    /// <summary>名前として使えるか(前後の空白を除いて 1〜40 コードポイント、改行なし)。</summary>
    public static bool IsValid(string? name)
    {
        if (name is null) return false;
        var c = Clean(name);
        if (c.Length == 0 || c.Contains('\n') || c.Contains('\r')) return false;
        var n = 0;
        foreach (var _ in c.EnumerateRunes()) n++;
        return n <= MaxLength;
    }

    /// <summary>同じ名前か(前後の空白を除いた UTF-8 のバイト列が同じ)。</summary>
    public static bool Same(string a, string b) => string.Equals(Clean(a), Clean(b), StringComparison.Ordinal);

    /// <summary>名前の UTF-8 のバイト列の SHA-256 の最初の 8 桁(16 進の小文字)。</summary>
    public static string Hash8(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
}

/// <summary>保存してあるユーザーのルック 1 つ。Path はファイルの絶対パス。</summary>
public sealed record UserLook(string Name, string Path, IReadOnlyList<LookEffect> Effects)
{
    /// <summary>ファイルにカタログの無い id の行があって読まなかった(適用のときに stack.unknown_effect を出す。E-24・AC-56 の (3))。</summary>
    public bool HasUnknownEffects { get; init; }

    /// <summary>ギャラリーのタイルのキー(ファイルの名前。拡張子なし)。</summary>
    public string Key => System.IO.Path.GetFileNameWithoutExtension(Path);

    public LookRecipe ToRecipe() => new(Key, Name, false, Effects);
}

public sealed class UserLookStore
{
    public const string FilePrefix = "look-";
    public const string FileExtension = ".json";

    private readonly string _dir;
    private readonly IEffectCatalog _catalog;
    private readonly ILogSink _log;
    private readonly Func<string, string> _hash8;
    private readonly IFileSystem _fs;
    private List<UserLook> _looks = new();

    /// <summary>dir は AppPaths.LooksDir。hash8 は 8 桁を作る関数(テストで衝突を作るときに差し替える。既定は LookNames.Hash8)。</summary>
    public UserLookStore(string dir, IEffectCatalog catalog, ILogSink? log = null, Func<string, string>? hash8 = null, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(catalog);
        _dir = dir;
        _catalog = catalog;
        _log = log ?? NullLogSink.Instance;
        _hash8 = hash8 ?? LookNames.Hash8;
        _fs = fileSystem ?? RealFileSystem.Instance;
    }

    public string Directory => _dir;

    /// <summary>保存してあるルック(名前の順。序数で比べる)。</summary>
    public IReadOnlyList<UserLook> Looks => _looks;

    /// <summary>保存・名前の変更・削除・読み込みのあとに上がる。</summary>
    public event Action? Changed;

    /// <summary>フォルダの look-*.json をすべて読む。JSON として読めないもの・schema が 1 でないもの・名前が不正なものは飛ばす。</summary>
    public void Load()
    {
        var list = new List<UserLook>();
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
                if (Read(_fs.ReadAllBytes(f), f) is { } look && !list.Any(l => LookNames.Same(l.Name, look.Name))) list.Add(look);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        _looks = Sorted(list);
        Changed?.Invoke();
    }

    /// <summary>同じ名前のルック(E-29 の比べ方)。無ければ null。</summary>
    public UserLook? Find(string name) => _looks.FirstOrDefault(l => LookNames.Same(l.Name, name));

    /// <summary>
    /// 保存する。同じ名前のものがあれば、そのファイルの中身を置き換える(上書きの確認は呼び出し側が先に出す)。
    /// 無ければ look-<8桁>.json(衝突したら -2、-3)に新しく書く。名前が不正なら ArgumentException。
    /// </summary>
    public UserLook Save(string name, IReadOnlyList<LookEffect> effects)
    {
        if (!LookNames.IsValid(name)) throw new ArgumentException("名前が 1〜40 文字でない", nameof(name));
        var clean = LookNames.Clean(name);
        var existing = Find(clean);
        var path = existing?.Path ?? NewPath(clean, null);
        var look = new UserLook(clean, path, effects.ToArray());
        AtomicFile.WriteAllBytes(path, Write(look), _fs);
        _looks = Sorted(_looks.Where(l => l.Path != path).Append(look));
        Changed?.Invoke();
        return look;
    }

    /// <summary>
    /// 名前を変える(FR-36)。別のルックと同じ名前にしたときは、その別のものを削除して変更した方を残す(E-29)。
    /// ファイルは新しい名前の 8 桁の名前に書き直し、古いファイルを消す。
    /// </summary>
    public UserLook Rename(UserLook look, string newName)
    {
        if (!LookNames.IsValid(newName)) throw new ArgumentException("名前が 1〜40 文字でない", nameof(newName));
        var clean = LookNames.Clean(newName);
        var current = _looks.FirstOrDefault(l => l.Path == look.Path) ?? throw new InvalidOperationException("ルックが一覧に無い");
        if (current.Name == clean) return current;
        var other = _looks.FirstOrDefault(l => l.Path != current.Path && LookNames.Same(l.Name, clean));
        var list = _looks.ToList();
        if (other is not null)
        {
            TryDelete(other.Path);
            list.Remove(other);
        }
        var path = NewPath(clean, current.Path);
        // 書き直すファイルには読めた行だけが入るので、未知の行はここで無くなる
        var renamed = current with { Name = clean, Path = path, HasUnknownEffects = false };
        AtomicFile.WriteAllBytes(path, Write(renamed), _fs);
        if (path != current.Path) TryDelete(current.Path);
        list.Remove(current);
        list.Add(renamed);
        _looks = Sorted(list);
        Changed?.Invoke();
        return renamed;
    }

    /// <summary>削除する(確認は出さない。§9.3)。</summary>
    public void Delete(UserLook look)
    {
        TryDelete(look.Path);
        _looks = Sorted(_looks.Where(l => l.Path != look.Path));
        Changed?.Invoke();
    }

    /// <summary>名前から新しいファイルのパスを決める。同じ 8 桁のファイルが(keep 以外に)あれば末尾に -2、-3 を付ける。</summary>
    private string NewPath(string name, string? keep)
    {
        var stem = FilePrefix + _hash8(name);
        for (var n = 1; ; n++)
        {
            var file = Path.Combine(_dir, (n == 1 ? stem : stem + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)) + FileExtension);
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) return file;
            var taken = _fs.Exists(file) || _looks.Any(l => string.Equals(l.Path, file, StringComparison.OrdinalIgnoreCase));
            if (!taken) return file;
        }
    }

    private static List<UserLook> Sorted(IEnumerable<UserLook> looks) =>
        looks.OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Path, StringComparer.Ordinal).ToList();

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

    // ---------------------------------------------------------------- ファイルの形(§9.11 の 5)

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        IndentCharacter = ' ',
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>{"schema":1,"name":"…","effects":[{"id":"…","amount":100,"params":{…}}, …]} を UTF-8(BOM なし)で書く。</summary>
    public static byte[] Write(UserLook look)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("name", look.Name);
            w.WriteStartArray("effects");
            foreach (var e in look.Effects)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteNumber("amount", Zero(e.Amount));
                w.WriteStartObject("params");
                foreach (var kv in e.Params)
                {
                    w.WritePropertyName(kv.Key);
                    WriteParam(w, kv.Value);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static double Zero(double v) => v == 0 ? 0 : v;

    private static void WriteParam(Utf8JsonWriter w, ParamValue v)
    {
        switch (v)
        {
            case NumberValue n:
                w.WriteNumberValue(Zero(n.Value));
                break;
            case BoolValue b:
                w.WriteBooleanValue(b.Value);
                break;
            case StringValue s:
                w.WriteStringValue(s.Value);
                break;
            case PointValue p:
                w.WriteStartArray();
                w.WriteNumberValue(Zero(p.X));
                w.WriteNumberValue(Zero(p.Y));
                w.WriteEndArray();
                break;
            case RectValue r:
                w.WriteStartObject();
                w.WriteNumber("l", Zero(r.L));
                w.WriteNumber("t", Zero(r.T));
                w.WriteNumber("r", Zero(r.R));
                w.WriteNumber("b", Zero(r.B));
                w.WriteEndObject();
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// ファイルの中身を読む。読めないときは null。カタログに無い id とルック・仕上げ以外のステージのエフェクトは外し、
    /// パラメータは宣言で直す(範囲外を直したときは param_clamped を書く。§9.11 の 1)。amount は 0〜100 に直す(欠けは 100)。
    /// </summary>
    public UserLook? Read(byte[] bytes, string path)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var sv) || sv != 1)
                return null;
            if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
            var name = nameEl.GetString() ?? "";
            if (!LookNames.IsValid(name)) return null;
            var effects = new List<LookEffect>();
            var unknown = false;
            if (root.TryGetProperty("effects", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                    var def = _catalog.Find(idEl.GetString() ?? "");
                    if (def is null) unknown = true;
                    if (def is null || !LookStacks.IsLookStage(def)) continue;
                    double amount = 100;
                    if (e.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number && am.TryGetDouble(out var a))
                    {
                        var c = DocumentRules.Clamp(a, 0, 100);
                        if (c != a || double.IsNaN(a)) _log.Write(LogEvents.ParamClamped(def.Id, "amount"));
                        amount = double.IsNaN(a) ? 100 : c;
                    }
                    var hasParams = e.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object;
                    var all = EffectSchema.AllParams(def);
                    var values = new KeyValuePair<string, ParamValue>[all.Count];
                    for (var i = 0; i < all.Count; i++)
                    {
                        var p = all[i];
                        ParamValue? raw = null;
                        if (hasParams && ps.TryGetProperty(p.Key, out var pv)) raw = DocumentJson.FromJson(p, pv);
                        values[i] = new(p.Key, ParamRules.Normalize(def.Id, p, raw, _log));
                    }
                    effects.Add(new LookEffect(def.Id, amount, ParamRules.ApplyConstraints(def.Id, values, null, null)));
                }
            }
            return new UserLook(LookNames.Clean(name), path, effects) { HasUnknownEffects = unknown };
        }
    }
}
