// テーマの切替(FR-95・FR-96・FR-98・§9.1): 設定(システムに合わせる・ライト・ダーク)と OS の情報から、カラートークンの辞書を差し替える。
// 「システムに合わせる」は注入した OS の情報(AppsUseLightTheme)で解き、Application.ThemeMode には Light か Dark を明示して渡す(Mica は Fluent に任せる。Q-19)。
// ウィンドウの背景は、ビルド 22621 以上なら透明、22620 以下とハイコントラストでは不透明(bg-base かシステムの色)にする。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlintLoop.App.Platform;
using GlintLoop.Core.Settings;

namespace GlintLoop.App.Theme;

public enum ThemeVariant { Light, Dark, HighContrast }

public sealed class ThemeManager : IDisposable
{
    /// <summary>Mica を使うビルド番号の下限(FR-96)。</summary>
    public const int MicaMinBuild = 22621;

    private readonly Application _app;
    private readonly IOsInfo _os;
    private readonly List<Window> _windows = new();
    private ResourceDictionary? _current;
    private ThemeSetting _setting = ThemeSetting.System;

    public ThemeManager(Application app, IOsInfo os)
    {
        _app = app;
        _os = os;
        _os.Changed += OnOsChanged;
        Apply();
    }

    public ThemeSetting Setting
    {
        get => _setting;
        set
        {
            if (_setting == value) return;
            _setting = value;
            Apply();
        }
    }

    public ThemeVariant Current { get; private set; }

    /// <summary>いまのカラートークンの辞書。</summary>
    public ResourceDictionary? CurrentTokens => _current;

    /// <summary>テーマを差し替えたあと。</summary>
    public event Action<ThemeVariant>? Changed;

    /// <summary>設定と OS のアプリのテーマから、ライトかダークかを決める(ハイコントラストは別に見る)。</summary>
    public static ThemeVariant Resolve(ThemeSetting setting, bool appsUseLightTheme) => setting switch
    {
        ThemeSetting.Light => ThemeVariant.Light,
        ThemeSetting.Dark => ThemeVariant.Dark,
        _ => appsUseLightTheme ? ThemeVariant.Light : ThemeVariant.Dark,
    };

    /// <summary>背景を透明にして Mica を見せるか(ビルド 22621 以上で、ハイコントラストでない)。</summary>
    public static bool UsesTransparentBackground(int build, bool highContrast) => build >= MicaMinBuild && !highContrast;

    /// <summary>ウィンドウを登録し、背景をテーマに合わせる(閉じたら外す)。</summary>
    public void Attach(Window w)
    {
        _windows.Add(w);
        w.Closed += (_, _) => _windows.Remove(w);
        ApplyBackground(w);
    }

    private void OnOsChanged()
    {
        if (_app.Dispatcher.CheckAccess()) Apply();
        else _app.Dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        var variant = _os.HighContrast ? ThemeVariant.HighContrast : Resolve(_setting, _os.AppsUseLightTheme);
        var tokens = variant switch
        {
            ThemeVariant.Light => AppResources.LightTokens,
            ThemeVariant.Dark => AppResources.DarkTokens,
            _ => HighContrastTokens.Build(AppResources.DarkTokens),
        };
        var merged = _app.Resources.MergedDictionaries;
        var at = _current is null ? -1 : merged.IndexOf(_current);
        if (at < 0) at = merged.IndexOf(AppResources.DarkTokens) is var d and >= 0 ? d : merged.IndexOf(AppResources.LightTokens);
        if (at >= 0) merged[at] = tokens;
        else merged.Insert(0, tokens);
        _current = tokens;

        // Fluent テーマの明暗も合わせる(Mica と DWM のダークの属性は Fluent が設定する。Q-19)
        var light = variant == ThemeVariant.Light || (variant == ThemeVariant.HighContrast && _os.AppsUseLightTheme);
        // ThemeMode を代入すると(同じ値でも)WPF がウィンドウの木を作り直してフォーカスが外れるので、変わるときだけ代入し、フォーカスを戻す
        var mode = light ? ThemeMode.Light : ThemeMode.Dark;
        if (_app.ThemeMode != mode)
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            _app.ThemeMode = mode;
            if (focused is UIElement u)
                _app.Dispatcher.BeginInvoke(() => { if (u.IsVisible) System.Windows.Input.Keyboard.Focus(u); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        Current = variant;
        foreach (var w in _windows) ApplyBackground(w);
        Changed?.Invoke(variant);
    }

    private void ApplyBackground(Window w)
    {
        if (UsesTransparentBackground(_os.BuildNumber, _os.HighContrast)) w.Background = Brushes.Transparent;
        else w.SetResourceReference(Control.BackgroundProperty, "BgBaseBrush");
    }

    public void Dispose()
    {
        _os.Changed -= OnOsChanged;
    }
}
