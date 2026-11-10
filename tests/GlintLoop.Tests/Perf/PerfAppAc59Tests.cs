// AC-59 の (3)〜(6)・(8)〜(10) のうち、アプリの部品とのつなぎ(render_calls・import_ok と paste・gallery・drag_frame・export・INV-15・失敗のあと)を調べる。
// 画面の外の非アクティブなウィンドウ・WARP・偽の時計・偽のクリップボード・偽のダイアログ・一時フォルダで動かす(INV-18)。記録は一時フォルダにだけ書く。
// 2026-11-06 の決め: import_ok の ms は操作を受け取った時刻から測る。LatestRender で置き換えた依頼は、置き換えた時点で render_calls に 1 足す。
using System.IO;
using GlintLoop.App.Export;
using GlintLoop.App.Viewing;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Exports;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;
using GlintLoop.Tests.Session;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Perf.PerfKit;

namespace GlintLoop.Tests.Perf;

public sealed class PerfAppAc59Tests
{
    private static byte[] Png(int w, int h, int seed = 0)
    {
        var px = Img.Pattern(w, h, alpha: false);
        for (var i = 0; i < px.Length; i += 4) px[i] = (byte)(px[i] + seed);
        return SkiaBytes.Png(w, h, px);
    }

    // ---------------------------------------------------------------- (3) render_calls

    /// <summary>描画の専用スレッドを gate が開くまで止め、そのあと失敗で終わる依頼を出す LatestRender(UI スレッドで作る)。</summary>
    private static LatestRender Blocking(RenderService service, ManualResetEventSlim gate, PerfRecorder perf, Action counted) =>
        UiHost.Invoke(() => new LatestRender((RenderRequest _) =>
        {
            counted();
            return service.InvokeAsync<DocumentImage>(_ =>
            {
                gate.Wait(TimeSpan.FromSeconds(30));
                throw new InvalidOperationException("偽の描画の終わり");
            });
        }) { Replaced = perf.CountRenderCall });

    [Test]
    public void AC59_3_RenderServiceに5件出しうち2件が置き換えられるとrender_callsが5増える()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var perf = Start(perfDir, clock, sample: clock);
        using var service = RenderService.Create(GpuAdapterRequest.Warp);
        service.Perf = perf;
        using var gate = new ManualResetEventSlim();
        var executed = 0;
        var lr = Blocking(service, gate, perf, () => Interlocked.Increment(ref executed));
        var doc = SessionTestKit.WithBase().Document!;

        clock.Advance(TimeSpan.FromMilliseconds(1000));
        UiHost.Invoke(() =>
        {
            lr.Request(doc, 0, 10); // 描き始める(止まる)
            lr.Request(doc, 1, 10); // 待つ
            lr.Request(doc, 2, 10); // 1 を置き換える
            lr.Request(doc, 3, 10); // 2 を置き換える
        });
        gate.Set();
        Check.True(UiHost.WaitUntil(() => !lr.IsBusy, PerfApp.Limit), "描画が終わらない");
        UiHost.Invoke(() => lr.Request(doc, 4, 10));
        Check.True(UiHost.WaitUntil(() => !lr.IsBusy, PerfApp.Limit), "描画が終わらない");
        clock.Advance(TimeSpan.FromMilliseconds(1000));

        var calls = Lines(File(perfDir)).Of("render_calls");
        Check.Equal(2, calls.Count, "render_calls の行数");
        Check.Equal(5L, calls[1].V - calls[0].V, "render_calls の増分");
        Check.Equal(3, Volatile.Read(ref executed), "実行された依頼(置き換えた 2 件は実行しない)");
    }

    [Test]
    public void AC59_3_置き換えがNマイナス1回起きるN件の依頼でrender_callsはNちょうど()
    {
        const int n = 6;
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var perf = Start(perfDir, clock, sample: clock);
        using var service = RenderService.Create(GpuAdapterRequest.Warp);
        service.Perf = perf;
        using var gate = new ManualResetEventSlim();
        var accepted = 0;
        var lr = Blocking(service, gate, perf, () => Interlocked.Increment(ref accepted));
        var doc = SessionTestKit.WithBase().Document!;

        // 1 件目は描き始めて止まり、2〜N 件目がそれぞれ 1 つ前を置き換える(1 件目は結果を捨て、2〜N−1 件目は描画に届かない)
        UiHost.Invoke(() =>
        {
            for (var k = 0; k < n; k++) lr.Request(doc, k, n);
        });
        gate.Set();
        Check.True(UiHost.WaitUntil(() => !lr.IsBusy, PerfApp.Limit), "描画が終わらない");
        clock.Advance(TimeSpan.FromMilliseconds(1000));

        Check.Equal(2, Volatile.Read(ref accepted), "RenderService.InvokeAsync に届いた依頼(1 件目と N 件目)");
        Same([L(1000, "render_calls", n)], Lines(File(perfDir)).Of("render_calls"), "render_calls(二重に数えない)");
    }

    // ---------------------------------------------------------------- (4) startup のつなぎ

    [Test]
    public void AC59_4_メインウィンドウの最初の描画でstartupを1行書く()
    {
        using var a = new PerfApp(gpu: false);
        UiHost.Idle();
        // 偽のプロセス情報の作成時刻は記録の開始の 1 秒前。時計を進めていないので t は 0
        Same([L(0, "startup", 1000)], a.Lines().Of("startup"), "startup の行");
    }

    // ---------------------------------------------------------------- (5) paste と gallery

    [Test]
    public void AC59_5_import_okのmsは操作を受け取った時刻から測りpasteとgalleryを書く()
    {
        using var a = new PerfApp();
        a.AdvanceTo(10_000);
        // 偽のクリップボードの読み取りに 321 ms かかる(読み取りの時間も ms に入る)
        var o = a.Paste(Png(64, 48), readMs: 321);
        Check.Equal(1, o.Added, "取り込めた枚数");
        var ok = a.Log().Where(l => l.GetProperty("event").GetString() == "import_ok").ToList();
        Check.Equal(1, ok.Count, "import_ok の行数");
        Check.Equal(321L, ok[0].GetProperty("ms").GetInt64(), "import_ok の ms");
        Same([L(10_321, "paste", 321)], a.Lines().Of("paste"), "paste の行");

        a.AdvanceTo(11_234);
        a.WaitGallery();
        Same([L(11_234, "gallery", 1234)], a.Lines().Of("gallery"), "gallery の行");
    }

    [Test]
    public void AC59_5_2回目の取り込みで作り直しが替わると2回目の分だけgalleryを書く()
    {
        using var a = new PerfApp();
        a.AdvanceTo(10_000);
        a.Paste(Png(64, 48), readMs: 321);
        a.AdvanceTo(10_500);
        Check.Equal(1, a.Paste(Png(40, 30, seed: 7)).Added, "2 回目に取り込めた枚数");
        a.AdvanceTo(12_000);
        a.WaitGallery();
        var lines = a.Lines();
        Same([L(10_321, "paste", 321), L(10_500, "paste", 0)], lines.Of("paste"), "paste の行");
        Same([L(12_000, "gallery", 1500)], lines.Of("gallery"), "gallery の行(1 回目の分は書かない)");
    }

    [Test]
    public void AC59_5_ユーザーのルックのタイルがあっても組み込みの24個が揃った時点で書く()
    {
        using var a = new PerfApp();
        a.App.Ui(() =>
        {
            Check.Equal(24, a.Looks.BuiltIns.Count, "組み込みのルックの数");
            var effects = a.Looks.BuiltIns[0].Effects;
            a.Looks.Store.Save("ユーザーのルック", effects);
        });
        a.AdvanceTo(10_000);
        a.Paste(Png(64, 48));
        a.AdvanceTo(10_900);
        a.WaitGallery();
        Same([L(10_900, "gallery", 900)], a.Lines().Of("gallery"), "gallery の行");
    }

    // ---------------------------------------------------------------- (6) drag_frame のつなぎ

    [Test]
    public void AC59_6_つまみや枠を押している間の表示の更新でdrag_frameを書く()
    {
        using var a = new PerfApp();
        a.Paste(Png(64, 48));
        a.WaitPresented();
        var session = a.App.Shell.Session;
        var opacity = 100;
        void Update(long ms)
        {
            a.AdvanceTo(ms);
            opacity -= 3;
            a.App.Ui(() => Check.True(session.SetLayerOpacity(0, opacity).Changed, "不透明度が変わらない"));
            a.WaitPresented();
        }

        Update(15_000); // 押していない
        a.App.Ui(session.BeginContinuous); // スライダーのつまみ(Thumb.DragStarted)と枠のドラッグが呼ぶ
        Update(20_000);
        Update(20_016);
        Update(20_040);
        a.App.Ui(session.EndContinuous);
        Update(25_000);
        a.App.Ui(session.BeginContinuous);
        Update(30_000);
        Update(30_030);
        a.App.Ui(session.EndContinuous);

        Same([L(20_016, "drag_frame", 16), L(20_040, "drag_frame", 24), L(30_030, "drag_frame", 30)], a.Lines().Of("drag_frame"), "drag_frame の行");
    }

    // ---------------------------------------------------------------- (8) export

    /// <summary>偽の時計を spend だけ進めてから、1 バイト書くか throws を投げるエンコーダー(フレームを描かない)。</summary>
    private sealed class ClockEncoder(string format, FakeClock clock) : IExportEncoder
    {
        public TimeSpan Spend { get; set; }

        public Exception? Throws { get; set; }

        public string Format => format;

        public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
        {
            clock.UtcNow += Spend;
            if (Throws is { } e) throw e;
            output.WriteByte(1);
            return new EncodeResult(1, false);
        }
    }

    [Test]
    public void AC59_8_書き出しの中身は完成と取り消しと失敗でexport_endを1回書く()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var perf = Start(perfDir, clock);
        using var kit = new ExportRenderKit();
        kit.Import(CompositeImg.Make(16, 16, (x, y) => (x * 16, y * 16, 128, 255)));
        var doc = kit.Doc;
        var encoder = new ClockEncoder(kit.Plan(doc).R0.Format, clock);
        var pipeline = new RenderExportPipeline(kit.Renderer, kit.Sources, kit.Rs.Effects, NullLogSink.Instance, [encoder], perf);
        using var scheduler = new ExportScheduler(ExportScheduler.DefaultThreadCount);
        var run = new ExportRun(scheduler, scheduler.DefaultParallelism, CancellationToken.None);
        ExportPipelineResult Export(long at, long spend, Exception? throws = null)
        {
            AdvanceTo(clock, at);
            encoder.Spend = TimeSpan.FromMilliseconds(spend);
            encoder.Throws = throws;
            return pipeline.Run(new ExportRequest(doc, 0, 1), new MemoryStream(), run);
        }

        var done = Export(40_000, 3210);
        done.Placed!();
        Check.Throws<OperationCanceledException>(() => Export(43_500, 300, new OperationCanceledException()), "取り消し");
        Check.Throws<InvalidDataException>(() => Export(44_000, 100, new InvalidDataException("偽の失敗")), "失敗");
        // 中身が返したあとに置けなかった(リネームの失敗)。そのあとで呼ばれても 2 回目は書かない
        var unplaced = Export(45_000, 50);
        AdvanceTo(clock, 45_070);
        unplaced.Failed!();
        unplaced.Placed!();
        unplaced.Abandoned!();

        Same([L(40_000, "export_begin", 0), L(43_210, "export_end", 3210), L(43_500, "export_begin", 0), L(43_800, "export_end", 300),
            L(44_000, "export_begin", 0), L(44_100, "export_end", 100), L(45_000, "export_begin", 0), L(45_070, "export_end", 70)],
            Lines(File(perfDir)), "書き出しの行");
    }

    /// <summary>画像を取り込み、自動の書き出しを完成させる(取り込みと書き出しが成功したこと)。</summary>
    private static void ImportAndExport(TestApp app, Func<int> paste)
    {
        Check.Equal(1, paste(), "取り込めた枚数");
        var band = EditingScreenKit.M<ExportBandModule>(app);
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, PerfApp.Limit), "本物の中身が入らない");
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, PerfApp.Limit), "書き出しが終わらない");
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "書き出しの状態(" + app.Ui(() => band.Auto.Error?.ToString()) + ")");
    }

    [Test]
    public void AC59_8_自動の書き出しでexport_beginとexport_endを書く()
    {
        using var a = new PerfApp();
        ImportAndExport(a.App, () => a.Paste(Png(64, 48)).Added);
        var lines = a.Lines().Where(l => l.K is "export_begin" or "export_end").ToList();
        Check.SequenceEqual(["export_begin", "export_end"], lines.Select(l => l.K).ToArray(), "書き出しの行の並び");
        Check.Equal(lines[1].T - lines[0].T, lines[1].V, "export_end の v");
    }

    // ---------------------------------------------------------------- (9) 書かないもの

    [Test]
    public void AC59_9_日本語の名前のファイルと日本語のテキストを扱っても記録はASCIIの印字できる文字とLFだけ()
    {
        using var a = new PerfApp();
        var path = a.Dir.Write("日本語の画像 ファイル.png", Png(64, 48));
        a.App.FileDialogs.OpenAnswers.Enqueue([path]);
        var t = a.App.Ui(() => a.Imports.OpenAsync());
        Check.True(UiHost.WaitUntil(() => t.IsCompleted, PerfApp.Limit), "開くが終わらない");
        Check.Equal(1, t.GetAwaiter().GetResult()?.Added, "取り込めた枚数");
        a.App.Ui(() =>
        {
            var s = a.App.Shell.Session;
            Check.True(s.AddEffect(Target.OfLayer(0), "text").Changed, "テキストを足せない");
            var row = s.Document!.Layers[0].Stack.Count - 1;
            Check.True(s.SetEffectParam(Target.OfLayer(0), row, "text", new StringValue("こんにちは、世界")).Changed, "文字列を入れられない");
        });
        a.WaitPresented();
        a.AdvanceTo(3000);

        var bytes = Bytes(File(a.PerfDir));
        Check.True(bytes.Length > 0, "記録が空");
        Check.True(bytes.All(b => b is >= 0x20 and <= 0x7E or 0x0A), "0x20〜0x7E と LF のほかのバイトがある");
        Lines(File(a.PerfDir));
    }

    // ---------------------------------------------------------------- (10) 失敗と寿命

    [Test]
    public void AC59_10_同名のファイルが既にあるときもアプリの取り込みと書き出しが成功する()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        Directory.CreateDirectory(perfDir);
        var name = Path.Combine(perfDir, "perf-20261014-203805.jsonl");
        System.IO.File.WriteAllBytes(name, [0x41, 0x0A]);
        var clock = FakeClock.ForPhase5();
        var perf = PerfRecorder.TryStart("1", perfDir, clock, new FakeFileSystem(), clock, clock, new FakeProcessStats());
        Check.True(perf is null, "同名のファイルがあるのに記録が始まった");
        using var app = TestApp.Start(clock: clock, gpu: GpuAdapterRequest.Warp, perf: perf);
        app.Clipboard.Data = new GlintLoop.Codec.Import.MemoryImportData().With(GlintLoop.Codec.Import.ImportFormatNames.Png, Png(64, 48));
        ImportAndExport(app, () =>
        {
            var screens = EditingScreenKit.M<GlintLoop.App.Screens.ScreensModule>(app);
            var task = app.Ui(() => screens.Imports.PasteAsync());
            Check.True(UiHost.WaitUntil(() => task.IsCompleted, PerfApp.Limit), "取り込みが終わらない");
            return task.GetAwaiter().GetResult()?.Added ?? 0;
        });
        Check.SequenceEqual(new byte[] { 0x41, 0x0A }, System.IO.File.ReadAllBytes(name), "既にあったファイルのバイト列");
        Check.Equal(1, Directory.GetFiles(perfDir).Length, "perf のファイルの数");
    }

    [Test]
    public void AC59_10_書き込みが失敗したあとも取り込みと書き出しが成功しクラッシュも通知もログも出ない()
    {
        using var a = new PerfApp();
        UiHost.Idle();
        a.Fs.FailOn(FileOps.Write, p => p.StartsWith(a.PerfDir, StringComparison.OrdinalIgnoreCase));
        ImportAndExport(a.App, () => a.Paste(Png(64, 48)).Added);
        Check.True(!a.Perf.IsRecording, "記録が止まらない");
        Check.Equal(0, a.App.CrashDialog.Shown.Count, "クラッシュのダイアログ");
        Check.Equal(0, a.App.ProcessExit.Codes.Count, "プロセスの終了の依頼");
        Check.True(a.App.Ui(() => a.App.Shell.NoticeBar.Current) is null, "通知バーに表示が出た: " + a.App.Ui(() => a.App.Shell.NoticeBar.Current?.Key));
        var log = a.Log();
        Check.True(log.All(l => l.GetProperty("event").GetString() is not ("crash" or "app_exit")), "クラッシュか終了のログがある");
        Check.True(log.All(l => !l.GetRawText().Contains("perf", StringComparison.OrdinalIgnoreCase)), "ログに記録に関する行がある");
        Check.True(log.Any(l => l.GetProperty("event").GetString() == "import_ok") && log.Any(l => l.GetProperty("event").GetString() == "export_done"),
            "取り込みと書き出しのログが無い");
        Check.Equal(0, Lines(File(a.PerfDir)).Count(l => l.K is "paste" or "export_begin" or "export_end"), "止まったあとの行");
    }
}
