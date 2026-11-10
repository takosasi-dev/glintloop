// 担当 screens の画面の検査: ウェルカム画面と取り込みの配線(AC-11 の (3)(4)(5)(6))、ショートカットの一覧とツールチップ(AC-22 の (4)(5))、
// 設定の画面の 9 項目(AC-30 の (1) の画面の部分)、「このソフトについて」(FR-103)。操作は UIA とウィンドウの中へ直接発生させるイベントだけで行う(INV-18)。
// ドラッグのイベントは DragEventArgs のコンストラクターが公開されていないので、リフレクションで作って RaiseEvent で発生させる。
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.App.Input;
using GlintLoop.App.Screens;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Static;

namespace GlintLoop.Tests.Ui;

public sealed class ScreensTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ScreensModule Screens(TestApp app) => app.Ui(() => app.Shell.Modules.OfType<ScreensModule>().Single());

    private static void WaitImports(TestApp app, ScreensModule m) =>
        Check.True(UiHost.WaitUntil(() => !m.Imports.IsImporting, Wait), "取り込みが終わらない");

    private static int Layers(TestApp app) => app.Ui(() => app.Shell.Session.Document?.Layers.Count ?? 0);

    private static string? NoticeKey(TestApp app) => app.Ui(() => app.Shell.NoticeBar.Current?.Key);

    private static void Sample(TestApp app, ScreensModule m)
    {
        app.Ui(() => { _ = m.Imports.SampleAsync(); });
        WaitImports(app, m);
        Check.Equal(1, Layers(app), "サンプルの取り込み");
    }

    /// <summary>Ctrl などの修飾キーを偽の状態に入れて、キーを 1 回押す(UI スレッド)。処理されたら true。</summary>
    private static bool Press(TestApp app, Key key, ModifierKeys mods = ModifierKeys.None)
    {
        return app.Ui(() =>
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
    }

    private static byte[] Png(byte r = 200) => SkiaBytes.Png(40, 30, Img.Solid(40, 30, r, 10, 10));

    // ---------------- ドラッグのイベント(DragEventArgs はコンストラクターが internal) ----------------

    private static DragEventArgs DragArgs(IDataObject data, RoutedEvent ev, UIElement target)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 5)
            ?? throw new InvalidOperationException("DragEventArgs のコンストラクターが見つからない");
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point(10, 10)]);
        args.RoutedEvent = ev;
        return args;
    }

    private static void RaiseDrag(TestApp app, IDataObject data, RoutedEvent ev)
    {
        var target = app.Shell.Window.RootLayer;
        target.RaiseEvent(DragArgs(data, ev, target));
    }

    // ================================================================ AC-11 (3) フォーカス(FR-2)

    [Test]
    public void AC11_3_文字入力欄の4種類ではCtrlVは取り込まずCtrlShiftVとボタンは取り込む()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        Sample(app, m);
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, Png());
        var inputs = app.Ui(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var list = new List<Control>
            {
                new TextBox(), // テキストの文字列欄
                new GlintLoop.App.Controls.NumberBox { Minimum = 0, Maximum = 10 }, // 数値入力欄
                new TextBox(), // 検索欄
                new TextBox(), // 色の 16 進入力
            };
            var names = new[] { "文字列", "数値", "検索", "色の16進" };
            for (var i = 0; i < list.Count; i++)
            {
                AutomationProperties.SetName(list[i], names[i]);
                list[i].Width = 80;
                panel.Children.Add(list[i]);
            }
            app.Shell.Window.TimelineHost.Content = panel;
            return list;
        });
        UiHost.Idle();
        var expected = 1;
        foreach (var input in inputs)
        {
            var kind = app.Ui(() =>
            {
                Keyboard.Focus(input);
                return ShortcutScope.Of(Keyboard.FocusedElement as DependencyObject);
            });
            Check.Equal(FocusKind.TextInput, kind, "欄のフォーカスの種類");
            Check.True(!Press(app, Key.V, ModifierKeys.Control), "文字入力欄の Ctrl+V を画面が処理した(欄への貼り付けにならない)");
            Check.True(app.Ui(() => !m.Imports.IsImporting), "文字入力欄の Ctrl+V で取り込みが始まった");
            Check.Equal(expected, Layers(app), "文字入力欄の Ctrl+V のあとのレイヤーの枚数");

            app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, Png((byte)(100 + expected)));
            Check.True(Press(app, Key.V, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+V が処理されない");
            WaitImports(app, m);
            expected++;
            Check.Equal(expected, Layers(app), "文字入力欄にフォーカスがあるときの Ctrl+Shift+V");
        }

        // 「画像を貼り付け」ボタン(コマンドバー)は、文字入力欄にフォーカスがあっても取り込む
        app.Ui(() => Keyboard.Focus(inputs[0]));
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, Png(7));
        Uia.Invoke(Uia.Find(Uia.Root(app.Hwnd), "画像を貼り付け"));
        UiHost.WaitUntil(() => Layers(app) == expected + 1, Wait);
        WaitImports(app, m);
        Check.Equal(expected + 1, Layers(app), "「画像を貼り付け」ボタン");

        // 確認ダイアログ(ダイアログ)の表示中は、ダイアログの中の操作だけが働く
        app.Ui(() => { app.Shell.Session.AddEmptyLayer(); });
        var before = Layers(app);
        Check.True(Press(app, Key.N, ModifierKeys.Control), "Ctrl+N が処理されない");
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is ReplaceConfirmView, Wait), "確認ダイアログが出ない");
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, Png(9));
        Press(app, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        app.Ui(() => app.Shell.Router.Invoke(ShortcutAction.PasteImage, null));
        UiHost.Idle();
        Check.True(app.Ui(() => !m.Imports.IsImporting), "ダイアログの表示中に取り込みが始まった");
        Check.Equal(before, Layers(app), "ダイアログの表示中の貼り付け");
        Uia.Invoke(Uia.Find(Uia.Root(app.Hwnd), "キャンセル"));
        Check.True(UiHost.WaitUntil(() => !app.Shell.Dialogs.IsOpen, Wait), "キャンセルで閉じない");
    }

    [Test]
    public void AC11_3_続けて2回貼り付けると両方取り込まれ_空とクリップボードの失敗は通知だけ()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        app.Clipboard.Data = new MemoryImportData();
        Check.True(Press(app, Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
        WaitImports(app, m);
        Check.Equal(NoticeKeys.PasteEmpty, NoticeKey(app), "空のクリップボード");
        app.Clipboard.CantOpen = true;
        Press(app, Key.V, ModifierKeys.Control);
        WaitImports(app, m);
        Check.Equal(NoticeKeys.ClipFailed, NoticeKey(app), "開けないクリップボード");
        app.Clipboard.CantOpen = false;
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, Png(1));
        Press(app, Key.V, ModifierKeys.Control);
        WaitImports(app, m);
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, Png(2));
        Press(app, Key.V, ModifierKeys.Control);
        WaitImports(app, m);
        Check.Equal(2, Layers(app), "2 回の貼り付け(取り込みの門が開いている)");
        Check.True(app.Ui(() => !m.Imports.Gate.IsBusy), "取り込みの門が閉じたまま");
        Check.Equal("貼り付け", app.Ui(() => app.Shell.Session.Document!.Layers[1].Name), "クリップボードからのレイヤーの名前");
    }

    // ================================================================ AC-11 (4) ドロップ(FR-3〜FR-5)

    [Test]
    public void AC11_4_受け入れ表示はドラッグの進入から100ms以内に出て文書の有無で文が変わる()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        using var dir = new TempDir();
        var data = new DataObject(DataFormats.FileDrop, new[] { dir.Write("a.png", Png()) });
        // 時刻の記録: 進入のイベントを発生させてから、受け入れ表示が表示の状態になるまで(UI スレッドの上で測る)。
        // UIA の問い合わせの時間はアプリの応答ではないので数えず、表示が UIA から見えることは別に確かめる
        var root = Uia.Root(app.Hwnd);
        Check.True(Uia.TryFind(root, "ここにドロップして取り込む") is null, "進入の前から受け入れ表示がある");
        var elapsed = app.Ui(() =>
        {
            var sw = Stopwatch.StartNew();
            RaiseDrag(app, data, DragDrop.DragEnterEvent);
            UiHost.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            return m.DropOverlay.IsVisible ? sw.Elapsed.TotalMilliseconds : double.PositiveInfinity;
        });
        Check.True(elapsed <= 100, $"受け入れ表示まで {elapsed:F1} ms");
        Check.True(Uia.TryFind(root, "ここにドロップして取り込む") is not null, "受け入れ表示が UIA で見つからない");
        Check.True(app.Ui(() => m.DropOverlay.IsShowing && m.DropOverlay.ActualWidth >= app.Shell.Window.RootLayer.ActualWidth - 0.5),
            "受け入れ表示がウィンドウ全体を覆っていない");

        app.Ui(() => RaiseDrag(app, data, DragDrop.DropEvent));
        WaitImports(app, m);
        Check.True(app.Ui(() => !m.DropOverlay.IsShowing), "ドロップのあとも受け入れ表示が残る");
        Check.Equal(1, Layers(app), "ドロップで取り込む");

        app.Ui(() => RaiseDrag(app, data, DragDrop.DragEnterEvent));
        Check.Equal("ここにドロップしてレイヤーを追加", app.Ui(() => m.DropOverlay.Text), "文書があるときの文");
        app.Ui(() =>
        {
            var a = DragArgs(data, DragDrop.DragLeaveEvent, app.Shell.Window.RootLayer);
            app.Shell.Window.RootLayer.RaiseEvent(a);
        });
        // 進入の位置(10, 10)はウィンドウの中なので、子の間の移動とみなして残す。ウィンドウの外へ出たら消える
        app.Ui(() => m.HideDropOverlay());
        Check.True(app.Ui(() => !m.DropOverlay.IsShowing), "受け入れ表示が消えない");
    }

    [Test]
    public void AC11_4_複数のファイルは読めたものを順に足し通知を1つ出しUndo1回で消える()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        using var dir = new TempDir();
        var a = dir.Write("A.png", Png(10));
        var t = dir.Write("text.png", "これは画像ではない"u8.ToArray());
        var c = dir.Write("C.jpg", SkiaBytes.Jpeg(40, 30, Img.Solid(40, 30, 0, 200, 0)));

        app.Ui(() => RaiseDrag(app, new DataObject(DataFormats.FileDrop, new[] { a, t, c }), DragDrop.DropEvent));
        WaitImports(app, m);
        Check.Equal(2, Layers(app), "読めた 2 個");
        app.Ui(() =>
        {
            var d = app.Shell.Session.Document!;
            Check.Equal("A", d.Layers[0].Name, "最初の 1 枚が土台");
            Check.Equal("C", d.Layers[1].Name, "2 枚目");
            var n = app.Shell.NoticeBar.Current!;
            Check.Equal(NoticeKeys.ImportMulti, n.Key, "通知");
            Check.Equal("3", n.Args["N"], "N");
            Check.Equal("2", n.Args["M"], "M");
        });

        // 読めるファイル 3 個は layer.added。Undo の 1 回で足した全部が消える
        var d1 = dir.Write("D.png", Png(20));
        var d2 = dir.Write("E.png", Png(30));
        var d3 = dir.Write("F.png", Png(40));
        app.Ui(() => RaiseDrag(app, new DataObject(DataFormats.FileDrop, new[] { d1, d2, d3 }), DragDrop.DropEvent));
        WaitImports(app, m);
        Check.Equal(5, Layers(app), "3 個を足したあと");
        Check.Equal(NoticeKeys.LayerAdded, NoticeKey(app), "3 個とも読めたときの通知");
        app.Ui(() => app.Shell.Session.Undo());
        Check.Equal(2, Layers(app), "Undo の 1 回で 3 枚が消える");

        // .glint と PNG: PNG だけ足し、.glint は開かない
        var g = dir.Write("p.glint", [0x50, 0x4B, 0x03, 0x04, 0, 0]);
        var p = dir.Write("G.png", Png(50));
        app.Ui(() => RaiseDrag(app, new DataObject(DataFormats.FileDrop, new[] { g, p }), DragDrop.DropEvent));
        WaitImports(app, m);
        Check.Equal(3, Layers(app), ".glint と PNG");
        app.Ui(() =>
        {
            var n = app.Shell.NoticeBar.Current!;
            Check.Equal(NoticeKeys.ImportMulti, n.Key, ".glint と PNG の通知");
            Check.Equal("2", n.Args["N"], "N");
            Check.Equal("1", n.Args["M"], "M");
        });

        // URL だけのデータは import.web_url。PNG の画像データは取り込む
        var url = new DataObject();
        url.SetData(DataFormats.UnicodeText, "https://example.invalid/a.png");
        app.Ui(() => RaiseDrag(app, url, DragDrop.DropEvent));
        WaitImports(app, m);
        Check.Equal(NoticeKeys.ImportWebUrl, NoticeKey(app), "URL だけのドロップ");
        Check.Equal(3, Layers(app), "URL だけのドロップでは足さない");
        var png = new DataObject();
        png.SetData(ImportFormatNames.Png, new MemoryStream(Png(60)));
        app.Ui(() => RaiseDrag(app, png, DragDrop.DropEvent));
        WaitImports(app, m);
        Check.Equal(4, Layers(app), "PNG の画像データのドロップ");
    }

    // ================================================================ AC-11 (5) 開く・起動・ウェルカム画面(FR-6・FR-7・FR-14)

    [Test]
    public void AC11_5_開くで選んだ画像を取り込み前回のフォルダを覚え_壊れたglintはproject_invalid()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        using var dir = new TempDir();
        var a = dir.Write("A.png", Png(10));
        var b = dir.Write("B.jpg", SkiaBytes.Jpeg(40, 30, Img.Solid(40, 30, 0, 200, 0)));
        app.FileDialogs.OpenAnswers.Enqueue([a, b]);
        Check.True(Press(app, Key.O, ModifierKeys.Control), "Ctrl+O が処理されない");
        WaitImports(app, m);
        Check.Equal(2, Layers(app), "開くで選んだ 2 個");
        Check.Equal(dir.Path, app.Ui(() => app.Shell.Settings.LastOpenDir), "前回開いたフォルダ");

        // キャンセルでは何も起きない
        Press(app, Key.O, ModifierKeys.Control);
        WaitImports(app, m);
        Check.Equal(2, Layers(app), "キャンセル");

        // ZIP の署名だけの .glint を 1 個だけ選んだとき: 開けず、文書を変えない(正しいファイルは ProjectUiTests)
        var g = dir.Write("p.glint", [0x50, 0x4B, 0x03, 0x04, 0, 0]);
        app.FileDialogs.OpenAnswers.Enqueue([g]);
        Uia.Invoke(Uia.Find(Uia.Root(app.Hwnd), "開く"));
        UiHost.WaitUntil(() => NoticeKey(app) == NoticeKeys.ProjectInvalid, Wait);
        WaitImports(app, m);
        Check.Equal(NoticeKeys.ProjectInvalid, NoticeKey(app), ".glint の通知");
        Check.Equal(2, Layers(app), ".glint では文書が変わらない");
    }

    [Test]
    public void AC11_5_起動引数の最初の1個だけを取り込み_読めなければウェルカム画面のまま()
    {
        using var dir = new TempDir();
        var a = dir.Write("A.png", Png(10));
        var b = dir.Write("B.png", Png(20));
        using (var app = TestApp.Start())
        {
            var m = Screens(app);
            app.Ui(() => { _ = m.ImportStartupArgs([a, b]); });
            WaitImports(app, m);
            Check.Equal(1, Layers(app), "最初の 1 個だけ");
            Check.Equal("A", app.Ui(() => app.Shell.Session.Document!.Layers[0].Name), "最初の引数の画像");
            Check.True(UiHost.WaitUntil(() => app.Shell.Window.WelcomeHost.Visibility == Visibility.Collapsed, Wait), "ウェルカム画面が残る");
        }
        var t = dir.Write("text.png", "画像ではない"u8.ToArray());
        using (var app = TestApp.Start())
        {
            var m = Screens(app);
            app.Ui(() => { _ = m.ImportStartupArgs([t]); });
            WaitImports(app, m);
            Check.Equal(0, Layers(app), "読めない引数");
            Check.Equal(NoticeKeys.ImportUnsupported, NoticeKey(app), "読めない引数の通知");
            Check.Equal(Visibility.Visible, app.Ui(() => app.Shell.Window.WelcomeHost.Visibility), "ウェルカム画面のまま");
        }
    }

    [Test]
    public void AC11_5_ウェルカム画面はペインを無効にして文を出し_4つのボタンが働く()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        var root = Uia.Root(app.Hwnd);
        foreach (var name in new[] { "画像を貼り付け", "開く", "サンプルで試す", "空のキャンバス" })
            Check.True(app.Ui(() => FindButton(m.Welcome, name)) is not null, $"ウェルカム画面のボタン「{name}」");
        Check.True(Uia.TryFind(root, "画像を貼る・ドロップする・開く") is not null, "見出し");
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            Check.True(!w.LeftPane.IsEnabled && !w.RightPane.IsEnabled && !w.ExportBandFrame.IsEnabled, "ペインと書き出し帯が無効でない");
            // 書き出し帯の文は状態欄に出す(各領域に 1 つずつの検査は PaneDisabledTextTests)
            Check.Equal("画像を取り込むと使えます", app.Shell.Modules.OfType<ExportBandModule>().Single().View.StatusLine1.Text, "書き出し帯の文");
        });
        // 当たり判定と名前(AC-21 の (2)(3) をウェルカム画面でも)
        var scale = app.DpiScale;
        foreach (var e in Uia.Focusables(root))
        {
            Check.True(!string.IsNullOrEmpty(e.Current.Name), $"名前が無い: {Uia.Describe(e)}");
            var r = Uia.RectDip(e, scale);
            Check.True(r.Width >= 31.5 && r.Height >= 31.5, $"小さい: {Uia.Describe(e)} {r.Width:F1}×{r.Height:F1}");
        }

        var emptyCanvas = 0;
        app.Ui(() => m.EmptyCanvasRequested = () => emptyCanvas++);
        Uia.Invoke(Uia.Find(root, "空のキャンバス"));
        UiHost.Idle();
        Check.Equal(1, emptyCanvas, "「空のキャンバス」が差し込み口を呼ばない");

        Uia.Invoke(Uia.Find(root, "サンプルで試す"));
        UiHost.WaitUntil(() => Layers(app) == 1, Wait);
        WaitImports(app, m);
        app.Ui(() =>
        {
            var size = app.Shell.Session.CompositeNative();
            Check.Equal(1024.0, size.W, "サンプルの幅");
            Check.Equal(768.0, size.H, "サンプルの高さ");
            Check.Equal("サンプル", app.Shell.Session.Document!.Layers[0].Name, "サンプルの名前");
        });
        Check.True(UiHost.WaitUntil(() => app.Shell.Modules.OfType<ExportBandModule>().Single().View.StatusLine1.Text != "画像を取り込むと使えます", Wait), "文書ができても「画像を取り込むと使えます」が残る");
    }

    private static Button? FindButton(DependencyObject root, string name)
    {
        if (root is Button b && AutomationProperties.GetName(b) == name) return b;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindButton(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name) is { } f) return f;
        }
        return null;
    }

    [Test]
    public void AC11_5_取り込みが200msを超えたときだけ進捗リングを出す()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        var shown = new TaskCompletionSource();
        app.Ui(() => m.Imports.WaitForDisplay = _ => shown.Task);
        app.Ui(() => { _ = m.Imports.SampleAsync(); });
        Check.True(UiHost.WaitUntil(() => m.Imports.Importer.Gate.IsBusy && app.Shell.Session.HasDocument, Wait), "取り込みが始まらない");
        app.Ui(() =>
        {
            Check.Equal(Visibility.Collapsed, m.Welcome.Ring.Visibility, "始めた直後のリング");
            app.Clock.Advance(TimeSpan.FromMilliseconds(199));
            Check.Equal(Visibility.Collapsed, m.Welcome.Ring.Visibility, "199 ms のリング");
            app.Clock.Advance(TimeSpan.FromMilliseconds(1));
            Check.Equal(Visibility.Visible, m.Welcome.Ring.Visibility, "200 ms のリング");
            Check.True(m.Imports.IsSlow, "IsSlow");
        });
        // 取り込み(最初の表示まで)の処理中の新しい取り込みは無視する(E-20)
        app.Ui(() => { _ = m.Imports.SampleAsync(); });
        app.Ui(() => shown.SetResult());
        WaitImports(app, m);
        Check.Equal(1, Layers(app), "処理中の 2 回目の取り込みは無視");
        app.Ui(() =>
        {
            Check.Equal(Visibility.Collapsed, m.Welcome.Ring.Visibility, "終わったあとのリング");
            Check.True(!m.Imports.Gate.IsBusy, "門が開かない");
        });
    }

    // ================================================================ AC-11 (6) 置き換えの確認(FR-13・FR-43)

    [Test]
    public void AC11_6_未渡しのときだけ新規で確認を出し_ボタンが期待どおりに動く()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        // ウェルカム画面での Ctrl+N は何も起こさない
        Press(app, Key.N, ModifierKeys.Control);
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "ウェルカム画面の Ctrl+N でダイアログ");

        // 文書を始めた直後(編集していない)は確認せずウェルカム画面へ戻る
        Sample(app, m);
        Press(app, Key.N, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => !app.Shell.Session.HasDocument, Wait), "編集していない文書の Ctrl+N");
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "編集していない文書で確認が出た");

        // 編集して未渡しになると確認が出る。キャンセルは何も変えない
        Sample(app, m);
        app.Ui(() => { app.Shell.Session.AddEmptyLayer(); });
        Check.True(app.Ui(() => app.Shell.Session.IsUndelivered), "未渡しにならない");
        var json = app.Ui(() => app.Shell.Session.Json);
        Press(app, Key.N, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is ReplaceConfirmView, Wait), "確認ダイアログが出ない");
        app.Ui(() =>
        {
            var v = (ReplaceConfirmView)app.Shell.Dialogs.Top!;
            Check.SequenceEqual(new[] { ReplaceChoice.SaveAndReplace, ReplaceChoice.ReplaceWithoutSaving, ReplaceChoice.Cancel }, v.Buttons.Keys.ToList(), "ボタン(FR-13 の 3 つ)");
            Check.True(v.DefaultButton.IsDefault && ReferenceEquals(v.DefaultButton, v.Buttons[ReplaceChoice.Cancel]), "既定のボタンがキャンセルでない");
        });
        Check.True(UiHost.WaitUntil(() => FocusCheck.Focused(app.Shell.Window) is Button { Content: "キャンセル" }, Wait), "最初のフォーカスがキャンセルでない");
        var root = Uia.Root(app.Hwnd);
        Check.True(Uia.TryFind(root, "保存して置き換え") is not null, "「保存して置き換え」が出ていない");
        // 表示中の別の取り込み・新規は無視する(E-20)
        app.Ui(() => { _ = m.Imports.SampleAsync(); });
        app.Ui(() => { _ = m.RequestNewAsync(); });
        Uia.Invoke(Uia.Find(root, "キャンセル"));
        Check.True(UiHost.WaitUntil(() => !app.Shell.Dialogs.IsOpen, Wait), "キャンセルで閉じない");
        Check.Equal(json, app.Ui(() => app.Shell.Session.Json), "キャンセルで文書が変わった");

        // Esc もキャンセル
        Press(app, Key.N, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is ReplaceConfirmView, Wait), "2 回目の確認ダイアログ");
        UiHost.Idle();
        Press(app, Key.Escape);
        Check.True(UiHost.WaitUntil(() => !app.Shell.Dialogs.IsOpen, Wait), "Esc で閉じない");
        Check.True(app.Ui(() => app.Shell.Session.HasDocument), "Esc で置き換わった");

        // 未渡しでも画像の取り込みは確認を出さずにレイヤーとして足す
        app.Ui(() => { _ = m.Imports.SampleAsync(); });
        WaitImports(app, m);
        Check.Equal(3, Layers(app), "未渡しのときのサンプル");
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "取り込みで確認が出た");

        // 「保存せず置き換え」はウェルカム画面へ
        Press(app, Key.N, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is ReplaceConfirmView, Wait), "3 回目の確認ダイアログ");
        Uia.Invoke(Uia.Find(root, "保存せず置き換え"));
        Check.True(UiHost.WaitUntil(() => !app.Shell.Session.HasDocument, Wait), "保存せず置き換えでウェルカム画面へ戻らない");
        Check.True(UiHost.WaitUntil(() => app.Shell.Window.WelcomeHost.Visibility == Visibility.Visible, Wait), "ウェルカム画面が出ない");

        // 渡したあと(MarkDelivered)は確認しない
        Sample(app, m);
        app.Ui(() =>
        {
            app.Shell.Session.AddEmptyLayer();
            app.Shell.Session.MarkDelivered();
        });
        Press(app, Key.N, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => !app.Shell.Session.HasDocument, Wait), "渡したあとの Ctrl+N");
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "渡したあとで確認が出た");
    }

    // ================================================================ AC-22 (4)(5)

    [Test]
    public void AC22_4_F1の一覧に表と同じ37行のキーと動作が出る()
    {
        using var app = TestApp.Start();
        Check.True(Press(app, Key.F1), "F1 が処理されない");
        app.Ui(() =>
        {
            var list = (ShortcutListView)app.Shell.Dialogs.Top!;
            var cells = list.Rows.Children.OfType<TextBlock>().ToList();
            Check.Equal(37 * 3, cells.Count, "一覧の文字の数(37 行×キー・動作・条件)");
            for (var i = 0; i < 37; i++)
            {
                var row = Shortcuts.Table[i];
                Check.Equal(i + 1, row.Number, "表の番号");
                Check.Equal(row.Keys, cells.Single(c => Grid.GetRow(c) == i && Grid.GetColumn(c) == 0).Text, $"{i + 1} 行目のキー");
                Check.Equal(row.Action, cells.Single(c => Grid.GetRow(c) == i && Grid.GetColumn(c) == 1).Text, $"{i + 1} 行目の動作");
                Check.Equal(row.Condition, cells.Single(c => Grid.GetRow(c) == i && Grid.GetColumn(c) == 2).Text, $"{i + 1} 行目の条件");
            }
        });
        // §9.12 の表から書き写した何行か(表の文字と一覧が同じであること)
        var root = Uia.Root(app.Hwnd);
        foreach (var s in new[] { "Ctrl+Shift+V", "画像を貼り付け(FR-1)", "Ctrl+カンマ", "設定を開く(FR-90)", "Tab、Shift+Tab", "フォーカスの移動", "Shift+F10、アプリケーションキー",
                     "文字入力欄にフォーカスが無いとき", "どこにフォーカスがあっても", "ルックのタイル、ユーザーのプリセットの項目にフォーカス" })
            Check.True(Uia.TryFind(root, s) is not null, $"一覧に「{s}」が無い");
    }

    [Test]
    public void AC22_5_アイコンだけのボタンのツールチップは500msと300msで名前とショートカットの形()
    {
        using var app = TestApp.Start();
        // §9.3 のコマンドバーの表から書き写した「名前(ショートカット)」(ショートカットの無いボタンは名前だけ)
        var expected = new[]
        {
            "新規(Ctrl+N)", "画像を貼り付け(Ctrl+V)", "開く(Ctrl+O)", "空のレイヤー(Ctrl+Shift+N)", "元に戻す(Ctrl+Z)", "やり直す(Ctrl+Y)",
            "元画像(Backslash(\\))", "分割比較", "実出力(F5)", "キャンバスの背景", "縮小(Ctrl+マイナス)", "拡大(Ctrl+プラス)", "フィット(Ctrl+0)",
            "バッチ(Ctrl+B)", "設定(Ctrl+カンマ)",
        };
        app.Ui(() =>
        {
            var bar = app.Shell.CommandBar;
            var got = CommandBarView.Commands.Select(c => (string)bar.Button(c.Icon).ToolTip).ToList();
            Check.SequenceEqual(expected, got, "コマンドバーのツールチップ");
        });
        Check.True(Press(app, Key.OemComma, ModifierKeys.Control), "Ctrl+カンマ が処理されない");
        UiHost.Idle();
        app.Ui(() => Screens(app).ShowAbout());
        UiHost.Idle();
        app.Ui(() =>
        {
            var icons = new List<ButtonBase>();
            icons.AddRange(app.Shell.CommandBar.Buttons);
            icons.Add(app.Shell.NoticeBar.CloseButton);
            var settings = Screens(app).Settings!;
            icons.Add(settings.CloseButton);
            icons.Add(settings.SaveFailedBand.CloseButton);
            icons.AddRange(AllButtons(app.Shell.Dialogs).Where(b => b.Style == (Style)app.Shell.Window.FindResource("IconButton")));
            Check.True(icons.Count >= 19, $"アイコンボタンが少ない: {icons.Count}");
            foreach (var b in icons.Distinct())
            {
                Check.Equal(500, ToolTipService.GetInitialShowDelay(b), $"{AutomationProperties.GetName(b)} の InitialShowDelay");
                Check.Equal(300, ToolTipService.GetBetweenShowDelay(b), $"{AutomationProperties.GetName(b)} の BetweenShowDelay");
                Check.True(b.ToolTip is string s && s.StartsWith(AutomationProperties.GetName(b), StringComparison.Ordinal), $"{AutomationProperties.GetName(b)} のツールチップ");
            }
        });
    }

    private static IEnumerable<ButtonBase> AllButtons(DependencyObject root)
    {
        if (root is ButtonBase b) yield return b;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var x in AllButtons(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return x;
        }
    }

    // ================================================================ AC-30 (1) 設定の画面(FR-90)

    // 9 項目と、それぞれの部品の種類(ラベルの文字と同じ名前を持つので、種類で見分ける)
    private static readonly (string Name, ControlType Type)[] SettingsItems =
    [
        ("テーマ", ControlType.List), ("コピーの載せ方", ControlType.List), ("既定の書き出し形式", ControlType.ComboBox), ("既定の保存先", ControlType.Button),
        ("アニメーションの縮小", ControlType.List), ("ログのフォルダを開く", ControlType.Button), ("キャッシュの上限", ControlType.Edit),
        ("キャッシュの削除", ControlType.Button), ("設定の初期化", ControlType.Button),
    ];

    /// <summary>UIA の木が追いつくまで(10 秒まで)繰り返し調べる(テストのスレッドから呼ぶ)。</summary>
    private static bool Poll(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    private static AutomationElement? FindControl(AutomationElement root, string name, ControlType type) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, type)));

    private static void SelectItem(AutomationElement list, string name) =>
        Uia.Select(list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            ?? throw new InvalidOperationException($"UIA で「{name}」が見つからない"));

    [Test]
    public void AC30_1_設定の画面の9項目がUIAで見つかり変更がすぐ反映される()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        var opened = new List<string>();
        var cleared = 0;
        app.Ui(() =>
        {
            m.OpenFolder = opened.Add;
            m.ClearCache = () => cleared++;
        });
        Check.True(Press(app, Key.OemComma, ModifierKeys.Control), "Ctrl+カンマ が処理されない");
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is SettingsView, Wait), "設定の画面が出ない");
        UiHost.Idle();
        var root = Uia.Root(app.Hwnd);
        foreach (var (name, type) in SettingsItems)
            Check.True(Poll(() => FindControl(root, name, type) is not null), $"設定の「{name}」({type.ProgrammaticName})が UIA で見つからない");
        Check.True(Poll(() => Uia.TryFind(root, "このソフトについて") is not null), "「このソフトについて」のボタン");

        // コピーの載せ方・アニメーションの縮小(横並びの切替ボタン)
        SelectItem(FindControl(root, "コピーの載せ方", ControlType.List)!, "画像");
        Check.Equal(CopyModeSetting.Image, app.Ui(() => app.Shell.Settings.CopyMode), "コピーの載せ方");
        SelectItem(FindControl(root, "アニメーションの縮小", ControlType.List)!, "オン");
        Check.Equal(ReduceMotionSetting.On, app.Ui(() => app.Shell.Settings.ReduceMotion), "アニメーションの縮小");
        Check.True(app.Ui(() => Motion.GetReduced(app.Shell.Window)), "アニメーションの縮小がすぐ反映されない");

        // 既定の書き出し形式(lastExport があれば format も書き換える)
        app.Ui(() =>
        {
            app.Shell.Settings.LastExport = new System.Text.Json.Nodes.JsonObject { ["format"] = "gif" };
            Screens(app).Settings!.DefaultFormat.SelectedIndex = 0;
            Check.Equal(ExportFormatSetting.Png, app.Shell.Settings.DefaultFormat, "既定の書き出し形式");
            Check.Equal("png", (string?)app.Shell.Settings.LastExport!["format"], "lastExport.format");
        });

        // 既定の保存先(偽のフォルダの選択)
        using var dir = new TempDir();
        app.FileDialogs.FolderAnswers.Enqueue(dir.Path);
        Uia.Invoke(FindControl(root, "既定の保存先", ControlType.Button)!);
        Check.True(UiHost.WaitUntil(() => app.Shell.Settings.DefaultSaveDir == dir.Path, Wait), "既定の保存先");
        Check.Equal(dir.Path, app.Ui(() => (string)Screens(app).Settings!.DefaultSaveDir.Content), "既定の保存先の表示");

        // ログのフォルダを開く(偽物)・キャッシュの削除(偽物)
        Uia.Invoke(Uia.Find(root, "ログのフォルダを開く"));
        UiHost.Idle();
        Check.SequenceEqual(new[] { app.Paths.LogsDir }, opened, "開いたフォルダ");
        Uia.Invoke(Uia.Find(root, "キャッシュの削除"));
        UiHost.Idle();
        Check.Equal(1, cleared, "キャッシュの削除");

        // キャッシュの上限: 範囲外は端に直る
        app.Ui(() =>
        {
            var box = Screens(app).Settings!.CacheLimit;
            box.Text = "5000";
            box.Commit();
            Check.Equal(4096, app.Shell.Settings.CacheLimitMB, "上限より大きい");
            Check.Equal("4096", box.Text, "欄の表示");
            box.Text = "100";
            box.Commit();
            Check.Equal(256, app.Shell.Settings.CacheLimitMB, "下限より小さい");
            box.Text = "abc";
            box.Commit();
            Check.Equal(256, app.Shell.Settings.CacheLimitMB, "読めない入力");
        });

        // 保存は最後の変更から 500 ms 後に 1 回
        app.Ui(() =>
        {
            var before = app.Shell.SettingsStore.SaveCount;
            app.Clock.Advance(TimeSpan.FromMilliseconds(499));
            Check.Equal(before, app.Shell.SettingsStore.SaveCount, "499 ms で保存した");
            app.Clock.Advance(TimeSpan.FromMilliseconds(1));
            Check.Equal(before + 1, app.Shell.SettingsStore.SaveCount, "500 ms で保存しない");
        });

        // 設定の初期化: 6 項目だけ戻す(画面にもすぐ出る)
        Uia.Invoke(Uia.Find(root, "設定の初期化"));
        UiHost.Idle();
        app.Ui(() =>
        {
            var s = app.Shell.Settings;
            Check.Equal(CopyModeSetting.Both, s.CopyMode, "初期化の copyMode");
            Check.Equal(ReduceMotionSetting.System, s.ReduceMotion, "初期化の reduceMotion");
            Check.Equal(ExportFormatSetting.Gif, s.DefaultFormat, "初期化の defaultFormat");
            Check.Equal("", s.DefaultSaveDir, "初期化の defaultSaveDir");
            Check.Equal(1024, s.CacheLimitMB, "初期化の cacheLimitMB");
            Check.Equal("png", (string?)s.LastExport!["format"], "初期化で lastExport.format が変わった");
            var v = Screens(app).Settings!;
            Check.Equal(2, v.CopyMode.SelectedIndex, "画面のコピーの載せ方");
            Check.Equal(2, v.DefaultFormat.SelectedIndex, "画面の既定の書き出し形式");
            Check.Equal("1024", v.CacheLimit.Text, "画面のキャッシュの上限");
        });

        // テーマ(最後に変える。木が作り直されるため)
        SelectItem(FindControl(Uia.Root(app.Hwnd), "テーマ", ControlType.List)!, "ライト");
        Check.Equal(ThemeSetting.Light, app.Ui(() => app.Shell.Settings.Theme), "テーマ");
        Check.Equal(ThemeSetting.Light, app.Ui(() => app.Shell.Theme.Setting), "テーマがすぐ反映されない");
    }

    [Test]
    public void AC30_1_キャッシュの削除は差し込み口が無いと無効で_保存の失敗は設定の上端の帯に出る()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        app.Ui(() => m.ClearCache = null);
        Press(app, Key.OemComma, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => m.Settings is not null, Wait), "設定の画面が出ない");
        Check.True(app.Ui(() => !m.Settings!.ClearCache.IsEnabled), "差し込み口が無いのにキャッシュの削除が有効");
        // 設定のファイルの場所をフォルダにして、保存を失敗させる
        Directory.CreateDirectory(app.Paths.SettingsFile);
        app.Ui(() =>
        {
            app.Shell.Settings.CopyMode = CopyModeSetting.File;
            app.Clock.Advance(TimeSpan.FromMilliseconds(500));
        });
        Check.True(UiHost.WaitUntil(() => m.Settings!.SaveFailedBand.Current?.Key == NoticeKeys.SettingsSaveFailed, Wait), "保存の失敗の帯が出ない");
        app.Ui(() =>
        {
            Check.Equal("設定を保存できません", m.Settings!.SaveFailedBand.Text, "帯の文");
            Check.Equal(40.0, m.Settings!.SaveFailedBand.Height, "帯の高さ");
        });
        Directory.Delete(app.Paths.SettingsFile);
    }

    [Test(Slow = true)]
    public void AC30_1_FR94_WARPのときは設定の画面にソフトウェア描画と出る()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        app.Shell.RenderReady!.GetAwaiter().GetResult();
        var m = Screens(app);
        Press(app, Key.OemComma, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => m.Settings?.SoftwareRendering.Visibility == Visibility.Visible, Wait), "「ソフトウェア描画」が出ない");
        Check.True(Uia.TryFind(Uia.Root(app.Hwnd), "ソフトウェア描画") is not null, "UIA で見つからない");
    }

    // ================================================================ FR-103 このソフトについて

    [Test]
    public void FR103_このソフトについては設定の上に開き版とLICENSEの全文と依存の表を出す()
    {
        using var app = TestApp.Start();
        var m = Screens(app);
        Press(app, Key.OemComma, ModifierKeys.Control);
        Check.True(UiHost.WaitUntil(() => m.Settings is not null, Wait), "設定の画面が出ない");
        UiHost.Idle();
        Uia.Invoke(Uia.Find(Uia.Root(app.Hwnd), "このソフトについて"));
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is AboutView, Wait), "このソフトについてが出ない");
        Check.Equal(2, app.Ui(() => app.Shell.Dialogs.Count), "設定の上に重ねていない");

        var license = File.ReadAllText(Path.Combine(RepoFiles.Root, "LICENSE")).Replace("\r\n", "\n");
        Check.True(license.StartsWith("MIT License\n\nCopyright (c) 2026 takosasi-dev\n", StringComparison.Ordinal), "LICENSE の先頭");
        var notices = File.ReadAllText(Path.Combine(RepoFiles.Root, "THIRD-PARTY-NOTICES.md"));
        app.Ui(() =>
        {
            var v = (AboutView)app.Shell.Dialogs.Top!;
            Check.True(v.VersionText.Text.Contains("0.0.0-test", StringComparison.Ordinal), "版: " + v.VersionText.Text);
            Check.Equal(license.TrimEnd('\n'), v.LicenseText.Text, "LICENSE の本文");
            Check.Equal(license, AboutContent.LicenseText, "埋め込みの LICENSE");

            // 依存の一覧: THIRD-PARTY-NOTICES.md の 2 つの表と同じ組(行ごとの欄)。
            // まず読み取りの関数を、リポジトリの THIRD-PARTY-NOTICES.md で、AC-9 の検査の読み方と照らす
            var sections = new[] { NoticesCheck.NuGetSection, NoticesCheck.NativeSection };
            var parsed = AboutContent.ReadTables(notices);
            for (var i = 0; i < sections.Length; i++)
            {
                var rows = NoticesCheck.ReadTable(notices, sections[i])!;
                var t = parsed.Single(p => "## " + p.Heading == sections[i]);
                Check.Equal(rows.Count, t.Rows.Count, $"{sections[i]} の行の数(読み取り)");
                for (var r = 0; r < rows.Count; r++)
                {
                    for (var c = 0; c < t.Columns.Count; c++)
                        Check.Equal(rows[r][t.Columns[c]], t.Rows[r][c], $"{sections[i]} の {r + 1} 行目の {t.Columns[c]}(読み取り)");
                }
            }
            // 画面の表: THIRD-PARTY-NOTICES.md を埋め込み(INV-3 の検査は埋め込みリソースの中身を見ない)、表と同じ組で出す
            Check.True(AboutContent.NoticesText is not null, "THIRD-PARTY-NOTICES.md を埋め込んでいない");
            Check.Equal(notices.Replace("\r\n", "\n"), AboutContent.NoticesText, "埋め込みの THIRD-PARTY-NOTICES.md");
            Check.Equal(sections.Length, v.DependencyTables.Count, "表の数");
            for (var i = 0; i < sections.Length; i++)
            {
                var rows = NoticesCheck.ReadTable(notices, sections[i])!;
                var grid = v.DependencyTables[i];
                var cols = grid.ColumnDefinitions.Count;
                string Cell(int r, int c) => grid.Children.OfType<TextBlock>().Single(t => Grid.GetRow(t) == r && Grid.GetColumn(t) == c).Text;
                Check.Equal(rows.Count + 1, grid.RowDefinitions.Count, $"{sections[i]} の行の数");
                var header = Enumerable.Range(0, cols).Select(c => Cell(0, c)).ToList();
                for (var r = 0; r < rows.Count; r++)
                {
                    foreach (var (name, c) in header.Select((h, c) => (h, c)))
                        Check.Equal(rows[r][name], Cell(r + 1, c), $"{sections[i]} の {r + 1} 行目の {name}");
                }
            }
            var nuget = NoticesCheck.ReadTable(notices, NoticesCheck.NuGetSection)!;
            Check.True(nuget.Any(r => r["パッケージ"] == "SkiaSharp" && r["ライセンス"] == "MIT"), "SkiaSharp の行");
        });

        // 閉じると設定に戻る
        Press(app, Key.Escape);
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is SettingsView, Wait), "閉じたあと設定に戻らない");
    }
}
