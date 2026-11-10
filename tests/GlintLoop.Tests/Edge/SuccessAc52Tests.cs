// AC-52: §1 の成功の状態。4000×3000 の JPEG を貼り、ルック・モーションのタブ・モーション・コピーのあと偽のチャットが貼るまでの 6 操作を、画面の外の非アクティブなウィンドウ・UIA・偽のクリップボード・WARP で通す。
// 載せ方(both・file・image)ごとに同じスクリプトを流し、ファイルと形式を調べる。出力設定は settings.json の lastExport から引き継いだものだけで、テストは文書へ入れない。
// タイルは InvokePattern を持たない(ListBoxItem)ので、「タイルを Invoke」はフォーカスと Enter の 1 回で行う(製品の入口はクリックと Enter)。
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Export;
using GlintLoop.App.Looks;
using GlintLoop.App.MotionTab;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Gif;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Edge;

public sealed class SuccessAc52Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private static readonly Lazy<byte[]> Jpeg = new(() => SkiaBytes.Jpeg(4000, 3000, Img.Pattern(4000, 3000, alpha: false), 90));

    private static string SettingsJson(string copyMode) =>
        new JsonObject
        {
            ["schema"] = 1,
            ["copyMode"] = copyMode,
            ["lastExport"] = new JsonObject
            {
                ["format"] = "gif",
                ["size"] = new JsonObject { ["mode"] = "long", ["value"] = 320 },
                ["limit"] = new JsonObject { ["kind"] = "preset", ["key"] = "chat_small" },
            },
        }.ToJsonString();

    private static void Prepare(GlintLoop.Core.Platform.AppPaths p, string copyMode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
        File.WriteAllText(p.SettingsFile, SettingsJson(copyMode));
    }

    private static bool IsExpectedOutput(OutputSettings o) =>
        o.Format == ExportFormat.Gif && o.Size.Mode == "long" && o.Size.Value == 320 && o.Limit.Kind == "preset" && o.Limit.Key == "chat_small";

    // ---------------------------------------------------------------- 前回の出力設定(§9.11 の lastExport)

    [Test]
    public void AC52_0_lastExportの出力設定を新しい文書が引き継ぐ()
    {
        using var app = TestApp.Start(prepare: p => Prepare(p, "both"));
        using var dir = new TempDir();
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(32, 32, Img.Pattern(32, 32, alpha: false)));
        Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
        WaitImports(app);
        var o = app.Ui(() => app.Shell.Session.Document!.Output);
        Check.True(IsExpectedOutput(o), $"新しい文書の出力設定: {o.Format}・{o.Size.Mode} {o.Size.Value}・{o.Limit.Kind} {o.Limit.Key}");
    }

    [Test]
    public void AC52_0_出力設定を変えるとlastExportに書かれ次の文書が引き継ぐ()
    {
        using var app = TestApp.Start();
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(32, 32, Img.Pattern(32, 32, alpha: false)));
        Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
        WaitImports(app);
        app.Ui(() =>
        {
            var p = BandLimitPresets.Builtin.Single(x => x.Key == "chat_small");
            app.Shell.Session.ApplyLimitPreset(p.Key, p.Format, p.LongSide, p.Loop);
        });
        UiHost.Idle();
        var last = app.Ui(() => app.Shell.Settings.LastExport);
        Check.True(last is not null, "出力設定を変えても lastExport が空のまま");
        Check.Equal("chat_small", (string?)last!["limit"]?["key"], "lastExport の limit.key");
    }

    // ---------------------------------------------------------------- スクリプト

    private sealed class Script(TestApp app)
    {
        public int Operations { get; private set; }

        public void Op(Action action)
        {
            Operations++;
            action();
        }

        public static AutomationElement Named(AutomationElement root, string name, ControlType type) =>
            root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, type)))
            ?? throw new InvalidOperationException($"UIA で「{name}」が見つからない");

        public void PressTile(System.Windows.Controls.ListBoxItem tile, string what) => app.Ui(() =>
        {
            Keyboard.Focus(tile);
            Check.True(ReferenceEquals(Keyboard.FocusedElement, tile), what + ": フォーカスが当たらない");
            Check.True(UiInput.Key(tile, System.Windows.Input.Key.Enter), what + ": Enter を処理しない");
        });
    }

    /// <summary>偽のチャットが受け取るもの(FileDrop の次に PNG の順に読む)。</summary>
    private sealed record Chat(string[] Formats, string? File, byte[]? Png);

    private static Chat ChatPaste(TestApp app) => app.Ui(() =>
    {
        var data = app.Clipboard.Placed.Single().Data;
        var formats = data.GetFormats(false).Where(f => f is not ("FileName" or "FileNameW")).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        string? file = null;
        if (data is DataObject d && d.ContainsFileDropList()) file = d.GetFileDropList()[0];
        byte[]? png = null;
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream m) png = m.ToArray();
        return new Chat(formats, file, png);
    });

    private static void CheckGif(string path, string what)
    {
        var bytes = File.ReadAllBytes(path);
        Check.True(bytes.Length <= 1_000_000, what + ": バイト数 " + bytes.Length);
        var decoder = new GifBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check.True(decoder.Frames.Count >= 2, what + ": フレーム数 " + decoder.Frames.Count);
        var gif = MiniGifDecoder.Parse(bytes);
        Check.True(gif.AppExtensions.Count >= 1, what + ": NETSCAPE2.0 が無い");
        var ext = gif.AppExtensions[0];
        var tag = System.Text.Encoding.ASCII.GetString(ext, 3, 11);
        Check.Equal("NETSCAPE2.0", tag, what + ": アプリケーション拡張");
        Check.Equal(0, ext[16] | (ext[17] << 8), what + ": ループ回数");
        var first = Composite(gif, 0);
        var mid = Composite(gif, gif.Frames.Count / 2);
        Check.True(!first.AsSpan().SequenceEqual(mid), what + ": 最初のフレームと中間のフレームの画素が違う");
    }

    /// <summary>frame 番目までのフレームを順に重ねた、グローバルのカラーテーブルの番号の画面(差分のフレームでも比べられるように)。</summary>
    private static int[] Composite(MiniGifFile gif, int frame)
    {
        var screen = new int[gif.Width * gif.Height];
        for (var i = 0; i <= frame; i++)
        {
            var f = gif.Frames[i];
            for (var y = 0; y < f.Height; y++)
            {
                for (var x = 0; x < f.Width; x++)
                {
                    var index = f.Indices[y * f.Width + x];
                    if (f.TransparentFlag && index == f.TransparentIndexField) continue;
                    screen[(y + f.Top) * gif.Width + x + f.Left] = index;
                }
            }
        }
        return screen;
    }

    /// <summary>6 操作のスクリプトを流して、偽のチャットが受け取るところまで行う。</summary>
    private static void Run(string copyMode, Action<TestApp, Chat> checkChat, Action<TestApp>? then = null)
    {
        using var app = TestApp.Start(prepare: p => Prepare(p, copyMode), gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        var source = dir.Write("大きな 写真.jpg", Jpeg.Value);
        var script = new Script(app);
        var looks = M<LooksModule>(app);
        var motion = M<MotionTabModule>(app);
        var canvas = M<CanvasModule>(app);
        var band = M<ExportBandModule>(app);
        var session = app.Shell.Session;
        app.Clipboard.Data = new MemoryImportData().WithFiles(source);

        // (1) ウィンドウにキーイベント Ctrl+V を直接送る
        var watch = Stopwatch.StartNew();
        script.Op(() => Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), "Ctrl+V が処理されない"));
        WaitImports(app);
        Check.True(app.Ui(() => session.HasDocument), "取り込めない: " + app.Ui(() => app.Shell.NoticeBar.Current?.Key));
        // 準備の lastExport をそのまま引き継ぐ(前書きの準備。テストは出力設定を入れない)
        Check.True(app.Ui(() => IsExpectedOutput(session.Document!.Output)), "lastExport を引き継いでいない");
        Check.True(UiHost.WaitUntil(() => canvas.Picture.Bitmap is not null, Limit), "最初の表示が出ない");
        UiHost.Idle();

        // (2) 左ペインの選択中のタブが「ルック」であることを確かめてから、タイル「ビビッド」を選ぶ(サムネイルの完成は待たない)
        Check.Equal(LeftPaneTab.Looks, app.Ui(() => looks.LeftPane.SelectedTab), "最初の表示のあとのタブ");
        var vivid = app.Ui(() => looks.Gallery.Tiles.Single(t => t.DisplayName == "ビビッド"));
        var vividIds = app.Ui(() => vivid.Effects.Select(e => e.Id).ToArray());
        script.Op(() => script.PressTile(vivid, "ビビッド"));
        UiHost.Idle();

        // (3) タブ「モーション」を Select する
        var root = Uia.Root(app.Hwnd);
        script.Op(() => Uia.Select(Script.Named(root, "モーション", ControlType.TabItem)));
        Check.True(UiHost.WaitUntil(() => looks.LeftPane.SelectedTab == LeftPaneTab.Motion, TimeSpan.FromSeconds(10)), "モーションのタブにならない");
        UiHost.Idle();

        // (4) タイル「くるくる」を選ぶ
        var spin = app.Ui(() => motion.View.Tiles.Single(t => t.DisplayName == "くるくる"));
        script.Op(() => script.PressTile(spin, "くるくる"));
        UiHost.Idle();

        // (5) 待たずに「コピー」を Invoke する(「完成したらコピー」になってよい)
        script.Op(() => Uia.Invoke(Script.Named(root, "コピー", ControlType.Button)));
        Check.True(UiHost.WaitUntil(() => band.Handoff.IsCopyWaiting || app.Clipboard.PlaceCalls > 0, TimeSpan.FromSeconds(10)), "コピーが押されない");

        // 偽の時計を 600 ms 進めて書き出しを始めさせ、コピーが行われるまで待つ
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, Limit), "本物の中身が入らない");
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => app.Clipboard.PlaceCalls >= 1 && band.Handoff.Busy is null, Limit),
            $"コピーが行われない: 状態 {app.Ui(() => band.Auto.State)}、{app.Ui(() => band.View.StatusText)}、{app.Ui(() => band.Auto.Error?.ToString())}");
        watch.Stop();
        Console.WriteLine($"MEASURE AC52 {copyMode}: 貼り付けからコピーの完了まで {watch.ElapsedMilliseconds} ms、{app.Ui(() => band.Auto.Outcome?.Bytes)} バイト、{app.Ui(() => band.Auto.Outcome?.Frames)} フレーム");

        // (6) 偽のチャットが貼り付ける
        var chat = default(Chat);
        script.Op(() => chat = ChatPaste(app));

        Check.Equal(6, script.Operations, "スクリプトの操作の数");
        Check.Equal(1, app.Ui(() => app.Clipboard.PlaceCalls), "コピーは 1 回だけ");
        var stack = app.Ui(() => session.Document!.Layers[0].Stack);
        Check.SequenceEqual(vividIds.Order(StringComparer.Ordinal).ToArray(), stack.Where(e => e.Origin == EffectOrigin.Look).Select(e => e.Id).Order(StringComparer.Ordinal).ToArray(), "ルック「ビビッド」のエフェクト(origin が look)");
        Check.True(stack.Any(e => e.Id == "spin" && e.Origin == EffectOrigin.User), "spin(origin が user): " + string.Join(",", stack.Select(e => e.Id + ":" + e.Origin)));
        checkChat(app, chat!);
        foreach (var bad in new[] { "export_fail", "render_fail", "crash", "clipboard_failed" })
            Check.Equal(0, EdgeLog.Count(app, bad), "ログに " + bad + " が無い");
        then?.Invoke(app);
    }

    private static string[] Sorted(params string[] names) => names.Order(StringComparer.Ordinal).ToArray();

    [Test(Slow = true)]
    public void AC52_1_bothで6操作のあと偽のチャットがGIFを受け取り2枚目はレイヤーとして足される()
    {
        Run("both", (app, chat) =>
        {
            Check.SequenceEqual(Sorted("FileDrop", "PNG", "DeviceIndependentBitmap", "Format17"), chat.Formats, "形式");
            Check.True(chat.File is not null, "FileDrop のファイル");
            CheckGif(chat.File!, "both");
        }, app =>
        {
            // 続けて 2 枚目の画像を貼る
            var looks = M<LooksModule>(app);
            var session = app.Shell.Session;
            Check.Equal(LeftPaneTab.Motion, app.Ui(() => looks.LeftPane.SelectedTab), "前提: いまはモーションのタブ");
            app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(64, 64, Img.Pattern(64, 64, alpha: false)));
            Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
            WaitImports(app);
            UiHost.Idle();
            Check.True(!app.Ui(() => app.Shell.Dialogs.IsOpen), "確認が出ない");
            Check.Equal(2, app.Ui(() => session.Document!.Layers.Count), "レイヤーの枚数");
            Check.Equal(Target.OfLayer(1), app.Ui(() => session.Selected), "最前面が選ばれる");
            Check.Equal(NoticeKeys.LayerAdded, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "layer.added");
            Check.Equal(LeftPaneTab.Looks, app.Ui(() => looks.LeftPane.SelectedTab), "ルックのタブに戻る");
        });
    }

    [Test(Slow = true)]
    public void AC52_2_fileで同じスクリプトを繰り返すと形式がFileDropだけでファイルの検査は同じ()
    {
        Run("file", (app, chat) =>
        {
            Check.SequenceEqual(["FileDrop"], chat.Formats, "形式");
            Check.True(chat.File is not null, "FileDrop のファイル");
            CheckGif(chat.File!, "file");
        });
    }

    [Test(Slow = true)]
    public void AC52_3_imageで同じスクリプトを繰り返すと形式が3つでPNGが1枚の静止画として読める()
    {
        Run("image", (app, chat) =>
        {
            Check.SequenceEqual(Sorted("PNG", "DeviceIndependentBitmap", "Format17"), chat.Formats, "形式");
            Check.True(chat.Png is not null, "PNG の形式");
            var decoder = new PngBitmapDecoder(new MemoryStream(chat.Png!), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check.Equal(1, decoder.Frames.Count, "1 枚の静止画");
            Check.True(decoder.Frames[0].PixelWidth > 0, "読める");
        });
    }
}
