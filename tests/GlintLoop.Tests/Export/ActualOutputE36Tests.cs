// E-36 の実出力(F5): 完成したキャッシュのファイルが外から消された、またはバイト数が json と合わないときは、通知を出さずに完成の扱いを取り消して書き出しをやり直す。
// やり直しの間の表示と F5 は完成前の F5 と同じ。バイト数が合って読めないファイルは E-36 に当たらないので export.failed のまま。偽の書き出しの中身と偽のファイルシステムで調べる。
using System.IO;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Notices;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Gif;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class ActualOutputE36Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private const int W = 48, H = 32;

    private static byte[] Gif()
    {
        var frame = new byte[W * H * 4];
        for (var i = 0; i < frame.Length; i += 4) (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]) = (200, 40, 90, 255);
        return GifEncoder.EncodeToArray(new GifFrameList(W, H, [frame, frame]), new GifEncoderOptions { Fps = 10, Parallelism = 1 });
    }

    /// <summary>1 回目を完成させる。Gate の残りが 0 なので、2 回目はフレームの前で止まる。</summary>
    private static (BandUiAc39Tests.Band Band, FakePipeline Pipeline, SemaphoreSlim Gate, string Path) Finished()
    {
        var gif = Gif();
        var gate = new SemaphoreSlim(2);
        var pipeline = new FakePipeline { FrameCount = 2, Content = _ => gif, Gate = gate, Result = new ExportPipelineResult(W, H, 2, 10, null, false, null) };
        var b = BandUiAc39Tests.Start(pipeline);
        b.Advance(600);
        b.WaitDone("1 回目");
        Check.True(UiHost.WaitUntil(() => b.Module.Handoff.PendingThumbnail is null, Wait), "サムネイルが終わらない");
        return (b, pipeline, gate, b.App.Ui(() => b.Module.Auto.Outcome!.Path));
    }

    private static void F5(BandUiAc39Tests.Band b) => b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));

    private static string Status(BandUiAc39Tests.Band b) => b.App.Ui(() => b.Module.View.StatusText);

    private static bool Noticed(BandUiAc39Tests.Band b) => b.App.Ui(() => b.App.Shell.NoticeBar.IsShowing);

    private static bool Checked(BandUiAc39Tests.Band b) => b.App.Ui(() =>
        b.App.Shell.CommandBar.Button(GlintLoop.App.Theme.Icons.ActualOutput) is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true });

    // やり直しが始まってから、完成して F5 で出せるところまで
    private static void CheckRewritten(BandUiAc39Tests.Band b, FakePipeline pipeline, SemaphoreSlim gate, string path)
    {
        Check.True(UiHost.WaitUntil(() => pipeline.Calls == 2 && pipeline.Waiting.IsSet && b.Module.PendingShow is null, Wait), "書き出しをやり直さない: " + pipeline.Calls + " " + Status(b));
        UiHost.Idle();
        Check.True(!Noticed(b), "通知を出さない: " + b.App.Ui(() => b.App.Shell.NoticeBar.Text));
        Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "完成前に切り替わった");
        Check.True(!Checked(b), "完成前はボタンが入らない");
        Check.True(b.Module.WritingRequested, "完成前の F5 と同じ扱い");
        Check.True(Status(b).StartsWith("書き出し中", StringComparison.Ordinal), "「書き出し中」が出ない: " + Status(b));
        Check.True(!b.App.Ui(() => b.Module.Auto.IsReady), "完成の扱いが取り消されていない");

        // やり直しの最中の F5 も、完成前の F5 と同じ(切り替えず、書き出しを増やさない)
        F5(b);
        UiHost.Idle();
        Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "やり直しの最中に切り替わった");
        Check.True(Status(b).StartsWith("書き出し中", StringComparison.Ordinal), "やり直しの最中の F5: " + Status(b));
        Check.Equal(2, pipeline.Calls, "書き出しの回数が増えない");
        Check.True(!Noticed(b), "やり直しの最中の F5 で通知を出さない");

        gate.Release(2);
        b.WaitDone("やり直し");
        Check.True(!Noticed(b), "完成のあとも通知なし");
        Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "完成しても自分では切り替えない");
        Check.True(!b.Module.WritingRequested, "「書き出し中」を戻す");
        Check.Equal(Gif().Length, new FileInfo(path).Length, "完成のファイルが元に戻る");
        Check.True(UiHost.WaitUntil(() => b.Module.Handoff.PendingThumbnail is null, Wait), "サムネイルが終わらない");
        F5(b);
        Check.True(UiHost.WaitUntil(() => b.Module.PendingShow is null && b.Canvas.IsActualOutput, Wait), "完成のあとの F5 で出ない");
        Check.Equal(2, pipeline.Calls, "完成のあとの F5 では書き出さない");
    }

    [Test]
    public void E36_F5で完成ファイルが消えていたら通知なしでやり直し完成前のF5と同じに扱う()
    {
        var (b, pipeline, gate, path) = Finished();
        using (b)
        {
            File.Delete(path);
            F5(b);
            CheckRewritten(b, pipeline, gate, path);
        }
    }

    [Test]
    public void E36_F5で完成ファイルのバイト数がjsonと合わなければ読めてもやり直す()
    {
        var (b, pipeline, gate, path) = Finished();
        using (b)
        {
            // 末尾に足した GIF はデコードできる。見つけるのはバイト数の確認だけ
            File.AppendAllBytes(path, [0]);
            F5(b);
            CheckRewritten(b, pipeline, gate, path);
        }
    }

    [Test]
    public void E36_F5の確認のあと読む前に消されてもやり直す()
    {
        var (b, pipeline, gate, path) = Finished();
        using (b)
        {
            var once = 0;
            b.App.FileSystem.Observer = (op, p) =>
            {
                if (op == FileOps.ReadAllBytes && string.Equals(p, path, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref once, 1) == 0) File.Delete(p);
            };
            F5(b);
            CheckRewritten(b, pipeline, gate, path);
            Check.Equal(1, Volatile.Read(ref once), "読む直前に消した");
        }
    }

    [Test]
    public void E36_消えたあとの完成前に編集するとF5のやり直しは編集のものだけになる()
    {
        var (b, pipeline, gate, path) = Finished();
        using (b)
        {
            File.Delete(path);
            // デコードの完了を待たずに編集する(完成の扱いはもう無いので、F5 の結果で文書の違うやり直しをしない)
            b.App.Ui(() =>
            {
                UiInput.Key(b.App.Shell.Window, Key.F5);
                b.App.Shell.Session.SetLength10(21);
            });
            UiHost.Idle();
            Check.True(UiHost.WaitUntil(() => b.Module.PendingShow is null, Wait), "F5 の処理が終わらない");
            UiHost.Idle();
            Check.True(!Noticed(b), "通知を出さない");
            Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "切り替わらない");
            Check.Equal(1, pipeline.Calls, "編集の 600 ms の前に書き出さない");
            gate.Release(100);
        }
    }

    [Test]
    public void E36_バイト数が合って読めないファイルはやり直さずexport_failedを出す()
    {
        var (b, pipeline, _, path) = Finished();
        using (b)
        {
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);
            F5(b);
            Check.True(UiHost.WaitUntil(() => b.App.Shell.NoticeBar.Current?.Key == NoticeKeys.ExportFailed, Wait), "export.failed が出ない");
            UiHost.Idle();
            Check.Equal(1, pipeline.Calls, "書き出しをやり直さない");
            Check.True(b.App.Ui(() => b.Module.Auto.IsReady), "完成の扱いのまま");
        }
    }

    [Test]
    public void E36_ファイルがあって開けないときはやり直さずexport_failedを出す()
    {
        var (b, pipeline, _, path) = Finished();
        using (b)
        {
            b.App.FileSystem.FailOn(FileOps.ReadAllBytes, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            F5(b);
            Check.True(UiHost.WaitUntil(() => b.App.Shell.NoticeBar.Current?.Key == NoticeKeys.ExportFailed, Wait), "export.failed が出ない");
            UiHost.Idle();
            Check.Equal(1, pipeline.Calls, "書き出しをやり直さない");
        }
    }
}
