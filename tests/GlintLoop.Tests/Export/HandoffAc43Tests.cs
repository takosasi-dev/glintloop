// AC-43: 完成前の操作(FR-83・INV-9・E-36)を、偽の時計・完成を合図できる偽の中身・偽のクリップボード・偽の保存ダイアログで調べる。設定の copyMode は file。
using System.IO;
using System.Windows.Input;
using GlintLoop.Core.Export;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class HandoffAc43Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>書き出しを計算中(門の前)で止めたアプリ。</summary>
    private static (HandoffRig R, SemaphoreSlim Gate) Running()
    {
        var gate = new SemaphoreSlim(0);
        var r = HandoffRig.Start(CopyModeSetting.File, pipeline: new HandoffPipeline { GifGate = gate });
        r.StartExport();
        Check.True(UiHost.WaitUntil(() => r.Pipeline.Calls == 1, Limit), "書き出しが始まらない");
        Check.Equal(ExportState.Running, r.App.Ui(() => r.Band.Auto.State), "計算中");
        return (r, gate);
    }

    private static string Label(HandoffRig r, System.Windows.Controls.Button b) => r.App.Ui(() => (string)b.Content);

    private static int Places(HandoffRig r) => r.App.Ui(() => r.App.Clipboard.PlaceCalls);

    private static int Dialogs(HandoffRig r) => r.App.Ui(() => r.App.FileDialogs.Calls);

    [Test]
    public void AC43_1_INV9_計算中のコピーは待機になり完成すると1回だけ行われ表示が戻る()
    {
        var (r, gate) = Running();
        using (r)
        {
            r.Copy();
            UiHost.Idle();
            Check.Equal("完成したらコピー", Label(r, r.View.Copy), "待機の表示");
            Check.Equal(0, Places(r), "待機の間は書かない");
            gate.Release();
            Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "完成しても行われない");
            Check.Equal("コピー", Label(r, r.View.Copy), "表示が戻る");
            r.Advance(40000);
            Check.Equal(1, Places(r), "1 回だけ");
        }
    }

    [Test]
    public void AC43_2_INV9_計算中の保存は待機になりダイアログを呼ばず完成すると1回だけ呼ぶ()
    {
        var (r, gate) = Running();
        using (r)
        {
            r.Save();
            UiHost.Idle();
            Check.Equal("完成したら保存", Label(r, r.View.Save), "待機の表示");
            Check.Equal(0, Dialogs(r), "待機の間は呼ばない");
            gate.Release();
            Check.True(UiHost.WaitUntil(() => r.App.FileDialogs.Calls == 1 && r.Handoff.Busy is null, Limit), "完成してもダイアログが出ない");
            Check.Equal("保存", Label(r, r.View.Save), "表示が戻る");
            r.Advance(40000);
            Check.Equal(1, Dialogs(r), "1 回だけ");
        }
    }

    private static void AfterCancel(HandoffRig r, SemaphoreSlim gate, string what)
    {
        Check.Equal("コピー", Label(r, r.View.Copy), what + ": コピーの表示");
        Check.Equal("保存", Label(r, r.View.Save), what + ": 保存の表示");
        gate.Release(5);
        r.Settle(what);
        // 編集のあとは書き出しがやり直されるので、完成まで進める
        if (r.App.Ui(() => r.Band.Auto.State) != ExportState.Done) r.Export();
        r.Settle(what);
        Check.Equal(0, Places(r), what + ": 完成してもコピーしない");
        Check.Equal(0, Dialogs(r), what + ": 完成しても保存しない");
    }

    [Test]
    public void AC43_3_新しい編集で取り消される()
    {
        var (r, gate) = Running();
        using (r)
        {
            r.Copy();
            r.Save();
            r.App.Ui(() => r.Session.SetLength10(21));
            UiHost.Idle();
            AfterCancel(r, gate, "編集");
        }
    }

    [Test]
    public void AC43_3_レイヤーとして足す取り込みでも取り消される()
    {
        var (r, gate) = Running();
        using (r)
        {
            r.Copy();
            var importer = new GlintLoop.Codec.Import.ImageImporter(r.App.Shell.ImportTarget, new GlintLoop.Core.Diagnostics.MemoryLogSink());
            Check.Equal(1, importer.Drop(new GlintLoop.Codec.Import.MemoryImportData().WithFiles(r.Source)).Added, "足せた枚数");
            UiHost.Idle();
            AfterCancel(r, gate, "取り込み");
        }
    }

    [Test]
    public void AC43_3_Escで取り消される()
    {
        var (r, gate) = Running();
        using (r)
        {
            r.Copy();
            r.Save();
            r.App.Ui(() => UiInput.Key(r.App.Shell.Window, Key.Escape));
            UiHost.Idle();
            AfterCancel(r, gate, "Esc");
        }
    }

    [Test]
    public void AC43_3_同じボタンのもう一度で取り消される()
    {
        var (r, gate) = Running();
        using (r)
        {
            r.Copy();
            r.Copy();
            r.Save();
            r.Save();
            UiHost.Idle();
            AfterCancel(r, gate, "同じボタン");
        }
    }

    [Test]
    public void AC43_3_299秒では取り消されず300秒で取り消される()
    {
        var (r, gate) = Running();
        using (r)
        {
            r.Copy();
            r.Advance(29900);
            Check.True(r.App.Ui(() => r.Handoff.IsCopyWaiting), "29.9 秒では取り消されない");
            r.Advance(100);
            Check.True(!r.App.Ui(() => r.Handoff.IsCopyWaiting), "30.0 秒で取り消される");
            AfterCancel(r, gate, "30 秒");
        }
    }

    [Test]
    public void AC43_4_INV9_編集の直後はドラッグ欄が無効で押すと待機になりクリップボードは変わらない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        Check.True(r.App.Ui(() => r.View.Drag.IsEnabled), "完成するとドラッグ欄が有効");
        r.App.Ui(() => r.Session.SetLength10(21));
        UiHost.Idle();
        Check.True(!r.App.Ui(() => r.View.Drag.IsEnabled), "編集の直後はドラッグ欄が無効");
        Check.True(r.App.Ui(() => r.View.StatusText).StartsWith("待機", StringComparison.Ordinal), "状態欄: " + r.App.Ui(() => r.View.StatusText));
        r.Copy();
        r.Save();
        UiHost.Idle();
        Check.True(r.App.Ui(() => r.Handoff.IsCopyWaiting && r.Handoff.IsSaveWaiting), "待機になる");
        Check.Equal(0, Places(r), "クリップボードは変わらない");
        Check.Equal(0, Dialogs(r), "保存ダイアログは出ない");
        r.Export();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.App.FileDialogs.Calls == 1 && r.Handoff.Busy is null, Limit), "完成するとコピーも保存も 1 回ずつ行われる");
    }

    [Test]
    public void AC43_4_INV9_編集の通知が届く前の押下でも古いファイルを渡さない()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        r.App.Ui(() =>
        {
            // 編集の通知は UI スレッドの次の仕事で届く。同じ仕事の中の押下でも待機にする(INV-9)
            r.Session.SetLength10(21);
            r.Handoff.PressCopy();
            Check.True(r.Handoff.IsCopyWaiting, "待機になる");
        });
        r.Settle();
        Check.Equal(0, Places(r), "古いファイルを渡さない");
        r.Export();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "完成すると行われる");
    }

    [Test]
    public void AC43_5_完成ファイルが消されると書き出しをやり直しコピーは待機になる()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var entry = r.App.Ui(() => r.Band.Auto.Entry!);
        File.Delete(entry.Path);
        var calls = r.Pipeline.Calls;
        r.Copy();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "やり直して完成すると行われる");
        Check.Equal(calls + 1, r.Pipeline.Calls, "書き出しをやり直した");
        Check.SequenceEqual(HandoffRig.Gif, File.ReadAllBytes(Path.Combine(r.HandoffDir, r.HandoffFiles().Single())), "新しいファイルを渡す");
        Check.Equal(0, r.App.Ui(() => r.App.FileDialogs.Calls), "保存は関係ない");
    }

    [Test]
    public void E36_完成ファイルの確認が失敗し続けても最初の押下から30秒で待機をやめ書き出しのやり直しも止まる()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        r.Fs.FailOn(FileOps.GetLength, p => p.EndsWith(".gif", StringComparison.Ordinal));
        var calls = r.Pipeline.Calls;
        r.Copy();
        Check.True(UiHost.WaitUntil(() => r.Pipeline.Calls >= calls + 2, Limit), "やり直しが繰り返されない(前提)");
        // 途中で進めて、待機に戻すたびに 30 秒を数え直していないことを確かめる(一度に 30 秒進めると、数え直していても切れる)
        r.Advance(15000);
        var midway = r.Pipeline.Calls;
        Check.True(UiHost.WaitUntil(() => r.Pipeline.Calls > midway, Limit), "15 秒ではまだやり直している(前提)");
        r.Advance(15000);
        Check.True(UiHost.WaitUntil(() => !r.Handoff.IsCopyWaiting && r.Handoff.Busy is null && r.Band.Auto.Idle.IsCompleted, Limit), "30 秒で待機をやめない");
        var stopped = r.Pipeline.Calls;
        r.Settle();
        Check.Equal(stopped, r.Pipeline.Calls, "やり直しが止まる");
        Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), "確かめられないファイルを渡さない");
        // サムネイルの読み取りが、やり直しのリネームとぶつかって失敗にしていない
        Check.Equal(0, r.Logs("export_fail").Count, "export_fail");
        Check.True(r.App.Ui(() => r.Band.Auto.State) != ExportState.Failed, "状態が失敗: " + r.App.Ui(() => r.Band.Auto.Error?.ToString()));
    }

    [Test]
    public void AC43_5_完成ファイルに1バイト足されるとやり直しの完成まで渡さない()
    {
        var gate = new SemaphoreSlim(1);
        using var r = HandoffRig.Start(CopyModeSetting.File, pipeline: new HandoffPipeline { GifGate = gate });
        r.Export();
        var entry = r.App.Ui(() => r.Band.Auto.Entry!);
        using (var s = new FileStream(entry.Path, FileMode.Append)) s.WriteByte(1);
        r.Copy();
        Check.True(UiHost.WaitUntil(() => r.Handoff.IsCopyWaiting && r.App.Clipboard.PlaceCalls == 0 && r.Band.Auto.State == ExportState.Running, Limit), "完成の扱いが取り消されて待機にならない");
        Check.True(!r.App.Ui(() => r.View.Drag.IsEnabled), "ドラッグ欄が無効");
        Check.Equal("完成したらコピー", Label(r, r.View.Copy), "待機の表示");
        gate.Release();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "完成すると行われる");
        Check.SequenceEqual(HandoffRig.Gif, File.ReadAllBytes(Path.Combine(r.HandoffDir, r.HandoffFiles().Single())), "足したバイトの無いファイルを渡す");
    }

    [Test]
    public void AC43_6_完成済みなら待機を挟まず実行される()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        r.Copy();
        Check.True(r.App.Ui(() => !r.Handoff.IsCopyWaiting), "待機にならない");
        Check.Equal("コピー", Label(r, r.View.Copy), "待機の表示にならない");
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "クリップボードへの書き込みが 1 回");
    }

    [Test]
    public void E32_失敗のあとの押下で1回だけやり直し自動では再試行しない()
    {
        var pipeline = new HandoffPipeline { Throw = _ => new InvalidOperationException("偽の失敗") };
        using var r = HandoffRig.Start(CopyModeSetting.File, pipeline: pipeline);
        r.StartExport();
        Check.True(UiHost.WaitUntil(() => r.Band.Auto.State == ExportState.Failed, Limit), "失敗しない");
        var calls = r.Pipeline.Calls;
        r.Advance(60000);
        Check.Equal(calls, r.Pipeline.Calls, "自動では再試行しない");
        r.Copy();
        Check.True(UiHost.WaitUntil(() => r.Pipeline.Calls == calls + 1 && r.Band.Auto.State == ExportState.Failed, Limit), "押下で 1 回だけやり直す");
        r.Settle();
        Check.True(r.App.Ui(() => !r.Handoff.IsCopyWaiting), "また失敗したら待機をやめる");
        Check.Equal(calls + 1, r.Pipeline.Calls, "1 回だけ");
        pipeline.Throw = null;
        r.Save();
        Check.True(UiHost.WaitUntil(() => r.App.FileDialogs.Calls == 1, Limit), "保存の押下でやり直して、完成するとダイアログが出る");
        Check.Equal(calls + 2, r.Pipeline.Calls, "1 回だけ");
    }

    [Test]
    public void E32_キャッシュの書き込みの失敗はwrite_errorを書き_tmpを残さない()
    {
        var r = HandoffRig.Start(CopyModeSetting.File);
        using (r)
        {
            r.Fs.FailOn(FileOps.Create, p => p.EndsWith(".gif.tmp", StringComparison.Ordinal));
            r.StartExport();
            Check.True(UiHost.WaitUntil(() => r.Band.Auto.State == ExportState.Failed, Limit), "失敗しない");
            Check.Equal("write_error", r.Logs("export_fail").Single().GetProperty("reason").GetString(), "reason");
            Check.Equal(0, Directory.GetFiles(r.App.Paths.CacheExportsDir, "*.tmp").Length, ".tmp が残らない");
        }
    }
}
