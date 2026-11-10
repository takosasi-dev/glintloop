// §9.11 の export_cancel の周り: 中身が返したあとの取り消しのうち、デバイスの消失で止めたときは書かないこと、バッチでも同じ口を通ること、取り消しが立っていなければ何もしないことを調べる。
// 新しい編集で止めたときに書くことは ExportDoneOrderTests.S9_11_中身のあとリネームの前の… が調べる。
using System.IO;
using GlintLoop.App.Batch;
using GlintLoop.App.Export;
using GlintLoop.Core.Export;
using GlintLoop.Render;
using GlintLoop.Tests.Batches;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;
using Ac49 = GlintLoop.Tests.Render.GpuRecoveryAc49Tests;

namespace GlintLoop.Tests.Exports;

public sealed class ExportCancelAfterRunTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    [Test]
    public void S9_11_ThrowIfCancelledは取り消しが立っているときだけAbandonedを呼んで投げる()
    {
        var calls = 0;
        var result = new ExportPipelineResult(1, 1, 1, 20, null, false, null) { Abandoned = () => calls++ };
        result.ThrowIfCancelled(CancellationToken.None);
        Check.Equal(0, calls, "取り消しが立っていない");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var thrown = false;
        try
        {
            result.ThrowIfCancelled(cts.Token);
        }
        catch (OperationCanceledException)
        {
            thrown = true;
        }
        Check.True(thrown, "OperationCanceledException を投げない");
        Check.Equal(1, calls, "取り消しが立っている");
    }

    /// <summary>本物の中身が返したあと、表示の依頼でデバイスの消失を起こし、消失の取り消しが届くまで待つ中身。</summary>
    private sealed class LoseDeviceAfterRun(IExportPipeline inner, TestApp app, FakeDeviceFactory factory) : IExportPipeline
    {
        public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
        {
            var r = inner.Run(job, output, run);
            factory.FailRender(FakeDeviceFactory.DeviceRemoved, 1, RenderPriority.Display);
            app.Ui(() => app.Shell.Frames.Set(11));
            if (!run.Cancel.WaitHandle.WaitOne(Limit)) throw new TimeoutException("消失の取り消しが届かない");
            return r;
        }

        public ExportCheck Check(ExportRequest job, ExportWritten written) => inner.Check(job, written);
    }

    [Test(Slow = true)]
    public void S9_11_中身のあとデバイスの消失で止めたときはexport_cancelを書かない()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory);
        var band = Ac49.Band(app);
        app.Ui(() => band.Pipeline = new LoseDeviceAfterRun(band.Pipeline!, app, factory));
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Ac49.WaitEvent(app, "device_recreated");
        Check.True(UiHost.WaitUntil(() => band.Auto.Idle.IsCompleted, Limit), "止めた仕事が終わらない");
        Check.Equal(1, Ac49.Count(app, "export_start"), "export_start");
        Check.Equal(0, Ac49.Count(app, "export_done"), "完成していない");
        Check.Equal(0, Ac49.Count(app, "export_fail"), "失敗に数えない");
        Check.Equal(0, Ac49.Count(app, "export_cancel"), "消失の取り消しは export_cancel に数えない");
    }

    /// <summary>書き終えて返す直前に取り消しのボタンを押す偽の中身。Abandoned を呼ばれた回数を数える。</summary>
    private sealed class CancelBeforeReturn(BatchModule module) : IExportPipeline
    {
        private int _abandoned;

        public int Abandoned => Volatile.Read(ref _abandoned);

        public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
        {
            output.Write([1, 2, 3, 4]);
            UiHost.Invoke(module.Cancel);
            return new ExportPipelineResult(1, 1, 1, 20, null, false, null) { Abandoned = () => Interlocked.Increment(ref _abandoned) };
        }

        public ExportCheck Check(ExportRequest job, ExportWritten written) => new(null, null);
    }

    [Test]
    public void S9_11_バッチでも中身のあとの取り消しはAbandonedを呼んで出力を残さない()
    {
        using var app = BatchAc48Tests.StartReal();
        var module = M<BatchModule>(app);
        var output = BatchAc48Tests.Folder(app, "out");
        var pipeline = new CancelBeforeReturn(module);
        var view = BatchAc48Tests.Open(app);
        app.Ui(() => module.Pipeline = pipeline);
        BatchAc48Tests.Drop(app, view, [Path.Combine(app.Root, "in", "a.png")]);
        BatchAc48Tests.Run(app, view, output);
        Check.Equal(1, pipeline.Abandoned, "Abandoned の回数");
        Check.SequenceEqual([], BatchAc48Tests.FileNames(output), "出力先に何も残らない");
        Check.Equal(0, app.Ui(() => view.Failures.Count), "取り消しは失敗の一覧に載らない");
    }
}
