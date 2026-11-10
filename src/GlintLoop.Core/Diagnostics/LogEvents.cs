// ログの 19 個の event を作る関数(仕様書 §9.11 の 6 の表)。event ごとの引数だけを受け取るので、表に無い項目は渡せない(AC-31 の (4))。
// 文字列の項目は列挙型で受け取り、表の綴りに直して入れる。例外は app_start の gpu と crash の type・stack だけ(INV-15)。
// lvl は LevelOf が event の名前から決める(ファイルへの書き出しは JsonlLogWriter)。
namespace GlintLoop.Core.Diagnostics;

public enum LogLevel { Info, Warn, Error }

public enum LogAdapter { Hardware, Warp }

public enum LogImportFormat { Png, Jpeg, Webp, Bmp, Ico, Gif, Dib }

public enum LogImportVia { Paste, Drop, Open, Arg }

public enum LogImportFailReason { Unsupported, TooLargeFile, TooLargeSide, TooManyPixels, TooManyLayers, OverBudget, DecodeError, ReadError }

public enum LogExportFormat { Png, Jpeg, Gif, Webp, Apng }

public enum LogExportFailReason { TooBigGif, EncodeError, WriteError, GpuError }

public enum LogRenderTarget { Display, Thumb }

public enum LogRenderFailReason { GpuError, Oom }

public enum LogHandoffKind { Copy, Drag, Save }

public enum LogCopyMode { File, Image, Both }

public enum LogWarpReason { NoAdapter, FloatRtUnsupported, RecreateFailed }

/// <summary>settings_invalid の key(設定の表のキーと、ファイル全体が壊れている file・schema が 1 でない schema)。</summary>
public enum LogSettingsKey
{
    File, Schema, Theme, CopyMode, DefaultFormat, DefaultSaveDir, LastOpenDir, LastSaveDir, ReduceMotion, CacheLimitMB, BgPreview, Window, LastExport,
}

public static class LogEvents
{
    /// <summary>§9.11 の表の event・lvl・項目(共通の ts・lvl・event・v を除く)。</summary>
    public static IReadOnlyList<(string Event, LogLevel Level, string[] Keys)> Table { get; } =
    [
        ("app_start", LogLevel.Info, ["os", "adapter", "gpu", "dpi"]),
        ("app_exit", LogLevel.Info, ["code"]),
        ("import_ok", LogLevel.Info, ["ms", "w", "h", "fmt", "bytes", "via", "layers"]),
        ("import_fail", LogLevel.Warn, ["reason"]),
        ("export_start", LogLevel.Info, ["fmt", "w", "h", "frames"]),
        ("export_done", LogLevel.Info, ["ms", "bytes", "frames", "fmt", "rungs", "trials"]),
        ("export_cancel", LogLevel.Info, []),
        ("export_fail", LogLevel.Error, ["reason"]),
        ("render_fail", LogLevel.Error, ["target", "reason"]),
        ("export_handoff", LogLevel.Info, ["kind", "fmt", "bytes", "frames", "mode"]),
        ("clipboard_failed", LogLevel.Warn, ["hr"]),
        ("device_lost", LogLevel.Warn, []),
        ("device_recreated", LogLevel.Info, ["ms", "adapter"]),
        ("warp_fallback", LogLevel.Warn, ["reason"]),
        ("param_clamped", LogLevel.Warn, ["effect", "key"]),
        ("effectdef_invalid", LogLevel.Error, ["id", "reason"]),
        ("cache_trim", LogLevel.Info, ["removed", "bytes"]),
        ("settings_invalid", LogLevel.Warn, ["key"]),
        ("crash", LogLevel.Error, ["type", "stack"]),
    ];

    private static readonly Dictionary<string, LogLevel> Levels = Table.ToDictionary(t => t.Event, t => t.Level, StringComparer.Ordinal);

    /// <summary>event の lvl。表に無い名前は null。</summary>
    public static LogLevel? LevelOf(string eventName) => Levels.TryGetValue(eventName, out var l) ? l : null;

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        _ => "info",
    };

    /// <summary>crash の stack の上限(20 個)。</summary>
    public const int MaxStackFrames = 20;

    public static LogEvent AppStart(int osBuild, LogAdapter adapter, string gpu, int dpiPercent) =>
        LogEvent.Of("app_start", ("os", osBuild), ("adapter", Name(adapter)), ("gpu", gpu), ("dpi", dpiPercent));

    public static LogEvent AppExit(int code) => LogEvent.Of("app_exit", ("code", code));

    public static LogEvent ImportOk(long ms, int w, int h, LogImportFormat fmt, long bytes, LogImportVia via, int layers) =>
        LogEvent.Of("import_ok", ("ms", ms), ("w", w), ("h", h), ("fmt", Name(fmt)), ("bytes", bytes), ("via", Name(via)), ("layers", layers));

    public static LogEvent ImportFail(LogImportFailReason reason) => LogEvent.Of("import_fail", ("reason", Name(reason)));

    public static LogEvent ExportStart(LogExportFormat fmt, int w, int h, int frames) =>
        LogEvent.Of("export_start", ("fmt", Name(fmt)), ("w", w), ("h", h), ("frames", frames));

    public static LogEvent ExportDone(long ms, long bytes, int frames, LogExportFormat fmt, int rungs, int trials) =>
        LogEvent.Of("export_done", ("ms", ms), ("bytes", bytes), ("frames", frames), ("fmt", Name(fmt)), ("rungs", rungs), ("trials", trials));

    public static LogEvent ExportCancel() => LogEvent.Of("export_cancel");

    public static LogEvent ExportFail(LogExportFailReason reason) => LogEvent.Of("export_fail", ("reason", Name(reason)));

    public static LogEvent RenderFail(LogRenderTarget target, LogRenderFailReason reason) =>
        LogEvent.Of("render_fail", ("target", Name(target)), ("reason", Name(reason)));

    /// <summary>mode は copy のときだけ渡す(ほかの kind では null。null の項目は書かない)。</summary>
    public static LogEvent ExportHandoff(LogHandoffKind kind, LogExportFormat fmt, long bytes, int frames, LogCopyMode? mode)
    {
        if ((kind == LogHandoffKind.Copy) != mode.HasValue)
            throw new ArgumentException("mode は copy のときだけ渡す", nameof(mode));
        return mode is { } m
            ? LogEvent.Of("export_handoff", ("kind", Name(kind)), ("fmt", Name(fmt)), ("bytes", bytes), ("frames", frames), ("mode", Name(m)))
            : LogEvent.Of("export_handoff", ("kind", Name(kind)), ("fmt", Name(fmt)), ("bytes", bytes), ("frames", frames));
    }

    /// <summary>hr は HRESULT を符号なし 32 ビットにした値(CLIPBRD_E_CANT_OPEN は 2147746256)。</summary>
    public static LogEvent ClipboardFailed(uint hr) => LogEvent.Of("clipboard_failed", ("hr", hr));

    public static LogEvent DeviceLost() => LogEvent.Of("device_lost");

    public static LogEvent DeviceRecreated(long ms, LogAdapter adapter) => LogEvent.Of("device_recreated", ("ms", ms), ("adapter", Name(adapter)));

    public static LogEvent WarpFallback(LogWarpReason reason) => LogEvent.Of("warp_fallback", ("reason", Name(reason)));

    /// <summary>effect はエフェクトの id、レイヤーの項目は layer、全体の lookStrength は all。key はパラメータか項目の key(どちらも列挙の値)。</summary>
    public static LogEvent ParamClamped(string effect, string key) => LogEvent.Of("param_clamped", ("effect", effect), ("key", key));

    /// <summary>reason は §9.2 の検査の番号 1〜9。</summary>
    public static LogEvent EffectDefInvalid(string id, int reason) => LogEvent.Of("effectdef_invalid", ("id", id), ("reason", reason));

    public static LogEvent CacheTrim(int removed, long bytes) => LogEvent.Of("cache_trim", ("removed", removed), ("bytes", bytes));

    public static LogEvent SettingsInvalid(LogSettingsKey key) => LogEvent.Of("settings_invalid", ("key", Name(key)));

    /// <summary>例外の型の完全名と、型の完全名.メソッド名 の配列(新しい順に 20 個まで)。例外のメッセージ・パス・行番号は入れない。</summary>
    public static LogEvent Crash(Exception e)
    {
        var frames = new System.Diagnostics.StackTrace(e, fNeedFileInfo: false).GetFrames();
        var stack = new List<string>(MaxStackFrames);
        foreach (var f in frames)
        {
            if (stack.Count >= MaxStackFrames) break;
            var m = f.GetMethod();
            if (m is null) continue;
            stack.Add(CrashName((m.DeclaringType is { } t ? TypeName(t) : "?") + "." + m.Name));
        }
        return LogEvent.Of("crash", ("type", CrashName(TypeName(e.GetType()))), ("stack", (IReadOnlyList<string>)stack));
    }

    // 総称型の FullName は引数の並び([[…, Version=…]])を持つか null になるので、並びを落とした名前にする
    private static string TypeName(Type t)
    {
        var name = t.FullName ?? (t.Namespace is { } ns ? ns + "." + t.Name : t.Name);
        var args = name.IndexOf('[');
        return args >= 0 ? name[..args] : name;
    }

    // crash の type と stack に入れる名前: ASCII の英数字と . _ + ` < > $ - だけを残す(空白・括弧・区切り・日本語を入れない。AC-50 の (5)・INV-15)
    private static string CrashName(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && !".+_`<>$-".Contains(chars[i])) chars[i] = '_';
        }
        return new string(chars).Replace(".cs", "_cs", StringComparison.Ordinal);
    }

    // ---- 列挙値の綴り(§9.11 の表のとおり) ----

    public static string Name(LogAdapter v) => v == LogAdapter.Warp ? "warp" : "hardware";

    public static string Name(LogImportFormat v) => v switch
    {
        LogImportFormat.Png => "png", LogImportFormat.Jpeg => "jpeg", LogImportFormat.Webp => "webp", LogImportFormat.Bmp => "bmp",
        LogImportFormat.Ico => "ico", LogImportFormat.Gif => "gif", _ => "dib",
    };

    public static string Name(LogImportVia v) => v switch
    {
        LogImportVia.Paste => "paste", LogImportVia.Drop => "drop", LogImportVia.Open => "open", _ => "arg",
    };

    public static string Name(LogImportFailReason v) => v switch
    {
        LogImportFailReason.Unsupported => "unsupported", LogImportFailReason.TooLargeFile => "too_large_file",
        LogImportFailReason.TooLargeSide => "too_large_side", LogImportFailReason.TooManyPixels => "too_many_pixels",
        LogImportFailReason.TooManyLayers => "too_many_layers", LogImportFailReason.OverBudget => "over_budget",
        LogImportFailReason.DecodeError => "decode_error", _ => "read_error",
    };

    public static string Name(LogExportFormat v) => v switch
    {
        LogExportFormat.Png => "png", LogExportFormat.Jpeg => "jpeg", LogExportFormat.Gif => "gif", LogExportFormat.Webp => "webp", _ => "apng",
    };

    public static string Name(LogExportFailReason v) => v switch
    {
        LogExportFailReason.TooBigGif => "too_big_gif", LogExportFailReason.EncodeError => "encode_error",
        LogExportFailReason.WriteError => "write_error", _ => "gpu_error",
    };

    public static string Name(LogRenderTarget v) => v == LogRenderTarget.Thumb ? "thumb" : "display";

    public static string Name(LogRenderFailReason v) => v == LogRenderFailReason.Oom ? "oom" : "gpu_error";

    public static string Name(LogHandoffKind v) => v switch { LogHandoffKind.Copy => "copy", LogHandoffKind.Drag => "drag", _ => "save" };

    public static string Name(LogCopyMode v) => v switch { LogCopyMode.File => "file", LogCopyMode.Image => "image", _ => "both" };

    public static string Name(LogWarpReason v) => v switch
    {
        LogWarpReason.NoAdapter => "no_adapter", LogWarpReason.FloatRtUnsupported => "float_rt_unsupported", _ => "recreate_failed",
    };

    public static string Name(LogSettingsKey v) => v switch
    {
        LogSettingsKey.File => "file", LogSettingsKey.Schema => "schema", LogSettingsKey.Theme => "theme", LogSettingsKey.CopyMode => "copyMode",
        LogSettingsKey.DefaultFormat => "defaultFormat", LogSettingsKey.DefaultSaveDir => "defaultSaveDir", LogSettingsKey.LastOpenDir => "lastOpenDir",
        LogSettingsKey.LastSaveDir => "lastSaveDir", LogSettingsKey.ReduceMotion => "reduceMotion", LogSettingsKey.CacheLimitMB => "cacheLimitMB",
        LogSettingsKey.BgPreview => "bgPreview", LogSettingsKey.Window => "window", _ => "lastExport",
    };
}
