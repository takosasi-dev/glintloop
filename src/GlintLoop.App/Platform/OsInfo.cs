// OS の情報の差し込み口(仕様書 §11 Phase 3 の前書きの (d)): アプリのテーマ・アニメーション効果・透明効果・ハイコントラスト・ビルド番号・モニターの一覧。
// 製品は Win32OsInfo(WM_SETTINGCHANGE を受けたら Refresh で読み直す)、テストは偽の実装を渡す。
// AppsUseLightTheme と EnableTransparency は RegGetValueW で読むだけで、書かない(INV-13)。
using System.Runtime.InteropServices;
using System.Windows;

namespace GlintLoop.App.Platform;

/// <summary>1 つのモニターの作業領域(仮想スクリーンの DIP)。</summary>
public sealed record MonitorInfo(Rect WorkArea, bool IsPrimary);

public interface IOsInfo
{
    /// <summary>Windows のアプリのテーマがライトか(AppsUseLightTheme)。</summary>
    bool AppsUseLightTheme { get; }

    /// <summary>Windows の「アニメーション効果」がオンか。</summary>
    bool AnimationsEnabled { get; }

    /// <summary>Windows の「透明効果」がオンか。</summary>
    bool TransparencyEnabled { get; }

    bool HighContrast { get; }

    /// <summary>OS のビルド番号(22621 以上で Mica。FR-96)。</summary>
    int BuildNumber { get; }

    IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>どれかの値が変わったとき(製品は Refresh の中、テストは偽の値を変えたとき)。UI スレッドで出す。</summary>
    event Action? Changed;

    /// <summary>読み直す(WM_SETTINGCHANGE を受けたとき)。</summary>
    void Refresh();
}

/// <summary>実際の OS から読む。</summary>
public sealed class Win32OsInfo : IOsInfo
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private Snapshot _now;

    public Win32OsInfo()
    {
        _now = Read();
    }

    public bool AppsUseLightTheme => _now.Light;

    public bool AnimationsEnabled => _now.Animations;

    public bool TransparencyEnabled => _now.Transparency;

    public bool HighContrast => _now.HighContrast;

    public int BuildNumber => _now.Build;

    public IReadOnlyList<MonitorInfo> Monitors => _now.Monitors;

    public event Action? Changed;

    public void Refresh()
    {
        var next = Read();
        var same = next.Light == _now.Light && next.Animations == _now.Animations && next.Transparency == _now.Transparency &&
                   next.HighContrast == _now.HighContrast && next.Build == _now.Build && next.Monitors.SequenceEqual(_now.Monitors);
        _now = next;
        if (!same) Changed?.Invoke();
    }

    private sealed record Snapshot(bool Light, bool Animations, bool Transparency, bool HighContrast, int Build, IReadOnlyList<MonitorInfo> Monitors);

    private static Snapshot Read() => new(
        ReadDword(PersonalizeKey, "AppsUseLightTheme") is not 0u,
        SystemParameters.ClientAreaAnimation,
        ReadDword(PersonalizeKey, "EnableTransparency") is not 0u,
        SystemParameters.HighContrast,
        Environment.OSVersion.Version.Build,
        ReadMonitors());

    /// <summary>HKEY_CURRENT_USER の DWORD を読む。無いときは null(既定はライト・透明効果オンとみなす)。</summary>
    private static uint? ReadDword(string subKey, string value)
    {
        uint size = sizeof(uint);
        var status = Native.RegGetValueW(Native.HKEY_CURRENT_USER, subKey, value, Native.RRF_RT_REG_DWORD, out _, out var data, ref size);
        return status == 0 ? data : null;
    }

    private static IReadOnlyList<MonitorInfo> ReadMonitors()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfoW(h, ref mi)) return true;
            var scale = 1.0;
            if (Native.GetDpiForMonitor(h, 0, out var dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
            var r = mi.rcWork;
            list.Add(new MonitorInfo(new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale),
                (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
