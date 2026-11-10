// 書き出しのキャッシュのキー(§9.9 の 7・D-19): 文書のスナップショット・出力設定・静止形式のフレーム番号・版と本体アセンブリ・描画のアダプター・青色雑音のタイルを
// キーの順を固定した JSON にし、その UTF-8 の SHA-256 の最初の 32 桁(16 進の小文字)にする。レイヤーの名前・選んでいる対象・saltCounter・assets の一覧は入れない。
// 文書の部分は DocumentJson.Write(キーの順が固定、数値は最短表記)の形をそのまま使い、ソース画像はレイヤーの asset(内容の SHA-256)で表す。
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlintLoop.Core.Dither;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

/// <summary>キーの (4)〜(6): 版と本体アセンブリ(Q-H)、描画のアダプターの種類(hardware・warp)と説明、青色雑音のタイルの SHA-256。</summary>
public sealed record ExportEnvironment(BuildIdentity Build, bool IsWarp, string AdapterDescription, string BlueNoiseSha256)
{
    /// <summary>アダプターの種類の文字(§9.9 の 7 の (5))。</summary>
    public string AdapterKind => IsWarp ? "warp" : "hardware";

    /// <summary>埋め込みの青色雑音のタイル(bluenoise64.bin)の SHA-256(16 進の小文字 64 桁)。</summary>
    public static string BlueNoiseTileSha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(BlueNoise.ReadResourceBytes()));
}

public static class ExportCacheKey
{
    /// <summary>キーの桁数(SHA-256 の 16 進の先頭 32 桁)。</summary>
    public const int Length = 32;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>
    /// キー。frameK は静止形式(ExportFormat.IsAnimated が false)のときだけ使う(動く形式では捨てる)。
    /// </summary>
    public static string Of(SessionDocument doc, int frameK, ExportEnvironment env)
    {
        var json = CanonicalJson(doc, frameK, env);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..Length];
    }

    /// <summary>ハッシュにかける JSON(テストで中身を見るため公開)。</summary>
    public static string CanonicalJson(SessionDocument doc, int frameK, ExportEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(env);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WritePropertyName("doc");
            w.WriteRawValue(DocumentJson.Write(Snapshot(doc)), skipInputValidation: true);
            if (!ExportFormat.IsAnimated(doc.Output.Format)) w.WriteNumber("k", frameK);
            w.WriteString("app", env.Build.AppVersion);
            w.WriteStartArray("modules");
            foreach (var m in env.Build.Modules)
            {
                w.WriteStartObject();
                w.WriteString("name", m.Key);
                w.WriteString("mvid", m.Value.ToString("D"));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("adapter");
            w.WriteString("kind", env.AdapterKind);
            w.WriteString("description", env.AdapterDescription);
            w.WriteEndObject();
            w.WriteString("blueNoise", env.BlueNoiseSha256);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>キーに入れる文書(レイヤーの名前を空に、選んでいる対象を全体に、saltCounter を 0 に、assets を空にしたもの)。</summary>
    internal static SessionDocument Snapshot(SessionDocument doc) => doc with
    {
        SaltCounter = 0,
        Selected = Target.All,
        Assets = Array.Empty<AssetEntry>(),
        Layers = doc.Layers.Select(l => l.Name.Length == 0 ? l : l with { Name = "" }).ToArray(),
    };
}
