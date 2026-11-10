// レイヤーの画面のテストの道具: empty のレイヤーだけでできた文書をコードで組み立てて開く(画像のファイルを作らずに、大きさと配置を自由に決める)、
// レイヤーの部品(LayersModule)を取り出す、一覧の行を UIA で探す、修飾キーつきのキーを送る。
// GPU を使うテストは、取り込み(ImageImporter)で画素を持つ文書を作る。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using GlintLoop.App.Layers;
using GlintLoop.Core.Session;

namespace GlintLoop.Tests.Ui;

internal static class LayersTestKit
{
    public static Layer Empty(string name, int w, int h, Placement? place = null, string? fill = null, bool visible = true,
        int opacity = 100, BlendMode blend = BlendMode.Normal) =>
        new(name, LayerKind.Empty, null, new EmptySize(w, h), fill ?? ColorText.Transparent, visible, opacity, blend,
            place ?? Placement.Identity, 100, Array.Empty<StackEntry>());

    public static SessionDocument Doc(params Layer[] layers) =>
        new(7, 0, TimeSettings.Default, Target.OfLayer(layers.Length - 1), Array.Empty<AssetEntry>(), layers, GlobalStack.Empty, OutputSettings.Default);

    /// <summary>文書を開き(履歴は空)、レイアウトが済むまで待つ。</summary>
    public static void Open(TestApp app, SessionDocument doc)
    {
        app.Ui(() => app.Shell.Session.OpenDocument(doc, Array.Empty<SourceInfo>()));
        UiHost.Idle();
    }

    public static LayersModule Module(TestApp app) => app.Ui(() => app.Shell.Modules.OfType<LayersModule>().Single());

    /// <summary>修飾キーを入れてキーを 1 回送る(UI スレッドで。フォーカスのある要素へ)。</summary>
    public static bool Key(TestApp app, Key key, ModifierKeys mods = ModifierKeys.None) => app.Ui(() =>
    {
        app.Modifiers.Current = mods;
        try
        {
            return UiInput.Key(app.Shell.Window, key);
        }
        finally
        {
            app.Modifiers.Current = ModifierKeys.None;
        }
    });

    /// <summary>一覧の行(UIA の ListItem)を上から順に。</summary>
    public static IReadOnlyList<AutomationElement> Rows(TestApp app)
    {
        var list = Uia.Find(Uia.Root(app.Hwnd), LayerTexts.List);
        return list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToList();
    }

    public static AutomationElement? Child(AutomationElement row, string name) =>
        row.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    /// <summary>番号の並びで、いまのレイヤーの名前。</summary>
    public static string[] Names(TestApp app) => app.Ui(() => app.Shell.Session.Document!.Layers.Select(l => l.Name).ToArray());

    /// <summary>一覧の、番号 index のレイヤーの行の部品(UI スレッドで呼ぶ)。</summary>
    public static LayerRowItem RowOf(LayersModule m, int index) => m.List.Rows.First(r => !r.IsAll && r.LayerIndex == index);

    public static Rect Dip(TestApp app, AutomationElement e) => Uia.RectDip(e, app.DpiScale);
}
