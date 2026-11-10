// テストの土台の確かめ: 製品のウィンドウが画面の外に出て、自前のスタイルが Fluent の暗黙のスタイルに勝ち、UIA でコマンドバーが見えること。
using System.Windows;
using System.Windows.Controls;
using GlintLoop.App.Theme;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class ShellSmokeTests
{
    [Test]
    public void U0_ウィンドウが画面の外に非アクティブで出て自前のテンプレートが使われる()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            Check.Near(-20000.0, w.Left, 1.0, "Left");
            Check.True(!w.ShowActivated, "ShowActivated が false でない");
            var b = new Button { Content = "x" };
            ((Panel)w.CommandBar.Children[0]).Children.Add(b);
            b.ApplyTemplate();
            Check.True(b.Template == ((Style)AppResources.Controls["SecondaryButton"]).Setters.OfType<Setter>().First(s => s.Property == Control.TemplateProperty).Value,
                "暗黙の Button のテンプレートが自前の SecondaryButton でない(Fluent に負けている)");
            ((Panel)w.CommandBar.Children[0]).Children.Remove(b);
        });
        var root = Uia.Root(app.Hwnd);
        Check.True(Uia.TryFind(root, "設定") is not null, "UIA でコマンドバーの「設定」が見つからない");
    }
}
