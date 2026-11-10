// ハイコントラスト(FR-98)のときのカラートークン: 同じキーに、システムの色(SystemColors)を入れた辞書を作る。
// 面は Window、文字と線とフォーカスの輪は WindowText、accent は Highlight、accent の上の文字は HighlightText、accent の文字は HotTrack。
// 意味を持たない装飾の色(scrim・キャンバスの背景)は元の辞書の値を写す。
using System.Windows;
using System.Windows.Media;

namespace GlintLoop.App.Theme;

public static class HighContrastTokens
{
    /// <summary>トークンの名前ごとの SystemColors の色。表に無いキーは元の辞書の値を使う。</summary>
    public static Color SystemColorFor(string key, Color original) => key switch
    {
        "BgBase" or "Surface1" or "Surface2" or "Surface3" => SystemColors.WindowColor,
        "BorderStrong" or "TextPrimary" or "TextSecondary" or "FocusRing" or "Danger" or "Success" or "Warning" or "Divider" => SystemColors.WindowTextColor,
        "Accent" or "AccentHover" or "AccentPressed" => SystemColors.HighlightColor,
        "OnAccent" => SystemColors.HighlightTextColor,
        "AccentText" => SystemColors.HotTrackColor,
        "DropOverlay" => Color.FromArgb(original.A, SystemColors.HighlightColor.R, SystemColors.HighlightColor.G, SystemColors.HighlightColor.B),
        _ => original,
    };

    public static ResourceDictionary Build(ResourceDictionary source)
    {
        var d = new ResourceDictionary();
        foreach (var key in source.Keys.OfType<string>())
        {
            if (source[key] is Color c) d[key] = SystemColorFor(key, c);
        }
        foreach (var key in source.Keys.OfType<string>())
        {
            if (source[key] is SolidColorBrush && key.EndsWith("Brush", StringComparison.Ordinal) && d[key[..^"Brush".Length]] is Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                d[key] = b;
            }
        }
        return d;
    }
}
