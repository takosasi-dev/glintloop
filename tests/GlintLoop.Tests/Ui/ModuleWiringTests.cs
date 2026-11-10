// 統合(ModuleWiring と各担当の受け口の結び)の確かめ: ウェルカム画面の空のキャンバス、取り込みの門が最初の表示まで閉じていること、文書の描画にテキストの受け口が最初から入っていること、
// すべてリセットのボタンが 1 つだけであること、左ペインのエフェクトのタブが 1 つだけで中身が stack の一覧であること、レイヤーの設定と行の選択の切り替え(FR-48・FR-50)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using GlintLoop.App.Layers;
using GlintLoop.App.Looks;
using GlintLoop.App.Screens;
using GlintLoop.App.Stack;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Ui;

public sealed class ModuleWiringTests
{
    [Test]
    public void FR44_ウェルカム画面の空のキャンバスのボタンでダイアログが開く()
    {
        using var app = TestApp.Start();
        var screens = M<ScreensModule>(app);
        app.Ui(() => screens.Welcome.EmptyCanvasButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is EmptyCanvasDialog, Wait), "空のキャンバスのダイアログが開かない");
        app.Ui(() => app.Shell.Dialogs.CancelTop());
    }

    [Test]
    public void E20_取り込みの門はキャンバスに加工後の表示を載せ終えてから開く()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var screens = M<ScreensModule>(app);
        var canvas = M<CanvasModule>(app);
        var shown = new List<bool>();
        app.Ui(() => screens.Imports.Completed += _ =>
            shown.Add(canvas.Picture.Bitmap is not null && ReferenceEquals(canvas.Processed.PresentedDocument, app.Shell.Session.Document)));
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(64, 48, Img.Solid(64, 48, 9, 90, 9)));
        Check.True(Key(app, System.Windows.Input.Key.V, System.Windows.Input.ModifierKeys.Control), "Ctrl+V");
        WaitImports(app);
        Check.SequenceEqual([true], shown, "門を出たときに表示が載っていない");
    }

    [Test]
    public void 共有の文書の描画は作ったときからテキストの受け口を持つ()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        Check.True(ReferenceEquals(app.Shell.TextRasterizer, renderer.TextRasterizer), "描画にテキストの受け口が入っていない");
        var text = M<GlintLoop.App.TextEffect.TextModule>(app);
        Check.True(ReferenceEquals(app.Shell.TextRasterizer, app.Ui(() => text.Rasterizer)), "担当 text の受け口が共有のものでない");
    }

    [Test]
    public void FR32_すべてリセットのボタンは右ペインの見出しに1つだけ()
    {
        using var app = Start(layers: 2);
        var root = Uia.Root(app.Hwnd);
        var buttons = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, NoticeName("layers.reset_all")),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        Check.Equal(1, buttons.Count, "すべてリセットのボタンの数");
        Check.Equal("stack.resetAll", buttons[0].Current.AutomationId, "AutomationId");
        var layers = Layers(app);
        Check.True(app.Ui(() => ReferenceEquals(app.Shell.Window.RightHeaderHost.Content, layers.Header)), "見出しが layers の部品でない");
        Uia.Invoke(buttons[0]);
        UiHost.Idle();
        Check.True(app.Ui(() => app.Shell.Session.Document!.Layers.All(l => l.Stack.Count == 0)), "すべてリセットでスタックが空にならない");
    }

    private static string NoticeName(string key) => GlintLoop.App.Text.NoticeTexts.UiText(key);

    [Test]
    public void FR26_左ペインのエフェクトのタブは1つだけで中身はstackの一覧()
    {
        using var app = Start(layers: 1, gpu: false);
        var looks = M<LooksModule>(app);
        var stack = M<StackModule>(app);
        app.Ui(() =>
        {
            Check.Equal(3, looks.LeftPane.Tabs.Items.Count, "左ペインのタブの数");
            Check.True(ReferenceEquals(looks.LeftPane.EffectsContent, stack.EffectsTab), "エフェクトのタブの中身");
        });
        var root = Uia.Root(app.Hwnd);
        var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        Check.Equal(1, tabs.Cast<AutomationElement>().Count(t => t.Current.Name == StackTexts.EffectsTab), "「エフェクト」のタブの数");
    }

    [Test]
    public void FR48_FR50_行を選ぶとレイヤーの設定が隠れて枠が消え同じ行のクリックで設定に戻る()
    {
        using var app = Start(layers: 2);
        var stack = M<StackModule>(app);
        var layers = Layers(app);
        app.Ui(() =>
        {
            Check.True(layers.ShowsSettings && layers.Settings.IsVisible, "行を選んでいないのに設定が出ない");
            Check.True((layers.Frame.ShownLayer is not null), "行を選んでいないのに枠が出ない");
            // ぼかし(既定は gaussian。角度は visibleWhen で隠れる)を選んでも、枠は出たまま(AC-29 の (3))
            stack.SelectRow(0);
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.True(!layers.ShowsSettings && !layers.Settings.IsVisible, "行を選んでも設定が出たまま");
            Check.True((layers.Frame.ShownLayer is not null), "ハンドルを持たない行を選ぶと枠が消える");
            Check.True(stack.AddEffect("crop").Changed, "切り抜きを足せない");
        });
        UiHost.Idle();
        app.Ui(() => Check.True(!(layers.Frame.ShownLayer is not null), "切り抜きの行を選んでも枠が出たまま"));
        // 一覧の同じ行をもう一度クリック → 行の選択を外して設定へ
        app.Ui(() =>
        {
            var row = layers.List.Rows.First(r => r.IsSelected);
            row.NameText.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent, Source = row.NameText });
            row.NameText.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent, Source = row.NameText });
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.True(stack.SelectedRow is null, "同じ行のクリックで行の選択が外れない");
            Check.True(layers.ShowsSettings && layers.Settings.IsVisible, "同じ行のクリックで設定が出ない");
            Check.True((layers.Frame.ShownLayer is not null), "設定に戻っても枠が出ない");
        });
    }
}
