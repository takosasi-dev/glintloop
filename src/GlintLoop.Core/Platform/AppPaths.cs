// データの置き場(仕様書 §9.11 の 1 の表)。ルートは起動処理の引数で与え、製品は %APPDATA%\GlintLoop と %LOCALAPPDATA%\GlintLoop(INV-13・INV-18)。
// テストは一時フォルダのルートを渡す。下のフォルダの名前はここ 1 か所で決め、ほかのコードは文字列で組み立てない。
namespace GlintLoop.Core.Platform;

/// <summary>2 つのルート(ローミング=設定・プリセット・ルック、ローカル=ログ・キャッシュ・退避・測定)と、その下の決まった場所。</summary>
public sealed record AppPaths(string RoamingRoot, string LocalRoot)
{
    /// <summary>製品の既定。%APPDATA%\GlintLoop と %LOCALAPPDATA%\GlintLoop。</summary>
    public static AppPaths Production()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(roaming, ProductFolder), Path.Combine(local, ProductFolder));
    }

    /// <summary>テストなどで、1 つのフォルダの下に roaming と local を並べる。</summary>
    public static AppPaths UnderOneRoot(string root) =>
        new(Path.Combine(root, "roaming", ProductFolder), Path.Combine(root, "local", ProductFolder));

    public const string ProductFolder = "GlintLoop";

    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    public string PresetsDir => Path.Combine(RoamingRoot, "presets");

    public string LooksDir => Path.Combine(RoamingRoot, "looks");

    public string LogsDir => Path.Combine(LocalRoot, "logs");

    public string CacheExportsDir => Path.Combine(LocalRoot, "cache", "exports");

    public string CacheHandoffDir => Path.Combine(LocalRoot, "cache", "handoff");

    public string PerfDir => Path.Combine(LocalRoot, "perf");

    public string RecoveryDir => Path.Combine(LocalRoot, "recovery");

    /// <summary>起動時に .tmp を消すフォルダ(INV-16)。ルートそのもの(settings.json の .tmp)も含める。</summary>
    public IReadOnlyList<string> TempSweepDirs =>
        [RoamingRoot, PresetsDir, LooksDir, LogsDir, CacheExportsDir, CacheHandoffDir, PerfDir, RecoveryDir];
}
