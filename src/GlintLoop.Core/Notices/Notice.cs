// 通知バーに出す 1 件の通知(仕様書 §9.12 の「通知バーとダイアログの文言」の表)。
// キーと置き換えの値だけを持ち、文言の組み立ては画面の側が行う。ここに無いキーを作らない。
// 置き換えの値の書式(小数 1 位など)は §9.12 の書式の表に従い、NoticeFormat が整数の演算で作る。
namespace GlintLoop.Core.Notices;

public enum NoticeLevel { Info, Warn, Error }

public sealed record Notice(string Key, NoticeLevel Level, IReadOnlyDictionary<string, string> Args)
{
    private static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>();

    public static Notice Of(string key, NoticeLevel level) => new(key, level, NoArgs);

    public static Notice Of(string key, NoticeLevel level, params (string Name, string Value)[] args)
    {
        var map = new Dictionary<string, string>(args.Length);
        foreach (var (name, value) in args)
        {
            map[name] = value;
        }
        return new Notice(key, level, map);
    }
}

/// <summary>§9.12 の通知とダイアログの文言のキー(表の 40 行のすべて。文言そのものは GlintLoop.App の Text/notices.tsv)。</summary>
public static class NoticeKeys
{
    public const string PasteEmpty = "paste.empty";
    public const string ImportMulti = "import.multi";
    public const string ImportWebUrl = "import.web_url";
    public const string ImportUnsupported = "import.unsupported";
    public const string ImportAnimated = "import.animated";
    public const string ImportTooLargeFile = "import.too_large_file";
    public const string ImportTooLargeSide = "import.too_large_side";
    public const string ImportTooManyPixels = "import.too_many_pixels";
    public const string ImportDecodeError = "import.decode_error";
    public const string ImportReadError = "import.read_error";
    public const string ImportGpuShrunk = "import.gpu_shrunk";
    public const string LayerAdded = "layer.added";
    public const string LayerFull = "layer.full";
    public const string LayerBudget = "layer.budget";
    public const string LayerRenderShrunk = "layer.render_shrunk";
    public const string StackFull = "stack.full";
    public const string BatchFull = "batch.full";
    public const string BatchLayersIgnored = "batch.layers_ignored";
    public const string StackUnknownEffect = "stack.unknown_effect";
    public const string FontMissing = "font.missing";
    public const string CopyDone = "copy.done";
    public const string SaveDone = "save.done";
    public const string SaveFailed = "save.failed";
    public const string ClipFailed = "clip.failed";
    public const string ExportSizeFail = "export.size_fail";
    public const string ExportConfirmOver = "export.confirm_over";
    public const string ExportDragAgain = "export.drag_again";
    public const string ExportGifTooBig = "export.gif_too_big";
    public const string ExportFailed = "export.failed";
    public const string ExportCondition = "export.condition";
    public const string RenderFailed = "render.failed";
    public const string GpuSoftware = "gpu.software";
    public const string SettingsSaveFailed = "settings.save_failed";
    public const string ProjectSaved = "project.saved";
    public const string ProjectTooNew = "project.too_new";
    public const string ProjectInvalid = "project.invalid";
    public const string ReplaceConfirm = "replace.confirm";
    public const string NameOverwrite = "name.overwrite";
    public const string CrashTitle = "crash.title";
    public const string CrashNoSave = "crash.nosave";

    /// <summary>表の順の 40 個。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PasteEmpty, ImportMulti, ImportWebUrl, ImportUnsupported, ImportAnimated, ImportTooLargeFile, ImportTooLargeSide, ImportTooManyPixels,
        ImportDecodeError, ImportReadError, ImportGpuShrunk, LayerAdded, LayerFull, LayerBudget, LayerRenderShrunk, StackFull, BatchFull,
        BatchLayersIgnored, StackUnknownEffect, FontMissing, CopyDone, SaveDone, SaveFailed, ClipFailed, ExportSizeFail, ExportConfirmOver, ExportDragAgain,
        ExportGifTooBig, ExportFailed, ExportCondition, RenderFailed, GpuSoftware, SettingsSaveFailed, ProjectSaved, ProjectTooNew,
        ProjectInvalid, ReplaceConfirm, NameOverwrite, CrashTitle, CrashNoSave,
    ];
}

/// <summary>§9.12 の置き換えの値の書式。割り算は切り捨ての整数除算。</summary>
public static class NoticeFormat
{
    /// <summary>{size}(import.too_large_file): t=(バイト数+50,000)÷100,000 の小数 1 位。</summary>
    public static string MegabytesOneDecimal(long bytes) => OneDecimal((bytes + 50_000) / 100_000);

    /// <summary>{mp}(import.too_many_pixels): t=(画素数+50,000)÷100,000 の小数 1 位。</summary>
    public static string MegapixelsOneDecimal(long pixels) => OneDecimal((pixels + 50_000) / 100_000);

    /// <summary>{size}(copy.done): t=(バイト数+50)÷100 の小数 1 位のあとに、半角空白と KB。</summary>
    public static string KilobytesOneDecimal(long bytes) => OneDecimal((bytes + 50) / 100) + " KB";

    /// <summary>{limit}・{size}(export.size_fail)と {N}・{M}・{w}・{h}: 10 進の整数(桁区切りなし)。</summary>
    public static string Integer(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string OneDecimal(long t) =>
        (t / 10).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
        (t % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
