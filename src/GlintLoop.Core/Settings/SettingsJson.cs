// settings.json の読み書き(仕様書 §9.11 の 2)。UTF-8・BOM なし・インデント 2・改行 LF で、キーは表の順に書く。
// 読むときは、不正な値(範囲外の数を含む)を既定に直して、キーごとに settings_invalid を 1 行書く。未知のキーは捨てる。
// ファイル全体が JSON として読めないときは key=file、schema が 1 でないときは key=schema(呼び出し側が .bad に改名する)。
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlintLoop.Core.Diagnostics;

namespace GlintLoop.Core.Settings;

public enum SettingsLoadStatus { Ok, Missing, Broken, WrongSchema }

public static class SettingsJson
{
    public static byte[] Serialize(AppSettings s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
               {
                   Indented = true,
                   IndentSize = 2,
                   IndentCharacter = ' ',
                   NewLine = "\n",
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", AppSettings.Schema);
            w.WriteString("theme", Name(s.Theme));
            w.WriteString("copyMode", Name(s.CopyMode));
            w.WriteString("defaultFormat", Name(s.DefaultFormat));
            w.WriteString("defaultSaveDir", s.DefaultSaveDir);
            w.WriteString("lastOpenDir", s.LastOpenDir);
            w.WriteString("lastSaveDir", s.LastSaveDir);
            w.WriteString("reduceMotion", Name(s.ReduceMotion));
            w.WriteNumber("cacheLimitMB", s.CacheLimitMB);
            w.WriteStartObject("bgPreview");
            w.WriteString("mode", Name(s.BgPreview.Mode));
            w.WriteString("color", ColorText(s.BgPreview.Color));
            w.WriteEndObject();
            w.WriteStartObject("window");
            var win = s.Window;
            if (win.X is { } x) w.WriteNumber("x", x);
            if (win.Y is { } y) w.WriteNumber("y", y);
            if (win.Width is { } width) w.WriteNumber("width", width);
            if (win.Height is { } height) w.WriteNumber("height", height);
            if (win.Maximized is { } max) w.WriteBoolean("maximized", max);
            if (win.LeftPane is { } lp) w.WriteNumber("leftPane", lp);
            if (win.RightPane is { } rp) w.WriteNumber("rightPane", rp);
            w.WriteEndObject();
            w.WritePropertyName("lastExport");
            if (s.LastExport is { } le) le.WriteTo(w);
            else
            {
                w.WriteStartObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    /// <summary>bytes を読んで into に入れる。直した項目ごとに log へ settings_invalid を書く。</summary>
    public static SettingsLoadStatus Read(byte[] bytes, AppSettings into, ILogSink log)
    {
        JsonObject? root;
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }
        if (root is null)
        {
            log.Write(LogEvents.SettingsInvalid(LogSettingsKey.File));
            return SettingsLoadStatus.Broken;
        }
        if (!(root["schema"] is JsonValue sv && sv.TryGetValue<int>(out var schema) && schema == AppSettings.Schema))
        {
            log.Write(LogEvents.SettingsInvalid(LogSettingsKey.Schema));
            return SettingsLoadStatus.WrongSchema;
        }

        var s = new AppSettings();
        void Bad(LogSettingsKey k) => log.Write(LogEvents.SettingsInvalid(k));

        if (root.ContainsKey("theme"))
        {
            if (TryEnum(root["theme"], ThemeNames, out ThemeSetting t)) s.Theme = t;
            else Bad(LogSettingsKey.Theme);
        }
        if (root.ContainsKey("copyMode"))
        {
            if (TryEnum(root["copyMode"], CopyModeNames, out CopyModeSetting c)) s.CopyMode = c;
            else Bad(LogSettingsKey.CopyMode);
        }
        if (root.ContainsKey("defaultFormat"))
        {
            if (TryEnum(root["defaultFormat"], FormatNames, out ExportFormatSetting f)) s.DefaultFormat = f;
            else Bad(LogSettingsKey.DefaultFormat);
        }
        ReadString(root, "defaultSaveDir", v => s.DefaultSaveDir = v, () => Bad(LogSettingsKey.DefaultSaveDir));
        ReadString(root, "lastOpenDir", v => s.LastOpenDir = v, () => Bad(LogSettingsKey.LastOpenDir));
        ReadString(root, "lastSaveDir", v => s.LastSaveDir = v, () => Bad(LogSettingsKey.LastSaveDir));
        if (root.ContainsKey("reduceMotion"))
        {
            if (TryEnum(root["reduceMotion"], ReduceMotionNames, out ReduceMotionSetting r)) s.ReduceMotion = r;
            else Bad(LogSettingsKey.ReduceMotion);
        }
        if (root.ContainsKey("cacheLimitMB"))
        {
            // ファイルの範囲外は既定に直す(E-42)。範囲の端に直すのは設定画面の入力だけ(AppSettings の代入。AC-30 の (1))
            if (root["cacheLimitMB"] is JsonValue cv && cv.TryGetValue<double>(out var mb) && double.IsFinite(mb) && mb == Math.Floor(mb) &&
                mb >= AppSettings.CacheLimitMinMB && mb <= AppSettings.CacheLimitMaxMB)
            {
                s.CacheLimitMB = (int)mb;
            }
            else
            {
                Bad(LogSettingsKey.CacheLimitMB);
            }
        }
        if (root.ContainsKey("bgPreview"))
        {
            if (TryBgPreview(root["bgPreview"], out var bg)) s.BgPreview = bg;
            else Bad(LogSettingsKey.BgPreview);
        }
        if (root.ContainsKey("window"))
        {
            if (TryWindow(root["window"], out var win)) s.Window = win;
            else Bad(LogSettingsKey.Window);
        }
        if (root.ContainsKey("lastExport"))
        {
            if (root["lastExport"] is JsonObject le) s.LastExport = le;
            else Bad(LogSettingsKey.LastExport);
        }
        into.LoadFrom(s);
        return SettingsLoadStatus.Ok;
    }

    private static void ReadString(JsonObject root, string key, Action<string> set, Action bad)
    {
        if (!root.ContainsKey(key)) return;
        if (root[key] is JsonValue v && v.TryGetValue<string>(out var s)) set(s);
        else bad();
    }

    private static bool TryEnum<T>(JsonNode? node, IReadOnlyDictionary<string, T> names, out T value)
    {
        value = default!;
        return node is JsonValue v && v.TryGetValue<string>(out var s) && names.TryGetValue(s, out value!);
    }

    private static bool TryBgPreview(JsonNode? node, out BgPreview value)
    {
        value = BgPreview.Default;
        if (node is not JsonObject o) return false;
        if (!TryEnum(o["mode"], BgModeNames, out BgPreviewMode mode)) return false;
        if (!(o["color"] is JsonValue cv && cv.TryGetValue<string>(out var text) && TryParseColor(text, out var rgb))) return false;
        value = new BgPreview(mode, rgb);
        return true;
    }

    private static bool TryWindow(JsonNode? node, out WindowSettings value)
    {
        value = WindowSettings.Empty;
        if (node is not JsonObject o) return false;
        double? Num(string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
        bool? Bool(string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        value = new WindowSettings(Num("x"), Num("y"), Num("width"), Num("height"), Bool("maximized"), Num("leftPane"), Num("rightPane"));
        return true;
    }

    /// <summary>#RRGGBB の文字列(大文字)。</summary>
    public static string ColorText(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    public static bool TryParseColor(string text, out int rgb)
    {
        rgb = 0;
        return text.Length == 7 && text[0] == '#' &&
               int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    // ---- 列挙の綴り(§9.11 の 2 の表) ----

    private static readonly Dictionary<string, ThemeSetting> ThemeNames = new(StringComparer.Ordinal)
    {
        ["system"] = ThemeSetting.System, ["light"] = ThemeSetting.Light, ["dark"] = ThemeSetting.Dark,
    };

    private static readonly Dictionary<string, CopyModeSetting> CopyModeNames = new(StringComparer.Ordinal)
    {
        ["file"] = CopyModeSetting.File, ["image"] = CopyModeSetting.Image, ["both"] = CopyModeSetting.Both,
    };

    private static readonly Dictionary<string, ExportFormatSetting> FormatNames = new(StringComparer.Ordinal)
    {
        ["png"] = ExportFormatSetting.Png, ["jpeg"] = ExportFormatSetting.Jpeg, ["gif"] = ExportFormatSetting.Gif,
        ["webp"] = ExportFormatSetting.Webp, ["apng"] = ExportFormatSetting.Apng,
    };

    private static readonly Dictionary<string, ReduceMotionSetting> ReduceMotionNames = new(StringComparer.Ordinal)
    {
        ["system"] = ReduceMotionSetting.System, ["on"] = ReduceMotionSetting.On, ["off"] = ReduceMotionSetting.Off,
    };

    private static readonly Dictionary<string, BgPreviewMode> BgModeNames = new(StringComparer.Ordinal)
    {
        ["checker"] = BgPreviewMode.Checker, ["black"] = BgPreviewMode.Black, ["white"] = BgPreviewMode.White,
        ["gray"] = BgPreviewMode.Gray, ["custom"] = BgPreviewMode.Custom,
    };

    private static string NameOf<T>(Dictionary<string, T> names, T value) where T : struct =>
        names.First(kv => EqualityComparer<T>.Default.Equals(kv.Value, value)).Key;

    public static string Name(ThemeSetting v) => NameOf(ThemeNames, v);

    public static string Name(CopyModeSetting v) => NameOf(CopyModeNames, v);

    public static string Name(ExportFormatSetting v) => NameOf(FormatNames, v);

    public static string Name(ReduceMotionSetting v) => NameOf(ReduceMotionNames, v);

    public static string Name(BgPreviewMode v) => NameOf(BgModeNames, v);
}
