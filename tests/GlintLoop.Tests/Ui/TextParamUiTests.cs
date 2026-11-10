// 担当 text の画面のつなぎ(TextModule)と、パラメータ欄の text・font の入力部品のテスト(AC-24 の (1)(2)、FR-37・FR-38・E-26・E-27)。
// 部品は ParamEditors から作り、メインウィンドウの ParamsHost に差し込んで確かめる(欄そのものは担当 stack が作る)。入力はウィンドウの中へ直接発生させ、
// 貼り付けはメモリの DataObject を Pasting のイベントで渡す(実際のクリップボードを使わない。INV-18)。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Params;
using GlintLoop.App.TextEffect;
using GlintLoop.Codec.Text;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class TextParamUiTests
{
    private const string Fake = "GlintLoop Test No Such Font 架空";

    /// <summary>空のキャンバスに text の行を 1 つ足し、key のパラメータの部品を作って ParamsHost に入れる(UI スレッドで呼ぶ)。</summary>
    private static FrameworkElement Editor(TestApp app, string key, out int row)
    {
        var s = app.Shell.Session;
        if (!s.HasDocument) s.NewEmptyCanvas(64, 64);
        row = s.AddEffect(Target.All, "text").Row!.Value;
        var def = s.Catalog.Find("text")!;
        var ctx = new ParamEditorContext(app.Shell, Target.All, row, def, def.FindParam(key)!);
        var editor = app.Shell.ParamEditors.TryCreate(ctx) ?? throw new CheckFailedException(key + " の部品が登録されていない");
        app.Shell.Window.ParamsHost.Content = editor;
        editor.UpdateLayout();
        return editor;
    }

    /// <summary>
    /// 部品の Loaded が済むまで待つ(テストのスレッドで呼ぶ)。text・font の部品は Loaded で初めて Session.Changed を受け始めるので、
    /// 作った直後に Session を操作して部品の読み直しを確かめるときは、先にこれを呼ぶ(UpdateLayout だけでは Loaded は上がらない)。
    /// </summary>
    private static void WaitLoaded(FrameworkElement editor) =>
        Check.True(UiHost.WaitUntil(() => editor.IsLoaded, TimeSpan.FromSeconds(5)), "部品の Loaded が上がらない");

    private static string Value(TestApp app, int row, string key) =>
        app.Ui(() => app.Shell.Session.Document!.All.Stack[row].Get(key) is StringValue s ? s.Value : throw new CheckFailedException(key + " の値が無い"));

    /// <summary>文字の入力(PreviewTextInput → TextInput)を発生させる。受け付けられたら true。</summary>
    private static void Type(TextBox box, string text)
    {
        var composition = new TextComposition(InputManager.Current, box, text);
        var e = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        box.RaiseEvent(e);
        if (e.Handled) return;
        e = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent };
        box.RaiseEvent(e);
    }

    private static void Paste(TextBox box, string text)
    {
        var data = new DataObject(DataFormats.UnicodeText, text);
        var e = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
        box.RaiseEvent(e);
        Check.True(e.CommandCancelled, "貼り付けの既定の動作が止められていない");
    }

    private static void Clear(TextBox box)
    {
        box.SelectAll();
        box.Text = "";
    }

    [Test]
    public void TextModuleがOrder500でつながりtextとfontの部品を登録する()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var module = app.Shell.Modules.OfType<TextModule>().SingleOrDefault() ?? throw new CheckFailedException("TextModule がつながっていない");
            Check.Equal(500, module.Order, "Order");
            Check.True(Editor(app, "text", out _) is TextParamEditor, "text の部品が TextParamEditor でない");
            Check.True(Editor(app, "font", out _) is FontParamEditor, "font の部品が FontParamEditor でない");
        });
    }

    [Test]
    public void 共有の文書の描画にテキストの受け口が入る()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        Check.True(UiHost.WaitUntil(() => renderer.TextRasterizer is not null, TimeSpan.FromSeconds(10)), "TextRasterizer が入らない");
        var module = app.Ui(() => app.Shell.Modules.OfType<TextModule>().Single());
        Check.True(ReferenceEquals(module.Rasterizer, renderer.TextRasterizer), "入った受け口が TextModule のものでない");
    }

    [Test]
    public void AC24_1_入力欄に200文字と改行5回までが入り201文字目と6回目の改行は受け付けない()
    {
        using var app = TestApp.Start();
        var row = 0;
        app.Ui(() =>
        {
            var editor = (TextParamEditor)Editor(app, "text", out row);
            var box = editor.Box;
            box.Focus();
            Clear(box);
            for (var i = 0; i < 200; i++) Type(box, "あ");
            Type(box, "い");
            Check.Equal(new string('あ', 200), box.Text, "201 文字目を入れたあとの欄");
            for (var i = 0; i < 6; i++)
            {
                box.CaretIndex = box.Text.Length;
                UiInput.Key(box, Key.Enter);
            }
            Check.Equal(new string('あ', 200) + "\n\n\n\n\n", box.Text, "6 回目の改行を入れたあとの欄(改行は LF 1 個)");
        });
        Check.Equal(new string('あ', 200) + "\n\n\n\n\n", Value(app, row, "text"), "保存される値");
    }

    [Test]
    public void AC24_1_絵文字は1文字で200個が入り201個目は入らない()
    {
        using var app = TestApp.Start();
        var emoji = "\U0001F600";
        var row = 0;
        app.Ui(() =>
        {
            var box = ((TextParamEditor)Editor(app, "text", out row)).Box;
            Clear(box);
            for (var i = 0; i < 200; i++)
            {
                box.CaretIndex = box.Text.Length;
                Type(box, emoji);
            }
            Type(box, emoji);
            Check.Equal(400, box.Text.Length, "絵文字 201 個を入れたあとの UTF-16 の長さ");
            Clear(box);
            Paste(box, string.Concat(Enumerable.Repeat(emoji, 201)));
            Check.Equal(string.Concat(Enumerable.Repeat(emoji, 200)), box.Text, "絵文字 201 個の貼り付け");
        });
        Check.Equal(400, Value(app, row, "text").Length, "保存される値の長さ");
    }

    [Test]
    public void AC24_1_E27_貼り付けは先頭から6行で切られ保存される値の改行はLF()
    {
        using var app = TestApp.Start();
        var row = 0;
        FrameworkElement? editor = null;
        app.Ui(() =>
        {
            editor = Editor(app, "text", out row);
            var box = ((TextParamEditor)editor).Box;
            Clear(box);
            Paste(box, "A\r\nB\nC\rD\r\nE\nF\r\nG");
            Check.Equal("A\nB\nC\nD\nE\nF", box.Text, "欄の文字");
        });
        Check.Equal("A\nB\nC\nD\nE\nF", Value(app, row, "text"), "保存される値");
        // 部品は Loaded で初めて Session.Changed を受け始める。Loaded の前に Undo すると欄を読み直さないので、Loaded を待つ
        WaitLoaded(editor!);
        // Undo で値が戻ると、欄も読み直す
        app.Ui(() =>
        {
            Check.True(app.Shell.Session.Undo(), "Undo できない");
            var box = ((TextParamEditor)app.Shell.Window.ParamsHost.Content).Box;
            Check.Equal("", box.Text, "Undo のあとの欄");
        });
    }

    [Test]
    public void AC24_2_フォントの一覧は名前順で各項目がそのフォントで表示される()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var editor = (FontParamEditor)Editor(app, "font", out _);
            var items = editor.List.Items.Cast<FontItem>().ToList();
            Check.True(items.Count > 10, "一覧の項目が少なすぎる");
            Check.SequenceEqual(InstalledFonts.Families, items.Select(i => i.Name).ToList(), "一覧の名前(システムのフォントだけ)");
            for (var i = 1; i < items.Count; i++)
                Check.True(InstalledFonts.Compare(items[i - 1].Name, items[i].Name) < 0, $"名前順でない: {items[i - 1].Name} と {items[i].Name}");
            foreach (var item in items)
            {
                Check.Equal(item.Name, item.Family.Source, item.Name + " の表示のフォント");
                Check.True(InstalledFonts.IsInstalled(item.Name), item.Name + " を SkiaSharp で引けない");
            }
            Check.Equal(TextSpec.DefaultFont, ((FontItem)editor.List.SelectedItem).Name, "既定で選ばれている項目");
            // 一覧は開かない(ポップアップは実際の画面に出る。INV-18)。項目のテンプレートに値を入れて、文字とフォントを調べる
            foreach (var i in new[] { 0, editor.List.Items.Count / 2, editor.List.Items.Count - 1 })
            {
                var item = (FontItem)editor.List.Items[i];
                var presenter = new ContentPresenter { Content = item, ContentTemplate = editor.List.ItemTemplate };
                presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var tb = (TextBlock)VisualTreeHelper.GetChild(presenter, 0);
                Check.Equal(item.Name, tb.Text, $"項目 {i} の文字");
                Check.Equal(item.Name, tb.FontFamily.Source, $"項目 {i} の表示のフォント");
            }
            Check.True(VirtualizingPanel.GetIsVirtualizing(editor.List), "一覧が仮想化されていない");
            Check.True(editor.List.ItemsPanel.VisualTree?.Type == typeof(VirtualizingStackPanel), "一覧のパネルが仮想化のものでない");
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }

    [Test]
    public void AC24_2_E26_保存済みの編集に無いフォントがあるとfont_missingが出て値は保存した名前のまま()
    {
        using var app = TestApp.Start();
        var row = 0;
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.NewEmptyCanvas(64, 64);
            row = s.AddEffect(Target.All, "text").Row!.Value;
            Check.True(s.SetEffectParam(Target.All, row, "font", new StringValue(Fake)).Changed, "架空のフォント名を入れられない");
            var read = DocumentJson.Read(s.Json!, s.Catalog, new MemoryLogSink());
            Check.True(read.IsValid, "保存した文書が読めない");
            s.CloseDocument();
            app.Shell.NoticeBar.Close();
            Check.True(!app.Shell.NoticeBar.IsShowing, "通知が閉じない");
            // 保存済みの編集を開く
            s.OpenDocument(read.Document!, []);
        });
        UiHost.Idle();
        var editor = app.Ui(() =>
        {
            Check.Equal(NoticeKeys.FontMissing, app.Shell.NoticeBar.Current?.Key, "通知バーの通知");
            Check.Equal(NoticeLevel.Warn, app.Shell.NoticeBar.Current!.Level, "通知の種別");
            var def = app.Shell.Session.Catalog.Find("text")!;
            var editor = (FontParamEditor)app.Shell.ParamEditors.TryCreate(new ParamEditorContext(app.Shell, Target.All, row, def, def.FindParam("font")!))!;
            app.Shell.Window.ParamsHost.Content = editor;
            editor.UpdateLayout();
            var selected = (FontItem)editor.List.SelectedItem;
            Check.Equal(Fake, selected.Name, "選ばれている項目(保存した名前)");
            Check.True(selected.Missing, "無いフォントの項目に印が無い");
            Check.Equal(TextSpec.DefaultFont, selected.Family.Source, "無いフォントの項目の表示のフォント");
            return editor;
        });
        // 部品が Session.Changed を受け始めてから(Loaded のあと)、Session を操作する
        WaitLoaded(editor);
        app.Ui(() =>
        {
            // 同じ文書の中では、同じ名前で何度も出さない
            app.Shell.NoticeBar.Close();
            app.Shell.Session.SetEffectParam(Target.All, row, "size", new NumberValue(150));
        });
        UiHost.Idle();
        app.Ui(() => Check.True(!app.Shell.NoticeBar.IsShowing, "同じ名前で通知がもう一度出た"));
        Check.Equal(Fake, Value(app, row, "font"), "font の値(保存した名前のまま)");
        Check.SequenceEqual([Fake], app.Ui(() => TextModule.MissingFonts(app.Shell.Session.Document)), "無いフォントの一覧");
    }
}
