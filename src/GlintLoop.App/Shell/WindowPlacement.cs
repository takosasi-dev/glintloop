// ウィンドウの位置と大きさの復元(FR-106・E-49): 保存した矩形が、どれかのモニターの作業領域と幅 100 DIP 以上かつ高さ 100 DIP 以上で重なれば使い、
// そうでなければ(保存が無いときも)プライマリモニターの作業領域の中央に既定の大きさで開く。座標はすべて仮想スクリーンの DIP。
using System.Windows;
using GlintLoop.App.Platform;
using GlintLoop.Core.Settings;

namespace GlintLoop.App.Shell;

public static class WindowPlacement
{
    /// <summary>重なりとみなす幅と高さの下限(DIP)。</summary>
    public const double MinOverlap = 100;

    public static Rect Restore(WindowSettings saved, IReadOnlyList<MonitorInfo> monitors, Size defaultSize)
    {
        if (saved is { X: { } x, Y: { } y, Width: { } w, Height: { } h } && w > 0 && h > 0)
        {
            var r = new Rect(x, y, w, h);
            foreach (var mon in monitors)
            {
                var i = Rect.Intersect(r, mon.WorkArea);
                if (!i.IsEmpty && i.Width >= MinOverlap && i.Height >= MinOverlap) return r;
            }
        }
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        var area = primary?.WorkArea ?? new Rect(new Point(0, 0), defaultSize);
        return new Rect(area.X + (area.Width - defaultSize.Width) / 2, area.Y + (area.Height - defaultSize.Height) / 2, defaultSize.Width, defaultSize.Height);
    }
}
