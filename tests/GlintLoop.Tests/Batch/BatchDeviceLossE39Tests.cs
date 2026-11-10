// E-39 のバッチ: 実行中に GPU のデバイスが失われたら、その 1 個を失敗にせず、.tmp を消してから作り直し、作り直しのあとで同じ 1 個をやり直して続ける。
// 偽のデバイス工場(実体は WARP)で消失を起こす。バイト列の一致は消失の無い実行と比べる。作り直しの失敗(2 回で WARP、WARP も作れなければクラッシュの入口)と取り消しも調べる。
using System.IO;
using GlintLoop.App.Batch;
using GlintLoop.App.Startup;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Render;
using GlintLoop.Tests.Exports;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;
using Ac49 = GlintLoop.Tests.Render.GpuRecoveryAc49Tests;
using Batch = GlintLoop.Tests.Batches.BatchAc48Tests;

namespace GlintLoop.Tests.Batches;

public sealed class BatchDeviceLossE39Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);
    private const int Removed = FakeDeviceFactory.DeviceRemoved;

    private sealed class CountingCrash : ICrashHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Handle(Exception exception, CrashSource source) => Interlocked.Increment(ref _calls);
    }

    /// <summary>a.png と b.png を一覧に入れ、出力フォルダを選んだところまで。</summary>
    private static (BatchView View, string Output) Prepare(TestApp app)
    {
        var input = Path.Combine(app.Root, "in");
        var b = Batch.WriteFile(input, "b.png", Batch.PatternPng(80, 64));
        var output = Batch.Folder(app, "out");
        var view = Batch.Open(app);
        Batch.Drop(app, view, [Path.Combine(input, "a.png"), b]);
        Batch.Choose(app, view, output);
        return (view, output);
    }

    private static string[] Tmp(string output) => Directory.GetFiles(output, "*.tmp");

    /// <summary>消失の無い実行の、2 個の出力の SHA-256 と、書き出しの描画の依頼の回数(1 個目のぶん)。</summary>
    private static (string A, string B, int RendersOfA) Reference()
    {
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var input = Path.Combine(app.Root, "in");
        var a = Batch.BatchOutput(app, "one", Path.Combine(input, "a.png"), "a-glint.gif");
        var rendersOfA = factory.RenderRequests(RenderPriority.Export);
        var (view, output) = Prepare(app);
        Batch.Start(app, view);
        Batch.Finish(app, view);
        Check.Equal(0, app.Ui(() => view.Failures.Count), "消失の無い実行の失敗");
        Check.SequenceEqual(a, File.ReadAllBytes(Path.Combine(output, "a-glint.gif")), "消失の無い実行は同じ出力になる");
        return (Batch.Sha(Path.Combine(output, "a-glint.gif")), Batch.Sha(Path.Combine(output, "b-glint.gif")), rendersOfA);
    }

    private static void CheckFinished(TestApp app, BatchView view, string output, (string A, string B, int) reference, int losses = 1)
    {
        Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗の一覧: " + string.Join("、", app.Ui(() => view.Failures.Select(f => f.Name + ":" + f.Reason).ToArray())));
        Check.Equal("2 / 2", app.Ui(() => view.ProgressText.Text), "進み(やり直した 1 個は 1 回だけ数える)");
        Check.SequenceEqual(["a-glint.gif", "b-glint.gif"], Batch.FileNames(output), "出力(番号が付かず .tmp が残らない)");
        Check.Equal(reference.A, Batch.Sha(Path.Combine(output, "a-glint.gif")), "1 個目の SHA-256 が消失の無い実行と一致");
        Check.Equal(reference.B, Batch.Sha(Path.Combine(output, "b-glint.gif")), "2 個目の SHA-256 が消失の無い実行と一致");
        Check.Equal(losses, Ac49.Count(app, "device_lost"), "device_lost");
        Check.Equal(losses, Ac49.Count(app, "device_recreated"), "device_recreated");
        Check.Equal(2, Ac49.Count(app, "export_done"), "export_done は 1 個に 1 行");
        Check.Equal(0, Ac49.Count(app, "export_fail"), "export_fail が無い");
        Check.Equal(0, Ac49.Count(app, "export_cancel"), "消失の取り消しは export_cancel に数えない");
        Check.Equal(0, Ac49.Count(app, "render_fail"), "render_fail が無い");
    }

    private static void LoseOn(FakeDeviceFactory factory, int nth, string output, TestApp app, List<int> tmpAtCreate)
    {
        factory.OnCreate = _ =>
        {
            lock (tmpAtCreate) tmpAtCreate.Add(Tmp(output).Length);
        };
        factory.FailRender(Removed, nth, RenderPriority.Export);
    }

    [Test(Slow = true)]
    public void E39_1個目の途中の消失はその1個を失敗にせずやり直して続ける()
    {
        var reference = Reference();
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var (view, output) = Prepare(app);
        var created = factory.Requests.Count;
        var tmpAtCreate = new List<int>();
        LoseOn(factory, 5, output, app, tmpAtCreate);
        Batch.Start(app, view);
        Batch.Finish(app, view);
        CheckFinished(app, view, output, reference);
        Check.Equal(created + 1, factory.Requests.Count, "作成の呼び出しが 1 回増える");
        lock (tmpAtCreate) Check.SequenceEqual([0], tmpAtCreate, "作成の呼び出しの時点で出力フォルダに .tmp が無い");
    }

    [Test(Slow = true)]
    public void E39_2個目の途中の消失は1個目の出力を変えず2個目をやり直す()
    {
        var reference = Reference();
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var (view, output) = Prepare(app);
        var tmpAtCreate = new List<int>();
        LoseOn(factory, reference.RendersOfA + 3, output, app, tmpAtCreate);
        Batch.Start(app, view);
        Batch.Finish(app, view);
        CheckFinished(app, view, output, reference);
        lock (tmpAtCreate) Check.SequenceEqual([0], tmpAtCreate, "作成の呼び出しの時点で出力フォルダに .tmp が無い");
    }

    [Test(Slow = true)]
    public void E39_同じ1個で2回消失してもやり直して続ける()
    {
        var reference = Reference();
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var (view, output) = Prepare(app);
        factory.FailRender(Removed, 3, RenderPriority.Export);
        factory.FailRender(Removed, 6, RenderPriority.Export);
        Batch.Start(app, view);
        Batch.Finish(app, view);
        CheckFinished(app, view, output, reference, losses: 2);
    }

    /// <summary>1 回目の呼び出しだけ、途中まで書いて止める合図を待つ偽の中身。止められたら、作成の呼び出しを少し待ってから抜ける(.tmp が残っている間に作り直しが始まれば、そこで見つかる)。</summary>
    private sealed class WaitForStop(ManualResetEventSlim created) : IExportPipeline
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ManualResetEventSlim Reached { get; } = new();

        public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
        {
            var n = Interlocked.Increment(ref _calls);
            output.Write([1, 2, 3, 4]);
            output.Flush();
            if (n == 1)
            {
                Reached.Set();
                run.Cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                created.Wait(TimeSpan.FromMilliseconds(500));
            }
            run.Cancel.ThrowIfCancellationRequested();
            return new ExportPipelineResult(1, 1, 1, 20, null, false, null);
        }

        public ExportCheck Check(ExportRequest job, ExportWritten written) => new(null, null);
    }

    [Test(Slow = true)]
    public void E39_表示の消失でもバッチの1個を止めてtmpを消してから作り直し同じ1個をやり直す()
    {
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var (view, output) = Prepare(app);
        using var createCalled = new ManualResetEventSlim();
        var tmpAtCreate = new List<int>();
        factory.OnCreate = _ =>
        {
            lock (tmpAtCreate) tmpAtCreate.Add(Tmp(output).Length);
            createCalled.Set();
        };
        var pipeline = new WaitForStop(createCalled);
        app.Ui(() => M<BatchModule>(app).Pipeline = pipeline);
        Batch.Start(app, view);
        Check.True(pipeline.Reached.Wait(Limit), "1 個目に届かない");
        Check.SequenceEqual(["a-glint.gif.tmp"], Batch.FileNames(output), "1 個目の処理中");
        factory.FailRender(Removed, 1, RenderPriority.Display);
        Ac49.Edit(app, 0);
        Ac49.WaitEvent(app, "device_recreated");
        Batch.Finish(app, view);
        lock (tmpAtCreate) Check.SequenceEqual([0], tmpAtCreate, "作成の呼び出しの時点で .tmp が無い");
        Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗の一覧");
        Check.Equal(3, pipeline.Calls, "1 個目を 2 回、2 個目を 1 回");
        Check.Equal("2 / 2", app.Ui(() => view.ProgressText.Text), "進み");
        Check.SequenceEqual(["a-glint.gif", "b-glint.gif"], Batch.FileNames(output), "出力");
    }

    [Test(Slow = true)]
    public void E39_作り直しが2回続けて失敗するとWARPで同じ1個をやり直す()
    {
        var reference = Reference();
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var (view, output) = Prepare(app);
        factory.FailNextCreates = 2;
        factory.FailRender(Removed, 5, RenderPriority.Export);
        Batch.Start(app, view);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        Check.True(app.Ui(() => view.IsRunning), "待っている間は実行中のまま");
        Check.Equal(0, app.Ui(() => view.Failures.Count), "待っている間に失敗に載らない");
        Check.SequenceEqual([], Batch.FileNames(output), "待っている間に 2 個目へ進まない");
        app.Ui(() => app.Clock.Advance(DeviceRecovery.RetryDelay));
        Batch.Finish(app, view);
        CheckFinished(app, view, output, reference);
        Check.Equal(GpuAdapterRequest.Warp, factory.Requests[^1], "最後の作成が WARP");
        Check.Equal(1, Ac49.Count(app, "warp_fallback"), "warp_fallback");
        Check.Equal(NoticeKeys.GpuSoftware, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "gpu.software");
    }

    [Test(Slow = true)]
    public void E39_作り直しを待っている間の取り消しは残りを処理せず失敗にも載せない()
    {
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var (view, output) = Prepare(app);
        factory.FailNextCreates = 1;
        factory.FailRender(Removed, 5, RenderPriority.Export);
        Batch.Start(app, view);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        app.Ui(M<BatchModule>(app).Cancel);
        Batch.Finish(app, view);
        Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗の一覧");
        Check.Equal("0 / 2", app.Ui(() => view.ProgressText.Text), "進み");
        Check.SequenceEqual([], Batch.FileNames(output), "出力");
        // 作り直しは続く。閉じる前に終わらせる
        app.Ui(() => app.Clock.Advance(DeviceRecovery.RetryDelay));
        Ac49.WaitEvent(app, "device_recreated");
        var service = app.Ui(() => app.Shell.RenderReady!.Result);
        Check.True(UiHost.WaitUntil(() => !service.IsDeviceLost, Limit), "作り直しが終わらない");
        service.InvokeAsync(_ => 0, RenderPriority.Export).GetAwaiter().GetResult();
        UiHost.Idle();
    }

    [Test(Slow = true)]
    public void AC49_7_バッチの途中でWARPも作れないとクラッシュの入口を1回呼び実行を止める()
    {
        var factory = new FakeDeviceFactory();
        var crash = new CountingCrash();
        using var app = Batch.StartReal(deviceFactory: factory, crashFactory: _ => crash);
        var (view, output) = Prepare(app);
        factory.Fail = r => r.Kind == GpuAdapterKind.Warp;
        factory.FailNextCreates = 2;
        factory.FailRender(Removed, 5, RenderPriority.Export);
        Batch.Start(app, view);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        app.Ui(() => app.Clock.Advance(DeviceRecovery.RetryDelay));
        Check.True(UiHost.WaitUntil(() => crash.Calls > 0, Limit), "クラッシュの入口が呼ばれない");
        Batch.Finish(app, view);
        Check.Equal(1, crash.Calls, "1 回だけ");
        Check.Equal(0, app.Ui(() => view.Failures.Count), "失敗の一覧に載らない");
        Check.SequenceEqual([], Batch.FileNames(output), "2 個目へ進まず .tmp も残らない");
        Check.Equal(0, Ac49.Count(app, "export_fail"), "export_fail が無い");
    }

    [Test(Slow = true)]
    public void E39_1個と1個のあいだに消失すると作り直しまで次の1個を始めない()
    {
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var input = Path.Combine(app.Root, "in");
        var b = Batch.WriteFile(input, "b.png", Batch.PatternPng(80, 64));
        var output = Batch.Folder(app, "out");
        var recovery = app.Shell.Recovery!;
        var service = app.Ui(() => app.Shell.RenderReady!.Result);
        var pipeline = new FakePipeline { FrameCount = 1 };
        var importer = new GlintLoop.Codec.Import.ImageImporter(app.Shell.ImportTarget, app.Shell.Log, fileSystem: app.FileSystem);
        var runner = new BatchRunner(importer, app.Shell.Catalog, app.FileSystem, app.Shell.Log, _ => pipeline, () => service);
        var job = new BatchJob([Path.Combine(input, "a.png"), b], output, app.Ui(() => app.Shell.Session.Document), 0);
        var lost = 0;
        // 1 個目を終えた進みの報告のあいだは、どの 1 個の処理の中でもない
        void Report(BatchStep step)
        {
            if (step is not { Done: 1, Current: null } || Interlocked.Exchange(ref lost, 1) != 0) return;
            factory.FailNextCreates = 1;
            factory.FailRender(Removed, 1, RenderPriority.Display);
            Ac49.Edit(app, 0);
            Check.True(UiHost.WaitUntil(() => recovery.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        }
        var work = Task.Run(() => runner.Run(job, TaskScheduler.Default, 1, Report, CancellationToken.None));
        Check.True(UiHost.WaitUntil(() => recovery.IsWaitingForRetry, Limit), "消失しない");
        Check.True(!work.Wait(TimeSpan.FromMilliseconds(500)), "デバイスが失われている間に次の 1 個を処理した");
        Check.Equal(1, pipeline.Calls, "作り直しまで 2 個目の中身を呼ばない");
        app.Ui(() => app.Clock.Advance(DeviceRecovery.RetryDelay));
        Check.True(work.Wait(Limit), "作り直しのあとで続かない");
        Check.Equal(2, pipeline.Calls, "2 個目を処理する");
        Check.SequenceEqual(["a-glint.gif", "b-glint.gif"], Batch.FileNames(output), "出力");
        service.InvokeAsync(_ => 0, RenderPriority.Export).GetAwaiter().GetResult();
        UiHost.Idle();
    }

    [Test(Slow = true)]
    public void E39_デバイスが失われていないのに中身が消失の例外を投げたら失敗に数えて止まらない()
    {
        var factory = new FakeDeviceFactory();
        using var app = Batch.StartReal(deviceFactory: factory);
        var (view, output) = Prepare(app);
        app.Ui(() => M<BatchModule>(app).Pipeline = new FakePipeline { Throw = new GpuDeviceLostException() });
        Batch.Start(app, view);
        Batch.Finish(app, view);
        Check.Equal(2, app.Ui(() => view.Failures.Count), "やり直さず失敗に載せる");
        Check.SequenceEqual([], Batch.FileNames(output), "出力");
    }
}
