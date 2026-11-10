// AC-19: Tokens.Dark.xaml・Tokens.Light.xaml・Tokens.Layout.xaml を読み込んで、§9.1 の色・コントラスト比・4 DIP の格子・文字のトークンを調べる。
// 表の値はこのファイルに書き写す(仕様書 §9.1 のカラートークンの表・装飾の色・コントラスト比の表・余白の表・文字の表、§9.3 の寸法)。
// 辞書は UI スレッドの物なので、読むのはすべて UiHost.Invoke の中で行う。
using System.Windows;
using System.Windows.Media;
using GlintLoop.App.Theme;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class TokenAc19Tests
{
    // §9.1 のカラートークンの表(ダーク・ライト)
    private static readonly (string Key, string Dark, string Light)[] ColorTable =
    [
        ("BgBase", "#16171B", "#F1F1F6"),
        ("Surface1", "#1E1F25", "#FFFFFF"),
        ("Surface2", "#272830", "#F6F6FA"),
        ("Surface3", "#31333D", "#EAEAF1"),
        ("BorderStrong", "#80839A", "#767990"),
        ("TextPrimary", "#F2F2F7", "#16161D"),
        ("TextSecondary", "#B4B6C6", "#51536A"),
        ("Accent", "#8E7BFF", "#5A44E6"),
        ("AccentHover", "#A190FF", "#4A35D2"),
        ("AccentPressed", "#7B67F0", "#3E2BBB"),
        ("OnAccent", "#101018", "#FFFFFF"),
        ("AccentText", "#B3A8FF", "#4B36D6"),
        ("FocusRing", "#D9D4FF", "#3B2BB5"),
        ("Danger", "#FF7A7A", "#B3202A"),
        ("Success", "#5BE3A4", "#0F6B46"),
        ("Warning", "#F7C45C", "#7A4F00"),
    ];

    // §9.1 の本文の装飾の色: (キー, ダークの色, ダークの不透明度 %, ライトの色, ライトの不透明度 %)。アルファは round(% × 255 / 100)。
    private static readonly (string Key, string Dark, int DarkPercent, string Light, int LightPercent)[] DecorTable =
    [
        ("Divider", "#FFFFFF", 8, "#000000", 10),
        ("Scrim", "#000000", 50, "#000000", 32),
        ("DropOverlay", "#8E7BFF", 12, "#5A44E6", 12),
        ("CanvasChecker1", "#FFFFFF", 100, "#FFFFFF", 100),
        ("CanvasChecker2", "#CCCCCC", 100, "#CCCCCC", 100),
        ("CanvasBlack", "#000000", 100, "#000000", 100),
        ("CanvasWhite", "#FFFFFF", 100, "#FFFFFF", 100),
        ("CanvasGray", "#808080", 100, "#808080", 100),
    ];

    // §9.1 のコントラスト比の表: (前景, 背景の一覧, 基準, ダークの最小, ライトの最小)
    private static readonly (string Fg, string[] Bg, double Min, double DarkMin, double LightMin)[] ContrastTable =
    [
        ("TextPrimary", ["BgBase", "Surface1", "Surface2", "Surface3"], 4.5, 11.26, 15.03),
        ("TextSecondary", ["BgBase", "Surface1", "Surface2", "Surface3"], 4.5, 6.25, 6.27),
        ("AccentText", ["Surface1", "Surface2", "Surface3"], 4.5, 5.96, 6.27),
        ("OnAccent", ["Accent", "AccentHover", "AccentPressed"], 4.5, 4.54, 6.17),
        ("Danger", ["Surface1", "Surface2", "Surface3"], 4.5, 4.98, 5.56),
        ("Success", ["Surface1", "Surface2"], 4.5, 9.05, 6.07),
        ("Warning", ["Surface1", "Surface2", "Surface3"], 4.5, 7.78, 5.95),
        ("BorderStrong", ["Surface1", "Surface2", "Surface3"], 3.0, 3.37, 3.57),
        ("FocusRing", ["BgBase", "Surface1", "Surface2", "Surface3"], 3.0, 8.86, 7.98),
        ("Accent", ["Surface1", "Surface2", "Surface3"], 3.0, 3.85, 5.15),
    ];

    private static Color Hex(string s, int percent = 100)
    {
        var c = (Color)ColorConverter.ConvertFromString(s);
        c.A = (byte)Math.Round(percent * 255 / 100.0, MidpointRounding.ToEven);
        return c;
    }

    /// <summary>WCAG 2.x の相対輝度。</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Ratio(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>コントラスト比の表のすべての組を調べ、基準を下回る組を返す。最小の比は minOut に入れる。</summary>
    private static List<string> ContrastProblems(Func<string, Color> color, string theme, Dictionary<string, double>? minOut = null)
    {
        var problems = new List<string>();
        foreach (var (fg, bgs, min, _, _) in ContrastTable)
        {
            var lowest = double.MaxValue;
            foreach (var bg in bgs)
            {
                var r = Ratio(color(fg), color(bg));
                lowest = Math.Min(lowest, r);
                if (r < min) problems.Add($"{theme}: {fg} / {bg} = {r:F2}(基準 {min})");
            }
            if (minOut is not null) minOut[fg] = lowest;
        }
        return problems;
    }

    private static Func<string, Color> Lookup(ResourceDictionary d) => key => (Color)d[key];

    [Test]
    public void AC19_1_カラートークンと装飾の色が表と一致しキーの集合が同じ()
    {
        UiHost.Invoke(() =>
        {
            var dark = AppResources.DarkTokens;
            var light = AppResources.LightTokens;
            foreach (var (key, d, l) in ColorTable)
            {
                Check.Equal(Hex(d), (Color)dark[key], $"ダークの {key}");
                Check.Equal(Hex(l), (Color)light[key], $"ライトの {key}");
                Check.Equal(Hex(d), ((SolidColorBrush)dark[key + "Brush"]).Color, $"ダークの {key}Brush");
                Check.Equal(Hex(l), ((SolidColorBrush)light[key + "Brush"]).Color, $"ライトの {key}Brush");
            }
            foreach (var (key, d, dp, l, lp) in DecorTable)
            {
                Check.Equal(Hex(d, dp), (Color)dark[key], $"ダークの {key}");
                Check.Equal(Hex(l, lp), (Color)light[key], $"ライトの {key}");
                Check.Equal(Hex(d, dp), ((SolidColorBrush)dark[key + "Brush"]).Color, $"ダークの {key}Brush");
            }
            var dk = dark.Keys.OfType<string>().Order(StringComparer.Ordinal).ToList();
            var lk = light.Keys.OfType<string>().Order(StringComparer.Ordinal).ToList();
            Check.SequenceEqual(dk, lk, "ダークとライトのキーの集合");
            Check.Equal((ColorTable.Length + DecorTable.Length) * 2, dk.Count, "キーの数(色とブラシ)");
        });
    }

    [Test]
    public void AC19_2_3_コントラスト比が基準以上で表の最小の値に一致する()
    {
        UiHost.Invoke(() =>
        {
            var darkMin = new Dictionary<string, double>();
            var lightMin = new Dictionary<string, double>();
            var problems = ContrastProblems(Lookup(AppResources.DarkTokens), "ダーク", darkMin)
                .Concat(ContrastProblems(Lookup(AppResources.LightTokens), "ライト", lightMin)).ToList();
            Check.True(problems.Count == 0, "基準を下回る組: " + string.Join(" / ", problems));
            foreach (var (fg, _, _, dmin, lmin) in ContrastTable)
            {
                Check.Near(dmin, darkMin[fg], 0.01, $"ダークの {fg} の最小の比");
                Check.Near(lmin, lightMin[fg], 0.01, $"ライトの {fg} の最小の比");
            }
        });
    }

    [Test]
    public void AC19_4_検査自身_text_secondaryをsurface3に近づけると失敗する()
    {
        UiHost.Invoke(() =>
        {
            var d = new ResourceDictionary();
            foreach (var key in AppResources.DarkTokens.Keys.OfType<string>())
                if (AppResources.DarkTokens[key] is Color c) d[key] = c;
            var s3 = (Color)d["Surface3"];
            d["TextSecondary"] = Color.FromRgb((byte)(s3.R + 8), (byte)(s3.G + 8), (byte)(s3.B + 8));
            var problems = ContrastProblems(Lookup(d), "改変したダーク");
            Check.True(problems.Any(p => p.Contains("TextSecondary", StringComparison.Ordinal)), "text-secondary を変えても検査が失敗しない");
        });
    }

    // §9.1 の余白の表と §9.3 の領域と画面の表の寸法(キー, 仕様の値)
    private static readonly (string Key, double Value)[] GridTable =
    [
        ("Space1", 4), ("Space2", 8), ("Space3", 12), ("Space4", 16), ("Space5", 24), ("Space6", 32), ("HitMin", 32),
        ("CommandBarHeight", 48), ("NoticeBarHeight", 40),
        ("LeftPaneWidth", 312), ("LeftPaneMinWidth", 240), ("LeftPaneMaxWidth", 420),
        ("RightPaneWidth", 320), ("RightPaneMinWidth", 280), ("RightPaneMaxWidth", 480),
        ("StackMinHeight", 160), ("ParamsMinHeight", 160), ("RightPaneHeaderHeight", 32),
        ("LayerListMinHeight", 128), ("LayerListDefaultHeight", 176), ("LayerRowHeight", 40),
        ("LayerRowToggle", 32), ("LayerRowThumb", 32), ("LayerRowDuplicate", 32), ("LayerRowDelete", 32), ("LayerRowNameMin", 56),
        ("LayerFrameHandle", 8), ("LayerFrameHandleHit", 16),
        ("TimelineHeight", 56), ("TimelineWrappedHeight", 96), ("ExportBandHeight", 72),
        ("DragAreaWidth", 64), ("DragAreaHeight", 48), ("TileSize", 132),
        ("WindowMinWidth", 1120), ("WindowMinHeight", 640), ("WindowDefaultWidth", 1280), ("WindowDefaultHeight", 800),
        ("CanvasMinWidth", 448), ("CanvasMinHeight", 240), ("CanvasCheckerCell", 8),
        ("TimelinePlayWidth", 32), ("TimelineScrubMinWidth", 120), ("TimelineLengthWidth", 88), ("TimelineFpsWidth", 80),
        ("TimelineLoopWidth", 88), ("TimelineHoldWidth", 104), ("TimelineReseedWidth", 32),
        ("ExportFormatWidth", 96), ("ExportOptionsSize", 32), ("ExportSizeWidth", 160), ("ExportLimitWidth", 200),
        ("ExportStatusMinWidth", 160), ("ExportCopyMinWidth", 96), ("ExportSaveMinWidth", 80), ("OutputFlyoutWidth", 320),
        ("DialogSettingsWidth", 520), ("DialogSettingsHeight", 640), ("DialogBatchWidth", 640), ("DialogBatchHeight", 560),
        ("DialogAboutWidth", 560), ("DialogAboutHeight", 520), ("DialogReplaceWidth", 440), ("DialogEmptyCanvasWidth", 400),
        ("DialogConfirmOverWidth", 440), ("DialogErrorWidth", 480), ("DialogShortcutsWidth", 560), ("DialogShortcutsHeight", 600),
        ("DialogNameWidth", 400), ("DialogOverwriteWidth", 440),
    ];

    [Test]
    public void AC19_5_余白と角丸と当たり判定と領域と画面の寸法が4の倍数()
    {
        UiHost.Invoke(() =>
        {
            var r = AppResources.LayoutTokens;
            foreach (var (key, value) in GridTable)
            {
                var v = (double)r[key];
                Check.Equal(value, v, $"{key} の値");
                Check.True(v % 4 == 0, $"{key}={v} が 4 の倍数でない");
            }
            Check.Equal(4.0, ((CornerRadius)r["RadiusControl"]).TopLeft, "radius-control");
            Check.Equal(8.0, ((CornerRadius)r["RadiusCard"]).TopLeft, "radius-card");
            Check.Equal(1.0, (double)r["Stroke"], "stroke");
            // 余白の Thickness(組み合わせを含む)の各辺も 4 の倍数
            foreach (var key in r.Keys.OfType<string>())
            {
                if (r[key] is not Thickness t || key.StartsWith("Stroke", StringComparison.Ordinal) || key == "TabUnderlineThickness") continue;
                foreach (var side in new[] { t.Left, t.Top, t.Right, t.Bottom })
                    Check.True(side % 4 == 0, $"{key} の辺 {side} が 4 の倍数でない");
            }
        });
    }

    [Test]
    public void AC19_6_文字のトークンが表と一致する()
    {
        UiHost.Invoke(() =>
        {
            var r = AppResources.LayoutTokens;
            (string Name, double Size, double Line, FontWeight Weight)[] table =
            [
                ("Caption", 12, 16, FontWeights.Normal),
                ("Body", 14, 20, FontWeights.Normal),
                ("BodyStrong", 14, 20, FontWeight.FromOpenTypeWeight(600)),
                ("Subtitle", 20, 28, FontWeight.FromOpenTypeWeight(600)),
                ("Title", 28, 36, FontWeight.FromOpenTypeWeight(600)),
            ];
            foreach (var (name, size, line, weight) in table)
            {
                Check.Equal(size, (double)r["FontSize" + name], $"{name} の大きさ");
                Check.Equal(line, (double)r["LineHeight" + name], $"{name} の行の高さ");
                Check.Equal(weight, (FontWeight)r["FontWeight" + name], $"{name} の太さ");
            }
            var text = ((FontFamily)r["FontFamilyText"]).Source;
            var display = ((FontFamily)r["FontFamilyDisplay"]).Source;
            Check.True(text.StartsWith("Segoe UI Variable Text", StringComparison.Ordinal) && text.Contains("Yu Gothic UI") && text.Contains("Meiryo UI"), $"本文のフォント: {text}");
            Check.True(display.StartsWith("Segoe UI Variable Display", StringComparison.Ordinal), $"見出しのフォント: {display}");
        });
    }
}
