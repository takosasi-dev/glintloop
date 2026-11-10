// §9.1 のアイコンの一覧(29 個)と、そのグリフ。コードポイントは Microsoft Learn の「Segoe Fluent Icons font」の一覧で名前から確かめた(2026-10-28)。
// 一覧に無い 2 つ(複製・分割比較)は、自作の Geometry(Controls.xaml の IconDuplicateGeometry・IconSplitGeometry)で描く。
// Create は、アイコンボタンの中身(グリフの TextBlock か Path)を作る。文字の色は親の Foreground を継ぐ。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlintLoop.App.Theme;

/// <summary>1 つのアイコン。Name は §9.1 の一覧の名前(UIA の Name とツールチップに使う)。Glyph か GeometryKey のどちらかを持つ。</summary>
public sealed record IconDef(string Name, string LearnName, char? Glyph, string? GeometryKey);

public static class Icons
{
    public static readonly IconDef Copy = new("コピー", "Copy", '', null);
    public static readonly IconDef Save = new("保存", "Save", '', null);
    public static readonly IconDef Undo = new("元に戻す", "Undo", '', null);
    public static readonly IconDef Redo = new("やり直す", "Redo", '', null);
    public static readonly IconDef Play = new("再生", "Play", '', null);
    public static readonly IconDef Stop = new("停止", "Stop", '', null);
    public static readonly IconDef Add = new("追加", "Add", '', null);
    public static readonly IconDef Delete = new("削除", "Delete", '', null);
    public static readonly IconDef Duplicate = new("複製", "(一覧に無い)", null, "IconDuplicateGeometry");
    public static readonly IconDef Show = new("表示", "RedEye", '', null);
    public static readonly IconDef Hide = new("非表示", "Hide", '', null);
    public static readonly IconDef Settings = new("設定", "Settings", '', null);
    public static readonly IconDef Search = new("検索", "Search", '', null);
    public static readonly IconDef Reseed = new("振り直し", "Refresh", '', null);
    public static readonly IconDef Eyedropper = new("スポイト", "Eyedropper", '', null);
    public static readonly IconDef ZoomIn = new("拡大", "ZoomIn", '', null);
    public static readonly IconDef ZoomOut = new("縮小", "ZoomOut", '', null);
    public static readonly IconDef Fit = new("フィット", "FitPage", '', null);
    public static readonly IconDef Close = new("閉じる", "Cancel", '', null);
    public static readonly IconDef PasteImage = new("画像を貼り付け", "Paste", '', null);
    public static readonly IconDef Open = new("開く", "OpenFile", '', null);
    public static readonly IconDef New = new("新規", "Page", '', null);
    public static readonly IconDef EmptyLayer = new("空のレイヤー", "MapLayers", '', null);
    public static readonly IconDef Original = new("元画像", "Picture", '', null);
    public static readonly IconDef SplitCompare = new("分割比較", "(一覧に無い)", null, "IconSplitGeometry");
    public static readonly IconDef ActualOutput = new("実出力", "Preview", '', null);
    public static readonly IconDef CanvasBackground = new("キャンバスの背景", "BackgroundToggle", '', null);
    public static readonly IconDef Batch = new("バッチ", "PhotoCollection", '', null);
    public static readonly IconDef OutputOptions = new("出力オプション", "Equalizer", '', null);

    /// <summary>ドロップダウンの右端の矢印(一覧の 29 個の外。ChevronDown)。</summary>
    public const char ChevronDown = '';

    /// <summary>§9.1 の一覧の 29 個(順も一覧のとおり)。</summary>
    public static IReadOnlyList<IconDef> All { get; } =
    [
        Copy, Save, Undo, Redo, Play, Stop, Add, Delete, Duplicate, Show, Hide, Settings, Search, Reseed, Eyedropper,
        ZoomIn, ZoomOut, Fit, Close, PasteImage, Open, New, EmptyLayer, Original, SplitCompare, ActualOutput, CanvasBackground,
        Batch, OutputOptions,
    ];

    /// <summary>アイコンの中身を作る。グリフは FontFamilyIcon・IconSize、Geometry は IconSize 四方の Path。</summary>
    public static FrameworkElement Create(IconDef icon)
    {
        if (icon.Glyph is { } g)
        {
            var t = new TextBlock { Text = g.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamilyIcon");
            t.SetResourceReference(TextBlock.FontSizeProperty, "IconSize");
            return t;
        }
        var p = new Path { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        p.SetResourceReference(Path.DataProperty, icon.GeometryKey!);
        p.SetResourceReference(FrameworkElement.WidthProperty, "IconSize");
        p.SetResourceReference(FrameworkElement.HeightProperty, "IconSize");
        p.SetBinding(Shape.FillProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
        return p;
    }
}
