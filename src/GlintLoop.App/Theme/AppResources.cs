// アプリの資源の組み立て(Tokens.Dark/Light/Layout と Controls を Application.Resources に入れる)。製品の起動とテストの UI ホストが同じ入口を呼ぶ。
// カラートークンの辞書は 1 プロセスに 1 回だけ読み、ThemeManager が差し替える。Application.ThemeMode は Light か Dark を明示する(Fluent。Q-19)。
// 自前のスタイルが Fluent の暗黙のスタイルに勝つように、Controls は Fluent の辞書より後ろに入れる(後ろの辞書が優先される)。
using System.Windows;

namespace GlintLoop.App.Theme;

public static class AppResources
{
    private const string Base = "pack://application:,,,/GlintLoop;component/Theme/";

    private static ResourceDictionary? _dark, _light, _layout, _controls;

    public static ResourceDictionary DarkTokens => _dark ??= Load("Tokens.Dark.xaml");

    public static ResourceDictionary LightTokens => _light ??= Load("Tokens.Light.xaml");

    public static ResourceDictionary LayoutTokens => _layout ??= Load("Tokens.Layout.xaml");

    public static ResourceDictionary Controls => _controls ??= Load("Controls.xaml");

    public static ResourceDictionary Load(string file) => new() { Source = new Uri(Base + file, UriKind.Absolute) };

    /// <summary>Application.Resources に資源を入れる(何度呼んでも 1 組だけ)。最初のカラートークンはダーク。</summary>
    public static void Apply(Application app)
    {
        if (app.ThemeMode == ThemeMode.None) app.ThemeMode = ThemeMode.Dark;
        var merged = app.Resources.MergedDictionaries;
        if (!merged.Contains(DarkTokens) && !merged.Contains(LightTokens) && !merged.Any(IsHighContrast)) merged.Add(DarkTokens);
        if (!merged.Contains(LayoutTokens)) merged.Add(LayoutTokens);
        merged.Remove(Controls);
        merged.Add(Controls);
    }

    private static bool IsHighContrast(ResourceDictionary d) => d.Source is null && d.Contains("BgBase");
}
