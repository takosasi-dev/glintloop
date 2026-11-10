// 設定のモデル(FR-90・仕様書 §9.11 の 2 の settings.json の表)。値を変えると Changed を出し、保存は SettingsStore が 500 ms 後に行う。
// 範囲のある値(cacheLimitMB)は代入のときに端に直す。初期化(ResetToDefaults)は FR-90 の 6 項目だけを既定に戻す。
// lastExport は §9.11 の output と同じ形の JSON をそのまま持つ(範囲の直しは使うときに OutputSettingsJson が行う)。
using System.Text.Json.Nodes;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Settings;

public enum ThemeSetting { System, Light, Dark }

public enum CopyModeSetting { File, Image, Both }

public enum ExportFormatSetting { Png, Jpeg, Gif, Webp, Apng }

public enum ReduceMotionSetting { System, On, Off }

public enum BgPreviewMode { Checker, Black, White, Gray, Custom }

/// <summary>キャンバスの背景の選択(FR-20)。Color は不透明の RGB(0xRRGGBB)。</summary>
public readonly record struct BgPreview(BgPreviewMode Mode, int Color)
{
    /// <summary>既定は mode が checker、色が灰(RGB がどれも 0x80)。</summary>
    public static BgPreview Default => new(BgPreviewMode.Checker, 0x80_80_80);
}

/// <summary>ウィンドウの状態(FR-106)。どの項目も無いことがある(既定は空)。座標と幅は DIP。</summary>
public sealed record WindowSettings(double? X, double? Y, double? Width, double? Height, bool? Maximized, double? LeftPane, double? RightPane)
{
    public static WindowSettings Empty { get; } = new(null, null, null, null, null, null, null);

    public bool IsEmpty => this == Empty;
}

public sealed class AppSettings
{
    public const int Schema = 1;
    public const int CacheLimitMinMB = 256;
    public const int CacheLimitMaxMB = 4096;
    public const int DefaultCacheLimitMB = 1024;

    private ThemeSetting _theme = ThemeSetting.System;
    private CopyModeSetting _copyMode = CopyModeSetting.Both;
    private ExportFormatSetting _defaultFormat = ExportFormatSetting.Gif;
    private string _defaultSaveDir = "";
    private string _lastOpenDir = "";
    private string _lastSaveDir = "";
    private ReduceMotionSetting _reduceMotion = ReduceMotionSetting.System;
    private int _cacheLimitMB = DefaultCacheLimitMB;
    private BgPreview _bgPreview = BgPreview.Default;
    private WindowSettings _window = WindowSettings.Empty;
    private JsonObject? _lastExport;

    /// <summary>どれかの値が変わったとき(引数は変わったキーの名前。settings.json の表のキー)。</summary>
    public event Action<string>? Changed;

    public ThemeSetting Theme { get => _theme; set => Set(ref _theme, value, "theme"); }

    public CopyModeSetting CopyMode { get => _copyMode; set => Set(ref _copyMode, value, "copyMode"); }

    /// <summary>既定の書き出し形式。設定画面から変えるときは SetDefaultFormatFromSettingsScreen を使う(lastExport.format も書き換える)。</summary>
    public ExportFormatSetting DefaultFormat { get => _defaultFormat; set => Set(ref _defaultFormat, value, "defaultFormat"); }

    public string DefaultSaveDir { get => _defaultSaveDir; set => Set(ref _defaultSaveDir, value ?? "", "defaultSaveDir"); }

    public string LastOpenDir { get => _lastOpenDir; set => Set(ref _lastOpenDir, value ?? "", "lastOpenDir"); }

    public string LastSaveDir { get => _lastSaveDir; set => Set(ref _lastSaveDir, value ?? "", "lastSaveDir"); }

    public ReduceMotionSetting ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value, "reduceMotion"); }

    /// <summary>キャッシュの上限(MB)。範囲外は端に直す(256〜4096)。</summary>
    public int CacheLimitMB
    {
        get => _cacheLimitMB;
        set => Set(ref _cacheLimitMB, Math.Clamp(value, CacheLimitMinMB, CacheLimitMaxMB), "cacheLimitMB");
    }

    public BgPreview BgPreview { get => _bgPreview; set => Set(ref _bgPreview, value, "bgPreview"); }

    public WindowSettings Window { get => _window; set => Set(ref _window, value ?? WindowSettings.Empty, "window"); }

    /// <summary>最後に使った出力設定(§9.11 の output の形)。空のときは null。返すのは写しで、変えるときは代入する。</summary>
    public JsonObject? LastExport
    {
        get => (JsonObject?)_lastExport?.DeepClone();
        set
        {
            var next = value is null || value.Count == 0 ? null : (JsonObject)value.DeepClone();
            if (JsonNode.DeepEquals(_lastExport, next)) return;
            _lastExport = next;
            Changed?.Invoke("lastExport");
        }
    }

    /// <summary>いまの lastExport と defaultFormat から決まる、新しい文書の出力設定(§9.11 の 2)。</summary>
    public OutputSettings NewDocumentOutput() => OutputSettingsJson.ForNewDocument(_lastExport, SettingsJson.Name(_defaultFormat));

    /// <summary>
    /// 設定画面で既定の書き出し形式を変えたとき(§9.11): lastExport が空でなければ lastExport.format も同じ値にする。
    /// </summary>
    public void SetDefaultFormatFromSettingsScreen(ExportFormatSetting format)
    {
        DefaultFormat = format;
        if (_lastExport is not null)
        {
            var copy = (JsonObject)_lastExport.DeepClone();
            copy["format"] = SettingsJson.Name(format);
            LastExport = copy;
        }
    }

    /// <summary>設定の初期化(FR-90): theme・copyMode・defaultFormat・defaultSaveDir・reduceMotion・cacheLimitMB だけを既定に戻す。lastExport.format は変えない。</summary>
    public void ResetToDefaults()
    {
        Theme = ThemeSetting.System;
        CopyMode = CopyModeSetting.Both;
        DefaultFormat = ExportFormatSetting.Gif;
        DefaultSaveDir = "";
        ReduceMotion = ReduceMotionSetting.System;
        CacheLimitMB = DefaultCacheLimitMB;
    }

    /// <summary>読み込んだ値を、変更の通知を出さずに入れる(SettingsStore が使う)。</summary>
    internal void LoadFrom(AppSettings other)
    {
        _theme = other._theme;
        _copyMode = other._copyMode;
        _defaultFormat = other._defaultFormat;
        _defaultSaveDir = other._defaultSaveDir;
        _lastOpenDir = other._lastOpenDir;
        _lastSaveDir = other._lastSaveDir;
        _reduceMotion = other._reduceMotion;
        _cacheLimitMB = other._cacheLimitMB;
        _bgPreview = other._bgPreview;
        _window = other._window;
        _lastExport = other._lastExport;
    }

    private void Set<T>(ref T field, T value, string key)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke(key);
    }
}
