// AC-42: コピーのデータ(FR-80・FR-81・FR-85・FR-88・§9.10)を偽のクリップボードで調べる。(1) 実体化 (2) 載せ方 (3) バイト列 (4) 静止画 (5) 通知とログ (6) 失敗 (7) 既定。
using System.IO;
using System.Windows;
using GlintLoop.App.Export;
using GlintLoop.Core.Export;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class HandoffAc42Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static IDataObject Placed(HandoffRig r, int index = 0) => r.App.Ui(() => r.App.Clipboard.Placed[index].Data);

    private static byte[] Bytes(IDataObject data, string format)
    {
        var v = data.GetData(format, false);
        Check.True(v is MemoryStream, format + " の値が MemoryStream でない: " + v?.GetType());
        return ((MemoryStream)v!).ToArray();
    }

    private static void WaitPlaced(HandoffRig r, int count) =>
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls >= count && r.Handoff.Busy is null, Limit), $"クリップボードへの書き込みが {count} 回にならない: " + r.App.Ui(() => r.App.Clipboard.PlaceCalls));

    private static void CopyNow(HandoffRig r, int count = 1)
    {
        r.Copy();
        WaitPlaced(r, count);
    }

    // ---------------------------------------------------------------- (1) 実体化

    [Test]
    public void AC42_1_実体はキャッシュと同じバイト列で名前は秒ごとに番号が付き最終更新時刻が渡した時刻()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        CopyNow(r);
        var first = Path.Combine(r.HandoffDir, "glint-20261014-203805.gif");
        Check.SequenceEqual(["glint-20261014-203805.gif"], r.HandoffFiles(), "1 回目の実体");
        Check.SequenceEqual(HandoffRig.Gif, File.ReadAllBytes(first), "実体のバイト列がキャッシュのファイルと同じ");
        var now = r.App.Ui(() => r.App.Clock.UtcNow.UtcDateTime);
        Check.Equal(now, File.GetLastWriteTimeUtc(first), "最終更新時刻が渡した時刻");
        CopyNow(r, 2);
        CopyNow(r, 3);
        Check.SequenceEqual(["glint-20261014-203805-2.gif", "glint-20261014-203805-3.gif", "glint-20261014-203805.gif"], r.HandoffFiles(), "同じ秒の 2 回目と 3 回目");
        r.Advance(1000);
        CopyNow(r, 4);
        Check.True(r.HandoffFiles().Contains("glint-20261014-203806.gif"), "1 秒後は番号なし: " + string.Join(",", r.HandoffFiles()));
    }

    [Test]
    public void AC42_1_ベース名が無く番号だけがあればベース名になり拡張子は形式ごとに決まる()
    {
        using var dir = new TempDir();
        var clock = FakeClock.ForPhase5();
        var files = new HandoffFiles(dir.Path, clock);
        dir.Write("glint-20261014-203805-2.gif", [1]);
        Check.Equal("glint-20261014-203805.gif", Path.GetFileName(files.NextPath(ExportFormat.Gif)), "ベース名が空いている");
        foreach (var (format, ext) in new[] { (ExportFormat.Png, "png"), (ExportFormat.Jpeg, "jpg"), (ExportFormat.Gif, "gif"), (ExportFormat.Webp, "webp"), (ExportFormat.Apng, "png") })
            Check.Equal("glint-20261014-203805." + ext, files.FileName(format), format + " の拡張子");
    }

    [Test]
    public void AC42_1_ソースのパスに日本語と絵文字と空白があっても実体の名前はASCIIのまま()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        Check.True(r.Source.Contains('\uD83D') && r.Source.Contains(' ') && r.Source.Contains('元'), "ソースのパスの前提");
        r.Export();
        CopyNow(r);
        var name = r.HandoffFiles().Single();
        Check.True(name.All(c => c < 128) && name.StartsWith("glint-", StringComparison.Ordinal), "実体の名前: " + name);
    }

    [Test]
    public void AC42_1_コピー中の例外で最終的な名前のファイルが残らず_tmpが消え何も置かない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        r.Fs.FailOn(FileOps.Write, p => p.StartsWith(r.HandoffDir, StringComparison.OrdinalIgnoreCase));
        r.Copy();
        r.Settle();
        Check.Equal(0, r.HandoffFiles().Length, "実体も .tmp も残らない: " + string.Join(",", r.HandoffFiles()));
        Check.True(r.Fs.Calls.Any(c => c.Operation == FileOps.Create && c.Path.EndsWith(".gif.tmp", StringComparison.Ordinal) && c.Path.StartsWith(r.HandoffDir, StringComparison.OrdinalIgnoreCase)), ".tmp を経由していない");
        Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), "クリップボードは変わらない");
        Check.Equal(0, r.Logs("export_handoff").Count, "export_handoff は書かない");
        Check.True(r.Notice().Length > 0, "失敗を知らせる");
    }

    // ---------------------------------------------------------------- (2) 載せ方

    [Test]
    public void AC42_2_fileはFileDropだけで実体のパス1個_第2引数が真()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        CopyNow(r);
        r.App.Ui(() =>
        {
            var (data, keep) = r.App.Clipboard.Placed[0];
            Check.True(keep, "アプリを閉じても残す");
            Check.SequenceEqual(["FileDrop"], Formats(data), "形式");
            var list = ((DataObject)data).GetFileDropList();
            Check.Equal(1, list.Count, "パスの個数");
            Check.Equal(Path.Combine(r.HandoffDir, "glint-20261014-203805.gif"), list[0]!, "実体のパス");
        });
    }

    // アプリが置いた形式(autoConvert なし)。SetFileDropList のときに WPF が足す FileName・FileNameW は数えない(INV-10 の 2026-11-05)
    private static string[] Formats(IDataObject data) =>
        data.GetFormats(false).Where(f => f is not ("FileName" or "FileNameW")).OrderBy(f => f, StringComparer.Ordinal).ToArray();

    [Test]
    public void AC42_2_imageはPNGとDIBとDIBV5だけで値がMemoryStream()
    {
        using var r = HandoffRig.Start(CopyModeSetting.Image);
        r.Export();
        CopyNow(r);
        r.App.Ui(() =>
        {
            var (data, keep) = r.App.Clipboard.Placed[0];
            Check.True(keep, "アプリを閉じても残す");
            Check.SequenceEqual(["DeviceIndependentBitmap", "Format17", "PNG"], Formats(data), "形式");
            foreach (var f in new[] { "PNG", "DeviceIndependentBitmap", "Format17" }) Check.True(data.GetData(f, false) is MemoryStream, f + " の値");
            Check.True(!data.GetDataPresent(DataFormats.Bitmap, false) && !data.GetDataPresent(DataFormats.Text, false), "Bitmap とテキストが無い");
        });
        Check.Equal(0, r.HandoffFiles().Length, "image では実体を作らない");
    }

    [Test]
    public void AC42_2_bothは4形式だけ()
    {
        using var r = HandoffRig.Start(CopyModeSetting.Both);
        r.Export();
        CopyNow(r);
        r.App.Ui(() =>
        {
            var (data, keep) = r.App.Clipboard.Placed[0];
            Check.True(keep, "アプリを閉じても残す");
            Check.SequenceEqual(["DeviceIndependentBitmap", "FileDrop", "Format17", "PNG"], Formats(data), "形式");
            Check.Equal(1, ((DataObject)data).GetFileDropList().Count, "パスの個数");
        });
    }

    // ---------------------------------------------------------------- (3) バイト列

    private static readonly byte[] Rgba3x2 =
    [
        255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0,
        0, 0, 0, 128, 255, 255, 255, 255, 100, 150, 200, 255,
    ];

    [Test]
    public void AC42_3_PNGはそのままでDIBとDIBV5は表のとおりのバイト列になる()
    {
        var png = SkiaBytes.Png(3, 2, Rgba3x2);
        var pipeline = new HandoffPipeline { Content = job => job.Format == ExportFormat.Png ? png : HandoffRig.Gif };
        using var r = HandoffRig.Start(CopyModeSetting.Image, pipeline: pipeline);
        r.Export();
        CopyNow(r);
        var data = Placed(r);
        var (p, dib, v5) = r.App.Ui(() => (Bytes(data, "PNG"), Bytes(data, DataFormats.Dib), Bytes(data, "Format17")));
        Check.SequenceEqual(png, p, "PNG の形式は与えた PNG と一致");

        Check.Equal(64, dib.Length, "DIB の長さ");
        var dh = new[] { 40, 3, 2 };
        for (var i = 0; i < 3; i++) Check.Equal(dh[i], BitConverter.ToInt32(dib, i * 4), "DIB のヘッダー " + i);
        Check.Equal((short)1, BitConverter.ToInt16(dib, 12), "biPlanes");
        Check.Equal((short)24, BitConverter.ToInt16(dib, 14), "biBitCount");
        Check.Equal(0, BitConverter.ToInt32(dib, 16), "biCompression");
        Check.Equal(24, BitConverter.ToInt32(dib, 20), "biSizeImage");
        for (var o = 24; o < 40; o += 4) Check.Equal(0, BitConverter.ToInt32(dib, o), "DIB のほかのフィールド " + o);
        Check.SequenceEqual<byte>(
            [187, 187, 187, 255, 255, 255, 200, 150, 100, 0, 0, 0, 0, 0, 255, 187, 255, 187, 255, 255, 255, 0, 0, 0],
            dib.AsSpan(40).ToArray(), "DIB の画素");

        Check.Equal(148, v5.Length, "DIBV5 の長さ");
        var expected = new Dictionary<int, uint>
        {
            [0] = 124, [4] = 3, [8] = 2, [16] = 3, [20] = 24, [40] = 0x00FF0000, [44] = 0x0000FF00, [48] = 0x000000FF, [52] = 0xFF000000, [56] = 0x73524742, [108] = 4,
        };
        Check.Equal((short)1, BitConverter.ToInt16(v5, 12), "bV5Planes");
        Check.Equal((short)32, BitConverter.ToInt16(v5, 14), "bV5BitCount");
        for (var o = 0; o < 124; o += 4)
        {
            if (o == 12) continue;
            Check.Equal(expected.GetValueOrDefault(o), BitConverter.ToUInt32(v5, o), "DIBV5 のヘッダー " + o);
        }
        Check.SequenceEqual<byte>(
            [0, 0, 0, 128, 255, 255, 255, 255, 200, 150, 100, 255, 0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 0],
            v5.AsSpan(124).ToArray(), "DIBV5 の画素");
    }

    [Test]
    public void AC42_3_幅が4の倍数でないDIBの行は0で埋める()
    {
        var bgra = new byte[5 * 1 * 4];
        Array.Fill(bgra, (byte)255);
        var dib = ClipboardImage.Dib(5, 1, bgra);
        Check.Equal(40 + 16, dib.Length, "行の長さは 16 バイト");
        Check.True(dib.AsSpan(40, 15).ToArray().All(b => b == 255) && dib[55] == 0, "最後の 1 バイトだけ 0");
    }

    // ---------------------------------------------------------------- (4) 静止画

    [Test]
    public void AC42_4_k10のPNGが未完成なら待機になり完成するとそのPNGを載せ同じ設定のPNGの書き出しと一致する()
    {
        var gate = new SemaphoreSlim(0);
        using var r = HandoffRig.Start(CopyModeSetting.Both, pipeline: new HandoffPipeline { PngGate = gate });
        r.Export();
        r.App.Ui(() => r.App.Shell.Frames.Set(10));
        Check.Equal(10, r.App.Ui(() => r.App.Shell.Frames.K), "スクラブバーの位置");
        r.Copy();
        r.Settle0(gate);
        Check.True(r.App.Ui(() => r.Handoff.IsCopyWaiting), "GIF が完成していても待機");
        Check.Equal("完成したらコピー", r.App.Ui(() => (string)r.View.Copy.Content), "ボタンの表示");
        Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), "クリップボードは変わらない");
        Check.Equal(ExportState.Done, r.App.Ui(() => r.Band.Auto.State), "GIF は完成のまま");

        gate.Release();
        WaitPlaced(r, 1);
        var png = r.App.Ui(() => Bytes(Placed(r), "PNG"));
        var job = r.Pipeline.Jobs.Last();
        Check.Equal(ExportFormat.Png, job.Format, "静止画の形式");
        Check.Equal(10, job.FrameK, "静止画のフレーム");
        Check.SequenceEqual(HandoffRig.StillPng(10), png, "載せた PNG");
        Check.Equal(ExportFormat.Gif, r.App.Ui(() => r.Session.Document!.Output.Format), "文書の形式は変えない");
        Check.True(r.App.Ui(() => !r.Handoff.IsCopyWaiting), "待機が解ける");

        // 同じ設定で k=10 の PNG を書き出すと、同じキャッシュのファイルを使い回す
        var calls = r.Pipeline.Calls;
        r.App.Ui(() => r.Session.SetOutputFormat(ExportFormat.Png));
        gate.Release(3);
        r.Export();
        Check.Equal(calls, r.Pipeline.Calls, "同じキャッシュのファイルを使い回す");
        Check.SequenceEqual(png, File.ReadAllBytes(r.App.Ui(() => r.Band.Auto.Entry!.Path)), "キャッシュのファイルと一致");
    }

    [Test]
    public void AC42_4_別のkにすると作り直し文書がPNGで同じkなら書き出しの結果をそのまま使う()
    {
        using var r = HandoffRig.Start(CopyModeSetting.Image);
        r.Export();
        r.App.Ui(() => r.App.Shell.Frames.Set(3));
        CopyNow(r);
        Check.SequenceEqual(HandoffRig.StillPng(3), r.App.Ui(() => Bytes(Placed(r), "PNG")), "k=3");
        r.App.Ui(() => r.App.Shell.Frames.Set(7));
        CopyNow(r, 2);
        Check.SequenceEqual(HandoffRig.StillPng(7), r.App.Ui(() => Bytes(Placed(r, 1), "PNG")), "k=7");
        var calls = r.Pipeline.Calls;
        CopyNow(r, 3);
        Check.Equal(calls, r.Pipeline.Calls, "同じ k のもう一度は作り直さない");
    }

    // ---------------------------------------------------------------- (5) 通知とログ

    [Test]
    public void AC42_5_GIFのfileの通知は123KBでログがパスを含まない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        CopyNow(r);
        Check.Equal("コピーしました(12.3 KB)", r.Notice(), "copy.done");
        var log = r.Logs("export_handoff").Single();
        Check.Equal("copy", log.GetProperty("kind").GetString(), "kind");
        Check.Equal("file", log.GetProperty("mode").GetString(), "mode");
        var text = r.LogText();
        Check.True(!text.Contains("glint-2026", StringComparison.Ordinal) && !text.Contains("handoff\\", StringComparison.Ordinal), "ログに実体の名前とパスが無い");
    }

    [Test]
    public void AC42_5_PNGのimageの通知は41KBでログはpngとフレーム1()
    {
        using var r = HandoffRig.Start(CopyModeSetting.Image);
        r.Export();
        CopyNow(r);
        Check.Equal("コピーしました(4.1 KB)", r.Notice(), "copy.done");
        var log = r.Logs("export_handoff").Single();
        Check.Equal("copy", log.GetProperty("kind").GetString(), "kind");
        Check.Equal("png", log.GetProperty("fmt").GetString(), "fmt");
        Check.Equal(HandoffRig.StillBytes, log.GetProperty("bytes").GetInt32(), "bytes");
        Check.Equal(1, log.GetProperty("frames").GetInt32(), "frames");
        Check.Equal("image", log.GetProperty("mode").GetString(), "mode");
    }

    [Test]
    public void AC42_5_GIFのbothのログは1回ごとに1行でGIFの値()
    {
        using var r = HandoffRig.Start(CopyModeSetting.Both);
        r.Export();
        CopyNow(r);
        CopyNow(r, 2);
        Check.Equal(2, r.Logs("export_handoff").Count, "1 回ごとに 1 行");
        var log = r.Logs("export_handoff")[0];
        Check.Equal("copy", log.GetProperty("kind").GetString(), "kind");
        Check.Equal("gif", log.GetProperty("fmt").GetString(), "fmt");
        Check.Equal(HandoffRig.GifBytes, log.GetProperty("bytes").GetInt32(), "bytes");
        Check.Equal(HandoffRig.GifFrames, log.GetProperty("frames").GetInt32(), "frames");
        Check.Equal("both", log.GetProperty("mode").GetString(), "mode");
        Check.Equal("コピーしました(12.3 KB)", r.Notice(), "both は実体のサイズ");
    }

    // ---------------------------------------------------------------- (6) 失敗

    [Test]
    public void AC42_6_開けないクリップボードでは何も置かずclip_failedとhrを書き未渡しのまま()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        r.App.Ui(() => r.App.Clipboard.PlaceHResult = unchecked((int)0x800401D0));
        Check.True(r.App.Ui(() => r.Session.IsUndelivered), "前提: 未渡し");
        r.Copy();
        r.Settle();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls > 0, Limit), "Place が呼ばれない");
        Check.Equal(0, r.App.Ui(() => r.App.Clipboard.Placed.Count), "何も置かれない");
        Check.Equal("クリップボードを使えませんでした。もう一度試してください", r.Notice(), "clip.failed");
        Check.Equal(2147746256L, r.Logs("clipboard_failed").Single().GetProperty("hr").GetInt64(), "hr");
        Check.Equal(0, r.Logs("export_handoff").Count, "export_handoff は書かない");
        Check.True(r.App.Ui(() => r.Session.IsUndelivered), "未渡しのまま");
    }

    // ---------------------------------------------------------------- (7) 既定と FR-13

    [Test]
    public void AC42_7_copyModeの既定はboth()
    {
        Check.Equal(CopyModeSetting.Both, new AppSettings().CopyMode, "copyMode の既定");
    }

    [Test]
    public void FR13_コピーが成功すると未渡しが解除されショートカットでも動く()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        Check.True(r.App.Ui(() => r.Session.IsUndelivered), "前提: 未渡し");
        r.App.Modifiers.Current = System.Windows.Input.ModifierKeys.Control;
        r.App.Ui(() => UiInput.Key(r.App.Shell.Window, System.Windows.Input.Key.C));
        r.App.Modifiers.Current = System.Windows.Input.ModifierKeys.None;
        WaitPlaced(r, 1);
        Check.True(!r.App.Ui(() => r.Session.IsUndelivered), "渡したら未渡しでない");
    }

    [Test]
    public void AC42_データオブジェクトを作る場所はHandoffDataだけ()
    {
        var hits = GlintLoop.Tests.Static.RepoFiles.Under(["src"], [".cs"])
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(GlintLoop.Tests.Static.RepoFiles.ReadText(f), @"\bnew DataObject\b|\bSetData\s*\(|\bSetFileDropList\s*\("))
            .Select(f => Path.GetFileName(f)).ToArray();
        Check.SequenceEqual(["HandoffData.cs"], hits, "データオブジェクトを作るファイル");
    }
}
