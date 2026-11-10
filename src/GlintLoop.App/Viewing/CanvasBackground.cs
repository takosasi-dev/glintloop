// キャンバスの背景(FR-20): 市松模様(8 DIP 四方の升目で CanvasChecker1 と CanvasChecker2 を交互)・黒・白・灰(トークンの色)・選んだ色の 5 種。
// 背景は合成の矩形の中だけに敷くので、透明な部分だけに見える。出力には影響しない(描画の結果の画素は変えない)。
// 選択は設定の bgPreview(AppSettings.BgPreview)に入れる(保存は SettingsStore が行う)。項目の表示名は ui.tsv の canvas.bg.*。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlintLoop.App.Text;
using GlintLoop.Core.Settings;

namespace GlintLoop.App.Viewing;

public static class CanvasBackground
{
    /// <summary>一覧の順と表示名(§9.3 のコマンドバーの表の 10 の順。表示名は ui.tsv の canvas.bg.*)。</summary>
    public static IReadOnlyList<(BgPreviewMode Mode, string Label)> Items { get; } =
    [
        (BgPreviewMode.Checker, NoticeTexts.UiText("canvas.bg.checker")),
        (BgPreviewMode.Black, NoticeTexts.UiText("canvas.bg.black")),
        (BgPreviewMode.White, NoticeTexts.UiText("canvas.bg.white")),
        (BgPreviewMode.Gray, NoticeTexts.UiText("canvas.bg.gray")),
        (BgPreviewMode.Custom, NoticeTexts.UiText("canvas.bg.custom")),
    ];

    public static string LabelOf(BgPreviewMode mode) => Items.First(i => i.Mode == mode).Label;

    /// <summary>背景を部品に塗る。黒・白・灰はトークンのブラシを参照し(テーマの切替に追従)、市松模様はトークンの 2 色から作る。</summary>
    public static void Apply(Border target, BgPreview bg)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (bg.Mode)
        {
            case BgPreviewMode.Black:
                target.SetResourceReference(Border.BackgroundProperty, "CanvasBlackBrush");
                break;
            case BgPreviewMode.White:
                target.SetResourceReference(Border.BackgroundProperty, "CanvasWhiteBrush");
                break;
            case BgPreviewMode.Gray:
                target.SetResourceReference(Border.BackgroundProperty, "CanvasGrayBrush");
                break;
            case BgPreviewMode.Custom:
                target.Background = Solid(bg.Color);
                break;
            default:
                target.Background = Checker(target);
                break;
        }
    }

    /// <summary>0xRRGGBB の不透明の色のブラシ。</summary>
    public static SolidColorBrush Solid(int rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    /// <summary>市松模様のブラシ。左上の升目が CanvasChecker1、その右と下が CanvasChecker2。部品の左上を升目の原点にする。</summary>
    public static Brush Checker(FrameworkElement scope)
    {
        // トークンの色からブラシを作る(共有のリソースのブラシを凍らせない)
        var c1 = new SolidColorBrush((Color)scope.FindResource("CanvasChecker1"));
        var c2 = new SolidColorBrush((Color)scope.FindResource("CanvasChecker2"));
        var cell = (double)scope.FindResource("CanvasCheckerCell");
        var tile = new Rect(0, 0, cell * 2, cell * 2);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(c1, null, new RectangleGeometry(tile)));
        var dark = new GeometryGroup();
        dark.Children.Add(new RectangleGeometry(new Rect(cell, 0, cell, cell)));
        dark.Children.Add(new RectangleGeometry(new Rect(0, cell, cell, cell)));
        group.Children.Add(new GeometryDrawing(c2, null, dark));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }
}
