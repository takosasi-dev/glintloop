// ウェルカム画面の「画像を取り込むと使えます」(ui.tsv の panes.disabled。§9.3・AC-11 の (5))が、左ペイン・レイヤーの一覧・スタック・
// パラメータ欄・書き出し帯のそれぞれに、ちょうど 1 つずつ見えていることの検査。見えている TextBlock を木から数え、中心がどの領域に入るかで分ける。
// ペインの中の文は各ペインの担当が出し、書き出し帯は状態欄に出す(部品の上に重ねない)。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlintLoop.App.Text;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class PaneDisabledTextTests
{
    [Test]
    public void AC11_5_ウェルカム画面の画像を取り込むと使えますは各領域にちょうど1つ見える()
    {
        using var app = TestApp.Start();
        UiHost.Idle();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            var text = NoticeTexts.UiText("panes.disabled");
            var regions = new (string Name, FrameworkElement Element)[]
            {
                ("左ペイン", w.LeftPane), ("レイヤーの一覧", w.LayerListHost), ("スタック", w.StackHost), ("パラメータ欄", w.ParamsHost),
                ("書き出し帯", w.ExportBandFrame),
            };
            var bounds = regions.Select(r => r.Element.TransformToAncestor(w).TransformBounds(new Rect(0, 0, r.Element.ActualWidth, r.Element.ActualHeight))).ToArray();
            var counts = new int[regions.Length];
            var total = 0;
            void Walk(DependencyObject d)
            {
                if (d is UIElement { IsVisible: false }) return;
                if (d is TextBlock t && t.Text == text && t.ActualWidth > 0 && t.ActualHeight > 0)
                {
                    total++;
                    var r = t.TransformToAncestor(w).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                    var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                    for (var i = 0; i < regions.Length; i++)
                        if (bounds[i].Contains(center)) counts[i]++;
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(w);
            for (var i = 0; i < regions.Length; i++) Check.Equal(1, counts[i], $"{regions[i].Name}に見えている「{text}」の数");
            Check.Equal(regions.Length, total, $"ウィンドウ全体で見えている「{text}」の数");
        });
    }
}
