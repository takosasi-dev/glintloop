// §9.11 の export_done: 完成のリネームが成功したあとに 1 行だけ書く。通常の書き出しはキャッシュの、バッチは出力先のリネームで確かめる(本物の中身と偽のファイルシステム)。
// リネームの呼び出しの時点でログに無いこと、リネームが失敗したら export_fail だけが残ること、rungs と trials の値が変わらないことを見る。
using System.IO;
using GlintLoop.App.Export;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using Ac49 = GlintLoop.Tests.Render.GpuRecoveryAc49Tests;
using Batch = GlintLoop.Tests.Batches.BatchAc48Tests;

namespace GlintLoop.Tests.Exports;

public sealed class ExportDoneOrderTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private static bool IsFinalGif(string path) => path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    [Test(Slow = true)]
    public void AC57_3_キャッシュのリネームが失敗するとexport_doneを書かずexport_failだけが残る()
    {
        var fs = new FakeFileSystem();
        using var app = Ac49.Start(new FakeDeviceFactory(), fileSystem: fs);
        var band = Ac49.Band(app);
        fs.FailOn(FileOps.Move, p => IsFinalGif(p) && p.Contains(Path.Combine("cache", "exports"), StringComparison.OrdinalIgnoreCase));
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State == ExportState.Failed, Limit), "失敗にならない: " + app.Ui(() => band.Auto.State));
        Check.Equal(1, Ac49.Count(app, "export_start"), "export_start");
        Check.Equal(0, Ac49.Count(app, "export_done"), "リネームが失敗したので export_done を書かない");
        Check.Equal(1, Ac49.Count(app, "export_fail"), "export_fail は 1 行");
        Check.Equal("write_error", Ac49.Last(app, "export_fail").GetProperty("reason").GetString(), "reason");
    }

    [Test(Slow = true)]
    public void S9_11_export_doneはキャッシュのリネームのあとに1行で値は完成のファイルと同じ()
    {
        var fs = new FakeFileSystem();
        using var app = Ac49.Start(new FakeDeviceFactory(), fileSystem: fs);
        var band = Ac49.Band(app);
        var atRename = new List<int>();
        fs.Observer = (op, path) =>
        {
            if (op == FileOps.Move && IsFinalGif(path)) lock (atRename) atRename.Add(Ac49.Count(app, "export_done"));
        };
        Ac49.ExportDone(app, band);
        lock (atRename) Check.SequenceEqual([0], atRename, "リネームの呼び出しの時点で export_done が無い");
        Check.Equal(1, Ac49.Count(app, "export_done"), "export_done は 1 行");
        var line = Ac49.Last(app, "export_done");
        var path = app.Ui(() => band.Auto.Outcome!.Path);
        Check.Equal(new FileInfo(path).Length, line.GetProperty("bytes").GetInt64(), "bytes");
        Check.Equal(40, line.GetProperty("frames").GetInt32(), "frames");
        Check.Equal("gif", line.GetProperty("fmt").GetString(), "fmt");
        Check.Equal(0, line.GetProperty("rungs").GetInt32(), "上限なしの rungs");
        Check.Equal(1, line.GetProperty("trials").GetInt32(), "上限なしの trials");
        Check.True(line.GetProperty("ms").GetInt64() >= 0, "ms");
    }

    [Test(Slow = true)]
    public void S9_11_上限に収まらないときのrungsとtrialsが残る()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory);
        var band = Ac49.Band(app);
        app.Ui(() =>
        {
            app.Shell.Session.SetLength10(5);
            Check.True(app.Shell.Session.SetOutputLimit(new OutputLimit("bytes", null, 10)).Changed, "上限を入れられない");
        });
        UiHost.Idle();
        Ac49.ExportDone(app, band);
        Check.Equal(1, Ac49.Count(app, "export_done"), "export_done は 1 行");
        var line = Ac49.Last(app, "export_done");
        Check.Equal(new FileInfo(app.Ui(() => band.Auto.Outcome!.Path)).Length, line.GetProperty("bytes").GetInt64(), "選んだ段の bytes");
        Check.True(line.GetProperty("rungs").GetInt32() >= 1, "rungs は適用した段の数");
        Check.True(line.GetProperty("trials").GetInt32() >= 2, "trials は試行の回数");
    }

    [Test(Slow = true)]
    public void S9_11_バッチは出力先のリネームのあとにexport_doneを書く()
    {
        var fs = new FakeFileSystem();
        using var app = Batch.StartReal(fileSystem: fs);
        var output = Batch.Folder(app, "out");
        var atRename = new List<int>();
        fs.Observer = (op, path) =>
        {
            if (op == FileOps.Move && path.EndsWith("a-glint.gif", StringComparison.OrdinalIgnoreCase)) lock (atRename) atRename.Add(Ac49.Count(app, "export_done"));
        };
        var view = Batch.Open(app);
        Batch.Drop(app, view, [Path.Combine(app.Root, "in", "a.png")]);
        Batch.Run(app, view, output);
        Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗の一覧");
        lock (atRename) Check.SequenceEqual([0], atRename, "リネームの呼び出しの時点で export_done が無い");
        Check.Equal(1, Ac49.Count(app, "export_done"), "export_done は 1 行");
        Check.Equal(new FileInfo(Path.Combine(output, "a-glint.gif")).Length, Ac49.Last(app, "export_done").GetProperty("bytes").GetInt64(), "bytes");
    }

    [Test(Slow = true)]
    public void S9_11_バッチの出力先のリネームが失敗するとexport_doneを書かない()
    {
        var fs = new FakeFileSystem();
        using var app = Batch.StartReal(fileSystem: fs);
        var output = Batch.Folder(app, "out");
        fs.FailOn(FileOps.Move, p => p.EndsWith("a-glint.gif", StringComparison.OrdinalIgnoreCase));
        var view = Batch.Open(app);
        Batch.Drop(app, view, [Path.Combine(app.Root, "in", "a.png")]);
        Batch.Run(app, view, output);
        Check.Equal(1, app.Ui(() => view.Failures.Count), "失敗の一覧");
        Check.Equal(0, Ac49.Count(app, "export_done"), "export_done を書かない");
        Check.Equal(1, Ac49.Count(app, "export_fail"), "export_fail は 1 行");
        Check.Equal("write_error", Ac49.Last(app, "export_fail").GetProperty("reason").GetString(), "reason");
        Check.SequenceEqual([], Batch.FileNames(output), "出力先に何も残らない");
    }

    /// <summary>本物の中身が返したあと、リネームの前に新しい編集で取り消す中身。</summary>
    private sealed class EditAfterRun(IExportPipeline inner, TestApp app, ExportBandModule band) : IExportPipeline
    {
        public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
        {
            var r = inner.Run(job, output, run);
            app.Ui(() => band.Auto.NotifyEdit());
            return r;
        }

        public ExportCheck Check(ExportRequest job, ExportWritten written) => inner.Check(job, written);
    }

    [Test(Slow = true)]
    public void S9_11_中身のあとリネームの前の新しい編集の取り消しはexport_cancelを1行書く()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory);
        var band = Ac49.Band(app);
        app.Ui(() => band.Pipeline = new EditAfterRun(band.Pipeline!, app, band));
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.Idle.IsCompleted && band.Auto.PipelineCalls == 1 && band.Auto.State == ExportState.Idle, Limit), "取り消されない");
        Check.Equal(1, Ac49.Count(app, "export_start"), "export_start");
        Check.Equal(0, Ac49.Count(app, "export_done"), "完成していないので export_done を書かない");
        Check.Equal(1, Ac49.Count(app, "export_cancel"), "止めた書き出しごとに export_cancel を 1 行");
    }
}
