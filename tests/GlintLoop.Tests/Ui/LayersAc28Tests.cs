// AC-28: レイヤーの操作(§6.4)を、EditorSession の関数と、画面の外のウィンドウの UIA とキーで調べる。画像はコードで作る(INV-18)。Session だけで済む部分の多くは
// Phase 2 の検査(EditorSessionTests の FR42_・FR44_・FR45_・FR52_・FR53_・FR57_、HistoryTests の FR30_AC28_11_ など)が通しているので、ここでは画面からの操作と残りの数を確かめる。
// (1) の左ペインのタブは担当 looks、(2) の Ctrl+N の配線と確認ダイアログは担当 screens、(10) のエフェクトのタブは担当 stack・looks、Ctrl+Z の登録は履歴の担当の範囲。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Layers;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;
using static GlintLoop.Tests.Ui.LayersTestKit;
using K = System.Windows.Input.Key;

namespace GlintLoop.Tests.Ui;

public sealed class LayersAc28Tests
{
    // ================================================================ (1) 足す

    [Test]
    public void AC28_1_取り込みで土台ができ2枚目は最前面に足されて一覧の先頭に出る()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        var o1 = importer.Drop(new MemoryImportData().WithFiles(dir.Write("cat.png", SkiaBytes.Png(400, 300, Img.Solid(400, 300, 9, 9, 9)))));
        Check.True(o1.Notice is null || o1.Notice.Key != NoticeKeys.LayerAdded, "最初の取り込みで layer.added が出た");
        o1.CompleteDisplay(); // 最初の表示を終えた知らせ(表示の担当が呼ぶ。E-20 の門を出る)
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.SequenceEqual(["cat"], s.Document!.Layers.Select(l => l.Name).ToList(), "土台の名前");
            Check.Equal(0, s.UndoCount, "最初の取り込みの履歴");
        });
        var o2 = importer.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", SkiaBytes.Png(200, 100, Img.Solid(200, 100, 9, 9, 9)))));
        Check.Equal(NoticeKeys.LayerAdded, o2.Notice?.Key, "2 枚目の通知");
        o2.CompleteDisplay();
        UiHost.Idle();
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.SequenceEqual(["cat", "a"], s.Document!.Layers.Select(l => l.Name).ToList(), "レイヤーの並び");
            Check.Equal(Target.OfLayer(1), s.Selected, "足したレイヤーが選ばれる");
            Check.Equal(Placement.Identity, s.Document.Layers[1].Place, "足したレイヤーの配置");
            Check.Equal(1, s.UndoCount, "2 枚目の履歴");
        });
        var rows = Rows(app);
        Check.SequenceEqual(["全体", "a", "cat"], rows.Select(r => r.Current.Name).ToList(), "一覧の行");
        Check.True(((SelectionItemPattern)rows[1].GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected, "足した行が選ばれていない");
        // scale の表(FR42_scaleの式 の検査と同じ数を、取り込みの関数で)
        foreach (var (w, h, scale) in new[] { (800, 600, 50.0), (4000, 3000, 10.0), (401, 300, 99.7), (401, 299, 99.7), (400, 299, 100.0), (400, 300, 100.0), (200, 100, 100.0), (8192, 8192, 5.0), (16384, 1, 5.0) })
            Check.Equal(scale, DocumentRules.ImportScale(400, 300, w, h), $"{w}×{h} の scale");
        foreach (var (file, name) in new[] { ("my.photo.png", "my.photo"), (new string('x', 45) + ".png", new string('x', 40)), (".png", "画像") })
            Check.Equal(name, DocumentRules.NameFromFileName(file), file + " の名前");
    }

    // ================================================================ (2) 新規

    [Test]
    public void AC28_2_編集していない文書の新規は確認なしでウェルカムに戻りウェルカムでは何も起きない()
    {
        var s = SessionTestKit.WithBase(400, 300);
        s.MarkDelivered();
        var confirm = new FakeConfirm(ReplaceChoice.Cancel);
        Check.Equal(ReplaceResult.Replaced, s.RequestNew(confirm), "新規の結果");
        Check.Equal(0, confirm.Calls, "確認を出した");
        Check.True(!s.HasDocument && s.UndoCount == 0 && s.RedoCount == 0, "文書と履歴が空でない");
        Check.Equal(ReplaceResult.Ignored, s.RequestNew(confirm), "ウェルカムでの新規");

        using var app = TestApp.Start();
        var newButton = Uia.Find(Uia.Root(app.Hwnd), "新規");
        Check.True(!newButton.Current.IsEnabled, "ウェルカムで「新規」が有効");
    }

    // ================================================================ (3) 空のキャンバスと空のレイヤー

    [Test]
    public void AC28_3_FR44_ダイアログの初期値と範囲の直しと作成()
    {
        using var app = TestApp.Start();
        var m = Module(app);
        app.Ui(() => _ = m.ShowEmptyCanvasDialog());
        UiHost.Idle();
        app.Ui(() =>
        {
            var d = (EmptyCanvasDialog)app.Shell.Dialogs.Top!;
            Check.Equal(1024.0, d.WidthBox.Value, "幅の初期値");
            Check.Equal(768.0, d.HeightBox.Value, "高さの初期値");
            Check.Equal("#FFFFFFFF", d.Fill, "塗りの初期値");
            Check.Equal("#FFFFFFFF", d.FillHex.Text, "塗りの 16 進数の初期値");
            foreach (var box in new[] { d.WidthBox, d.HeightBox })
            {
                var before = box.Value;
                foreach (var (text, want) in new[] { ("5", 16.0), ("5000", 4096.0), ("abc", 4096.0), ("", 4096.0), ("300", 300.0), ("abc", 300.0) })
                {
                    box.Text = text;
                    box.Commit();
                    Check.Equal(want, box.Value, $"「{text}」の結果");
                }
                box.Text = before.ToString(System.Globalization.CultureInfo.InvariantCulture);
                box.Commit();
            }
            d.Create();
            var doc = app.Shell.Session.Document!;
            var l = doc.Layers.Single();
            Check.Equal("キャンバス", l.Name, "名前");
            Check.Equal(LayerKind.Empty, l.Kind, "種類");
            Check.Equal(new EmptySize(1024, 768), l.Size, "大きさ");
            Check.Equal("#FFFFFFFF", l.Fill, "塗り");
            Check.Equal(0, app.Shell.Session.UndoCount, "新しい文書の履歴");
            Check.True(!app.Shell.Dialogs.IsOpen, "作成で閉じない");
        });
    }

    [Test]
    public void AC28_3_FR45_CtrlShiftNは合成と同じ大きさの透明な空のレイヤーを足して選ぶ()
    {
        using var app = TestApp.Start();
        EmptySize? AddVia(int w, int h, RectValue? crop)
        {
            app.Ui(() =>
            {
                var s = app.Shell.Session;
                s.CloseDocument();
                s.AddImportedImages([SessionTestKit.Image(1, w, h)]);
                if (crop is { } c)
                {
                    var row = s.AddEffect(Target.OfLayer(0), "crop").Row!.Value;
                    s.SetEffectParam(Target.OfLayer(0), row, "rect", c);
                }
                Keyboard.Focus(app.Shell.CommandBar.Button(Icons.Settings));
            });
            Check.True(Key(app, K.N, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+N が処理されない");
            return app.Ui(() =>
            {
                var doc = app.Shell.Session.Document!;
                var top = doc.Layers[^1];
                Check.Equal("空のレイヤー", top.Name, "名前");
                Check.Equal(ColorText.Transparent, top.Fill, "塗り");
                Check.Equal(Target.OfLayer(doc.TopIndex), doc.Selected, "足したレイヤーを選ぶ");
                return top.Size;
            });
        }
        Check.Equal(new EmptySize(400, 300), AddVia(400, 300, null), "400×300");
        Check.Equal(new EmptySize(200, 300), AddVia(400, 300, new RectValue(250, 0, 250, 0)), "400×300 を切り抜き");
        Check.Equal(new EmptySize(151, 200), AddVia(301, 200, new RectValue(250, 0, 250, 0)), "301×200 を切り抜き(150.5 が切り上がる)");
        Check.Equal(new EmptySize(4096, 1000), AddVia(6000, 1000, null), "6000×1000");
    }

    [Test]
    public void AC28_3_塗りFF000080の空のレイヤーは同じ色のPNGと出力が一致する()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        importer.Drop(new MemoryImportData().WithFiles(dir.Write("half.png", SkiaBytes.Png(100, 100, Img.Solid(100, 100, 255, 0, 0, 128)))));
        var fromPng = app.Ui(() => app.Shell.Session.Document!);
        var fromEmpty = Doc(Empty("空", 100, 100, fill: "#FF000080"));
        var a = renderer.RenderExportAsync(fromPng, 100).GetAwaiter().GetResult();
        var b = renderer.RenderExportAsync(fromEmpty, 100).GetAwaiter().GetResult();
        Check.Equal(100, b.Width, "幅");
        Check.SequenceEqual(a.Pixels, b.Pixels, "出力のバイト列");
        Check.Equal((byte)128, b.Pixels[3], "α");
    }

    // ================================================================ (4) 表示

    [Test]
    public void AC28_4_FR47_表示をオフにした出力は足していない文書と一致しトグルはUndoの1件()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var gray = Empty("灰", 100, 100, fill: "#808080FF");
        var red = Empty("赤", 50, 50, new Placement(10, -5, 100), fill: "#FF0000FF");
        Open(app, Doc(gray, red));
        byte[] Out(SessionDocument d) => renderer.RenderExportAsync(d, 100).GetAwaiter().GetResult().Pixels;
        var withRed = Out(app.Ui(() => app.Shell.Session.Document!));
        var without = Out(Doc(gray));
        var row = Rows(app)[1];
        var toggle = Child(row, "表示")!;
        var undo = app.Ui(() => app.Shell.Session.UndoCount);
        Uia.Toggle(toggle);
        UiHost.Idle();
        Check.Equal(undo + 1, app.Ui(() => app.Shell.Session.UndoCount), "トグルの件数");
        Check.True(app.Ui(() => !app.Shell.Session.Document!.Layers[1].Visible), "トグルでオフにならない");
        Check.SequenceEqual(without, Out(app.Ui(() => app.Shell.Session.Document!)), "オフの出力と、足していない文書の出力");
        Uia.Toggle(Child(Rows(app)[1], "表示")!);
        UiHost.Idle();
        Check.SequenceEqual(withRed, Out(app.Ui(() => app.Shell.Session.Document!)), "オンに戻した出力");

        // 土台をオフにしても 100×100 のままで、赤の外は透明
        Uia.Toggle(Child(Rows(app)[2], "表示")!);
        UiHost.Idle();
        var img = renderer.RenderExportAsync(app.Ui(() => app.Shell.Session.Document!), 100).GetAwaiter().GetResult();
        Check.Equal(100, img.Width, "土台がオフの幅");
        Check.Equal(100, img.Height, "土台がオフの高さ");
        Check.Equal((byte)0, img.Pixels[3], "赤の外の α");
        var c = (50 + 10) * 4 + (50 - 5) * 100 * 4;
        Check.Equal((byte)255, img.Pixels[c + 3], "赤の中の α");
    }

    // ================================================================ (5) 設定

    private static AutomationElement? FindId(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    [Test]
    public void AC28_5_FR48_FR49_設定の部品が対象に合わせてUIAで見つかる()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100)));
        var root = Uia.Root(app.Hwnd);
        string[] common = ["LayerOpacitySlider", "LayerOpacityBox", "LayerBlendBox", "LayerNameBox"];
        string[] place = ["LayerXBox", "LayerYBox", "LayerScaleSlider", "LayerScaleBox", "LayerCenterButton", "LayerScale100Button"];
        string[] fill = ["LayerFillSwatch", "LayerFillHex", "LayerFillEyedropper"];
        foreach (var id in common.Concat(place).Concat(fill))
            Check.True(FindId(root, id) is { } e && !e.Current.IsOffscreen, $"empty の非土台のレイヤーで {id} が見つからない");
        Check.True(FindId(root, "LayerBlendBox")!.Current.IsEnabled, "非土台のブレンドが無効");
        Check.Equal("中央に置く", FindId(root, "LayerCenterButton")!.Current.Name, "中央に置くの名前");
        Check.Equal("100% にする", FindId(root, "LayerScale100Button")!.Current.Name, "100% にするの名前");

        app.Ui(() => app.Shell.Session.Select(Target.OfLayer(0)));
        UiHost.Idle();
        foreach (var id in place)
            Check.True(FindId(root, id) is not { } e || e.Current.IsOffscreen, $"土台で {id} が見える");
        Check.True(!FindId(root, "LayerBlendBox")!.Current.IsEnabled, "土台のブレンドが有効");

        app.Ui(() => app.Shell.Session.Select(Target.All));
        UiHost.Idle();
        Check.True(FindId(root, "LayerOpacityBox") is not { } e2 || e2.Current.IsOffscreen, "全体でレイヤーの設定が見える");
    }

    [Test]
    public void AC28_5_数値入力は範囲の端に直して反映し読めない値は直前に戻し変更はUndoの1件()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100)));
        var m = Module(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            var v = m.Settings;
            Layer L() => s.Document!.Layers[1];
            void Enter(System.Windows.Controls.TextBox box, string text, Func<double> read, double want, string what)
            {
                var undo = s.UndoCount;
                var before = read();
                box.Text = text;
                ((GlintLoop.App.Controls.NumberBox)box).Commit();
                Check.Equal(want, read(), what);
                Check.Equal(before == want ? undo : undo + 1, s.UndoCount, what + " の件数");
            }
            foreach (var (t, want) in new[] { ("-5", 0.0), ("150", 100.0), ("49.5", 50.0), ("49.4", 49.0), ("abc", 49.0), ("", 49.0) })
                Enter(v.OpacityBox, t, () => L().Opacity, want, $"不透明度「{t}」");
            Check.Equal("49", v.OpacityBox.Text, "読めない値のあとの欄の文字");
            foreach (var (box, read) in new[] { (v.XBox, (Func<double>)(() => L().Place.X)), (v.YBox, () => L().Place.Y) })
            {
                foreach (var (t, want) in new[] { ("100001", 100000.0), ("-100001", -100000.0), ("1.234", 1.23), ("1.236", 1.24), ("abc", 1.24), ("", 1.24) })
                    Enter(box, t, read, want, $"位置「{t}」");
            }
            foreach (var (t, want) in new[] { ("4", 5.0), ("401", 400.0), ("33.34", 33.3), ("33.36", 33.4), ("abc", 33.4) })
                Enter(v.ScaleBox, t, () => L().Place.Scale, want, $"拡縮「{t}」");

            // 中央に置く・100% にする
            var undo = s.UndoCount;
            v.CenterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check.Equal(new Placement(0, 0, 33.4), L().Place, "中央に置くは x と y だけ");
            v.Scale100Button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check.Equal(new Placement(0, 0, 100), L().Place, "100% にするは scale だけ");
            Check.Equal(undo + 2, s.UndoCount, "2 つのボタンの件数");

            // ブレンド
            v.BlendBox.SelectedIndex = (int)BlendMode.Multiply;
            Check.Equal(BlendMode.Multiply, L().Blend, "ブレンドの選択");
            Check.Equal(undo + 3, s.UndoCount, "ブレンドの件数");

            // スライダーの開始・更新 30 回・終了は 1 件
            undo = s.UndoCount;
            var thumb = FindThumb(v.OpacitySlider);
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
            for (var i = 1; i <= 30; i++) v.OpacitySlider.Value = i * 3;
            Check.Equal(undo + 1, s.UndoCount, "ドラッグの途中");
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            Check.Equal(undo + 1, s.UndoCount, "スライダーのドラッグの件数");
            Check.Equal(90, L().Opacity, "スライダーの値");
        });
    }

    private static Thumb FindThumb(DependencyObject d)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is Thumb t) return t;
            if (FindThumbOrNull(c) is { } f) return f;
        }
        throw new CheckFailedException("スライダーのつまみが無い");
    }

    private static Thumb? FindThumbOrNull(DependencyObject d)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is Thumb t) return t;
            if (FindThumbOrNull(c) is { } f) return f;
        }
        return null;
    }

    [Test]
    public void AC28_5_同じ行のもう一度のクリックでスタックの行の選択から設定へ戻る()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100)));
        var m = Module(app);
        app.Ui(() =>
        {
            int? row = 0;
            m.SelectedStackRow = () => row;
            m.SettingsRequested += () =>
            {
                row = null;
                m.StackSelectionChanged();
            };
            m.StackSelectionChanged();
            Check.True(!m.ShowsSettings, "スタックの行を選んでいるのに設定が出る");
            var r = RowOf(m, 1);
            // マウスの押下(トンネルの PreviewMouseDown。WPF が各要素で PreviewMouseLeftButtonDown に直す)を名前の文字に送る
            r.NameText.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = r.NameText });
            r.NameText.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = r.NameText });
            Check.True(m.ShowsSettings, "同じ行のクリックで設定が出ない");
            Check.True(m.Settings.IsVisible, "設定の部品が見えない");
        });
    }

    // ================================================================ (6) 順序

    [Test]
    public void AC28_6_FR52_AltとCtrlZで並べ替えと戻しが1件ずつ()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100, new Placement(5, 6, 70)),
            Empty("C", 120, 80, new Placement(30, 10, 50), blend: BlendMode.Multiply)));
        var m = Module(app);
        app.Ui(() => m.List.FocusSelected());
        Check.Equal(Target.OfLayer(2), app.Ui(() => app.Shell.Session.Selected), "C が選ばれていない");
        Check.True(Key(app, K.Up, ModifierKeys.Alt), "Alt+上が一覧に届かない");
        Check.SequenceEqual(["A", "B", "C"], Names(app), "最前面で Alt+上");
        Key(app, K.Down, ModifierKeys.Alt);
        Check.SequenceEqual(["A", "C", "B"], Names(app), "Alt+下 1 回");
        Key(app, K.Down, ModifierKeys.Alt);
        Check.SequenceEqual(["C", "A", "B"], Names(app), "Alt+下 2 回");
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            var doc = s.Document!;
            Check.Equal(Placement.Identity, doc.Layers[0].Place, "新しい土台の配置");
            Check.Equal(BlendMode.Normal, doc.Layers[0].Blend, "新しい土台のブレンド");
            Check.Equal(new SizeD(120, 80), s.CompositeNative(), "合成の大きさが C で決まる");
            Check.Equal(Placement.Identity, doc.Layers[1].Place, "A の配置");
            Check.Equal(new Placement(5, 6, 70), doc.Layers[2].Place, "B の配置");
        });
        Key(app, K.Down, ModifierKeys.Alt);
        Check.SequenceEqual(["C", "A", "B"], Names(app), "いちばん下で Alt+下");
        Check.True(app.Ui(() => Keyboard.FocusedElement is LayerRowItem), "並べ替えのあとフォーカスが一覧に無い");
        // 「全体」の行では何も起きない
        app.Ui(() => app.Shell.Session.Select(Target.All));
        app.Ui(() => m.List.FocusSelected());
        Check.True(Key(app, K.Down, ModifierKeys.Alt), "全体での Alt+下が一覧に届かない");
        Check.SequenceEqual(["C", "A", "B"], Names(app), "全体で Alt+下");
        app.Ui(() => app.Shell.Session.Select(Target.OfLayer(0)));
        app.Ui(() => app.Shell.Session.Undo()); // Ctrl+Z の登録は履歴の担当(この部品の外)
        Check.SequenceEqual(["A", "C", "B"], Names(app), "Ctrl+Z 1 回");
        app.Ui(() => app.Shell.Session.Undo()); // Ctrl+Z の登録は履歴の担当(この部品の外)
        Check.SequenceEqual(["A", "B", "C"], Names(app), "Ctrl+Z 2 回");
        app.Ui(() =>
        {
            var c = app.Shell.Session.Document!.Layers[2];
            Check.Equal(new Placement(30, 10, 50), c.Place, "C の配置が戻る");
            Check.Equal(BlendMode.Multiply, c.Blend, "C のブレンドが戻る");
            // 行のドラッグ(ドロップ先を返す関数)でも同じ結果
            m.List.Refresh(app.Shell.Session.Document, _ => null);
        });
        app.Ui(() =>
        {
            var to = LayerListMath.DropTarget(3, 2, 4)!.Value;
            app.Shell.Session.MoveLayer(2, to);
        });
        Check.SequenceEqual(["C", "A", "B"], Names(app), "C を最後の行の下へドロップ");
        app.Ui(() => Check.Equal(Placement.Identity, app.Shell.Session.Document!.Layers[0].Place, "ドロップの新しい土台の配置"));
    }

    // ================================================================ (7) 複製と削除

    [Test]
    public void AC28_7_FR53_CtrlDとDeleteとボタンの複製と削除()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100, new Placement(5, 6, 70)), Empty("C", 100, 100)));
        var m = Module(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.AddEffect(Target.OfLayer(1), "exposure");
            s.AddEffect(Target.OfLayer(1), "invert");
            s.AddEffect(Target.OfLayer(2), "invert");
            s.Select(Target.OfLayer(1));
            m.List.FocusSelected();
        });
        Key(app, K.D, ModifierKeys.Control);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            var doc = s.Document!;
            Check.SequenceEqual(["A", "B", "Bのコピー", "C"], doc.Layers.Select(l => l.Name).ToList(), "複製の並び");
            Check.Equal(Target.OfLayer(2), doc.Selected, "コピーが選ばれる");
            var b = doc.Layers[1];
            var copy = doc.Layers[2];
            Check.Equal(b.Place, copy.Place, "コピーの配置");
            Check.Equal(b.Stack.Count, copy.Stack.Count, "コピーのスタックの行の数");
            var salts = doc.Layers.SelectMany(l => l.Stack).Select(e => e.Salt).ToList();
            Check.Equal(salts.Count, salts.Distinct().Count(), "salt が重なる");
        });
        // 37 文字の名前のコピーは 40 文字
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetLayerName(2, new string('a', 37));
            s.DuplicateLayer(2);
            Check.Equal(new string('a', 37) + "のコピ", s.Document!.Layers[3].Name, "40 文字に切ったコピーの名前");
            s.Undo();
            s.Undo();
        });
        // 全体を選んで Ctrl+D と Delete は何も起きない
        app.Ui(() =>
        {
            app.Shell.Session.Select(Target.All);
            m.List.FocusSelected();
        });
        var count = Names(app).Length;
        Check.True(Key(app, K.D, ModifierKeys.Control), "全体での Ctrl+D が一覧に届かない");
        Check.True(Key(app, K.Delete), "全体での Delete が一覧に届かない");
        Check.Equal(count, Names(app).Length, "全体での Ctrl+D と Delete");
        // 最前面の C を Delete で消すと B のコピーが選ばれ、Ctrl+Z で元の位置・選択に戻る
        app.Ui(() =>
        {
            app.Shell.Session.Select(Target.OfLayer(3));
            m.List.FocusSelected();
        });
        Key(app, K.Delete);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.SequenceEqual(["A", "B", "Bのコピー"], s.Document!.Layers.Select(l => l.Name).ToList(), "C の削除");
            Check.Equal(Target.OfLayer(2), s.Selected, "最前面を消したあとの選択");
        });
        app.Ui(() => app.Shell.Session.Undo()); // Ctrl+Z の登録は履歴の担当(この部品の外)
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.SequenceEqual(["A", "B", "Bのコピー", "C"], s.Document!.Layers.Select(l => l.Name).ToList(), "Ctrl+Z で戻る");
            Check.Equal(Target.OfLayer(3), s.Selected, "Ctrl+Z で選択も戻る");
        });
        // 行の削除ボタンで土台 A を消すと B が土台になり配置が既定に
        var rows = Rows(app);
        Uia.Invoke(Child(rows[^1], "削除")!);
        UiHost.Idle();
        app.Ui(() =>
        {
            var doc = app.Shell.Session.Document!;
            Check.Equal("B", doc.Layers[0].Name, "新しい土台");
            Check.Equal(Placement.Identity, doc.Layers[0].Place, "新しい土台の配置");
            Check.Equal(BlendMode.Normal, doc.Layers[0].Blend, "新しい土台のブレンド");
        });
        // 行の複製ボタンで、その行のレイヤーを複製する
        Uia.Invoke(Child(Rows(app)[^1], "複製")!);
        UiHost.Idle();
        Check.SequenceEqual(["B", "Bのコピー", "Bのコピー", "C"], Names(app), "土台の複製");
        app.Ui(() => Check.Equal(Placement.Identity, app.Shell.Session.Document!.Layers[1].Place, "土台のコピーの配置"));
    }

    [Test]
    public void AC28_7_E12_最後の1枚は消せず32枚の複製はlayer_full()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 64, 64)));
        var m = Module(app);
        app.Ui(() => m.List.FocusSelected());
        Key(app, K.Delete);
        Check.Equal(1, Names(app).Length, "最後の 1 枚を消した");
        Check.True(!Child(Rows(app)[1], "削除")!.Current.IsEnabled, "最後の 1 枚の削除ボタンが有効");
        Open(app, Doc(Enumerable.Range(0, 32).Select(i => Empty("L" + i, 16, 16)).ToArray()));
        app.Ui(() => m.List.FocusSelected());
        Key(app, K.D, ModifierKeys.Control);
        Check.Equal(32, Names(app).Length, "32 枚で複製した");
        Check.Equal(NoticeKeys.LayerFull, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "layer.full が出ない");
    }

    // ================================================================ (8) 名前

    [Test]
    public void AC28_8_FR54_F2とダブルクリックで名前を編集しEnterで確定しEscで取り消す()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100)));
        var m = Module(app);
        app.Ui(() => m.List.FocusSelected());
        Key(app, K.F2);
        UiHost.Idle();
        var undo = app.Ui(() => app.Shell.Session.UndoCount);
        app.Ui(() =>
        {
            var box = RowOf(m, 1).NameEditor ?? throw new CheckFailedException("F2 で編集にならない");
            Check.True(box.IsKeyboardFocused, "編集欄にフォーカスが無い");
            box.Text = "新しい名前";
        });
        Key(app, K.Enter);
        Check.Equal("新しい名前", Names(app)[1], "Enter の確定");
        Check.Equal(undo + 1, app.Ui(() => app.Shell.Session.UndoCount), "名前の変更の件数");
        Check.True(app.Ui(() => RowOf(m, 1).NameEditor is null), "確定のあとも編集のまま");

        // ダブルクリックで編集、Esc で取り消す
        app.Ui(() =>
        {
            var r = RowOf(m, 1);
            // MouseDoubleClick は行(Control)そのものに上がる直接のイベント。押した場所(OriginalSource)は名前の文字
            r.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = r.NameText });
        });
        UiHost.Idle();
        app.Ui(() => (RowOf(m, 1).NameEditor ?? throw new CheckFailedException("ダブルクリックで編集にならない")).Text = "取り消す名前");
        Key(app, K.Escape);
        Check.Equal("新しい名前", Names(app)[1], "Esc で取り消さない");
        Check.True(app.Ui(() => RowOf(m, 1).NameEditor is null), "Esc で編集が終わらない");

        // 空白だけは反映せず、41 文字は 40 文字
        foreach (var (text, want) in new[] { ("   ", "新しい名前"), ("", "新しい名前"), (new string('n', 41), new string('n', 40)) })
        {
            app.Ui(() => m.List.FocusSelected());
            Key(app, K.F2);
            UiHost.Idle();
            app.Ui(() => RowOf(m, 1).NameEditor!.Text = text);
            Key(app, K.Enter);
            Check.Equal(want, Names(app)[1], $"「{text}」の確定");
        }

        // 設定の名前欄も同じ規則
        app.Ui(() =>
        {
            var box = m.Settings.NameBox;
            Keyboard.Focus(box);
            box.Text = "  ";
        });
        Key(app, K.Enter);
        Check.Equal(new string('n', 40), Names(app)[1], "設定の名前欄の空白");
        Check.Equal(new string('n', 40), app.Ui(() => m.Settings.NameBox.Text), "設定の名前欄が直前の名前に戻らない");
        app.Ui(() => m.Settings.NameBox.Text = new string('m', 41));
        Key(app, K.Enter);
        Check.Equal(new string('m', 40), Names(app)[1], "設定の名前欄の 41 文字");
    }

    // ================================================================ (10) 全体の行

    [Test]
    public void AC28_10_全体の行には表示サムネイル複製削除が無く設定も出ない()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 64, 64), Empty("B", 32, 32)));
        app.Ui(() => app.Shell.Session.Select(Target.All));
        UiHost.Idle();
        var all = Rows(app)[0];
        Check.Equal("全体", all.Current.Name, "全体の行");
        foreach (var n in new[] { "表示", "サムネイル", "複製", "削除" })
            Check.True(Child(all, n) is null, $"全体の行に「{n}」がある");
        var m = Module(app);
        Check.True(app.Ui(() => !m.ShowsSettings && !m.Settings.IsVisible), "全体でレイヤーの設定(不透明度・ブレンド・位置・拡縮・名前)が出る");
    }

    // ================================================================ (11) 選択は履歴を増やさない

    [Test]
    public void AC28_11_一覧の行を選ぶだけでは履歴が増えずUndoで対象を選び直す()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 400, 300), Empty("B", 100, 100)));
        var m = Module(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.Select(Target.OfLayer(0));
            m.Settings.OpacityBox.Text = "50";
            m.Settings.OpacityBox.Commit();
            s.Select(Target.OfLayer(1));
            m.Settings.OpacityBox.Text = "30";
            m.Settings.OpacityBox.Commit();
            Check.Equal(2, s.UndoCount, "2 つの変更");
        });
        var rows = Rows(app);
        Uia.Select(rows[2]);
        Uia.Select(rows[0]);
        UiHost.Idle();
        Check.Equal(2, app.Ui(() => app.Shell.Session.UndoCount), "行を選んで履歴が増えた");
        app.Ui(() => app.Shell.Session.Undo()); // Ctrl+Z の登録は履歴の担当(この部品の外)
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.Equal(100, s.Document!.Layers[1].Opacity, "1 回目の Ctrl+Z の B");
            Check.Equal(Target.OfLayer(1), s.Selected, "1 回目の Ctrl+Z の選択");
        });
        Check.True(app.Ui(() => RowOf(m, 1).IsSelected), "一覧の選択が Undo に追従しない");
        app.Ui(() => app.Shell.Session.Undo()); // Ctrl+Z の登録は履歴の担当(この部品の外)
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.Equal(100, s.Document!.Layers[0].Opacity, "2 回目の Ctrl+Z の A");
            Check.Equal(Target.OfLayer(0), s.Selected, "2 回目の Ctrl+Z の選択");
        });
    }
}
