// 統合(Phase 4)の確かめ: 書き出し帯が本物の中身(RenderExportPipeline)と BelowNormal の専用スレッドで動き、完成したファイルがキャッシュに置かれ、モーションのタブが左ペインに入る。
// 画面の外に立てたアプリと WARP で、偽の時計を 600 ms 進めて自動の書き出しを動かす。担当ごとの細かい検査は Band・Export の各テストにある。
using System.IO;
using System.Windows.Controls;
using GlintLoop.App.Export;
using GlintLoop.App.Looks;
using GlintLoop.App.MotionTab;
using GlintLoop.Codec.Preview;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Exports;

public sealed class ExportWireTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>長辺 64・0.5 秒・20 fps の小さい書き出しの文書と、本物の中身が入った帯。</summary>
    private static ExportBandModule Band(TestApp app)
    {
        var band = M<ExportBandModule>(app);
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, Limit), "本物の中身が入らない");
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetLength10(5);
            s.SetFps(20);
            s.SetOutputSize("long", 64);
        });
        return band;
    }

    private static void Export(TestApp app, ExportBandModule band)
    {
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, Limit), "書き出しが終わらない: " + app.Ui(() => band.Auto.State));
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "状態(" + app.Ui(() => band.Auto.Error?.ToString()) + ")");
    }

    private static string Logs(TestApp app)
    {
        var text = "";
        foreach (var f in Directory.GetFiles(app.Paths.LogsDir, "*.jsonl"))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            text += r.ReadToEnd();
        }
        return text;
    }

    [Test]
    public void 帯はBelowNormalの専用スレッドを持ちエンコードの並列度をそれに合わせる()
    {
        using var app = TestApp.Start();
        var band = M<ExportBandModule>(app);
        var scheduler = app.Ui(() => band.Scheduler) as ExportScheduler;
        Check.True(scheduler is not null, "スケジューラーが ExportScheduler でない");
        Check.Equal(scheduler!.DefaultParallelism, app.Ui(() => band.Auto.Parallelism), "並列度");
    }

    [Test]
    public void モーションのタブの中身が左ペインに入る()
    {
        using var app = TestApp.Start();
        var motion = M<MotionTabModule>(app);
        Check.True(app.Ui(() => ReferenceEquals(M<LooksModule>(app).LeftPane.MotionContent, motion.View)), "モーションのタブの中身が違う");
    }

    [Test]
    public void 容量上限の一覧はCoreの表と同じで大きすぎる失敗は専用の文になる()
    {
        Check.SequenceEqual(LimitPresets.All.Select(p => p.Key).ToArray(), BandLimitPresets.Builtin.Select(p => p.Key).ToArray(), "プリセットの並び");
        var big = new ExportTooBigException(new ExportSettings { Format = ExportFormat.Apng, Width = 5000, Height = 5000, LongSide = 5000 });
        using var app = TestApp.Start();
        Check.True(app.Ui(() => BandTexts.Line2(ExportState.Failed, null, null, big)).Contains("APNG", StringComparison.Ordinal), "大きすぎる失敗の文に形式が無い");
    }

    [Test]
    public void ウェルカム画面の帯は一覧に既定の値を出しサイズとバイト数の欄は空()
    {
        using var app = TestApp.Start();
        UiHost.Idle();
        var view = M<ExportBandModule>(app).View;
        app.Ui(() =>
        {
            Check.True(view.Format.SelectedItem is ComboBoxItem { Tag: string f } && f == OutputSettings.Default.Format, "形式の一覧が既定でない");
            Check.True(view.Limit.SelectedItem is ComboBoxItem { Tag: LimitPresetItem { Key: "none" } }, "容量上限の一覧が「上限なし」でない");
            Check.True(view.SizeMode.SelectedItem is not null, "サイズの指定が空");
            Check.Equal("", view.SizeValue.Text, "サイズの欄");
            Check.Equal("", view.LimitBytes.Text, "バイト数の欄");
        });
    }

    [Test]
    public void バイト数の欄は直接入力のときだけ値を出し空のまま離れても上限を変えない()
    {
        using var b = BandUiAc39Tests.Start(new FakePipeline());
        var view = b.Module.View;
        var s = b.App.Shell.Session;
        Check.Equal("", b.App.Ui(() => view.LimitBytes.Text), "上限なしのとき");
        b.App.Ui(() => view.LimitBytes.Commit());
        Check.Equal(new OutputLimit("none", null, null), b.App.Ui(() => s.Document!.Output.Limit), "空のまま離れても上限なしのまま");
        Check.Equal("", b.App.Ui(() => view.LimitBytes.Text), "空のまま離れても空");
        b.App.Ui(() => s.SetOutputLimit(new OutputLimit("bytes", null, 2000)));
        UiHost.Idle();
        Check.Equal("2000", b.App.Ui(() => view.LimitBytes.Text), "直接入力のとき");
        b.App.Ui(() => s.SetOutputLimit(new OutputLimit("preset", "chat_small", null)));
        UiHost.Idle();
        Check.Equal("", b.App.Ui(() => view.LimitBytes.Text), "プリセットのとき");
        b.App.Ui(() =>
        {
            view.LimitBytes.Text = "5000";
            view.LimitBytes.Commit();
        });
        Check.Equal(new OutputLimit("bytes", null, 5000), b.App.Ui(() => s.Document!.Output.Limit), "空の欄に入れると直接入力になる");
        UiHost.Idle();
        Check.Equal("5000", b.App.Ui(() => view.LimitBytes.Text), "入れた値が残る");
    }

    [Test(Slow = true)]
    public void 本物の中身でGIFを書きキャッシュの完成ファイルをデコードできログに残り2回目は使い回す()
    {
        using var app = Start(layers: 1);
        var band = Band(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetOutputFormat(ExportFormat.Gif);
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
        });
        Export(app, band);
        var first = app.Ui(() => band.Auto.Outcome!);
        var decoded = OutputFrameDecoder.Decode(File.ReadAllBytes(first.Path), first.Format);
        Check.Equal(10, decoded.Frames.Count, "フレーム数(0.5 秒 × 20 fps)");
        Check.Equal(10, first.Frames, "結果のフレーム数");
        Check.Equal(64, Math.Max(decoded.Width, decoded.Height), "長辺");
        Check.Equal(new FileInfo(first.Path).Length, first.Bytes, "ファイルの大きさ");
        Check.True(app.Ui(() => band.View.StatusLine1.Text).Contains("10 フレーム", StringComparison.Ordinal), "状態欄の 1 行目: " + app.Ui(() => band.View.StatusLine1.Text));
        var log = Logs(app);
        Check.True(log.Contains("export_start", StringComparison.Ordinal) && log.Contains("export_done", StringComparison.Ordinal), "ログに書き出しの行が無い");

        // 形式を変えて戻すと、2 回目はエンコーダーを呼ばずに同じファイルを使う
        var calls = app.Ui(() => band.Auto.PipelineCalls);
        app.Ui(() => app.Shell.Session.SetOutputFormat(ExportFormat.Png));
        Export(app, band);
        Check.Equal(calls + 1, app.Ui(() => band.Auto.PipelineCalls), "PNG は書く");
        app.Ui(() => app.Shell.Session.SetOutputFormat(ExportFormat.Gif));
        Export(app, band);
        Check.Equal(calls + 1, app.Ui(() => band.Auto.PipelineCalls), "2 回目の GIF はキャッシュを使う");
        Check.Equal(first.Path, app.Ui(() => band.Auto.Outcome!.Path), "同じファイル");
    }

    [Test(Slow = true)]
    public void 本物の中身でbothのコピーがGIFの実体とkのPNGを載せPNGとDIBの大きさが出力と同じ()
    {
        using var app = Start(layers: 1);
        var band = Band(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetOutputFormat(ExportFormat.Gif);
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
        });
        Export(app, band);
        var gif = app.Ui(() => band.Auto.Outcome!);
        app.Ui(() => app.Shell.Frames.Set(3));
        app.Ui(() => band.Handoff.PressCopy());
        Check.True(UiHost.WaitUntil(() => app.Clipboard.PlaceCalls == 1 && band.Handoff.Busy is null, Limit), "コピーされない: " + app.Ui(() => band.Auto.State));
        app.Ui(() =>
        {
            var data = (System.Windows.DataObject)app.Clipboard.Placed[0].Data;
            var png = ((MemoryStream)data.GetData("PNG", false)!).ToArray();
            var decoded = OutputFrameDecoder.Decode(png, ExportFormat.Png);
            Check.Equal((gif.Width, gif.Height), (decoded.Width, decoded.Height), "PNG の大きさ");
            var v5 = ((MemoryStream)data.GetData("Format17", false)!).ToArray();
            Check.Equal(124 + gif.Width * 4 * gif.Height, v5.Length, "DIBV5 の長さ");
            Check.SequenceEqual(File.ReadAllBytes(gif.Path), File.ReadAllBytes(data.GetFileDropList()[0]!), "実体は GIF のキャッシュのファイル");
            Check.Equal(ExportFormat.Gif, app.Shell.Session.Document!.Output.Format, "文書の形式は変えない");
        });
    }

    [Test(Slow = true)]
    public void 本物の中身で容量上限の段を選び選んだ結果がキャッシュのファイルになり使い回しでも警告が同じ()
    {
        using var app = Start(layers: 1);
        var band = Band(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetOutputFormat(ExportFormat.Gif);
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
            s.SetOutputLimit(new OutputLimit("bytes", null, 1));
        });
        Export(app, band);
        var first = app.Ui(() => band.Auto.Outcome!);
        Check.True(first.SizeFail, "1 バイトには収まらない");
        Check.True(first.Bytes > 1, "最後の段を打ち切りなしで作り直す");
        Check.Equal(new FileInfo(first.Path).Length, first.Bytes, "選んだ結果がファイルになる");
        Check.True(OutputFrameDecoder.Decode(File.ReadAllBytes(first.Path), first.Format).Frames.Count >= 1, "デコードできる");
        Check.True(!string.IsNullOrEmpty(app.Ui(() => band.View.StatusLine2.Text)), "状態欄の 2 行目に警告が出る");

        var calls = app.Ui(() => band.Auto.PipelineCalls);
        app.Ui(() => app.Shell.Session.SetOutputLimit(new OutputLimit("none", null, null)));
        Export(app, band);
        app.Ui(() => app.Shell.Session.SetOutputLimit(new OutputLimit("bytes", null, 1)));
        Export(app, band);
        Check.Equal(calls + 1, app.Ui(() => band.Auto.PipelineCalls), "上限ありの 2 回目はキャッシュを使う");
        var again = app.Ui(() => band.Auto.Outcome!);
        Check.True(again.SizeFail, "使い回しでも上限の警告が出る");
        Check.Equal(first.Condition ?? "", again.Condition ?? "", "使い回しでも制約の警告が同じ");
    }
}
