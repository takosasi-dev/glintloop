// AC-59 の (1)〜(4)・(6)〜(8)・(10) のうち、記録の部品(PerfRecorder)だけで決まるもの。偽の時計・偽のプロセス情報・偽の環境変数の値・偽のタイマー・一時フォルダで調べる。
// 偽の時計は 2026-10-14 20:38:05.000(ローカル +9)。記録の経過時間は 0 ms から始まる。製品の exe もアプリの画面も使わない。
// 2 つ目のインスタンスの確かめ(10)は、AC-51 と同じ実際のミューテックスとパイプ(名前は実行ごとの GUID)で行う。
using System.IO;
using GlintLoop.App.Startup;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Perf.PerfKit;

namespace GlintLoop.Tests.Perf;

public sealed class PerfRecorderAc59Tests
{
    private const string FileName = "perf-20261014-203805.jsonl";

    // ---------------------------------------------------------------- (1) 有効にする条件

    [Test]
    public void AC59_1_GLINT_PERFが1と一致しないときはフォルダもファイルもタイマーも作らない()
    {
        foreach (var env in new string?[] { null, "", "0", "true", "01", "1 " })
        {
            using var dir = new TempDir();
            var perfDir = Path.Combine(dir.Path, "perf");
            var clock = FakeClock.ForPhase5();
            var fs = new FakeFileSystem();
            var sample = new CountingTimers(clock);
            var ui = new CountingTimers(clock);
            var r = PerfRecorder.TryStart(env, perfDir, clock, fs, sample, ui, new FakeProcessStats());
            var what = env is null ? "未設定" : $"「{env}」";
            Check.True(r is null, what + "で記録が始まった");
            Check.Equal(0, fs.Count(FileOps.CreateDirectory), what + "のフォルダの作成");
            Check.Equal(0, fs.Count(FileOps.Create) + fs.Count(FileOps.OpenAppend), what + "のファイルの作成");
            Check.Equal(0, sample.Created + ui.Created, what + "のタイマーの生成");
            Check.True(!Directory.Exists(perfDir), what + "で perf フォルダがある");
        }
    }

    [Test]
    public void AC59_1_GLINT_PERFが1のときperfフォルダと起動時刻の名前のファイルができる()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        var sample = new CountingTimers(new FakeClock());
        var ui = new CountingTimers(new FakeClock());
        using var r = PerfRecorder.TryStart("1", perfDir, clock, RealFileSystem(), sample, ui, new FakeProcessStats());
        Check.True(r is not null, "記録が始まらない");
        Check.True(Directory.Exists(perfDir), "perf フォルダが無い");
        Check.SequenceEqual([FileName], Directory.GetFiles(perfDir).Select(Path.GetFileName).ToArray(), "ファイルの名前");
        Check.Equal(0L, r!.NowMs, "記録の経過時間の始まり");
        Check.Equal(1, sample.Created, "1 秒ごとの行のタイマー");
        Check.Equal(1, ui.Created, "UI スレッドのタイマー");
    }

    private static GlintLoop.Core.Platform.IFileSystem RealFileSystem() => GlintLoop.Core.Platform.RealFileSystem.Instance;

    // ---------------------------------------------------------------- (2) 形式

    [Test]
    public void AC59_2_BOMとCRが無く全行が正規表現に一致し1行ごとにフラッシュする()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        var fs = new FlushCountingFileSystem(RealFileSystem());
        using var r = Start(perfDir, clock, fs);
        r.Write("mem", 1234);
        clock.Advance(TimeSpan.FromMilliseconds(7.9));
        r.Write("paste", 321);
        r.Write("zzz", 1);
        r.Write("cpu", -5);
        r.ExportEnd(r.ExportBegin());

        var lines = Lines(File(perfDir));
        Same([L(0, "mem", 1234), L(7, "paste", 321), L(7, "cpu", 0), L(7, "export_begin", 0), L(7, "export_end", 0)], lines, "行");
        Check.Equal(lines.Count, fs.Flushes, "フラッシュの回数");
    }

    [Test]
    public void AC59_2_4つのスレッドから1000回ずつ同時に書いても4000行が混ざらない()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        var fs = new FlushCountingFileSystem(RealFileSystem());
        using var r = Start(perfDir, clock, fs);
        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            go.Wait();
            for (var n = 0; n < 1000; n++) r.Write("ui_stall", i * 100000 + n);
        })).ToArray();
        foreach (var t in threads) t.Start();
        go.Set();
        foreach (var t in threads) Check.True(t.Join(TimeSpan.FromSeconds(30)), "書き込みのスレッドが終わらない");

        var lines = Lines(File(perfDir));
        Check.Equal(4000, lines.Count, "行数");
        Check.Equal(4000, lines.Select(l => l.V).Distinct().Count(), "値の種類(行が混ざっていない)");
        Check.Equal(4000, fs.Flushes, "フラッシュの回数");
    }

    // ---------------------------------------------------------------- (3) 1 秒ごとの行

    [Test]
    public void AC59_3_1000msごとにmemとcpuとrender_callsの3行を書く()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        var process = new FakeProcessStats { WorkingSetBytes = 1_234_567_890, TotalProcessorTime = TimeSpan.FromMilliseconds(12_345.9) };
        using var r = Start(perfDir, clock, sample: clock, process: process);
        for (var i = 0; i < 7; i++) r.CountRenderCall();
        for (var i = 0; i < 3; i++) clock.Advance(TimeSpan.FromMilliseconds(1000));

        var expected = new[] { 1000L, 2000, 3000 }.SelectMany(t => new[] { L(t, "mem", 1234), L(t, "cpu", 12345), L(t, "render_calls", 7) }).ToArray();
        Same(expected, Lines(File(perfDir)), "9 行");
    }

    [Test]
    public void AC59_3_memは1000000バイト単位で切り捨てる()
    {
        foreach (var (bytes, mb) in new[] { (999_999L, 0L), (1_000_000L, 1L) })
        {
            using var dir = new TempDir();
            var perfDir = Path.Combine(dir.Path, "perf");
            var clock = FakeClock.ForPhase5();
            using var r = Start(perfDir, clock, sample: clock, process: new FakeProcessStats { WorkingSetBytes = bytes });
            clock.Advance(TimeSpan.FromMilliseconds(1000));
            Check.Equal(mb, Lines(File(perfDir)).Of("mem").Single().V, $"{bytes} バイトの mem");
        }
    }

    // ---------------------------------------------------------------- (4) startup

    [Test]
    public void AC59_4_startupはプロセスの作成時刻からContentRenderedまで()
    {
        var created = new DateTimeOffset(2026, 10, 14, 20, 38, 4, 200, TimeSpan.FromHours(9));
        Check.SequenceEqual([L(350, "startup", 1150)], Startup(created, 350), "作成時刻が前");
        Check.SequenceEqual([L(350, "startup", 0)], Startup(new DateTimeOffset(2026, 10, 14, 20, 38, 6, 0, TimeSpan.FromHours(9)), 350), "作成時刻が後");
    }

    private static string[] Startup(DateTimeOffset created, long renderedMs)
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var r = Start(perfDir, clock, process: new FakeProcessStats { StartTimeUtc = created });
        AdvanceTo(clock, renderedMs);
        r.ContentRendered();
        r.ContentRendered(); // 2 回目は書かない(起動のたびに 1 行)
        return Lines(File(perfDir)).Select(l => l.ToString()).ToArray();
    }

    // ---------------------------------------------------------------- (5) gallery の打ち切り(部品だけ)

    [Test]
    public void AC59_5_作り直しが途中で次の作り直しに替わると前の取り込みのgalleryは書かない()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var r = Start(perfDir, clock);
        AdvanceTo(clock, 10_000);
        r.ImportApplied(((GlintLoop.Core.Platform.IClock)clock).Monotonic);
        AdvanceTo(clock, 10_400);
        r.GalleryRebuildStarted();
        AdvanceTo(clock, 10_500);
        r.ImportApplied(((GlintLoop.Core.Platform.IClock)clock).Monotonic);
        AdvanceTo(clock, 10_800);
        r.GalleryRebuildStarted(); // 1 回目の作り直しは揃う前に打ち切られた
        AdvanceTo(clock, 12_000);
        r.GalleryBuiltInsDone();
        r.GalleryBuiltInsDone(); // 同じ作り直しで 2 回目は書かない
        Same([L(12_000, "gallery", 1500)], Lines(File(perfDir)), "gallery の行");
    }

    // ---------------------------------------------------------------- (6) drag_frame(部品だけ)

    [Test]
    public void AC59_6_押している間の2回目以降の表示の更新で1つ前との差を書き押し直すと数え直す()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var r = Start(perfDir, clock);
        void At(long ms)
        {
            AdvanceTo(clock, ms);
            r.DisplayUpdated();
        }

        At(15_000); // 押していない
        r.SetDragging(true);
        At(20_000);
        At(20_016);
        At(20_040);
        r.SetDragging(false);
        At(25_000); // 離したあと
        r.SetDragging(true);
        At(30_000); // 20,040 との差の 9,960 は書かない
        At(30_030);
        r.SetDragging(false);
        r.SetDragging(true); // 枠を押す
        At(50_000);
        At(50_020);
        r.SetDragging(false);

        Same([L(20_016, "drag_frame", 16), L(20_040, "drag_frame", 24), L(30_030, "drag_frame", 30), L(50_020, "drag_frame", 20)], Lines(File(perfDir)), "drag_frame の行");
    }

    // ---------------------------------------------------------------- (7) ui_stall

    [Test]
    public void AC59_7_UIのタイマーの間隔から32を引いた値が100以上のときだけ書く()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var r = Start(perfDir, clock);
        foreach (var ms in new long[] { 0, 32, 64, 195, 326, 458, 590, 2590 })
        {
            AdvanceTo(clock, ms);
            r.UiTick();
        }
        Same([L(458, "ui_stall", 100), L(590, "ui_stall", 100), L(2590, "ui_stall", 1968)], Lines(File(perfDir)), "ui_stall の行");
        Check.True(PerfRecorder.UiInterval == TimeSpan.FromMilliseconds(32), "UI のタイマーの周期");
    }

    // ---------------------------------------------------------------- (8) export(部品だけ)

    [Test]
    public void AC59_8_export_endはexport_beginを書いた時刻からの差()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        using var r = Start(perfDir, clock);
        foreach (var (begin, end) in new[] { (40_000L, 43_210L), (43_500L, 43_800L), (44_000L, 44_100L) })
        {
            AdvanceTo(clock, begin);
            var t = r.ExportBegin();
            AdvanceTo(clock, end);
            r.ExportEnd(t);
        }
        Same([L(40_000, "export_begin", 0), L(43_210, "export_end", 3210), L(43_500, "export_begin", 0), L(43_800, "export_end", 300),
            L(44_000, "export_begin", 0), L(44_100, "export_end", 100)], Lines(File(perfDir)), "書き出しの行");
    }

    // ---------------------------------------------------------------- (10) 失敗と寿命

    [Test]
    public void AC59_10_同名のファイルが既にあるときは記録を始めずファイルを変えない()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        Directory.CreateDirectory(perfDir);
        var old = new byte[] { 0x7B, 0x7D, 0x0A, 0x00, 0xFF };
        System.IO.File.WriteAllBytes(Path.Combine(perfDir, FileName), old);
        var clock = FakeClock.ForPhase5();
        var r = PerfRecorder.TryStart("1", perfDir, clock, RealFileSystem(), new FakeClock(), new FakeClock(), new FakeProcessStats());
        Check.True(r is null, "同名のファイルがあるのに記録が始まった");
        Check.SequenceEqual(old, System.IO.File.ReadAllBytes(Path.Combine(perfDir, FileName)), "既にあったファイルのバイト列");
    }

    [Test]
    public void AC59_10_書き込みでIOExceptionが起きると記録が止まり例外は外に出ない()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var clock = FakeClock.ForPhase5();
        var fs = new FakeFileSystem();
        using var r = Start(perfDir, clock, fs, sample: clock, ui: clock);
        r.Write("paste", 1);
        fs.FailOn(FileOps.Write, p => p.EndsWith(FileName, StringComparison.Ordinal));
        r.Write("paste", 2);
        Check.True(!r.IsRecording, "記録が止まらない");
        var failedWrites = fs.Count(FileOps.Write);
        r.Write("paste", 3);
        r.ExportEnd(r.ExportBegin());
        r.UiTick();
        clock.Advance(TimeSpan.FromSeconds(5));
        Check.Equal(failedWrites, fs.Count(FileOps.Write), "止まったあとに書こうとした");
        Same([L(0, "paste", 1)], Lines(File(perfDir)), "止まる前の行だけが残る");
    }

    [Test]
    public void AC59_10_古いファイルが30個あっても消さずに31個になる()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        Directory.CreateDirectory(perfDir);
        for (var i = 0; i < 30; i++)
            System.IO.File.WriteAllBytes(Path.Combine(perfDir, $"perf-202601{(i / 24) + 1:00}-{i % 24:00}0000.jsonl"), [0x0A]);
        using var r = Start(perfDir, FakeClock.ForPhase5());
        r.Write("paste", 1);
        Check.Equal(31, Directory.GetFiles(perfDir).Length, "perf のファイルの数");
    }

    [Test]
    public void AC59_10_2つ目のインスタンスは記録を始めずperfのファイルが増えない()
    {
        using var dir = new TempDir();
        var perfDir = Path.Combine(dir.Path, "perf");
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var starts = 0;
        PerfRecorder? StartPerf()
        {
            Interlocked.Increment(ref starts);
            return PerfRecorder.TryStart("1", perfDir, FakeClock.ForPhase5(), RealFileSystem(), new FakeClock(), new FakeClock(), new FakeProcessStats());
        }

        var first = new BlockingApp();
        var firstPorts = new StartupPorts(sid, _ => first) { StartPerf = StartPerf };
        var firstThread = new Thread(() => AppStartup.Run([], firstPorts)) { IsBackground = true };
        firstThread.Start();
        try
        {
            Check.True(first.Running.Wait(TimeSpan.FromSeconds(10)), "1 つ目の起動が始まらない");
            Check.Equal(1, Directory.GetFiles(perfDir).Length, "1 つ目の起動の記録のファイル");

            // 2 つ目は時計を 1 秒進めた名前になるはずだが、判定に負けるので記録を始めない
            var secondPorts = new StartupPorts(sid, _ => throw new InvalidOperationException("2 つ目の起動がアプリを作った")) { StartPerf = StartPerf };
            var code = -1;
            var second = new Thread(() => code = AppStartup.Run([], secondPorts)) { IsBackground = true };
            second.Start();
            Check.True(second.Join(TimeSpan.FromSeconds(10)), "2 つ目の起動が戻らない");
            Check.Equal(0, code, "2 つ目の起動の終了コード");
            Check.Equal(1, Volatile.Read(ref starts), "記録を始めた回数");
            Check.Equal(1, Directory.GetFiles(perfDir).Length, "perf のファイルの数");
        }
        finally
        {
            first.Stop();
            firstThread.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>1 つ目の起動で動いているアプリの偽物(Stop まで Run が戻らない)。</summary>
    private sealed class BlockingApp : IRunningApp
    {
        private readonly ManualResetEventSlim _stop = new();

        public readonly ManualResetEventSlim Running = new();

        public ICrashHandler Crash { get; } = new NoCrash();

        public IUnhandledExceptionSource Exceptions { get; } = new FakeExceptionSource();

        public void ReceiveFromSecondInstance(string? path)
        {
        }

        public int Run()
        {
            Running.Set();
            _stop.Wait(TimeSpan.FromSeconds(120));
            return 0;
        }

        public void Stop() => _stop.Set();

        public void Dispose()
        {
        }

        private sealed class NoCrash : ICrashHandler
        {
            public void Handle(Exception exception, CrashSource source)
            {
            }
        }
    }
}
