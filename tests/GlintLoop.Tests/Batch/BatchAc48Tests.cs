// AC-48: バッチ(FR-87・INV-2・INV-16)。偽の開くダイアログ・偽のフォルダ選択・止められる偽の書き出しの中身・一時フォルダで調べる。バイト列の一致は WARP の本物の中身で確かめる。
// ドロップは DragEventArgs をリフレクションで作り、バッチ画面の上で Drop を起こす(ウィンドウ全体のドロップに届かないことも確かめる)。
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Batch;
using GlintLoop.App.Export;
using GlintLoop.App.Screens;
using GlintLoop.App.Text;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Exports;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Batches;

public sealed class BatchAc48Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    // ---------------- 道具 ----------------

    private static BatchModule Module(TestApp app) => M<BatchModule>(app);

    internal static string Folder(TestApp app, string name)
    {
        var dir = Path.Combine(app.Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string WriteFile(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Png(int w = 40, int h = 30, byte r = 200) => SkiaBytes.Png(w, h, Img.Solid(w, h, r, 40, 90));

    internal static byte[] PatternPng(int w = 96, int h = 64) => SkiaBytes.Png(w, h, Img.Pattern(w, h, alpha: false));

    private static byte[] BrokenPng()
    {
        var png = SkiaBytes.Png(64, 64, Img.Pattern(64, 64, alpha: false));
        return png[..(png.Length / 2)];
    }

    private static byte[] TooWidePng() => PngBytes.OneBitGray(16385, 1, white: true);

    /// <summary>WPF の GifBitmapEncoder で 2 フレームの GIF を作る。</summary>
    private static byte[] TwoFrameGif(int w = 48, int h = 32) => UiHost.Invoke(() =>
    {
        var encoder = new GifBitmapEncoder();
        foreach (var v in new byte[] { 40, 220 })
        {
            var px = new byte[w * h * 4];
            for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (v, 90, 30, 255);
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        }
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    });

    internal static BatchView Open(TestApp app)
    {
        var m = Module(app);
        app.Ui(m.Show);
        UiHost.Idle();
        var view = app.Ui(() => m.View);
        Check.True(view is not null, "バッチ画面が開かない");
        return view!;
    }

    private static DragEventArgs DragArgs(IDataObject data, RoutedEvent ev, UIElement target)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point(10, 10)]);
        args.RoutedEvent = ev;
        return args;
    }

    private static DataObject Files(IEnumerable<string> paths) => new(DataFormats.FileDrop, paths.ToArray());

    internal static void Drop(TestApp app, BatchView view, IEnumerable<string> paths) => app.Ui(() =>
    {
        var args = DragArgs(Files(paths), DragDrop.DropEvent, view);
        view.RaiseEvent(args);
        Check.True(args.Handled, "ドロップをバッチ画面が受けていない");
    });

    /// <summary>出力フォルダを選んで実行し、終わるまで待つ。</summary>
    internal static void Run(TestApp app, BatchView view, string folder)
    {
        Choose(app, view, folder);
        Start(app, view);
        Finish(app, view);
    }

    internal static void Choose(TestApp app, BatchView view, string folder)
    {
        app.FileDialogs.FolderAnswers.Enqueue(folder);
        var root = Uia.Root(app.Hwnd);
        Uia.Invoke(Uia.FindById(root, "BatchPickFolder"));
        Check.True(UiHost.WaitUntil(() => view.Folder == folder, Limit), "出力フォルダが入らない");
    }

    internal static void Start(TestApp app, BatchView view)
    {
        var root = Uia.Root(app.Hwnd);
        UiHost.Idle();
        var run = Uia.FindById(root, "BatchRun");
        Check.True(run.Current.IsEnabled, "「実行」を押せない");
        Uia.Invoke(run);
        UiHost.Idle(); // クリックは Input の優先度で起きる。起きた時点で実行が始まっている(Running が入る)
    }

    internal static void Finish(TestApp app, BatchView view)
    {
        var m = Module(app);
        Check.True(UiHost.WaitUntil(() => m.Running is null && !view.IsRunning, Limit), "実行が終わらない");
        UiHost.Idle();
    }

    private static void RunWithFake(TestApp app, IExportPipeline pipeline)
    {
        var m = Module(app);
        app.Ui(() => m.Pipeline = pipeline);
    }

    private static string[] ListNames(TestApp app, ListBox list) =>
        app.Ui(() => list.Items.Cast<ListBoxItem>().Select(i => AutomationProperties.GetName(i)).ToArray());

    internal static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static string[] FileNames(string dir) => Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static int CacheFiles(TestApp app) => Directory.Exists(app.Paths.CacheExportsDir) ? Directory.GetFiles(app.Paths.CacheExportsDir).Length : 0;

    /// <summary>進みの文(n / N)と処理中の名前の変化を、古い順に集める。</summary>
    private static (List<string> Progress, List<string> Names) Record(TestApp app, BatchView view)
    {
        var progress = new List<string>();
        var names = new List<string>();
        app.Ui(() =>
        {
            var d = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            d.AddValueChanged(view.ProgressText, (_, _) => progress.Add(view.ProgressText.Text));
            d.AddValueChanged(view.CurrentName, (_, _) => names.Add(view.CurrentName.Text));
        });
        return (progress, names);
    }

    /// <summary>a.png を取り込んだ文書と本物の中身を用意する(長辺 64・0.5 秒・20 fps の GIF に spin 1 個)。</summary>
    internal static TestApp StartReal(string aName = "a.png", byte[]? a = null, FakeFileSystem? fileSystem = null, FakeDeviceFactory? deviceFactory = null, Func<GlintLoop.App.Startup.AppShell, GlintLoop.App.Startup.ICrashHandler>? crashFactory = null)
    {
        var app = TestApp.Start(gpu: deviceFactory is null ? GpuAdapterRequest.Warp : GpuAdapterRequest.HighPerformance, fileSystem: fileSystem, deviceFactory: deviceFactory, crashFactory: crashFactory);
        var input = Folder(app, "in");
        var path = WriteFile(input, aName, a ?? PatternPng());
        Import(app, path);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetLength10(5);
            s.SetFps(20);
            s.SetOutputFormat(ExportFormat.Gif);
            s.SetOutputSize("long", 64);
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
        });
        return app;
    }

    private static void Import(TestApp app, params string[] paths)
    {
        var screens = M<ScreensModule>(app);
        app.Ui(() => { _ = screens.Imports.OpenPathsAsync(paths); });
        Check.True(UiHost.WaitUntil(() => !screens.Imports.IsImporting && !screens.Imports.Gate.IsBusy, Limit), "取り込みが終わらない");
    }

    /// <summary>通常の書き出し(自動の書き出し)を動かして、キャッシュのファイルのバイト列を返す。</summary>
    private static byte[] NormalExport(TestApp app)
    {
        var band = M<ExportBandModule>(app);
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, Limit), "本物の中身が入らない");
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, Limit), "書き出しが終わらない");
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "通常の書き出しの状態(" + app.Ui(() => band.Auto.Error?.ToString()) + ")");
        return File.ReadAllBytes(app.Ui(() => band.Auto.Outcome!.Path));
    }

    /// <summary>1 枚目の画像のバッチの出力を、新しい出力フォルダへ作ってバイト列を返す。</summary>
    internal static byte[] BatchOutput(TestApp app, string folderName, string input, string expectedName)
    {
        var folder = Folder(app, folderName);
        var view = Open(app);
        Drop(app, view, [input]);
        Run(app, view, folder);
        Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗: " + string.Join("、", app.Ui(() => view.Failures.Select(f => f.Reason).ToArray())));
        app.Ui(() => app.Shell.Dialogs.Close());
        UiHost.Idle();
        return File.ReadAllBytes(Path.Combine(folder, expectedName));
    }

    // ================================================================ 画面の部品

    [Test]
    public void AC48_0_コマンドバーのボタンとCtrlBで開き_部品がUIAで見つかり_幅と高さが表のとおり()
    {
        using var app = TestApp.Start();
        var root = Uia.Root(app.Hwnd);
        Check.True(Uia.Find(root, "バッチ").Current.IsEnabled, "ウェルカム画面のバッチのボタンが無効");
        Uia.Invoke(Uia.Find(root, "バッチ"));
        UiHost.Idle();
        Check.True(app.Ui(() => app.Shell.Dialogs.Top is BatchView), "ボタンで開かない");
        foreach (var id in new[] { "BatchOpen", "BatchRun", "BatchCancel", "BatchList", "BatchProgress", "BatchFailures", "BatchPickFolder" })
            Uia.FindById(root, id);
        Check.Equal("開く", Uia.FindById(root, "BatchOpen").Current.Name, "「開く」");
        Check.Equal("実行", Uia.FindById(root, "BatchRun").Current.Name, "「実行」");
        Check.Equal("取り消し", Uia.FindById(root, "BatchCancel").Current.Name, "「取り消し」");
        var card = app.Ui(() => (FrameworkElement)((FrameworkElement)app.Shell.Dialogs.Top!.Parent).Parent);
        Check.Near(640, app.Ui(() => card.ActualWidth), 1, "幅");
        Check.Near(560, app.Ui(() => card.ActualHeight), 1, "高さ");

        // 閉じて、Ctrl+B でも開く
        app.Ui(() => app.Shell.Dialogs.Close());
        UiHost.Idle();
        Check.True(Key(app, System.Windows.Input.Key.B, ModifierKeys.Control), "Ctrl+B が処理されない");
        UiHost.Idle();
        Check.True(app.Ui(() => app.Shell.Dialogs.Top is BatchView), "Ctrl+B で開かない");
        // 文書があっても押せる
        app.Ui(() => app.Shell.Dialogs.Close());
        app.Ui(() => app.Shell.Session.NewEmptyCanvas(64, 48));
        UiHost.Idle();
        Check.True(Uia.Find(root, "バッチ").Current.IsEnabled, "編集画面のバッチのボタンが無効");
    }

    // ================================================================ (1) 追加

    [Test]
    public void AC48_1_ドロップと重複と空の文と開くダイアログ()
    {
        using var app = TestApp.Start();
        var dir = Folder(app, "in");
        var a = WriteFile(dir, "a.png", Png());
        var b = WriteFile(dir, "b.png", Png());
        var c = WriteFile(dir, "c.png", Png());
        var view = Open(app);
        var root = Uia.Root(app.Hwnd);
        Check.Equal("画像をドロップするか、開くで追加してください", Uia.FindById(root, "BatchEmpty").Current.Name, "空のときの文");

        // ダイアログの層より外へ上がらない(ウィンドウ全体の受け手 FR-3 は Handled にするので、ウィンドウ自身では見分けられない。1 つ内側の RootLayer で数える)
        var reachedWindow = 0;
        app.Ui(() => app.Shell.Window.RootLayer.Drop += (_, _) => reachedWindow++);
        Drop(app, view, [a, b, c]);
        UiHost.Idle();
        Check.Equal(0, app.Ui(() => reachedWindow), "ダイアログの層の外へ上がったドロップ");
        Check.SequenceEqual(["a.png", "b.png", "c.png"], ListNames(app, view.List), "3 個のドロップ");
        Check.True(app.Ui(() => !view.EmptyText.IsVisible), "空のときの文が消えない");
        Drop(app, view, [a]);
        Drop(app, view, [b.ToUpperInvariant(), c.Replace("c.png", "C.PNG", StringComparison.Ordinal)]);
        Check.Equal(3, ListNames(app, view.List).Length, "同じパスと、大文字小文字を変えた同じパス");
        Check.Equal(null, app.Ui(() => app.Shell.NoticeBar.Current), "重複で通知は出ない");

        // ウィンドウ全体のドロップ(FR-3)へ届かない: 文書ができず、受け入れ表示も出ない
        app.Ui(() =>
        {
            var over = DragArgs(Files([a]), DragDrop.DragOverEvent, view);
            view.RaiseEvent(over);
            Check.True(over.Handled, "ドラッグの通過を受けていない");
            Check.Equal(DragDropEffects.Copy, over.Effects, "ドラッグの効果");
            Check.True(!M<ScreensModule>(app).DropOverlay.IsShowing, "ウィンドウ全体の受け入れ表示が出た");
        });
        UiHost.Idle();
        Check.True(app.Ui(() => !app.Shell.Session.HasDocument), "ドロップで文書に取り込まれた");

        // 「開く」の結果も一覧に足す
        var d = WriteFile(dir, "d.png", Png());
        app.FileDialogs.OpenAnswers.Enqueue([d, a]);
        Uia.Invoke(Uia.FindById(root, "BatchOpen"));
        Check.True(UiHost.WaitUntil(() => view.Paths.Paths.Count == 4, Limit), "「開く」で増えない");
    }

    [Test]
    public void AC48_1_ドロップはバッチ画面が閉じていれば今までどおり取り込む()
    {
        using var app = TestApp.Start();
        var a = WriteFile(Folder(app, "in"), "a.png", Png());
        var w = app.Ui(() => app.Shell.Window.RootLayer);
        app.Ui(() => w.RaiseEvent(DragArgs(Files([a]), DragDrop.DropEvent, w)));
        Check.True(UiHost.WaitUntil(() => app.Shell.Session.HasDocument, Limit), "取り込まれない");
    }

    [Test]
    public void AC48_1_205個を1回でドロップすると200個でbatch_fullが1回_さらに1個でも出る()
    {
        using var app = TestApp.Start();
        var dir = Folder(app, "in");
        var png = Png(1, 1);
        var paths = Enumerable.Range(0, 206).Select(i => WriteFile(dir, $"p{i}.png", png)).ToArray();
        var view = Open(app);
        Drop(app, view, paths[..205]);
        Check.Equal(200, ListNames(app, view.List).Length, "一覧の個数");
        Check.Equal(NoticeKeys.BatchFull, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "batch.full");
        Check.Equal("バッチは 200 個までです", app.Ui(() => app.Shell.NoticeBar.Text), "文");
        // 1 回の追加で 1 回: 閉じてから、重複だけの追加では出ない
        app.Ui(() => app.Shell.NoticeBar.Close());
        UiHost.Idle();
        Drop(app, view, paths[..3]);
        Check.Equal(null, app.Ui(() => app.Shell.NoticeBar.Current), "重複だけの追加で出た");
        Drop(app, view, [paths[205]]);
        Check.Equal(200, ListNames(app, view.List).Length, "200 個のときの追加");
        Check.Equal(NoticeKeys.BatchFull, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "200 個のときの batch.full");
    }

    // ================================================================ (2) 実行

    [Test(Slow = true)]
    public void AC48_2_正常と壊れたものと大きすぎるものを実行し_出力と失敗と進みが表のとおり()
    {
        using var app = StartReal();
        var dir = Folder(app, "in2");
        var a = WriteFile(dir, "a.png", PatternPng());
        var b = WriteFile(dir, "b.jpeg", SkiaBytes.Jpeg(96, 64, Img.Pattern(96, 64, alpha: false)));
        var c = WriteFile(dir, "c.png", BrokenPng());
        var d = WriteFile(dir, "d.gif", TwoFrameGif());
        var e = WriteFile(dir, "e.png", TooWidePng());
        var output = Folder(app, "out");
        var view = Open(app);
        Drop(app, view, [a, b, c, d, e]);
        var (progress, names) = Record(app, view);
        Run(app, view, output);

        Check.SequenceEqual(["a-glint.gif", "b-glint.gif", "d-glint.gif"], FileNames(output), "出力のファイル");
        foreach (var f in FileNames(output))
        {
            var frames = app.Ui(() =>
            {
                using var s = File.OpenRead(Path.Combine(output, f));
                return new GifBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Count;
            });
            Check.Equal(10, frames, f + " のフレーム数(0.5 秒 × 20 fps)");
        }
        var failures = app.Ui(() => view.Failures.ToArray());
        Check.SequenceEqual(["c.png", "e.png"], failures.Select(x => x.Name).ToArray(), "失敗の名前");
        Check.Equal("画像を読み込めませんでした。ファイルが壊れている可能性があります", failures[0].Reason, "c.png の理由");
        Check.Equal("画像の縦または横が大きすぎます(16385×1 px。上限は 16384 px)", failures[1].Reason, "e.png の理由");
        Check.Equal(2, ListNames(app, view.FailureList).Length, "失敗の一覧の行");
        Check.True(ListNames(app, view.FailureList)[0].StartsWith("c.png", StringComparison.Ordinal), "失敗の一覧の 1 行目");

        // 実行の前から「0 / 5」が出ているので、変化は 1 からの 5 回
        Check.SequenceEqual(["1 / 5", "2 / 5", "3 / 5", "4 / 5", "5 / 5"], app.Ui(() => progress.ToArray()), "進み");
        foreach (var n in new[] { "a.png", "b.jpeg", "c.png", "d.gif", "e.png" })
            Check.True(app.Ui(() => names.Contains(n)), "処理中の名前に出ない: " + n);
        var root = Uia.Root(app.Hwnd);
        Check.Equal("5 / 5", Uia.FindById(root, "BatchProgress").Current.Name, "UIA の進み");
    }

    // ================================================================ (3) 通常の書き出しとの一致・キャッシュ・.tmp

    [Test(Slow = true)]
    public void AC48_3_通常の書き出しとバイト列が一致しキャッシュが増えない()
    {
        using var app = StartReal();
        var normal = NormalExport(app);
        var cached = CacheFiles(app);
        var input = Path.Combine(app.Root, "in", "a.png");
        var batch = BatchOutput(app, "out", input, "a-glint.gif");
        Check.True(normal.AsSpan().SequenceEqual(batch), $"バイト列が違う({normal.Length} と {batch.Length})");
        Check.Equal(cached, CacheFiles(app), "cache\\exports のファイルの数");
    }

    [Test(Slow = true)]
    public void AC48_3_静止形式でもスクラブバーの位置が通常の書き出しと同じになりバイト列が一致する()
    {
        using var app = StartReal();
        app.Ui(() =>
        {
            app.Shell.Session.SetOutputFormat(ExportFormat.Png);
            app.Shell.Frames.Set(3);
        });
        var normal = NormalExport(app);
        var input = Path.Combine(app.Root, "in", "a.png");
        var batch = BatchOutput(app, "out", input, "a-glint.png");
        Check.True(normal.AsSpan().SequenceEqual(batch), "PNG のバイト列が違う");
    }

    [Test(Slow = true)]
    public void AC48_3_途中で書けなくなると最終的な名前のファイルが残らず_tmpも消える()
    {
        using var app = StartReal();
        var output = Folder(app, "out");
        app.FileSystem.FailOn(FileOps.Write, p => p.StartsWith(output, StringComparison.OrdinalIgnoreCase));
        var view = Open(app);
        Drop(app, view, [Path.Combine(app.Root, "in", "a.png")]);
        Run(app, view, output);
        Check.SequenceEqual([], FileNames(output), "出力フォルダ");
        Check.True(app.FileSystem.Calls.Any(c => c.Operation == FileOps.Create && c.Path.EndsWith("a-glint.gif.tmp", StringComparison.OrdinalIgnoreCase)), ".tmp を経由していない");
        Check.Equal("保存できませんでした。保存先を変えて試してください", app.Ui(() => view.Failures.Single().Reason), "理由");
    }

    [Test]
    public void AC48_3_リネームの失敗でも残らない_書き出しは出力先の_tmpからのリネーム()
    {
        using var app = TestApp.Start();
        var input = WriteFile(Folder(app, "in"), "a.png", Png());
        var output = Folder(app, "out");
        var view = Open(app);
        RunWithFake(app, new FakePipeline());
        Drop(app, view, [input]);
        app.FileSystem.FailOn(FileOps.Move, p => p.StartsWith(output, StringComparison.OrdinalIgnoreCase));
        Run(app, view, output);
        Check.SequenceEqual([], FileNames(output), "リネームの失敗");
        Check.Equal("保存できませんでした。保存先を変えて試してください", app.Ui(() => view.Failures.Single().Reason), "理由");
        app.FileSystem.ClearFailures();
        Run(app, view, output);
        Check.SequenceEqual(["a-glint.gif"], FileNames(output), "成功");
        Check.True(app.FileSystem.Calls.Any(c => c.Operation == FileOps.Move && c.Path == Path.Combine(output, "a-glint.gif")), "リネームしていない");
    }

    // ================================================================ (4) 名前

    private static string[] RunNames(string format, Action<TestApp, string>? prepare, params (string Folder, string Name)[] inputs)
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            app.Shell.Session.NewEmptyCanvas(64, 48);
            app.Shell.Session.SetOutputFormat(format);
        });
        UiHost.Idle();
        var output = Folder(app, "out");
        prepare?.Invoke(app, output);
        var paths = inputs.Select(i => WriteFile(Folder(app, i.Folder), i.Name, Png())).ToArray();
        var view = Open(app);
        RunWithFake(app, new FakePipeline());
        Drop(app, view, paths);
        Run(app, view, output);
        Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗");
        return FileNames(output);
    }

    [Test]
    public void AC48_4_名前の規則()
    {
        Check.SequenceEqual(["a-glint-2.gif", "a-glint.gif"], RunNames(ExportFormat.Gif, (_, o) => File.WriteAllBytes(Path.Combine(o, "a-glint.gif"), [1]), ("in", "a.png")), "既にある");
        Check.SequenceEqual(["a-glint-2.gif", "a-glint-3.gif", "a-glint.gif"], RunNames(ExportFormat.Gif, null, ("x", "a.png"), ("y", "a.png"), ("z", "a.jpeg")), "別のフォルダの同名");
        Check.SequenceEqual(["a-glint.jpg"], RunNames(ExportFormat.Jpeg, null, ("in", "a.png")), "JPEG");
        Check.SequenceEqual(["a-glint.png"], RunNames(ExportFormat.Apng, null, ("in", "a.png")), "APNG");
        Check.SequenceEqual(["写真-glint.gif"], RunNames(ExportFormat.Gif, null, ("in", "写真.png")), "日本語の名前");
    }

    [Test]
    public void AC48_4_大文字と小文字だけが違う名前も同じ名前として番号を付ける()
    {
        var names = RunNames(ExportFormat.Gif, null, ("x", "a.png"), ("y", "A.PNG"));
        Check.Equal(2, names.Length, "2 個が別の名前になる: " + string.Join("、", names));
        Check.True(names.Any(n => n.Equals("a-glint.gif", StringComparison.OrdinalIgnoreCase)) && names.Any(n => n.Equals("a-glint-2.gif", StringComparison.OrdinalIgnoreCase)), string.Join("、", names));
    }

    [Test]
    public void AC48_4_名前の決め方は入力と書いた名前を避ける()
    {
        var folder = Path.Combine("C:\\", "out");
        var taken = new HashSet<string>([Path.Combine(folder, "a-glint.gif"), Path.Combine(folder, "a-glint-2.gif")], StringComparer.OrdinalIgnoreCase);
        Check.Equal(Path.Combine(folder, "a-glint-3.gif"), BatchNames.Pick(folder, "D:\\in\\a.png", ExportFormat.Gif, taken.Contains), "番号");
        Check.Equal(Path.Combine(folder, "a.b-glint.jpg"), BatchNames.Pick(folder, "D:\\in\\a.b.png", ExportFormat.Jpeg, _ => false), "最後の拡張子だけを除く");
    }

    // ================================================================ (5) 取り消し

    /// <summary>StopAt 回目の呼び出しで、途中まで書いて取り消しの合図を待つ偽の中身(フレームの切れ目で止まる)。</summary>
    private sealed class StoppablePipeline(int stopAt) : IExportPipeline
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ManualResetEventSlim Reached { get; } = new();

        public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
        {
            var n = Interlocked.Increment(ref _calls);
            output.Write([1, 2, 3, 4]);
            output.Flush();
            if (n == stopAt)
            {
                Reached.Set();
                run.Cancel.WaitHandle.WaitOne();
            }
            run.Cancel.ThrowIfCancellationRequested();
            return new ExportPipelineResult(1, 1, 1, 20, null, false, null);
        }

        public ExportCheck Check(ExportRequest job, ExportWritten written) => new(null, null);
    }

    private static (TestApp App, BatchView View, StoppablePipeline Pipeline, string Output) StartStopped()
    {
        var app = TestApp.Start();
        var dir = Folder(app, "in");
        var output = Folder(app, "out");
        var paths = new[] { "a.png", "b.png", "c.png", "d.png", "e.png" }.Select(n => WriteFile(dir, n, Png())).ToArray();
        var view = Open(app);
        var pipeline = new StoppablePipeline(3);
        RunWithFake(app, pipeline);
        Drop(app, view, paths);
        Choose(app, view, output);
        Start(app, view);
        Check.True(pipeline.Reached.Wait(Limit), "3 個目に届かない");
        return (app, view, pipeline, output);
    }

    [Test]
    public void AC48_5_取り消しのボタンで3個目が止まりtmpが消え残りを処理せず2_5で止まる()
    {
        var (app, view, pipeline, output) = StartStopped();
        using (app)
        {
            Check.SequenceEqual(["a-glint.gif", "b-glint.gif", "c-glint.gif.tmp"], FileNames(output), "3 個目の処理中");
            Check.True(UiHost.WaitUntil(() => view.CurrentName.Text == "c.png", Limit), "処理中の名前: " + app.Ui(() => view.CurrentName.Text));
            var root = Uia.Root(app.Hwnd);
            Check.True(!Uia.FindById(root, "BatchRun").Current.IsEnabled, "実行中の「実行」");
            Uia.Invoke(Uia.FindById(root, "BatchCancel"));
            Finish(app, view);
            Check.SequenceEqual(["a-glint.gif", "b-glint.gif"], FileNames(output), "取り消しのあと");
            Check.Equal(3, pipeline.Calls, "4 個目と 5 個目を処理しない");
            Check.Equal("2 / 5", app.Ui(() => view.ProgressText.Text), "進み");
            Check.Equal(0, app.Ui(() => view.Failures.Count), "取り消しは失敗の一覧に載らない");
            Check.True(app.Ui(() => view.RunButton.IsEnabled), "終わったあとの「実行」");
        }
    }

    [Test]
    public void AC48_5_実行中のEscは取り消しでダイアログを閉じず_終わったあとのEscで閉じる()
    {
        var (app, view, pipeline, output) = StartStopped();
        using (app)
        {
            Check.True(Key(app, System.Windows.Input.Key.Escape), "Esc が処理されない");
            Check.True(app.Ui(() => app.Shell.Dialogs.Top is BatchView), "実行中の Esc でダイアログが閉じた");
            Finish(app, view);
            Check.Equal(3, pipeline.Calls, "残りを処理しない");
            Check.Equal("2 / 5", app.Ui(() => view.ProgressText.Text), "進み");
            Check.SequenceEqual(["a-glint.gif", "b-glint.gif"], FileNames(output), "出力");
            Check.True(Key(app, System.Windows.Input.Key.Escape), "Esc が処理されない");
            UiHost.Idle();
            Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "終わったあとの Esc で閉じない");
        }
    }

    // ================================================================ (6) 容量上限

    [Test]
    public void AC48_6_discord_emojiで収まらないと結果を書き失敗の一覧にsize_failで載る()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            app.Shell.Session.NewEmptyCanvas(64, 48);
            Check.True(app.Shell.Session.SetOutputLimit(new OutputLimit("preset", "discord_emoji", null)).Changed, "上限を入れられない");
        });
        UiHost.Idle();
        var input = WriteFile(Folder(app, "in"), "a.png", Png());
        var output = Folder(app, "out");
        var content = new byte[1000];
        var view = Open(app);
        RunWithFake(app, new FakePipeline { Content = _ => content, Result = new ExportPipelineResult(16, 16, 1, 20, null, true, null) });
        Drop(app, view, [input]);
        Run(app, view, output);
        Check.SequenceEqual(["a-glint.gif"], FileNames(output), "最小の設定の結果が書き出される");
        var limit = OutputPlan.Limit(new OutputLimit("preset", "discord_emoji", null)).Bytes!.Value;
        Check.Equal(NoticeTexts.Format(NoticeKeys.ExportSizeFail, ("limit", limit.ToString()), ("size", "1000")), app.Ui(() => view.Failures.Single().Reason), "理由");
        Check.Equal("1 / 1", app.Ui(() => view.ProgressText.Text), "進み");
    }

    [Test(Slow = true)]
    public void AC48_6_本物の中身でも上限に収まらないと結果を書き失敗の一覧に載る()
    {
        using var app = StartReal();
        app.Ui(() => app.Shell.Session.SetOutputLimit(new OutputLimit("bytes", null, 10)));
        var output = Folder(app, "out");
        var view = Open(app);
        Drop(app, view, [Path.Combine(app.Root, "in", "a.png")]);
        Run(app, view, output);
        Check.SequenceEqual(["a-glint.gif"], FileNames(output), "出力");
        var size = new FileInfo(Path.Combine(output, "a-glint.gif")).Length;
        Check.True(size > 10, "上限に収まっている");
        Check.Equal(NoticeTexts.Format(NoticeKeys.ExportSizeFail, ("limit", "10"), ("size", size.ToString())), app.Ui(() => view.Failures.Single().Reason), "理由");
    }

    [Test]
    public void AC48_6_大きすぎる失敗と書き出しの失敗の文()
    {
        using var app = TestApp.Start();
        var input = WriteFile(Folder(app, "in"), "a.png", Png());
        var output = Folder(app, "out");
        var view = Open(app);
        Drop(app, view, [input]);
        Choose(app, view, output);
        RunWithFake(app, new FakePipeline { Throw = new ExportTooBigException(new ExportSettings { Format = ExportFormat.Apng, Width = 5000, Height = 5000, LongSide = 5000 }) });
        Start(app, view);
        Finish(app, view);
        Check.Equal(NoticeTexts.Format(NoticeKeys.ExportGifTooBig, ("fmt", "APNG")), app.Ui(() => view.Failures.Single().Reason), "大きすぎる");
        RunWithFake(app, new FakePipeline { Throw = new InvalidOperationException("偽の失敗") });
        Start(app, view);
        Finish(app, view);
        Check.Equal(NoticeTexts.Format(NoticeKeys.ExportFailed), app.Ui(() => view.Failures.Single().Reason), "書き出しの失敗(前回の失敗は消える)");
        Check.SequenceEqual([], FileNames(output), "出力");
    }

    // ================================================================ (7) 元のファイル

    [Test]
    public void AC48_7_元のファイルが変わらず_読み取り専用でも処理でき_同じフォルダの同名を上書きしない()
    {
        using var app = TestApp.Start();
        var dir = Folder(app, "in");
        var a = WriteFile(dir, "a.png", Png());
        var other = WriteFile(dir, "a-glint.gif", TwoFrameGif());
        var ro = WriteFile(dir, "ro.png", Png(r: 80));
        var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (var p in new[] { a, other, ro }) File.SetLastWriteTimeUtc(p, old);
        var before = new[] { a, other, ro }.Select(Sha).ToArray();
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try
        {
            var view = Open(app);
            RunWithFake(app, new FakePipeline());
            Drop(app, view, [a, other, ro]);
            Run(app, view, dir);
            Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗: " + string.Join("、", app.Ui(() => view.Failures.Select(f => f.Name + f.Reason).ToArray())));
            Check.SequenceEqual(before, new[] { a, other, ro }.Select(Sha).ToArray(), "元のファイルの SHA-256");
            foreach (var p in new[] { a, other, ro }) Check.Equal(old, File.GetLastWriteTimeUtc(p), "最終更新時刻: " + p);
            Check.True(File.Exists(Path.Combine(dir, "a-glint-2.gif")), "a.png の出力が a-glint-2.gif でない: " + string.Join("、", FileNames(dir)));
            Check.True(File.Exists(Path.Combine(dir, "a-glint-glint.gif")), "a-glint.gif の出力");
            Check.True(File.Exists(Path.Combine(dir, "ro-glint.gif")), "読み取り専用のファイルの出力");
            Check.True(!app.FileSystem.Calls.Any(c => c.Operation is FileOps.Create or FileOps.OpenAppend or FileOps.Delete or FileOps.SetLastWriteUtc
                                                     && new[] { a, other, ro }.Contains(c.Path, StringComparer.OrdinalIgnoreCase)), "元のファイルを書き込みで開いた");
        }
        finally
        {
            File.SetAttributes(ro, FileAttributes.Normal);
        }
    }

    // ================================================================ (8) 書き込めない

    [Test]
    public void AC48_8_出力先に書き込めないと各ファイルがsave_failedで載る()
    {
        using var app = TestApp.Start();
        var dir = Folder(app, "in");
        var paths = new[] { "a.png", "b.png" }.Select(n => WriteFile(dir, n, Png())).ToArray();
        var output = Folder(app, "out");
        var view = Open(app);
        RunWithFake(app, new FakePipeline());
        Drop(app, view, paths);
        app.FileSystem.FailOn(FileOps.Create, p => p.StartsWith(output, StringComparison.OrdinalIgnoreCase), () => new UnauthorizedAccessException("偽"));
        Run(app, view, output);
        var failures = app.Ui(() => view.Failures.ToArray());
        Check.SequenceEqual(["a.png", "b.png"], failures.Select(f => f.Name).ToArray(), "失敗の名前");
        foreach (var f in failures) Check.Equal("保存できませんでした。保存先を変えて試してください", f.Reason, f.Name);
        Check.Equal("2 / 2", app.Ui(() => view.ProgressText.Text), "進み");
        Check.SequenceEqual([], FileNames(output), "出力");
    }

    // ================================================================ (9) レイヤーのある文書

    [Test]
    public void AC48_9_レイヤーが2枚の文書は画面の中に文が出続け_1枚では出ない()
    {
        using var app = TestApp.Start();
        app.Ui(() => app.Shell.Session.NewEmptyCanvas(64, 48));
        UiHost.Idle();
        var view = Open(app);
        var root = Uia.Root(app.Hwnd);
        Check.True(app.Ui(() => !view.LayersIgnored.IsVisible), "1 枚の文書で出た");
        app.Ui(() => app.Shell.Dialogs.Close());
        UiHost.Idle();
        app.Ui(() => app.Shell.Session.AddEmptyLayer());
        UiHost.Idle();
        view = Open(app);
        Check.Equal(
            "いちばん下のレイヤーと全体のスタックだけを適用します。ほかのレイヤーは使いません",
            Uia.FindById(root, "BatchLayersIgnored").Current.Name, "文");
        Check.True(app.Ui(() => view.LayersIgnored.IsVisible), "2 枚の文書で出ない");
        Check.Equal(null, app.Ui(() => app.Shell.NoticeBar.Current), "通知バーには出さない");
        // 実行の間も出たまま
        var input = WriteFile(Folder(app, "in"), "a.png", Png());
        RunWithFake(app, new FakePipeline());
        Drop(app, view, [input]);
        Run(app, view, Folder(app, "out"));
        Check.True(app.Ui(() => view.LayersIgnored.IsVisible), "実行のあとで消えた");
        // 文書が 1 枚に戻れば消える
        app.Ui(() => app.Shell.Session.Undo());
        UiHost.Idle();
        Check.True(app.Ui(() => !view.LayersIgnored.IsVisible), "1 枚に戻っても出ている");
    }

    [Test]
    public void AC48_9_文書を写す内容_土台の表示と不透明度は使わず2枚目は使わない()
    {
        using var app = TestApp.Start();
        var dir = Folder(app, "in");
        var baseFile = WriteFile(dir, "base.png", Png());
        Import(app, baseFile);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.AddEmptyLayer();
            s.AddEffect(Target.OfLayer(0), "spin");
            s.SetLookStrength(Target.OfLayer(0), 40);
            s.AddEffect(Target.All, "vignette");
            s.SetLookStrength(Target.All, 70);
            s.SetLayerOpacity(0, 50);
            s.SetLayerVisible(0, false);
            s.SetLength10(7);
            s.SetFps(25);
            s.SetOutputSize("long", 64);
        });
        UiHost.Idle();
        var doc = app.Ui(() => app.Shell.Session.Document!);
        var fake = new FakePipeline();
        var view = Open(app);
        RunWithFake(app, fake);
        var a = WriteFile(dir, "a.png", Png(r: 30));
        Drop(app, view, [a]);
        Run(app, view, Folder(app, "out"));
        Check.True(fake.Jobs.TryDequeue(out var job), "中身が呼ばれない");
        var d = job!.Document;
        Check.Equal(1, d.Layers.Count, "レイヤーの枚数");
        Check.Equal(100, d.Base.Opacity, "不透明度");
        Check.Equal(true, d.Base.Visible, "表示");
        Check.Equal(LayerKind.Image, d.Base.Kind, "種類");
        Check.True(d.Base.Asset != doc.Base.Asset, "画像が差し替わっていない");
        Check.True(ReferenceEquals(d.Base.Stack, doc.Base.Stack), "土台のスタック");
        Check.Equal(40, d.Base.LookStrength, "土台のルックの強さ");
        Check.True(ReferenceEquals(d.All, doc.All), "全体");
        Check.Equal(doc.Seed, d.Seed, "seed");
        Check.Equal(doc.SaltCounter, d.SaltCounter, "saltCounter");
        Check.Equal(doc.Time, d.Time, "時間");
        Check.True(ReferenceEquals(d.Output, doc.Output), "出力設定");
        // 元の文書は土台を隠しているので N は 1 だが、バッチの土台はオンなので spin の分の N になる(0.7 秒 × 25 fps を丸めた 18)
        Check.Equal(18, job.TimelineFrames, "N");
        // 仕事は BelowNormal の専用スレッドで動く(INV-5)
        Check.True(fake.Priorities.TryDequeue(out var priority) && priority == ThreadPriority.BelowNormal, "優先度");
    }

    [Test(Slow = true)]
    public void AC48_9_2枚目と土台の不透明度は出力に効かず_写した文書の通常の書き出しと一致する()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var dir = Folder(app, "in");
        var baseFile = WriteFile(dir, "base.png", SkiaBytes.Png(80, 80, Img.Solid(80, 80, 10, 200, 30)));
        var second = WriteFile(dir, "second.png", SkiaBytes.Png(80, 80, Img.Solid(80, 80, 240, 240, 10)));
        var second2 = WriteFile(dir, "second2.png", SkiaBytes.Png(80, 80, Img.Solid(80, 80, 10, 10, 240)));
        var a = WriteFile(dir, "a.png", PatternPng());
        Import(app, baseFile);
        Import(app, second);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetLength10(5);
            s.SetFps(20);
            s.SetOutputFormat(ExportFormat.Gif);
            s.SetOutputSize("long", 64);
            s.SetLayerOpacity(1, 50);
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin");
            Check.True(s.AddEffect(Target.All, "vignette").Changed, "vignette");
        });
        UiHost.Idle();
        var first = BatchOutput(app, "out1", a, "a-glint.gif");

        // 2 枚目の画像を差し替えても同じ
        app.Ui(() => app.Shell.Session.DeleteLayer(1));
        Import(app, second2);
        app.Ui(() => app.Shell.Session.SetLayerOpacity(1, 50));
        UiHost.Idle();
        Check.True(first.AsSpan().SequenceEqual(BatchOutput(app, "out2", a, "a-glint.gif")), "2 枚目の差し替えで変わった");

        // 土台の不透明度が 50 でも同じ
        app.Ui(() => app.Shell.Session.SetLayerOpacity(0, 50));
        UiHost.Idle();
        Check.True(first.AsSpan().SequenceEqual(BatchOutput(app, "out3", a, "a-glint.gif")), "土台の不透明度で変わった");

        // a.png を土台 1 枚にして写した文書の、通常の書き出しと一致する
        var doc = app.Ui(() => app.Shell.Session.Document!);
        var importer = new ImageImporter(new FakeTarget(), new GlintLoop.Core.Diagnostics.NullLogSink());
        var image = importer.LoadSingle(a, out _)!;
        var copy = doc with
        {
            Layers =
            [
                new Layer("a", LayerKind.Image, image.Sha256, null, null, true, 100, BlendMode.Normal, Placement.Identity, doc.Base.LookStrength, doc.Base.Stack),
            ],
            Assets = [new AssetEntry(image.Sha256, image.Ext, image.ByteCount)],
            Selected = Target.OfLayer(0),
        };
        app.Ui(() => app.Shell.ImportTarget.OpenDocument(copy, [image]));
        Check.True(first.AsSpan().SequenceEqual(NormalExport(app)), "写した文書の通常の書き出しと違う");
    }

    // ================================================================ 仮の決め

    [Test]
    public void 実行中の編集は結果に効かず押した時点の文書を使う()
    {
        using var app = TestApp.Start();
        app.Ui(() => app.Shell.Session.NewEmptyCanvas(64, 48));
        UiHost.Idle();
        var dir = Folder(app, "in");
        var paths = new[] { "a.png", "b.png" }.Select(n => WriteFile(dir, n, Png())).ToArray();
        var output = Folder(app, "out");
        using var gate = new SemaphoreSlim(0);
        var fake = new FakePipeline { FrameCount = 1, Gate = gate };
        var view = Open(app);
        RunWithFake(app, fake);
        Drop(app, view, paths);
        Choose(app, view, output);
        Start(app, view);
        Check.True(fake.Waiting.Wait(Limit), "1 個目に届かない");
        app.Ui(() => app.Shell.Session.SetOutputFormat(ExportFormat.Jpeg));
        gate.Release(2);
        Finish(app, view);
        Check.SequenceEqual(["a-glint.gif", "b-glint.gif"], FileNames(output), "押した時点の形式で書く");
    }

    [Test]
    public void 文書が無いときは既定の出力設定で空のスタックを適用する()
    {
        using var app = TestApp.Start();
        var input = WriteFile(Folder(app, "in"), "a.png", Png());
        var output = Folder(app, "out");
        var fake = new FakePipeline();
        var view = Open(app);
        RunWithFake(app, fake);
        Drop(app, view, [input]);
        Run(app, view, output);
        Check.SequenceEqual(["a-glint.gif"], FileNames(output), "既定の形式は GIF");
        Check.True(fake.Jobs.TryDequeue(out var job), "中身が呼ばれない");
        Check.Equal(0, job!.Document.Base.Stack.Count, "土台のスタック");
        Check.Equal(0, job.Document.All.Stack.Count, "全体のスタック");
        Check.Equal(OutputSettings.Default, job.Document.Output, "出力設定");
        Check.True(app.Ui(() => !app.Shell.Session.HasDocument), "文書ができた");
    }

    [Test]
    public void 一覧と出力フォルダが揃うまで実行を押せず_既定の保存先があれば入っている()
    {
        using var app = TestApp.Start(prepare: null);
        app.Ui(() => app.Shell.Settings.DefaultSaveDir = "");
        var input = WriteFile(Folder(app, "in"), "a.png", Png());
        var view = Open(app);
        var root = Uia.Root(app.Hwnd);
        Check.True(!Uia.FindById(root, "BatchRun").Current.IsEnabled, "空のとき");
        Check.True(!Uia.FindById(root, "BatchCancel").Current.IsEnabled, "実行していないときの「取り消し」");
        Drop(app, view, [input]);
        UiHost.Idle();
        Check.True(!Uia.FindById(root, "BatchRun").Current.IsEnabled, "出力フォルダが無いとき");
        Choose(app, view, Folder(app, "out"));
        UiHost.Idle();
        Check.True(Uia.FindById(root, "BatchRun").Current.IsEnabled, "揃ったとき");
    }
}
