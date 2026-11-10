// 渡す操作(AC-42〜AC-44)のテストの道具: 画面の外に立てたアプリに、偽の時計(2026-10-14 20:38:05)・偽のファイルシステム・偽のクリップボード・手動で完成を合図できる偽の書き出しの中身を入れる。
// 中身の形式は GIF(12,345 バイト。N=40、フレーム 40)と PNG(k ごとの静止画)。押す操作はボタンの Click を発生させ、ログはログのファイルを読み直す。
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlintLoop.App.Export;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

/// <summary>形式と k で中身と結果を返す偽の書き出しの中身。Gate(形式ごと)が閉じている間は書き終えない。</summary>
internal sealed class HandoffPipeline : IExportPipeline
{
    private int _calls;
    private readonly object _gate = new();
    private readonly List<ExportRequest> _jobs = new();

    public Func<ExportRequest, byte[]> Content { get; set; } = job => job.Format == ExportFormat.Png ? HandoffRig.StillPng(job.FrameK) : HandoffRig.Gif;

    public SemaphoreSlim? GifGate { get; set; }

    public SemaphoreSlim? PngGate { get; set; }

    public Func<ExportRequest, Exception?>? Throw { get; set; }

    public bool SizeFail { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<ExportRequest> Jobs
    {
        get
        {
            lock (_gate) return _jobs.ToArray();
        }
    }

    public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
    {
        Interlocked.Increment(ref _calls);
        lock (_gate) _jobs.Add(job);
        run.Cancel.ThrowIfCancellationRequested();
        var gate = job.Format == ExportFormat.Png ? PngGate : GifGate;
        gate?.Wait(run.Cancel);
        if (Throw?.Invoke(job) is { } e) throw e;
        var bytes = Content(job);
        output.Write(bytes);
        var frames = ExportFormat.IsAnimated(job.Format) ? HandoffRig.GifFrames : 1;
        return new ExportPipelineResult(48, 32, frames, 20, null, SizeFail, null);
    }

    public ExportCheck Check(ExportRequest job, ExportWritten written) => new(null, null);
}

internal sealed class HandoffRig : IDisposable
{
    public const int GifBytes = 12345;
    public const int StillBytes = 4096;
    public const int GifFrames = 40;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>GIF のキャッシュのファイル(12,345 バイト)。</summary>
    public static readonly byte[] Gif = MakeGif();

    private static byte[] MakeGif()
    {
        var b = new byte[GifBytes];
        "GIF89a"u8.CopyTo(b);
        for (var i = 6; i < b.Length; i++) b[i] = (byte)(i * 31);
        return b;
    }

    /// <summary>フレーム k の PNG の静止画(末尾を埋めて 4,096 バイトにする。k で画素が変わる)。</summary>
    public static byte[] StillPng(int k)
    {
        var png = SkiaBytes.Png(4, 4, BandTestKit.Solid(4, 4, (byte)k, 20, 30));
        var o = new byte[StillBytes];
        png.CopyTo(o, 0);
        return o;
    }

    private HandoffRig(TestApp app, ExportBandModule band, HandoffPipeline pipeline, TempDir dir, string source)
    {
        App = app;
        Band = band;
        Pipeline = pipeline;
        Dir = dir;
        Source = source;
    }

    public TestApp App { get; }

    public ExportBandModule Band { get; }

    public ExportHandoff Handoff => Band.Handoff;

    public ExportBandView View => Band.View;

    public HandoffPipeline Pipeline { get; }

    public TempDir Dir { get; }

    /// <summary>取り込んだ PNG のパス(名前に日本語・絵文字・空白がある)。</summary>
    public string Source { get; }

    public FakeFileSystem Fs => App.FileSystem;

    public EditorSession Session => App.Shell.Session;

    public string HandoffDir => App.Paths.CacheHandoffDir;

    /// <summary>GIF・モーションあり(0 → N=40)の文書と、偽の中身が入ったアプリ。書き出しはまだ始まらない(600 ms を進める前)。</summary>
    public static HandoffRig Start(CopyModeSetting mode, string format = ExportFormat.Gif, HandoffPipeline? pipeline = null)
    {
        pipeline ??= new HandoffPipeline();
        var app = TestApp.Start(clock: FakeClock.ForPhase5());
        var dir = new TempDir();
        var source = dir.Write("元の 画像\U0001F600.png", SkiaBytes.Png(48, 32, Img.Pattern(48, 32, alpha: false)));
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        Check.Equal(1, importer.Drop(new MemoryImportData().WithFiles(source)).Added, "取り込めた枚数");
        UiHost.Idle();
        var band = app.Ui(() => app.Shell.Modules.OfType<ExportBandModule>().Single());
        app.Ui(() =>
        {
            app.Shell.Settings.CopyMode = mode;
            var s = app.Shell.Session;
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
            s.SetLength10(20);
            s.SetFps(20);
            s.SetOutputFormat(format);
            band.Auto.Environment = BandTestKit.Env();
            band.Pipeline = pipeline;
        });
        UiHost.Idle();
        return new HandoffRig(app, band, pipeline, dir, source);
    }

    public void Dispose()
    {
        App.Dispose();
        Dir.Dispose();
    }

    /// <summary>待ちの仕事を済ませてから、偽の時計を進める(UI スレッドで)。</summary>
    public void Advance(double ms)
    {
        UiHost.Idle();
        App.Ui(() => App.Clock.Advance(TimeSpan.FromMilliseconds(ms)));
        UiHost.Idle();
    }

    public void StartExport() => Advance(600);

    public void WaitDone(string what = "書き出し") =>
        Check.True(UiHost.WaitUntil(() => Band.Auto.State == ExportState.Done, Limit), what + "が完了しない: " + App.Ui(() => Band.Auto.State + " " + Band.Auto.Error));

    /// <summary>600 ms 進めて完成するまで待つ。</summary>
    public void Export()
    {
        StartExport();
        WaitDone();
    }

    public void Click(Button button) => App.Ui(() =>
    {
        Check.True(button.IsEnabled, button.Content + " が無効");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    });

    public void Copy() => Click(View.Copy);

    public void Save() => Click(View.Save);

    /// <summary>渡す操作と書き出しが落ち着くまで待つ(待機は残っていてよい)。</summary>
    public void Settle(string what = "渡す操作")
    {
        Check.True(UiHost.WaitUntil(() => Handoff.Busy is null && Band.Auto.Idle.IsCompleted, Limit), what + "が終わらない");
        UiHost.Idle();
    }

    /// <summary>静止画の依頼が門の前に来るまで待つ(門は閉じたまま)。</summary>
    public void Settle0(SemaphoreSlim gate)
    {
        Check.True(UiHost.WaitUntil(() => Pipeline.Jobs.Any(j => j.Format == ExportFormat.Png) && gate.CurrentCount == 0 && Handoff.Busy is null, Limit), "静止画の依頼が来ない");
    }

    /// <summary>ドラッグ欄で押して、しきい値(4)を超えて動かす。</summary>
    public void Drag() => App.Ui(() =>
    {
        View.DragPress(new Point(10, 10));
        View.DragMove(new Point(15, 10), 4, 4);
    });

    /// <summary>出ている上限超過の確認ダイアログ。</summary>
    public ExportConfirmOverView WaitConfirm()
    {
        Check.True(UiHost.WaitUntil(() => App.Shell.Dialogs.Top is ExportConfirmOverView, Limit), "上限超過の確認が出ない");
        return App.Ui(() => (ExportConfirmOverView)App.Shell.Dialogs.Top!);
    }

    public string[] HandoffFiles() => Directory.Exists(HandoffDir)
        ? Directory.GetFiles(HandoffDir).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray()
        : [];

    public string Notice() => App.Ui(() => App.Shell.NoticeBar.IsShowing ? App.Shell.NoticeBar.Text : "");

    /// <summary>ログの event の行(古い順)。</summary>
    public List<JsonElement> Logs(string name)
    {
        var result = new List<JsonElement>();
        if (!Directory.Exists(App.Paths.LogsDir)) return result; // まだ 1 行も書いていない
        foreach (var f in Directory.GetFiles(App.Paths.LogsDir, "*.jsonl"))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.GetProperty("event").GetString() == name) result.Add(doc.RootElement.Clone());
            }
        }
        return result;
    }

    public string LogText()
    {
        var text = "";
        foreach (var f in Directory.GetFiles(App.Paths.LogsDir, "*.jsonl"))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            text += r.ReadToEnd();
        }
        return text;
    }
}
