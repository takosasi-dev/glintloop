// 文書(EditorSession)の JSON の書き出しと読み込み(仕様書 §9.11 の 3。Undo の履歴・プロジェクト・退避が共有する形)。
// 書き出しはキーの順を固定し、読み込みは表のとおりに直す(欠けは既定、未知のキーは捨てる、範囲外は端、param_clamped)。
// project.invalid に当たる文書は、DocumentLoadResult の InvalidReason を返して読み込まない(通知は呼び出し側が出す)。
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Session;

/// <summary>読み込みの結果。Document が null のときは project.invalid(InvalidReason に理由)。</summary>
public sealed record DocumentLoadResult(SessionDocument? Document, string? InvalidReason, IReadOnlyList<Notice> Notices)
{
    public bool IsValid => Document is not null;

    /// <summary>session.json の assets に書かれていた項目のすべて(layers が使わないものを含む)。.glint のエントリーの数と bytes の合計の検査に使う。</summary>
    public IReadOnlyList<AssetEntry> DeclaredAssets { get; init; } = Array.Empty<AssetEntry>();
}

public static class DocumentJson
{
    /// <summary>ログの event 名(§9.7 の規則 4 の 5、§9.11 の 3)。</summary>
    public const string ParamClampedEvent = "param_clamped";

    /// <summary>assets の ext の列挙(§9.11 の 4)。</summary>
    public static readonly IReadOnlyList<string> AssetExts = ["png", "jpg", "webp", "bmp", "ico", "gif", "dib"];

    /// <summary>fps の選択肢(FR-58)。</summary>
    public static readonly IReadOnlyList<int> FpsChoices = [5, 8, 10, 12, 15, 20, 24, 25, 30, 40, 50];

    public const long MaxAssetBytes = 100_000_000;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    // ---------------------------------------------------------------- 書き出し

    /// <summary>文書を JSON にする。キーの順は §9.11 の 3 の例と同じに固定する。</summary>
    public static string Write(SessionDocument doc)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteNumber("seed", doc.Seed);
            w.WriteNumber("saltCounter", doc.SaltCounter);
            WriteTime(w, doc.Time);
            if (doc.Selected.IsAll) w.WriteString("selected", "all");
            else w.WriteNumber("selected", doc.Selected.LayerIndex);
            w.WriteStartArray("assets");
            foreach (var a in doc.Assets)
            {
                w.WriteStartObject();
                w.WriteString("sha256", a.Sha256);
                w.WriteString("ext", a.Ext);
                w.WriteNumber("bytes", a.Bytes);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("layers");
            for (var i = 0; i < doc.Layers.Count; i++) WriteLayer(w, doc.Layers[i], i == 0);
            w.WriteEndArray();
            w.WriteStartObject("all");
            w.WriteNumber("lookStrength", doc.All.LookStrength);
            WriteStack(w, doc.All.Stack);
            w.WriteEndObject();
            WriteOutput(w, doc.Output, doc.Time.Loop);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTime(Utf8JsonWriter w, TimeSettings t)
    {
        w.WriteStartObject("time");
        w.WritePropertyName("length");
        w.WriteRawValue(
            (t.Length10 / 10).ToString(CultureInfo.InvariantCulture) + "." + (t.Length10 % 10).ToString(CultureInfo.InvariantCulture));
        w.WriteNumber("fps", t.Fps);
        w.WriteString("loop", t.Loop == LoopMode.Once ? "once" : "infinite");
        w.WriteNumber("holdMs", t.HoldMs);
        w.WriteEndObject();
    }

    private static void WriteLayer(Utf8JsonWriter w, Layer l, bool isBase)
    {
        w.WriteStartObject();
        w.WriteString("name", l.Name);
        if (l.Kind == LayerKind.Image)
        {
            w.WriteString("kind", "image");
            w.WriteString("asset", l.Asset);
        }
        else
        {
            var size = l.Size ?? DocumentRules.DefaultEmptySize;
            w.WriteString("kind", "empty");
            w.WriteStartObject("size");
            w.WriteNumber("w", size.W);
            w.WriteNumber("h", size.H);
            w.WriteEndObject();
            w.WriteString("fill", l.Fill ?? ColorText.Transparent);
        }
        w.WriteBoolean("visible", l.Visible);
        w.WriteNumber("opacity", l.Opacity);
        w.WriteString("blend", DocumentRules.BlendName(isBase ? BlendMode.Normal : l.Blend));
        var p = isBase ? Placement.Identity : l.Place;
        w.WriteStartObject("place");
        WriteNumber(w, "x", p.X);
        WriteNumber(w, "y", p.Y);
        WriteNumber(w, "scale", p.Scale);
        w.WriteEndObject();
        w.WriteNumber("lookStrength", l.LookStrength);
        WriteStack(w, l.Stack);
        w.WriteEndObject();
    }

    private static void WriteStack(Utf8JsonWriter w, IReadOnlyList<StackEntry> stack)
    {
        w.WriteStartArray("stack");
        foreach (var e in stack)
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            w.WriteBoolean("enabled", e.Enabled);
            w.WriteString("origin", e.Origin == EffectOrigin.Look ? "look" : "user");
            w.WriteNumber("salt", e.Salt);
            WriteNumber(w, "amount", e.Amount);
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
    }

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
                WriteNumber(w, "l", r.L);
                WriteNumber(w, "t", r.T);
                WriteNumber(w, "r", r.R);
                WriteNumber(w, "b", r.B);
                w.WriteEndObject();
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }

    private static void WriteOutput(Utf8JsonWriter w, OutputSettings o, LoopMode loop)
    {
        w.WritePropertyName("output");
        WriteOutputObject(w, o, loop == LoopMode.Infinite ? 0 : o.Apng.Loops);
    }

    /// <summary>output のオブジェクトを書く(文書と書き出しプリセットで同じ形。apng.loops の値だけ呼ぶ側が決める)。</summary>
    internal static void WriteOutputObject(Utf8JsonWriter w, OutputSettings o, long apngLoops)
    {
        w.WriteStartObject();
        w.WriteString("format", o.Format);
        w.WriteStartObject("size");
        w.WriteString("mode", o.Size.Mode);
        if (o.Size.Mode != "auto" && o.Size.Value is double v) WriteNumber(w, "value", v);
        w.WriteEndObject();
        w.WriteStartObject("limit");
        w.WriteString("kind", o.Limit.Kind);
        if (o.Limit.Kind == "preset" && o.Limit.Key is not null) w.WriteString("key", o.Limit.Key);
        if (o.Limit.Kind == "bytes" && o.Limit.Bytes is long bytes) w.WriteNumber("bytes", bytes);
        w.WriteEndObject();
        w.WriteStartObject("options");
        w.WriteStartObject("gif");
        w.WriteNumber("colors", o.Gif.Colors);
        w.WriteString("dither", o.Gif.Dither);
        if (o.Gif.Matte is null) w.WriteNull("matte");
        else w.WriteString("matte", o.Gif.Matte);
        w.WriteBoolean("diff", o.Gif.Diff);
        w.WriteEndObject();
        w.WriteStartObject("png");
        w.WriteEndObject();
        w.WriteStartObject("jpeg");
        w.WriteNumber("quality", o.Jpeg.Quality);
        w.WriteString("background", o.Jpeg.Background);
        w.WriteEndObject();
        w.WriteStartObject("webp");
        w.WriteNumber("quality", o.Webp.Quality);
        w.WriteBoolean("lossless", o.Webp.Lossless);
        w.WriteEndObject();
        w.WriteStartObject("apng");
        w.WriteBoolean("palette", o.Apng.Palette);
        w.WriteNumber("loops", apngLoops);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>output のオブジェクトを読む(欠けは既定。文書と書き出しプリセットで同じ読み方)。</summary>
    internal static OutputSettings ReadOutputObject(JsonElement o)
    {
        var d = OutputSettings.Default;
        var format = GetString(o, "format") is { } name && ExportFormat.All.Contains(name) ? name : d.Format;

        var size = d.Size;
        if (TryGet(o, "size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Object)
        {
            var mode = GetString(sizeEl, "mode");
            if (mode is "long" or "width" or "height" or "scale" && TryNumber(sizeEl, "value", out var v)) size = new OutputSize(mode, v);
            else size = new OutputSize("auto", null);
        }

        var limit = d.Limit;
        if (TryGet(o, "limit", out var limEl) && limEl.ValueKind == JsonValueKind.Object)
        {
            var kind = GetString(limEl, "kind");
            var key = GetString(limEl, "key");
            if (kind == "preset" && key is not null) limit = new OutputLimit("preset", key, null);
            else if (kind == "bytes" && TryGet(limEl, "bytes", out var bEl) && bEl.ValueKind == JsonValueKind.Number && bEl.TryGetInt64(out var b))
                limit = new OutputLimit("bytes", null, b);
            else limit = new OutputLimit("none", null, null);
        }

        var gif = d.Gif;
        var jpeg = d.Jpeg;
        var webp = d.Webp;
        var apng = d.Apng;
        if (TryGet(o, "options", out var opt) && opt.ValueKind == JsonValueKind.Object)
        {
            if (TryGet(opt, "gif", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                var dither = GetString(g, "dither");
                gif = new GifOptions(
                    GetInt(g, "colors", d.Gif.Colors),
                    dither is "blue" or "error" or "none" ? dither : d.Gif.Dither,
                    GetString(g, "matte"),
                    GetBool(g, "diff", d.Gif.Diff));
            }
            if (TryGet(opt, "jpeg", out var j) && j.ValueKind == JsonValueKind.Object)
                jpeg = new JpegOptions(GetInt(j, "quality", d.Jpeg.Quality), GetString(j, "background") ?? d.Jpeg.Background);
            if (TryGet(opt, "webp", out var wp) && wp.ValueKind == JsonValueKind.Object)
                webp = new WebpOptions(GetInt(wp, "quality", d.Webp.Quality), GetBool(wp, "lossless", d.Webp.Lossless));
            if (TryGet(opt, "apng", out var ap) && ap.ValueKind == JsonValueKind.Object)
            {
                var loops = Math.Clamp(GetInt(ap, "loops", d.Apng.Loops), 1, 4);
                apng = new ApngOptions(GetBool(ap, "palette", d.Apng.Palette), loops);
            }
        }
        return new OutputSettings(format, size, limit, gif, jpeg, webp, apng);
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, Zero(v));

    /// <summary>−0 を 0 にする(書き出しを決定的にするため)。</summary>
    private static double Zero(double v) => v == 0 ? 0 : v;

    // ---------------------------------------------------------------- 読み込み

    /// <summary>
    /// JSON を読んで文書にする。直した項目は log に param_clamped を書く。stack の未対応の行と 64 個を超えた分は
    /// Notices(stack.unknown_effect・stack.full)で返す。
    /// </summary>
    public static DocumentLoadResult Read(string json, IEffectCatalog catalog, ILogSink? log = null)
    {
        log ??= NullLogSink.Instance;
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Invalid("JSON として読めない");
        }
        using (parsed)
        {
            return new Reader(catalog, log).Read(parsed.RootElement);
        }
    }

    private static DocumentLoadResult Invalid(string reason) => new(null, reason, Array.Empty<Notice>());

    private sealed class Reader(IEffectCatalog catalog, ILogSink log)
    {
        private bool _unknownEffect;
        private bool _stackFull;
        private long _maxSalt;

        private void Clamped(string effect, string key) =>
            log.Write(LogEvent.Of(ParamClampedEvent, ("effect", effect), ("key", key)));

        public DocumentLoadResult Read(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return Invalid("文書がオブジェクトでない");
            if (!TryGet(root, "schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt64(out var schemaValue) || schemaValue != 1)
                return Invalid("schema が 1 でない");
            if (!TryGet(root, "layers", out var layersEl) || layersEl.ValueKind != JsonValueKind.Array)
                return Invalid("layers が配列でない");
            var layerCount = layersEl.GetArrayLength();
            if (layerCount == 0) return Invalid("layers が空");
            if (layerCount > DocumentRules.MaxLayers) return Invalid("layers が 33 個以上");

            // assets の検査
            var assets = new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
            if (TryGet(root, "assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assetsEl.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) return Invalid("assets の項目がオブジェクトでない");
                    var sha = GetString(a, "sha256");
                    if (sha is null || !IsSha256(sha)) return Invalid("assets の sha256 が小文字の 16 進 64 桁でない");
                    if (assets.ContainsKey(sha)) return Invalid("assets の sha256 が 2 回出る");
                    var ext = GetString(a, "ext");
                    if (ext is null || !AssetExts.Contains(ext)) return Invalid("assets の ext が列挙に無い");
                    if (!TryGet(a, "bytes", out var bytesEl) || bytesEl.ValueKind != JsonValueKind.Number ||
                        !bytesEl.TryGetInt64(out var bytes) || bytes < 1 || bytes > MaxAssetBytes)
                        return Invalid("assets の bytes が 1〜100,000,000 の整数でない");
                    assets[sha] = new AssetEntry(sha, ext, bytes);
                }
            }

            // layers の kind と asset の検査(直しより先に、開かない条件をすべて見る)
            foreach (var l in layersEl.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Object) return Invalid("layers の項目がオブジェクトでない");
                var kind = GetString(l, "kind");
                if (kind is not ("image" or "empty")) return Invalid("kind が image と empty のどちらでもない");
                if (kind == "image")
                {
                    var asset = GetString(l, "asset");
                    if (asset is null) return Invalid("image のレイヤーの asset が無い");
                    if (!assets.ContainsKey(asset)) return Invalid("image のレイヤーの asset が assets に無い");
                }
            }

            var layerDrafts = new List<(Layer Layer, List<StackDraft> Stack)>();
            var index = 0;
            foreach (var l in layersEl.EnumerateArray())
            {
                layerDrafts.Add(ReadLayer(l, index == 0));
                index++;
            }

            var allLook = 100;
            var allStack = new List<StackDraft>();
            if (TryGet(root, "all", out var allEl) && allEl.ValueKind == JsonValueKind.Object)
            {
                allLook = ReadPercent(allEl, "lookStrength", "all", "lookStrength");
                allStack = ReadStack(allEl, isAll: true);
            }

            // saltCounter: 読み込んだ salt の最大値以上。salt が欠けた行には、そのあとに続けて新しい salt を振る(仮の決め)。
            long saltCounter = 0;
            if (TryNumber(root, "saltCounter", out var sc) && double.IsFinite(sc)) saltCounter = (long)Math.Max(0, Math.Floor(sc));
            if (saltCounter < _maxSalt) saltCounter = _maxSalt;
            var layers = new List<Layer>(layerDrafts.Count);
            foreach (var (layer, drafts) in layerDrafts) layers.Add(layer with { Stack = Finish(drafts, ref saltCounter) });
            var all = new GlobalStack(allLook, Finish(allStack, ref saltCounter));

            var seed = 0;
            if (TryNumber(root, "seed", out var seedValue))
                seed = (int)DocumentRules.Clamp(MathX.RoundHalfUp(seedValue), 0, DocumentRules.MaxSeed);

            var selected = Target.OfLayer(layers.Count - 1);
            if (TryGet(root, "selected", out var selEl))
            {
                if (selEl.ValueKind == JsonValueKind.String && selEl.GetString() == "all") selected = Target.All;
                else if (selEl.ValueKind == JsonValueKind.Number)
                {
                    if (selEl.TryGetInt32(out var si) && si >= 0 && si < layers.Count) selected = Target.OfLayer(si);
                    else Clamped("layer", "selected");
                }
            }

            var time = ReadTime(root);
            var output = ReadOutput(root);

            var notices = new List<Notice>();
            if (_unknownEffect) notices.Add(Notice.Of(NoticeKeys.StackUnknownEffect, NoticeLevel.Warn));
            if (_stackFull) notices.Add(Notice.Of(NoticeKeys.StackFull, NoticeLevel.Warn));

            var doc = new SessionDocument(seed, saltCounter, time, selected, Array.Empty<AssetEntry>(), layers, all, output);
            doc = DocumentRules.Normalize(doc, assets);
            return new DocumentLoadResult(doc, null, notices) { DeclaredAssets = assets.Values.ToArray() };
        }

        private (Layer, List<StackDraft>) ReadLayer(JsonElement l, bool isBase)
        {
            var kind = GetString(l, "kind") == "image" ? LayerKind.Image : LayerKind.Empty;
            var name = DocumentRules.DefaultName(kind);
            if (TryGet(l, "name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var s = nameEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(s)) Clamped("layer", "name");
                else if (s.Length > DocumentRules.MaxNameLength)
                {
                    name = DocumentRules.Truncate(s);
                    Clamped("layer", "name");
                }
                else name = s;
            }

            string? asset = null;
            EmptySize? size = null;
            string? fill = null;
            if (kind == LayerKind.Image) asset = GetString(l, "asset");
            else
            {
                var w = DocumentRules.DefaultEmptySize.W;
                var h = DocumentRules.DefaultEmptySize.H;
                if (TryGet(l, "size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Object)
                {
                    w = ReadSide(sizeEl, "w", w, "size.w");
                    h = ReadSide(sizeEl, "h", h, "size.h");
                }
                size = new EmptySize(w, h);
                fill = ColorText.TryNormalizeRgba(GetString(l, "fill"), out var f) ? f : ColorText.Transparent;
            }

            var visible = GetBool(l, "visible", true);
            var opacity = ReadPercent(l, "opacity", "layer", "opacity");

            var blend = BlendMode.Normal;
            var place = Placement.Identity;
            if (!isBase)
            {
                if (TryGet(l, "blend", out var blendEl) && blendEl.ValueKind == JsonValueKind.String)
                {
                    if (!DocumentRules.TryParseBlend(blendEl.GetString() ?? "", out blend)) Clamped("layer", "blend");
                }
                if (TryGet(l, "place", out var placeEl) && placeEl.ValueKind == JsonValueKind.Object)
                {
                    var x = ReadPlace(placeEl, "x", 0, 2, -DocumentRules.PlaceLimit, DocumentRules.PlaceLimit);
                    var y = ReadPlace(placeEl, "y", 0, 2, -DocumentRules.PlaceLimit, DocumentRules.PlaceLimit);
                    var sc = ReadPlace(placeEl, "scale", 100, 1, DocumentRules.MinScale, DocumentRules.MaxScale);
                    place = new Placement(x, y, sc);
                }
            }
            var look = ReadPercent(l, "lookStrength", "layer", "lookStrength");
            var stack = ReadStack(l, isAll: false);
            var layer = new Layer(name, kind, asset, size, fill, visible, opacity, blend, place, look, Array.Empty<StackEntry>());
            return (layer, stack);
        }

        private int ReadSide(JsonElement obj, string key, int fallback, string logKey)
        {
            if (!TryNumber(obj, key, out var v)) return fallback;
            var r = MathX.RoundHalfUp(v);
            var c = DocumentRules.Clamp(r, DocumentRules.MinEmptySide, DocumentRules.MaxEmptySide);
            if (c != r) Clamped("layer", logKey);
            return (int)c;
        }

        private double ReadPlace(JsonElement obj, string key, double fallback, int digits, double min, double max)
        {
            if (!TryNumber(obj, key, out var v)) return fallback;
            var r = DocumentRules.RoundTo(v, digits);
            var c = DocumentRules.Clamp(r, min, max);
            if (c != r) Clamped("layer", "place." + key);
            return c == 0 ? 0 : c;
        }

        private int ReadPercent(JsonElement obj, string key, string logEffect, string logKey)
        {
            if (!TryNumber(obj, key, out var v)) return 100;
            var r = MathX.RoundHalfUp(v);
            var c = DocumentRules.Clamp(r, 0, 100);
            if (c != r) Clamped(logEffect, logKey);
            return (int)c;
        }

        private List<StackDraft> ReadStack(JsonElement owner, bool isAll)
        {
            var drafts = new List<StackDraft>();
            if (!TryGet(owner, "stack", out var stackEl) || stackEl.ValueKind != JsonValueKind.Array) return drafts;
            var fixedSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in stackEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    _unknownEffect = true;
                    continue;
                }
                var id = GetString(e, "id");
                var def = id is null ? null : catalog.Find(id);
                if (def is null || id is null)
                {
                    _unknownEffect = true;
                    continue;
                }
                if (EffectSchema.IsFixedShape(id) && (isAll || !fixedSeen.Add(id)))
                {
                    // 全体の回転・切り抜き・余白(E-24)と、同じ固定の行の 2 個目(仮の決め)は読み込まない。
                    _unknownEffect = true;
                    continue;
                }
                var enabled = GetBool(e, "enabled", true);
                var origin = GetString(e, "origin") == "look" ? EffectOrigin.Look : EffectOrigin.User;
                long? salt = null;
                if (TryGet(e, "salt", out var saltEl) && saltEl.ValueKind == JsonValueKind.Number && saltEl.TryGetInt64(out var sv) && sv >= 0)
                {
                    salt = sv;
                    if (sv > _maxSalt) _maxSalt = sv;
                }
                double amount = 100;
                if (TryNumber(e, "amount", out var av))
                {
                    var c = DocumentRules.Clamp(av, 0, 100);
                    if (c != av) Clamped(id, "amount");
                    amount = c;
                }
                TryGet(e, "params", out var paramsEl);
                var values = ReadParams(def, paramsEl.ValueKind == JsonValueKind.Object ? paramsEl : (JsonElement?)null);
                drafts.Add(new StackDraft(def, enabled, origin, salt, amount, values));
            }
            return drafts;
        }

        private IReadOnlyList<KeyValuePair<string, ParamValue>> ReadParams(EffectDef def, JsonElement? obj)
        {
            var all = EffectSchema.AllParams(def);
            var list = new KeyValuePair<string, ParamValue>[all.Count];
            for (var i = 0; i < all.Count; i++)
            {
                var p = all[i];
                ParamValue? raw = null;
                if (obj is JsonElement o && TryGet(o, p.Key, out var el)) raw = FromJson(p, el);
                list[i] = new(p.Key, ParamRules.Normalize(def.Id, p, raw, log));
            }
            return ParamRules.ApplyConstraints(def.Id, list, null, null);
        }

        private IReadOnlyList<StackEntry> Finish(List<StackDraft> drafts, ref long saltCounter)
        {
            var ordered = drafts.Select((d, i) => (d, i)).OrderBy(x => EffectSchema.OrderKey(x.d.Def)).ThenBy(x => x.i)
                .Select(x => x.d).ToList();
            if (ordered.Count > EffectSchema.MaxStackEntries)
            {
                ordered.RemoveRange(EffectSchema.MaxStackEntries, ordered.Count - EffectSchema.MaxStackEntries);
                _stackFull = true;
            }
            var result = new StackEntry[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                var d = ordered[i];
                var salt = d.Salt ?? ++saltCounter;
                result[i] = new StackEntry(d.Def.Id, d.Enabled, d.Origin, salt, d.Amount, d.Params);
            }
            return result;
        }

        private static TimeSettings ReadTime(JsonElement root)
        {
            var t = TimeSettings.Default;
            if (!TryGet(root, "time", out var el) || el.ValueKind != JsonValueKind.Object) return t;
            var l10 = t.Length10;
            // FR-58: 0.1 秒刻みに round(0.05 ちょうどは大きい方へ)、範囲外は端(5〜100)。入力欄と同じ TimeInput の規則
            if (TryGet(el, "length", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number)
            {
                if (lenEl.TryGetDecimal(out var len)) l10 = TimeInput.Length10Of(len);
                else if (lenEl.TryGetDouble(out var lv) && double.IsFinite(lv)) l10 = TimeInput.Length10Of(lv > 0 ? decimal.MaxValue : decimal.MinValue);
            }
            var fps = t.Fps;
            if (TryGet(el, "fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number && fpsEl.TryGetInt32(out var f) && FpsChoices.Contains(f))
                fps = f;
            var loop = GetString(el, "loop") == "once" ? LoopMode.Once : LoopMode.Infinite;
            var hold = t.HoldMs;
            // FR-61・Q-G: 100 ms 刻みへ round(50 ちょうどは大きい方へ)、範囲外は 0〜5000 の端(入力欄と同じ TimeInput の規則)
            if (TryGet(el, "holdMs", out var holdEl) && holdEl.ValueKind == JsonValueKind.Number)
            {
                if (holdEl.TryGetDecimal(out var hd)) hold = TimeInput.HoldMsOf(hd);
                else if (holdEl.TryGetDouble(out var hv) && double.IsFinite(hv)) hold = TimeInput.HoldMsOf(hv > 0 ? decimal.MaxValue : decimal.MinValue);
            }
            return new TimeSettings(l10, fps, loop, hold);
        }

        private static OutputSettings ReadOutput(JsonElement root) =>
            TryGet(root, "output", out var o) && o.ValueKind == JsonValueKind.Object ? ReadOutputObject(o) : OutputSettings.Default;
    }

    private sealed record StackDraft(
        EffectDef Def, bool Enabled, EffectOrigin Origin, long? Salt, double Amount, IReadOnlyList<KeyValuePair<string, ParamValue>> Params);

    /// <summary>JSON の値を、パラメータの型の値にする。型が違うときは null(既定値になる)。</summary>
    internal static ParamValue? FromJson(ParamDef def, JsonElement el)
    {
        switch (def.Type)
        {
            case ParamType.Float:
            case ParamType.Int:
            case ParamType.Angle:
                return el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d) ? new NumberValue(d) : null;
            case ParamType.Bool:
                return el.ValueKind switch
                {
                    JsonValueKind.True => new BoolValue(true),
                    JsonValueKind.False => new BoolValue(false),
                    _ => null,
                };
            case ParamType.Enum:
            case ParamType.Color:
            case ParamType.Text:
            case ParamType.Font:
                return el.ValueKind == JsonValueKind.String ? new StringValue(el.GetString() ?? "") : null;
            case ParamType.Point:
            {
                if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 2) return null;
                var x = el[0];
                var y = el[1];
                if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number) return null;
                if (!x.TryGetDouble(out var px) || !y.TryGetDouble(out var py)) return null;
                return new PointValue(px, py);
            }
            case ParamType.Rect:
            {
                if (el.ValueKind != JsonValueKind.Object) return null;
                var dr = def.Default as RectValue ?? new RectValue(0, 0, 0, 0);
                return new RectValue(
                    TryNumber(el, "l", out var l) ? l : dr.L,
                    TryNumber(el, "t", out var t) ? t : dr.T,
                    TryNumber(el, "r", out var r) ? r : dr.R,
                    TryNumber(el, "b", out var b) ? b : dr.B);
            }
            default:
                return null;
        }
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value);
    }

    private static bool TryNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        return TryGet(obj, name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static string? GetString(JsonElement obj, string name) =>
        TryGet(obj, name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static long GetInt(JsonElement obj, string name, long fallback) =>
        TryGet(obj, name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : fallback;

    private static bool GetBool(JsonElement obj, string name, bool fallback) =>
        TryGet(obj, name, out var el) ? el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        } : fallback;

    /// <summary>小文字の 16 進 64 桁か(§9.11 の 3)。</summary>
    public static bool IsSha256(string s)
    {
        if (s.Length != 64) return false;
        foreach (var c in s)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }
}
