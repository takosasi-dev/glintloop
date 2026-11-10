// AC-49: GPU のデバイスの消失と WARP への切り替え(FR-93・FR-94・E-39・E-40・E-41)と、AC-57 の (3) の書き出しの失敗の理由。
// 偽のデバイス工場(実体は WARP)と偽の時計で、画面の外のアプリの消失・作り直し・切り替え・クラッシュの入口を確かめる。画像は 64×64、spin 1 個、GIF、L=2.0、F=20。
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using GlintLoop.App.Startup;
using GlintLoop.App.Export;
using GlintLoop.App.Screens;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Render;

public sealed class GpuRecoveryAc49Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    private const int Removed = FakeDeviceFactory.DeviceRemoved;
    private const int Reset = FakeDeviceFactory.DeviceReset;
    private const int Oom = FakeDeviceFactory.OutOfMemory;
    private const int EFail = FakeDeviceFactory.EFail;

    // ================================================================ 道具

    /// <summary>AC-49 の前書きの設定の文書を作り、最初の表示まで待つ。</summary>
    internal static TestApp Start(FakeDeviceFactory factory, GpuAdapterRequest? gpu = null, FakeFileSystem? fileSystem = null)
    {
        var app = TestApp.Start(gpu: gpu ?? GpuAdapterRequest.HighPerformance, deviceFactory: factory, fileSystem: fileSystem);
        try
        {
            app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(64, 64, Img.Pattern(64, 64, alpha: false)));
            Check.True(Key(app, System.Windows.Input.Key.V, System.Windows.Input.ModifierKeys.Control), "Ctrl+V が処理されない");
            WaitImports(app);
            var canvas = M<CanvasModule>(app);
            app.Ui(() =>
            {
                var s = app.Shell.Session;
                Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
                s.SetOutputFormat(ExportFormat.Gif);
                s.SetLength10(20);
                s.SetFps(20);
                s.SetOutputSize("long", 64);
                app.Shell.Frames.Set(7);
            });
            WaitShown(app, canvas);
            return app;
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    internal static void WaitShown(TestApp app, CanvasModule canvas) =>
        Check.True(UiHost.WaitUntil(() => !canvas.Processed.IsBusy && ReferenceEquals(canvas.Processed.PresentedDocument, app.Shell.Session.Document), Limit), "表示が更新されない");

    /// <summary>スライダーの変更の代わりに不透明度を変えて、表示を 1 回頼む。</summary>
    internal static void Edit(TestApp app, int i) => app.Ui(() => app.Shell.Session.SetLayerOpacity(0, 90 - i));

    internal static void EditAndWait(TestApp app, CanvasModule canvas, int i)
    {
        Edit(app, i);
        WaitShown(app, canvas);
    }

    internal static List<JsonElement> Log(TestApp app)
    {
        var lines = new List<JsonElement>();
        if (!Directory.Exists(app.Paths.LogsDir)) return lines; // まだ 1 行も書いていない
        foreach (var f in Directory.GetFiles(app.Paths.LogsDir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                if (line.Length > 0) lines.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
        }
        return lines;
    }

    internal static string[] Events(TestApp app, params string[] names) =>
        Log(app).Select(l => l.GetProperty("event").GetString()!).Where(names.Contains).ToArray();

    internal static JsonElement Last(TestApp app, string name) => Log(app).Last(l => l.GetProperty("event").GetString() == name);

    internal static int Count(TestApp app, string name) => Events(app, name).Length;

    internal static void WaitEvent(TestApp app, string name, int count = 1) =>
        Check.True(UiHost.WaitUntil(() => Count(app, name) >= count, Limit), $"ログ {name} が {count} 行にならない: " + string.Join(",", Log(app).Select(l => l.GetProperty("event").GetString())));

    private static string Json(TestApp app) => app.Ui(() => DocumentJson.Write(app.Shell.Session.Document!));

    internal static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] Tmp(TestApp app) =>
        Directory.Exists(app.Paths.CacheExportsDir) ? Directory.GetFiles(app.Paths.CacheExportsDir, "*.tmp") : [];

    internal static ExportBandModule Band(TestApp app)
    {
        var band = M<ExportBandModule>(app);
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, Limit), "本物の中身が入らない");
        return band;
    }

    internal static void ExportDone(TestApp app, ExportBandModule band)
    {
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, Limit), "書き出しが終わらない: " + app.Ui(() => band.Auto.State));
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "状態(" + app.Ui(() => band.Auto.Error?.ToString()) + ")");
    }

    /// <summary>消失の無い実行の、3 回編集したあとの表示。</summary>
    private static byte[] ReferenceDisplay(GpuAdapterRequest? gpu = null)
    {
        using var app = Start(new FakeDeviceFactory(), gpu);
        var canvas = M<CanvasModule>(app);
        for (var i = 0; i < 3; i++) EditAndWait(app, canvas, i);
        return app.Ui(() => canvas.Processed.PresentedImage!.Pixels);
    }

    // ================================================================ (1) 表示中の消失

    [Test(Slow = true)]
    public void AC49_1_表示中の消失は同じアダプターですぐ作り直し同じ画素を出して通知もrender_failも無い()
    {
        var reference = ReferenceDisplay();
        foreach (var hr in new[] { Removed, Reset })
        {
            var factory = new FakeDeviceFactory();
            using var app = Start(factory);
            var canvas = M<CanvasModule>(app);
            var created = factory.Requests.Count;
            factory.FailRender(hr, 3, RenderPriority.Display);
            var startedAt = app.Clock.UtcNow;
            EditAndWait(app, canvas, 0);
            EditAndWait(app, canvas, 1);
            var json = Json(app);
            Edit(app, 2);
            var expectedJson = Json(app);
            Check.True(json != expectedJson, "編集で JSON が変わる");
            WaitShown(app, canvas);
            Check.Equal(startedAt, app.Clock.UtcNow, "時計は進めていない");
            WaitEvent(app, "device_recreated");

            var requests = factory.Requests;
            Check.Equal(created + 1, requests.Count, "工場の作成の呼び出しが 1 回増える");
            Check.Equal(GpuAdapterRequest.ByLuid(FakeDeviceFactory.DefaultLuid), requests[^1], "最初のデバイスの識別子で頼む");
            Check.SequenceEqual<long?>([FakeDeviceFactory.DefaultLuid, FakeDeviceFactory.DefaultLuid], factory.CreatedLuids, "アダプターの識別子が最初のデバイスと同じ");
            Check.Equal(expectedJson, Json(app), "EditorSession の JSON が変わらない");
            Check.SequenceEqual(reference, app.Ui(() => canvas.Processed.PresentedImage!.Pixels), "作り直したあとの表示が消失の無い実行と一致");
            Check.True(!app.Ui(() => app.Shell.NoticeBar.IsShowing), "通知バーに何も出ない");
            Check.SequenceEqual(["device_lost", "device_recreated"], Events(app, "device_lost", "device_recreated", "warp_fallback", "render_fail"), "ログの順");
            var line = Last(app, "device_recreated");
            Check.Equal("hardware", line.GetProperty("adapter").GetString(), "adapter");
            Check.Equal(0L, line.GetProperty("ms").GetInt64(), "ms");
            Check.True(!app.Ui(() => app.Shell.RenderReady!.Result.IsWarp), "WARP に切り替わっていない");
        }
    }

    [Test(Slow = true)]
    public void AC49_1_サムネイルの描画で消失しても表示をやり直す()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var canvas = M<CanvasModule>(app);
        var started = app.Ui(() => canvas.Processed.Started);
        factory.FailRender(Removed, 1, RenderPriority.Thumbnail);
        // レイヤーの行のサムネイルの描き直しだけで消失させる(FR-55 の 300 ms)
        app.Ui(() => app.Shell.Session.AddEffect(Target.OfLayer(0), "exposure"));
        WaitShown(app, canvas);
        started = app.Ui(() => canvas.Processed.Started);
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(300)));
        WaitEvent(app, "device_recreated");
        Check.True(UiHost.WaitUntil(() => canvas.Processed.Started > started && !canvas.Processed.IsBusy, Limit), "作り直したあとに表示を描き直さない");
        Check.Equal(0, Count(app, "render_fail"), "render_fail が無い");
    }

    // ================================================================ (2) 2 回目で成功

    [Test(Slow = true)]
    public void AC49_2_1回目が失敗すると1000msの偽の時計のあとで2回目を作りWARPにしない()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var canvas = M<CanvasModule>(app);
        var recovery = app.Shell.Recovery!;
        var created = factory.Requests.Count;
        factory.FailNextCreates = 1;
        factory.FailRender(Removed, 1, RenderPriority.Display);
        Edit(app, 0);
        Check.True(UiHost.WaitUntil(() => recovery.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        Check.Equal(created + 1, factory.Requests.Count, "1 回目の作成");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(999)));
        UiHost.Idle();
        Check.Equal(created + 1, factory.Requests.Count, "999 ms では 2 回目が始まらない");
        Check.True(app.Ui(() => recovery.IsWaitingForRetry), "999 ms ではまだ待っている");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1)));
        WaitEvent(app, "device_recreated");
        Check.Equal(created + 2, factory.Requests.Count, "1000 ms で 2 回目が始まる");
        Check.Equal(GpuAdapterRequest.ByLuid(FakeDeviceFactory.DefaultLuid), factory.Requests[^1], "同じアダプター");
        WaitShown(app, canvas);
        Check.Equal(0, Count(app, "warp_fallback"), "warp_fallback が無い");
        var line = Last(app, "device_recreated");
        Check.Equal("hardware", line.GetProperty("adapter").GetString(), "adapter");
        Check.Equal(1000L, line.GetProperty("ms").GetInt64(), "ms");
        Check.True(!app.Ui(() => app.Shell.NoticeBar.IsShowing), "gpu.software が出ない");
    }

    // ================================================================ (3) 2 回続けて失敗

    [Test(Slow = true)]
    public void AC49_3_2回続けて失敗するとWARPに切り替え通知と表示が出て画素がWARPの描画と一致する()
    {
        var reference = ReferenceDisplay(GpuAdapterRequest.Warp);
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var canvas = M<CanvasModule>(app);
        var band = Band(app);
        var recovery = app.Shell.Recovery!;
        EditAndWait(app, canvas, 0);
        EditAndWait(app, canvas, 1);
        factory.FailNextCreates = 2;
        factory.FailRender(Removed, 1, RenderPriority.Display);
        Edit(app, 2);
        var json = Json(app);
        Check.True(UiHost.WaitUntil(() => recovery.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1000)));
        WaitEvent(app, "device_recreated");
        WaitShown(app, canvas);

        Check.Equal(1, factory.Requests.Count(r => r.Kind == GpuAdapterKind.Warp), "WARP を 1 回だけ要求する");
        Check.Equal(GpuAdapterRequest.Warp, factory.Requests[^1], "最後の作成が WARP");
        Check.SequenceEqual(["device_lost", "warp_fallback", "device_recreated"], Events(app, "device_lost", "warp_fallback", "device_recreated"), "ログの順");
        Check.Equal("recreate_failed", Last(app, "warp_fallback").GetProperty("reason").GetString(), "warp_fallback の reason");
        var line = Last(app, "device_recreated");
        Check.Equal("warp", line.GetProperty("adapter").GetString(), "adapter");
        Check.Equal(1000L, line.GetProperty("ms").GetInt64(), "ms");
        Check.Equal(json, Json(app), "EditorSession の JSON が変わらない");
        Check.SequenceEqual(reference, app.Ui(() => canvas.Processed.PresentedImage!.Pixels), "切り替えたあとの表示が WARP で最初から描いたものと一致");
        Check.True(app.Ui(() => app.Shell.RenderReady!.Result.IsWarp), "RenderService が WARP を報告する");

        Notice? shown = app.Ui(() => app.Shell.NoticeBar.Current);
        Check.Equal(NoticeKeys.GpuSoftware, shown?.Key, "gpu.software");
        Check.Equal(NoticeLevel.Warn, shown?.Level, "warn");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(10)));
        Check.Equal(NoticeKeys.GpuSoftware, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "10 秒進めても残る");

        Check.True(UiHost.WaitUntil(() => band.View.StatusText.EndsWith("ソフトウェア描画", StringComparison.Ordinal), Limit), "状態欄: " + app.Ui(() => band.View.StatusText));
        var screens = M<ScreensModule>(app);
        app.Ui(screens.ShowSettings);
        Check.True(UiHost.WaitUntil(() => screens.Settings?.SoftwareRendering.Visibility == Visibility.Visible, Limit), "設定画面に出ない");
        Check.True(Uia.TryFind(Uia.Root(app.Hwnd), "ソフトウェア描画") is not null, "UIA で見つからない");
    }

    [Test(Slow = true)]
    public void FR94_設定画面を開いている間に切り替わっても表示が出る()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var canvas = M<CanvasModule>(app);
        var screens = M<ScreensModule>(app);
        app.Ui(screens.ShowSettings);
        Check.True(UiHost.WaitUntil(() => screens.Settings?.IsLoaded == true, Limit), "設定画面が開かない");
        Check.Equal(Visibility.Collapsed, app.Ui(() => screens.Settings!.SoftwareRendering.Visibility), "ハードウェアのあいだは出ない");
        factory.FailNextCreates = 2;
        factory.FailRender(Removed, 1, RenderPriority.Display);
        Edit(app, 0);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1000)));
        WaitShown(app, canvas);
        Check.True(UiHost.WaitUntil(() => screens.Settings!.SoftwareRendering.Visibility == Visibility.Visible, Limit), "開いている画面に出ない");
    }

    // ================================================================ (4) 書き出し中の消失

    private static string ReferenceExportSha()
    {
        using var app = Start(new FakeDeviceFactory());
        var band = Band(app);
        ExportDone(app, band);
        return Sha(app.Ui(() => band.Auto.Outcome!.Path));
    }

    /// <summary>失敗で抜ける前に、工場の作成が呼ばれるのを少し待つ中身(.tmp が残っている間に作り直しが始まれば、そこで見つかる)。</summary>
    private sealed class SlowToUnwind(IExportPipeline inner, ManualResetEventSlim created) : IExportPipeline
    {
        public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
        {
            try
            {
                return inner.Run(job, output, run);
            }
            catch
            {
                created.Wait(TimeSpan.FromMilliseconds(500));
                throw;
            }
        }

        public ExportCheck Check(ExportRequest job, ExportWritten written) => inner.Check(job, written);
    }

    [Test(Slow = true)]
    public void AC49_4_書き出し中の消失はtmpを消してから作り直し自動でやり直して同じファイルになる()
    {
        var reference = ReferenceExportSha();
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var band = Band(app);
        var created = factory.Requests.Count;
        var tmpAtCreate = new List<int>();
        using var createCalled = new ManualResetEventSlim();
        factory.OnCreate = _ =>
        {
            lock (tmpAtCreate) tmpAtCreate.Add(Tmp(app).Length);
            createCalled.Set();
        };
        app.Ui(() => band.Pipeline = new SlowToUnwind(band.Pipeline!, createCalled));
        factory.FailRender(Removed, 10, RenderPriority.Export);
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        WaitEvent(app, "device_recreated");
        Check.Equal(created + 1, factory.Requests.Count, "作成の呼び出しが 1 回増える");
        lock (tmpAtCreate) Check.SequenceEqual([0], tmpAtCreate, "作成の呼び出しの時点で .tmp が無い");

        // 偽の時計を進めて待つと(10 秒まで)、自動でやり直して完成する
        var waited = TimeSpan.Zero;
        while (app.Ui(() => band.Auto.State) != ExportState.Done && waited < TimeSpan.FromSeconds(10))
        {
            UiHost.Idle();
            app.Ui(() => app.Clock.Advance(AutoExport.Delay));
            waited += AutoExport.Delay;
            UiHost.WaitUntil(() => band.Auto.State is not ExportState.Idle and not ExportState.Running, TimeSpan.FromSeconds(30));
        }
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "やり直して完成する(" + app.Ui(() => band.Auto.Error?.ToString()) + ")");
        Check.Equal(reference, Sha(app.Ui(() => band.Auto.Outcome!.Path)), "SHA-256 が消失の無い実行と一致(INV-8)");
        Check.Equal(1, Count(app, "export_done"), "export_done は 1 行だけ");
        Check.Equal(0, Count(app, "export_fail"), "export_fail が無い");
        Check.Equal(0, Count(app, "export_cancel"), "消失の取り消しは export_cancel に数えない");
        Check.Equal(0, Count(app, "render_fail"), "render_fail が無い");
        Check.Equal(0, Tmp(app).Length, ".tmp が残らない");
        Check.True(!app.Ui(() => app.Shell.NoticeBar.IsShowing), "通知バーに何も出ない");
    }

    [Test(Slow = true)]
    public void AC49_4_表示で消失したとき実行中の書き出しも取り消してやり直す()
    {
        var reference = ReferenceExportSha();
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var band = Band(app);
        factory.OnCreate = _ => Check.Equal(0, Tmp(app).Length, "作成の呼び出しの時点で .tmp が無い");
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        // 書き出しの途中で、表示の依頼が消失を起こす
        Check.True(UiHost.WaitUntil(() => factory.RenderRequests(RenderPriority.Export) >= 3, Limit), "書き出しが始まらない");
        factory.FailRender(Removed, 1, RenderPriority.Display);
        app.Ui(() => app.Shell.Frames.Set(9));
        WaitEvent(app, "device_recreated");
        UiHost.Idle();
        // 消失の前に書き終わっていたらやり直さない。取り消されたときだけ時計を進める
        if (app.Ui(() => band.Auto.State) != ExportState.Done) ExportDone(app, band);
        Check.Equal(reference, Sha(app.Ui(() => band.Auto.Outcome!.Path)), "SHA-256 が一致");
        Check.Equal(1, Count(app, "export_done"), "export_done は 1 行だけ");
        Check.Equal(0, Count(app, "export_fail"), "export_fail が無い");
        Check.Equal(0, Count(app, "export_cancel"), "消失の取り消しは export_cancel に数えない");
    }

    // ================================================================ (5) 完成済みのキャッシュ

    [Test(Slow = true)]
    public void AC49_5_完成済みのキャッシュは消失のあとも書き換わらずエンコーダーを呼ばない()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var band = Band(app);
        var canvas = M<CanvasModule>(app);
        ExportDone(app, band);
        var path = app.Ui(() => band.Auto.Outcome!.Path);
        var sha = Sha(path);
        var time = File.GetLastWriteTimeUtc(path);
        var calls = app.Ui(() => band.Auto.PipelineCalls);
        Check.Equal(1, calls, "エンコーダーの呼び出しが 1 回");

        factory.FailRender(Removed, 1, RenderPriority.Display);
        app.Ui(() => app.Shell.Frames.Set(11));
        WaitEvent(app, "device_recreated");
        Check.True(UiHost.WaitUntil(() => !canvas.Processed.IsBusy && canvas.Processed.PresentedRequest?.K == 11, Limit), "スクラブの表示が出ない");
        Check.True(File.Exists(path), "キャッシュのファイルが消えない");
        Check.Equal(sha, Sha(path), "書き換わらない");
        Check.Equal(time, File.GetLastWriteTimeUtc(path), "最終更新時刻が同じ");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(10)));
        UiHost.Idle();
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "完了のまま");
        Check.True(app.Ui(() => band.Auto.IsReady), "コピーできる");
        Check.Equal(calls, app.Ui(() => band.Auto.PipelineCalls), "エンコーダーの呼び出しが増えない");
    }

    // ================================================================ (6) 起動時の WARP

    private static void CheckStartupWarp(FakeDeviceFactory factory, string reason)
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.HighPerformance, deviceFactory: factory);
        var band = Band(app);
        Check.True(app.Ui(() => app.Shell.RenderReady!.Result.IsWarp), "WARP で起動する");
        Check.Equal(reason, Last(app, "warp_fallback").GetProperty("reason").GetString(), "warp_fallback の reason");
        Check.Equal("warp", Last(app, "app_start").GetProperty("adapter").GetString(), "app_start の adapter");
        Check.SequenceEqual(["warp_fallback", "app_start"], Events(app, "warp_fallback", "app_start"), "warp_fallback は app_start の直前");
        Check.True(UiHost.WaitUntil(() => band.View.Software.Visibility == Visibility.Visible, Limit), "状態欄に出ない");
        Check.True(app.Ui(() => band.View.StatusText).EndsWith("ソフトウェア描画", StringComparison.Ordinal), "状態欄: " + app.Ui(() => band.View.StatusText));
        var screens = M<ScreensModule>(app);
        app.Ui(screens.ShowSettings);
        Check.True(UiHost.WaitUntil(() => screens.Settings?.SoftwareRendering.Visibility == Visibility.Visible, Limit), "設定画面に出ない");
        Check.True(Uia.TryFind(Uia.Root(app.Hwnd), "ソフトウェア描画") is not null, "UIA で見つからない");
        Check.True(!app.Ui(() => app.Shell.NoticeBar.IsShowing), "起動時は通知バーに何も出ない");
    }

    [Test(Slow = true)]
    public void AC49_6_作成に失敗すると理由no_adapterでWARPで起動する() =>
        CheckStartupWarp(new FakeDeviceFactory { Fail = r => r.Kind == GpuAdapterKind.HighPerformance }, "no_adapter");

    [Test(Slow = true)]
    public void AC49_6_確認に失敗すると理由float_rt_unsupportedでWARPで起動する() =>
        CheckStartupWarp(new FakeDeviceFactory { FailStartupCheck = true }, "float_rt_unsupported");

    [Test(Slow = true)]
    public void AC49_6_確認に成功するとハードウェアで起動しソフトウェア描画を出さない()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.HighPerformance, deviceFactory: new FakeDeviceFactory());
        var band = Band(app);
        Check.Equal("hardware", Last(app, "app_start").GetProperty("adapter").GetString(), "app_start の adapter");
        Check.Equal(0, Count(app, "warp_fallback"), "warp_fallback が無い");
        Check.Equal(Visibility.Collapsed, app.Ui(() => band.View.Software.Visibility), "状態欄に出ない");
        var screens = M<ScreensModule>(app);
        app.Ui(screens.ShowSettings);
        Check.True(UiHost.WaitUntil(() => screens.Settings?.IsLoaded == true, Limit), "設定画面が開かない");
        UiHost.Idle();
        Check.Equal(Visibility.Collapsed, app.Ui(() => screens.Settings!.SoftwareRendering.Visibility), "設定画面に出ない");
    }

    [Test]
    public void AC49_6_切り替えの理由は列挙で分かれ検査の例外は確認の失敗に数える()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        GpuFallbackReason? Reason(FakeDeviceFactory f, Func<PassRenderer, GpuCheckResult>? check = null) => thread.Invoke(() =>
        {
            var pick = check is null
                ? GpuSelfTest.CreateWithFallback(GpuAdapterRequest.HighPerformance, f)
                : GpuSelfTest.CreateWithFallback(GpuAdapterRequest.HighPerformance, f.Create, check);
            using (pick.Device)
            using (pick.Renderer)
            {
                return pick.Fallback;
            }
        });
        Check.Equal((GpuFallbackReason?)null, Reason(new FakeDeviceFactory()), "切り替えない");
        Check.Equal((GpuFallbackReason?)GpuFallbackReason.NoAdapter, Reason(new FakeDeviceFactory { FailNextCreates = 1 }), "作れない");
        Check.Equal((GpuFallbackReason?)GpuFallbackReason.FloatRtUnsupported, Reason(new FakeDeviceFactory { FailStartupCheck = true }), "確認に落ちる");
        Check.Equal((GpuFallbackReason?)GpuFallbackReason.FloatRtUnsupported, Reason(new FakeDeviceFactory(), _ => throw new InvalidOperationException("描けない")), "確認が例外");
    }

    // ================================================================ (7) 例外として扱う場合

    private sealed class CountingCrash : ICrashHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Handle(Exception exception, CrashSource source) => Interlocked.Increment(ref _calls);
    }

    /// <summary>クラッシュの入口を数える包みを入れる。</summary>
    private static List<Exception> CountFatal(TestApp app)
    {
        var calls = new List<Exception>();
        app.Ui(() => app.Shell.Recovery!.FatalHandler = calls.Add);
        return calls;
    }

    [Test(Slow = true)]
    public void AC49_7_起動時にWARPも作れないとクラッシュの入口を1回呼ぶ()
    {
        // 本物のつなぎ(AppShell が FatalHandler を CrashHandler の入口につなぐ)を通すので、入口のほうを数える
        var factory = new FakeDeviceFactory { Fail = _ => true };
        var crash = new CountingCrash();
        using var app = TestApp.Start(gpu: GpuAdapterRequest.HighPerformance, deviceFactory: factory, crashFactory: _ => crash);
        Check.True(UiHost.WaitUntil(() => crash.Calls > 0, Limit), "クラッシュの入口が呼ばれない");
        UiHost.Idle();
        Check.Equal(1, crash.Calls, "1 回だけ");
        Check.Equal(0, Count(app, "app_start"), "app_start を書かない");
    }

    [Test(Slow = true)]
    public void AC49_7_切り替えでWARPも作れないとクラッシュの入口を1回呼ぶ()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var calls = CountFatal(app);
        factory.Fail = r => r.Kind == GpuAdapterKind.Warp;
        factory.FailNextCreates = 2;
        factory.FailRender(Removed, 1, RenderPriority.Display);
        Edit(app, 0);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1000)));
        Check.True(UiHost.WaitUntil(() => calls.Count > 0, Limit), "クラッシュの入口が呼ばれない");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(10)));
        UiHost.Idle();
        Check.Equal(1, app.Ui(() => calls.Count), "1 回だけ");
        Check.Equal(0, Count(app, "device_recreated"), "device_recreated が無い");
        Check.Equal(0, Count(app, "render_fail"), "render_fail が無い");
        Check.True(app.Ui(() => app.Shell.NoticeBar.Current?.Key != NoticeKeys.RenderFailed), "render.failed が出ない");
    }

    [Test(Slow = true)]
    public void AC49_7_WARPで2回続けて作り直せないとgpu_softwareを出さずクラッシュの入口を1回呼ぶ()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory, GpuAdapterRequest.Warp);
        var calls = CountFatal(app);
        factory.FailNextCreates = 2;
        factory.FailRender(Removed, 1, RenderPriority.Display);
        Edit(app, 0);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        var requested = factory.Requests.Count;
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1000)));
        Check.True(UiHost.WaitUntil(() => calls.Count > 0, Limit), "クラッシュの入口が呼ばれない");
        UiHost.Idle();
        Check.Equal(1, app.Ui(() => calls.Count), "1 回だけ");
        Check.Equal(requested + 1, factory.Requests.Count, "3 回目を試さない");
        Check.True(app.Ui(() => app.Shell.NoticeBar.Current?.Key != NoticeKeys.GpuSoftware), "gpu.software が出ない");
        Check.Equal(0, Count(app, "warp_fallback"), "warp_fallback が無い");
        Check.Equal(0, Count(app, "device_recreated"), "device_recreated が無い");
    }

    // ================================================================ (8) 描画の失敗(E-41)

    [Test(Slow = true)]
    public void AC49_8_画面の描画のメモリ不足は表示を残しrender_failを書き次の編集で再開する()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var canvas = M<CanvasModule>(app);
        factory.FailRender(Oom, 5, RenderPriority.Display);
        for (var i = 0; i < 4; i++) EditAndWait(app, canvas, i);
        var before = app.Ui(() => canvas.Processed.PresentedImage!.Pixels);
        var bitmap = app.Ui(() => canvas.Picture.Bitmap);
        Edit(app, 4);
        Check.True(UiHost.WaitUntil(() => app.Shell.NoticeBar.Current?.Key == NoticeKeys.RenderFailed, Limit), "render.failed が出ない");
        Check.Equal(NoticeLevel.Error, app.Ui(() => app.Shell.NoticeBar.Current!.Level), "error");
        Check.SequenceEqual(before, app.Ui(() => canvas.Processed.PresentedImage!.Pixels), "表示の画素が変わらない");
        Check.True(app.Ui(() => ReferenceEquals(bitmap, canvas.Picture.Bitmap)), "表示の画像が変わらない");
        var line = Last(app, "render_fail");
        Check.Equal("display", line.GetProperty("target").GetString(), "target");
        Check.Equal("oom", line.GetProperty("reason").GetString(), "reason");
        Check.Equal(0, Count(app, "device_lost"), "消失として扱わない");

        var requests = factory.RenderRequests(RenderPriority.Display);
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(10)));
        UiHost.Idle();
        Check.Equal(requests, factory.RenderRequests(RenderPriority.Display), "自動では再試行しない");

        EditAndWait(app, canvas, 5);
        Check.True(factory.RenderRequests(RenderPriority.Display) > requests, "次の変更で描画が再開する");
        var after = app.Ui(() => canvas.Processed.PresentedImage!.Pixels);
        Check.True(!before.AsSpan().SequenceEqual(after), "表示が更新される");
    }

    [Test(Slow = true)]
    public void AC49_8_サムネイルの描画の失敗はtargetがthumbでE_FAILはgpu_error()
    {
        foreach (var (hr, reason) in new[] { (EFail, "gpu_error"), (Oom, "oom") })
        {
            var factory = new FakeDeviceFactory();
            using var app = Start(factory);
            var canvas = M<CanvasModule>(app);
            // 起動時のサムネイルが落ち着いてから、次のサムネイルの依頼を失敗させる
            UiHost.Idle();
            factory.FailRender(hr, 1, RenderPriority.Thumbnail);
            app.Ui(() => app.Shell.Session.AddEffect(Target.OfLayer(0), "exposure"));
            WaitShown(app, canvas);
            app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(300)));
            WaitEvent(app, "render_fail");
            var line = Last(app, "render_fail");
            Check.Equal("thumb", line.GetProperty("target").GetString(), "target");
            Check.Equal(reason, line.GetProperty("reason").GetString(), "reason");
            Check.True(UiHost.WaitUntil(() => app.Shell.NoticeBar.Current?.Key == NoticeKeys.RenderFailed, Limit), "render.failed が出ない");
            Check.Equal(0, Count(app, "device_lost"), "消失として扱わない");
        }
    }

    [Test(Slow = true)]
    public void AC49_8_画面の描画のE_FAILはreasonがgpu_error()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        factory.FailRender(EFail, 1, RenderPriority.Display);
        Edit(app, 0);
        WaitEvent(app, "render_fail");
        var line = Last(app, "render_fail");
        Check.Equal("display", line.GetProperty("target").GetString(), "target");
        Check.Equal("gpu_error", line.GetProperty("reason").GetString(), "reason");
    }

    // ================================================================ AC-57 の (3) の理由

    private static void CheckExportFailed(TestApp app, ExportBandModule band, string reason)
    {
        Check.Equal(reason, Last(app, "export_fail").GetProperty("reason").GetString(), "export_fail の reason");
        Check.Equal(0, Count(app, "render_fail"), "render_fail が無い");
        Check.Equal(0, Tmp(app).Length, ".tmp が消える");
        Check.True(app.Ui(() => band.View.StatusText).Contains(NoticeTexts(), StringComparison.Ordinal), "状態欄: " + app.Ui(() => band.View.StatusText));
        var calls = app.Ui(() => band.Auto.PipelineCalls);
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(60)));
        UiHost.Idle();
        Check.Equal(calls, app.Ui(() => band.Auto.PipelineCalls), "自動では再試行しない");
    }

    private static string NoticeTexts() => GlintLoop.App.Text.NoticeTexts.Format(NoticeKeys.ExportFailed);

    private static void WaitFailed(TestApp app, ExportBandModule band)
    {
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Failed or ExportState.Done, Limit), "書き出しが終わらない");
        Check.Equal(ExportState.Failed, app.Ui(() => band.Auto.State), "状態");
    }

    [Test(Slow = true)]
    public void AC57_3_書き出し中のE_FAILはgpu_errorで失敗し次の編集でやり直す()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var band = Band(app);
        factory.FailRender(EFail, 10, RenderPriority.Export);
        WaitFailed(app, band);
        CheckExportFailed(app, band, "gpu_error");
        Check.Equal(0, Count(app, "device_lost"), "消失として扱わない");
        var calls = app.Ui(() => band.Auto.PipelineCalls);
        Edit(app, 0);
        UiHost.Idle();
        ExportDone(app, band);
        Check.Equal(calls + 1, app.Ui(() => band.Auto.PipelineCalls), "編集で 1 回だけやり直す");
    }

    private sealed class ThrowingEncoder : IExportEncoder
    {
        public string Format => ExportFormat.Gif;

        public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run) =>
            throw new InvalidDataException("偽のエンコーダーの失敗");
    }

    [Test(Slow = true)]
    public void AC57_3_エンコーダーの例外はencode_error()
    {
        var factory = new FakeDeviceFactory();
        using var app = Start(factory);
        var band = Band(app);
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        app.Ui(() => band.Pipeline = new RenderExportPipeline(renderer, app.Shell.Sources, app.Shell.Catalog, app.Shell.Log, [new ThrowingEncoder()]));
        WaitFailed(app, band);
        CheckExportFailed(app, band, "encode_error");
    }

    [Test(Slow = true)]
    public void AC57_3_キャッシュの書き込みの例外はwrite_error()
    {
        foreach (var op in new[] { FileOps.Create, FileOps.Write, FileOps.Move })
        {
            var fs = new FakeFileSystem();
            using var app = Start(new FakeDeviceFactory(), fileSystem: fs);
            var band = Band(app);
            fs.FailOn(op, p => p.Contains(Path.Combine("cache", "exports"), StringComparison.OrdinalIgnoreCase));
            WaitFailed(app, band);
            CheckExportFailed(app, band, "write_error");
            Check.Equal(1, Count(app, "export_fail"), "export_fail は 1 行だけ: " + op);
        }
    }

    // ================================================================ 描画の窓口の単体

    private static (RenderService Service, DeviceRecovery Recovery, DocumentRenderer Renderer) Unit(FakeDeviceFactory factory, Func<string, SourceImage?>? find = null)
    {
        var service = RenderService.Create(GpuAdapterRequest.HighPerformance, deviceFactory: factory);
        var clock = new FakeClock();
        var recovery = new DeviceRecovery(clock, clock, GlintLoop.Core.Diagnostics.NullLogSink.Instance, a => a());
        recovery.Attach(service);
        return (service, recovery, new DocumentRenderer(service, new FuncSourceImages(find ?? (_ => null))));
    }

    [Test]
    public void FR93_作り直すと古いRenderContextは使えず新しい依頼は新しいデバイスで動きキャッシュは空になる()
    {
        var factory = new FakeDeviceFactory();
        var (service, recovery, renderer) = Unit(factory);
        using (service)
        using (recovery)
        using (renderer)
        {
            var old = service.InvokeAsync(c => c).GetAwaiter().GetResult();
            // 消失の途中で作ったテクスチャを持ち越しても、作り直しのあとに使われない
            var attempts = 0;
            var result = service.InvokeAsync(c =>
            {
                attempts++;
                if (attempts == 1)
                {
                    renderer.Cache.Add("k", c.CreateWorking(4, 4));
                    throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(FakeDeviceFactory.DeviceRemoved));
                }
                return (Same: ReferenceEquals(c, old), Cached: renderer.Cache.Count);
            }).GetAwaiter().GetResult();
            Check.Equal(2, attempts, "消失した依頼を作り直しのあとで 1 回やり直す");
            Check.True(!result.Same, "新しい RenderContext で動く");
            Check.Equal(0, result.Cached, "古いデバイスのテクスチャがキャッシュに残らない");
            var staleThrows = service.InvokeAsync(_ =>
            {
                try
                {
                    old.CreateWorking(4, 4);
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }).GetAwaiter().GetResult();
            Check.True(staleThrows, "古い RenderContext を使うと例外になり、落ちない");
        }
    }

    [Test]
    public void FR93_別のデバイスのテクスチャを描画に渡すと例外になる()
    {
        var factory = new FakeDeviceFactory();
        using var thread = new RenderThread("GlintLoop.Render.Test");
        thread.Invoke(() =>
        {
            using var a = factory.Create(GpuAdapterRequest.Warp);
            using var b = factory.Create(GpuAdapterRequest.Warp);
            using var pa = new PassRenderer(a);
            using var pb = new PassRenderer(b);
            using var ca = new RenderContext(a, pa);
            using var cb = new RenderContext(b, pb);
            using var ta = ca.CreateWorking(4, 4);
            using var tb = cb.CreateWorking(4, 4);
            Check.Throws<InvalidOperationException>(() => cb.Draw(RenderContext.IngestShader, tb, [ta], []), "別のデバイスのテクスチャ");
        });
    }

    [Test]
    public void FR93_待っている間も専用スレッドは止まらず書き出しの依頼はすぐ消失で失敗する()
    {
        var factory = new FakeDeviceFactory();
        var (service, recovery, renderer) = Unit(factory);
        using (service)
        using (recovery)
        using (renderer)
        {
            // 最初のデバイスを作ったあとで指定する(前に指定すると起動の作成が失敗して WARP になり、作り直しがすぐ成功する)
            factory.FailNextCreates = 1;
            factory.FailRender(FakeDeviceFactory.DeviceRemoved, 1);
            var parked = service.InvokeAsync(_ => 1);
            Check.True(SpinWait.SpinUntil(() => service.IsDeviceLost, Limit), "消失を検出しない");
            var export = service.InvokeAsync(_ => 2, RenderPriority.Export);
            var failure = Check.Throws<GpuDeviceLostException>(() => export.GetAwaiter().GetResult(), "書き出しは待たずに失敗する");
            Check.True(failure is not null, "例外");
            Check.True(!parked.IsCompleted, "表示の依頼は作り直しまで待つ");
        }
        Check.True(true, "破棄で固まらない");
    }

    [Test]
    public void FR93_入口を付けないときは消失の例外をそのまま返す()
    {
        var factory = new FakeDeviceFactory();
        using var service = RenderService.Create(GpuAdapterRequest.HighPerformance, deviceFactory: factory);
        factory.FailRender(FakeDeviceFactory.DeviceRemoved, 1);
        var e = Check.Throws<SharpGen.Runtime.SharpGenException>(() => service.InvokeAsync(_ => 1).GetAwaiter().GetResult(), "素の例外");
        Check.True(GpuErrors.IsDeviceLost(e), "消失に分類する");
        Check.Equal(2, service.InvokeAsync(_ => 2).GetAwaiter().GetResult(), "デバイスはそのまま");
    }

    [Test]
    public void E41_失敗の分類はHRESULTで分ける()
    {
        static SharpGen.Runtime.SharpGenException Hr(int hr) => new(new SharpGen.Runtime.Result(hr));
        Check.True(GpuErrors.IsDeviceLost(Hr(Removed)) && GpuErrors.IsDeviceLost(Hr(Reset)), "消失の 2 つ");
        Check.True(!GpuErrors.IsDeviceLost(Hr(EFail)) && !GpuErrors.IsDeviceLost(Hr(Oom)), "消失でない");
        Check.True(GpuErrors.IsDeviceLost(new AggregateException(Hr(Removed))), "包まれていても分かる");
        Check.True(GpuErrors.IsOutOfMemory(Hr(Oom)) && GpuErrors.IsOutOfMemory(new OutOfMemoryException()), "メモリ不足");
        Check.True(!GpuErrors.IsOutOfMemory(Hr(EFail)), "E_FAIL はメモリ不足でない");
        Check.True(GpuErrors.IsGpuError(Hr(EFail)) && !GpuErrors.IsGpuError(new InvalidDataException()), "GPU のエラー");
        Check.True(!GpuErrors.IsRenderFailure(new ObjectDisposedException("x")) && !GpuErrors.IsRenderFailure(new OperationCanceledException()) && !GpuErrors.IsRenderFailure(Hr(Removed)),
            "閉じたあとと取り消しと消失は描画の失敗に数えない");
        Check.True(GpuErrors.IsRenderFailure(Hr(EFail)) && GpuErrors.IsRenderFailure(new InvalidOperationException()), "そのほかは描画の失敗");
    }
}
