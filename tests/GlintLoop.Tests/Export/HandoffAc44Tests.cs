// AC-44: ドラッグと保存(FR-82・FR-84・FR-88・E-38・E-35・E-34・§9.10 の 3 と 4)を、偽のドラッグ実行器・偽の保存ダイアログ・一時フォルダで調べる。E-30(上限超過の確認)と AC-57 の (5) もここで調べる。
using System.IO;
using System.Windows;
using GlintLoop.App.Export;
using GlintLoop.Core.Export;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class HandoffAc44Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static void WaitDrag(HandoffRig r, int count = 1) =>
        Check.True(UiHost.WaitUntil(() => r.App.DragRunner.Runs.Count >= count && r.Handoff.Busy is null, Limit), "ドラッグの実行器が呼ばれない");

    [Test]
    public void AC44_1_移動量がしきい値ちょうどでは始まらず超えると始まる()
    {
        var p = new Point(100, 100);
        Check.True(!ExportBandView.DragThresholdExceeded(p, new Point(104, 100), 4, 4), "横 4 では始まらない");
        Check.True(!ExportBandView.DragThresholdExceeded(p, new Point(100, 96), 4, 4), "縦 4 では始まらない");
        Check.True(!ExportBandView.DragThresholdExceeded(p, new Point(96, 104), 4, 4), "両方 4 でも始まらない");
        Check.True(ExportBandView.DragThresholdExceeded(p, new Point(105, 100), 4, 4), "横 5 で始まる");
        Check.True(ExportBandView.DragThresholdExceeded(p, new Point(95, 100), 4, 4), "左へ 5 で始まる");
        Check.True(ExportBandView.DragThresholdExceeded(p, new Point(100, 105), 4, 4), "縦 5 で始まる");
    }

    [Test]
    public void AC44_2_許可する効果はCopyだけでデータはFileDrop1個で実体を指す()
    {
        using var r = HandoffRig.Start(CopyModeSetting.Both);
        r.Export();
        r.App.Ui(() =>
        {
            r.View.DragPress(new Point(10, 10));
            r.View.DragMove(new Point(14, 10), 4, 4);
        });
        r.Settle();
        Check.Equal(0, r.App.DragRunner.Runs.Count, "しきい値ちょうどでは始まらない");
        r.App.Ui(() => r.View.DragMove(new Point(15, 10), 4, 4));
        WaitDrag(r);
        r.App.Ui(() =>
        {
            var (source, data, allowed) = r.App.DragRunner.Runs.Single();
            Check.True(ReferenceEquals(source, r.View.Drag), "ドラッグ欄から始める");
            Check.Equal(DragDropEffects.Copy, allowed, "許可する効果");
            Check.SequenceEqual(["FileDrop"], data.GetFormats(false).Where(f => f is not ("FileName" or "FileNameW")).ToArray(), "形式");
            var list = ((DataObject)data).GetFileDropList();
            Check.Equal(1, list.Count, "パスの個数");
            Check.Equal(Path.Combine(r.HandoffDir, "glint-20261014-203805.gif"), list[0]!, "実体のパス");
            Check.True(File.Exists(list[0]!), "実体がある");
        });
    }

    [Test]
    public void AC44_3_Copyを返すとログが1行でNoneならログが無く実体が残る()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        r.Drag();
        WaitDrag(r);
        var log = r.Logs("export_handoff").Single();
        Check.Equal("drag", log.GetProperty("kind").GetString(), "kind");
        Check.Equal("gif", log.GetProperty("fmt").GetString(), "fmt");
        Check.Equal(HandoffRig.GifBytes, log.GetProperty("bytes").GetInt32(), "bytes");
        Check.Equal(HandoffRig.GifFrames, log.GetProperty("frames").GetInt32(), "frames");
        Check.True(!log.TryGetProperty("mode", out _), "mode なし");
        Check.True(!r.App.Ui(() => r.Session.IsUndelivered), "渡したら未渡しでない");

        r.App.DragRunner.Result = DragDropEffects.None;
        r.App.Ui(() => r.Session.SetLength10(21));
        r.Export();
        var before = r.HandoffFiles().Length;
        r.Drag();
        WaitDrag(r, 2);
        Check.Equal(1, r.Logs("export_handoff").Count, "取り消しではログを書かない");
        Check.Equal(before + 1, r.HandoffFiles().Length, "実体は残る");
        Check.True(r.App.Ui(() => r.Session.IsUndelivered), "取り消しでは未渡しのまま");
    }

    [Test]
    public void AC44_4_完成前は無効で押しても実行器が呼ばれず完成すると有効になる()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        Check.True(!r.App.Ui(() => r.View.Drag.IsEnabled), "完成前は無効");
        r.Drag();
        r.Settle();
        Check.Equal(0, r.App.DragRunner.Runs.Count, "実行器が呼ばれない");
        r.Export();
        Check.True(r.App.Ui(() => r.View.Drag.IsEnabled), "完成すると有効");
    }

    [Test]
    public void AC44_5_ドラッグ中の実体はキャッシュの合計が上限を超えていても削除されない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        UiHost.Idle();
        Check.True(r.App.Ui(() => r.Band.CacheWork).Wait(Limit), "完了の確認が終わらない"); // 上限を変える前に済ませる(完成したファイルが先に消えないように)
        string? during = null;
        r.App.Ui(() => r.Band.CacheManager.LimitBytesSource = () => 1);
        r.App.DragRunner.During = () =>
        {
            r.Band.CacheManager.Trim();
            during = r.HandoffFiles().SingleOrDefault();
        };
        r.Drag();
        WaitDrag(r);
        Check.Equal("glint-20261014-203805.gif", during ?? "", "ドラッグ中の実体");
    }

    // ---------------------------------------------------------------- (6) 既定

    [Test]
    public void AC44_6_既定の名前は番号なしで既定のフォルダはdefaultSaveDir_lastSaveDir_ピクチャの順()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var pictures = r.Dir.PathOf("pictures");
        var a = Directory.CreateDirectory(r.Dir.PathOf("defaultDir")).FullName;
        var b = Directory.CreateDirectory(r.Dir.PathOf("lastDir")).FullName;
        r.App.Ui(() => r.Handoff.PicturesFolder = () => pictures);
        // 実体がすでにあっても、保存の既定の名前に番号は付かない
        Directory.CreateDirectory(r.HandoffDir);
        File.WriteAllBytes(Path.Combine(r.HandoffDir, "glint-20261014-203805.gif"), [1]);

        void Check1(string defaultDir, string lastDir, string expected, string what)
        {
            r.App.Ui(() =>
            {
                r.App.Shell.Settings.DefaultSaveDir = defaultDir;
                r.App.Shell.Settings.LastSaveDir = lastDir;
            });
            var n = r.App.Ui(() => r.App.FileDialogs.SaveCalls.Count);
            r.Save();
            Check.True(UiHost.WaitUntil(() => r.App.FileDialogs.SaveCalls.Count == n + 1 && r.Handoff.Busy is null, Limit), what + ": ダイアログが出ない");
            var call = r.App.Ui(() => r.App.FileDialogs.SaveCalls[n]);
            Check.Equal(expected, call.InitialDir ?? "", what + ": 既定のフォルダ");
            Check.Equal("glint-20261014-203805.gif", call.FileName, what + ": 既定の名前");
            Check.Equal("*.gif", call.Filters.Single().Patterns, what + ": フィルタは拡張子 1 種類");
        }

        var missing = r.Dir.PathOf("missing");
        Check1(a, b, a, "defaultSaveDir");
        Check1(missing, b, b, "defaultSaveDir が実在しない");
        Check1("", b, b, "defaultSaveDir が空");
        Check1("", missing, pictures, "どちらも使えない");
        Check1("", "", pictures, "どちらも空");

        var target = Path.Combine(b, "選んだ.gif");
        r.App.Ui(() => r.App.FileDialogs.SaveAnswers.Enqueue(target));
        r.Save();
        Check.True(UiHost.WaitUntil(() => File.Exists(target) && r.Handoff.Busy is null, Limit), "保存されない");
        Check.Equal(b, r.App.Ui(() => r.App.Shell.Settings.LastSaveDir), "保存に成功すると lastSaveDir が保存したフォルダ");
    }

    // ---------------------------------------------------------------- (7) 実行

    private static void SaveTo(HandoffRig r, string path)
    {
        r.App.Ui(() => r.App.FileDialogs.SaveAnswers.Enqueue(path));
        var n = r.App.Ui(() => r.App.FileDialogs.Calls);
        r.Save();
        Check.True(UiHost.WaitUntil(() => r.App.FileDialogs.Calls == n + 1 && r.Handoff.Busy is null, Limit), "保存が終わらない");
        r.Settle();
    }

    [Test]
    public void AC44_7_選んだパスに完成ファイルのバイト列が_tmp経由で書かれsave_doneとログが出る()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var path = Path.Combine(r.Dir.Path, "out.gif");
        SaveTo(r, path);
        Check.SequenceEqual(HandoffRig.Gif, File.ReadAllBytes(path), "バイト列");
        Check.True(r.Fs.Calls.Any(c => c.Operation == FileOps.Create && string.Equals(c.Path, path + ".tmp", StringComparison.OrdinalIgnoreCase)), ".tmp を経由する");
        Check.True(!File.Exists(path + ".tmp"), ".tmp が残らない");
        Check.Equal("保存しました", r.Notice(), "save.done");
        var log = r.Logs("export_handoff").Single();
        Check.Equal("save", log.GetProperty("kind").GetString(), "kind");
        Check.Equal("gif", log.GetProperty("fmt").GetString(), "fmt");
        Check.Equal(HandoffRig.GifBytes, log.GetProperty("bytes").GetInt32(), "bytes");
        Check.Equal(HandoffRig.GifFrames, log.GetProperty("frames").GetInt32(), "frames");
        Check.True(!r.App.Ui(() => r.Session.IsUndelivered), "渡したら未渡しでない");
    }

    [Test]
    public void AC44_7_書き込み中の例外で既存の同名ファイルが変わらず_tmpが消える()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var path = r.Dir.Write("exists.gif", [9, 9]);
        r.Fs.FailOn(FileOps.Write, p => string.Equals(p, path + ".tmp", StringComparison.OrdinalIgnoreCase));
        SaveTo(r, path);
        Check.SequenceEqual<byte>([9, 9], File.ReadAllBytes(path), "既存のファイルが変わらない");
        Check.True(!File.Exists(path + ".tmp"), ".tmp が消える");
    }

    [Test]
    public void AC44_7_日本語と絵文字と空白を含み260文字を超えるパスでも成功する()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var dir = r.Dir.Path;
        while (dir.Length < 300) dir = Path.Combine(dir, "日本語 \U0001F600 のフォルダ名を長くする");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "保存 \U0001F600.gif");
        SaveTo(r, path);
        Check.True(path.Length > 260, "前提: 260 文字を超える");
        Check.SequenceEqual(HandoffRig.Gif, File.ReadAllBytes(path), "バイト列");
        Check.Equal("保存しました", r.Notice(), "save.done");
    }

    // ---------------------------------------------------------------- (8) 取り消しと失敗

    [Test]
    public void AC44_8_ダイアログの取り消しでは何も書かれず通知もログも無い()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        SaveTo(r, null!);
        Check.Equal("", r.Notice(), "通知なし");
        Check.Equal(0, r.Logs("export_handoff").Count + r.Logs("export_fail").Count, "ログなし");
        Check.True(r.App.Ui(() => r.Session.IsUndelivered), "未渡しのまま");
    }

    [Test]
    public void AC44_8_書き込みの失敗はsave_failedとwrite_errorで_export_handoffは書かない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var path = Path.Combine(r.Dir.Path, "out.gif");
        r.Fs.FailOn(FileOps.Create, p => string.Equals(p, path + ".tmp", StringComparison.OrdinalIgnoreCase), () => new UnauthorizedAccessException());
        SaveTo(r, path);
        Check.Equal("保存できませんでした。保存先を変えて試してください", r.Notice(), "save.failed");
        Check.Equal("write_error", r.Logs("export_fail").Single().GetProperty("reason").GetString(), "reason");
        Check.Equal(0, r.Logs("export_handoff").Count, "export_handoff は書かない");
        Check.True(r.App.Ui(() => r.Session.IsUndelivered), "未渡しのまま");
    }

    [Test]
    public void AC44_8_INV2_取り込んだPNGと同じパスは綴りを変えても書かない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var before = File.ReadAllBytes(r.Source);
        foreach (var path in new[] { r.Source, r.Source.ToUpperInvariant(), Path.Combine(Path.GetDirectoryName(r.Source)!, ".", Path.GetFileName(r.Source)) })
        {
            r.App.Ui(() => r.App.Shell.NoticeBar.Close());
            SaveTo(r, path);
            Check.SequenceEqual(before, File.ReadAllBytes(r.Source), "バイト列が変わらない: " + path);
            Check.Equal("保存できませんでした。保存先を変えて試してください", r.Notice(), "save.failed: " + path);
            Check.True(!File.Exists(r.Source + ".tmp"), ".tmp が残らない: " + path);
        }
        Check.Equal(0, r.Logs("export_handoff").Count, "export_handoff は書かない");
        Check.Equal(0, r.Fs.Calls.Count(c => c.Operation == FileOps.Create && c.Path.Contains(Path.GetFileName(r.Source), StringComparison.OrdinalIgnoreCase)), "書き込みで開かない");
    }

    // ---------------------------------------------------------------- (9) 保存は実体を作らない

    [Test]
    public void AC44_9_保存の前後でhandoffのファイルの数が変わらない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var before = r.HandoffFiles().Length;
        SaveTo(r, Path.Combine(r.Dir.Path, "out.gif"));
        Check.Equal(before, r.HandoffFiles().Length, "ファイルの数");
    }

    // ---------------------------------------------------------------- E-30・AC-57 の (5)

    [Test]
    public void E30_上限超過はコピーと保存とドラッグの前に確認し既定はやめるでやめると何も呼ばない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File, pipeline: new HandoffPipeline { SizeFail = true });
        r.Export();
        var dialog = r.App.Ui(() => r.App.FileDialogs);
        void Stop(string what)
        {
            var v = r.WaitConfirm();
            Check.True(UiHost.WaitUntil(() => ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(r.App.Shell.Window), v.Stop), Limit), what + ": 最初のフォーカスが「やめる」でない");
            r.App.Ui(() =>
            {
                Check.True(v.Stop.IsDefault && !v.Pass.IsDefault, what + ": 既定のボタンは「やめる」");
                Check.Equal("上限を超えています。それでも渡しますか", v.Message.Text, what + ": 文言");
                v.Stop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            });
            r.Settle(what);
            Check.True(UiHost.WaitUntil(() => !r.App.Shell.Dialogs.IsOpen, Limit), what + ": ダイアログが閉じない");
        }
        r.Copy();
        Stop("コピー");
        r.Save();
        Stop("保存");
        r.Drag();
        Stop("ドラッグ");
        Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), "クリップボードが呼ばれない");
        Check.Equal(0, r.App.Ui(() => dialog.Calls), "保存ダイアログが呼ばれない");
        Check.Equal(0, r.App.DragRunner.Runs.Count, "ドラッグ実行器が呼ばれない");
        Check.Equal(0, r.HandoffFiles().Length, "実体を作らない");
        Check.Equal(0, r.Logs("export_handoff").Count, "ログなし");
    }

    [Test]
    public void E30_渡すを選ぶと最後の段の結果が渡る()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File, pipeline: new HandoffPipeline { SizeFail = true });
        r.Export();
        void Pass()
        {
            var v = r.WaitConfirm();
            r.App.Ui(() => v.Pass.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
        }
        r.Copy();
        Pass();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "コピーされない");
        var path = Path.Combine(r.Dir.Path, "over.gif");
        r.App.Ui(() => r.App.FileDialogs.SaveAnswers.Enqueue(path));
        r.Save();
        Pass();
        Check.True(UiHost.WaitUntil(() => File.Exists(path) && r.Handoff.Busy is null, Limit), "保存されない");
        r.Drag();
        Pass();
        // ドラッグは「渡す」のあとの案内を見て、もう一度ドラッグして渡す(AC-57 の (1) の注記)
        Check.True(UiHost.WaitUntil(() => !r.App.Shell.Dialogs.IsOpen && r.Handoff.Busy is null, Limit), "確認が閉じない");
        UiHost.Idle();
        r.Drag();
        WaitDrag(r);
        Check.Equal(3, r.Logs("export_handoff").Count, "3 回とも渡る");
    }

    [Test]
    public void AC57_5_保存の失敗のあとに直してもう一度押すと成功する()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var path = Path.Combine(r.Dir.Path, "retry.gif");
        r.Fs.FailOn(FileOps.Write, p => string.Equals(p, path + ".tmp", StringComparison.OrdinalIgnoreCase));
        SaveTo(r, path);
        Check.Equal("保存できませんでした。保存先を変えて試してください", r.Notice(), "1 回目は失敗");
        Check.True(!File.Exists(path + ".tmp"), "1 回目の .tmp が残らない");
        r.Fs.ClearFailures();
        SaveTo(r, path);
        Check.Equal("保存しました", r.Notice(), "2 回目は save.done");
        Check.SequenceEqual(HandoffRig.Gif, File.ReadAllBytes(path), "バイト列");
    }

    [Test]
    public void ドラッグ欄に完成したファイルのサムネイルが出て編集で消える()
    {
        var png = GlintLoop.Tests.Codec.SkiaBytes.Png(300, 100, BandTestKit.Solid(300, 100, 200, 10, 10));
        using var r = HandoffRig.Start(CopyModeSetting.File, ExportFormat.Png, new HandoffPipeline { Content = _ => png });
        r.Export();
        Check.True(UiHost.WaitUntil(() => r.Handoff.PendingThumbnail is null && r.View.DragThumbnail.Source is not null, Limit), "サムネイルが出ない");
        r.App.Ui(() =>
        {
            var b = (System.Windows.Media.Imaging.BitmapSource)r.View.DragThumbnail.Source;
            Check.Equal((128, 42), (b.PixelWidth, b.PixelHeight), "欄の 2 倍に収める");
        });
        r.App.Ui(() => r.Session.SetLength10(21));
        UiHost.Idle();
        Check.True(r.App.Ui(() => r.View.DragThumbnail.Source is null), "編集で消える");
    }
}
