// AC-59 の (1)〜(10) のテストの道具: 偽のプロセス情報・作成を数えるタイマー・フラッシュを数えるファイルシステム・記録の読み取り。
// 記録は一時フォルダにだけ書く(INV-18)。偽の時計は 2026-10-14 20:38:05.000(ローカル +9)から始め、記録の経過時間は 0 ms から始まる。
// PerfApp は、記録を差し込んだ画面の外のアプリ(WARP)と、取り込み・待ち合わせの操作をまとめる。
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlintLoop.App.Looks;
using GlintLoop.App.Platform;
using GlintLoop.App.Screens;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Perf;

/// <summary>決まった値を返す偽のプロセス情報。</summary>
internal sealed class FakeProcessStats : IProcessStats
{
    public DateTimeOffset StartTimeUtc { get; set; } = FakeClock.Phase5Start - TimeSpan.FromSeconds(1);

    public long WorkingSetBytes { get; set; }

    public TimeSpan TotalProcessorTime { get; set; }
}

/// <summary>作ったタイマーの数を数える(中身は渡した偽の時計のタイマー)。</summary>
internal sealed class CountingTimers(ITimerFactory inner) : ITimerFactory
{
    public int Created { get; private set; }

    public IOneShotTimer Create()
    {
        Created++;
        return inner.Create();
    }
}

/// <summary>中のファイルシステムに素通しし、OpenAppend で開いたストリームの Flush を数える。</summary>
internal sealed class FlushCountingFileSystem(IFileSystem inner) : IFileSystem
{
    private int _flushes;

    public int Flushes => Volatile.Read(ref _flushes);

    public Stream OpenRead(string path) => inner.OpenRead(path);

    public Stream Create(string path) => inner.Create(path);

    public Stream OpenAppend(string path) => new Counted(inner.OpenAppend(path), this);

    public void Move(string source, string destination) => inner.Move(source, destination);

    public void Delete(string path) => inner.Delete(path);

    public bool Exists(string path) => inner.Exists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern) => inner.EnumerateFiles(directory, pattern);

    public long GetLength(string path) => inner.GetLength(path);

    public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

    public DateTime GetLastWriteUtc(string path) => inner.GetLastWriteUtc(path);

    public void SetLastWriteUtc(string path, DateTime utc) => inner.SetLastWriteUtc(path, utc);

    private sealed class Counted(Stream s, FlushCountingFileSystem owner) : Stream
    {
        public override bool CanRead => s.CanRead;

        public override bool CanSeek => s.CanSeek;

        public override bool CanWrite => s.CanWrite;

        public override long Length => s.Length;

        public override long Position
        {
            get => s.Position;
            set => s.Position = value;
        }

        public override void Flush()
        {
            Interlocked.Increment(ref owner._flushes);
            s.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => s.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => s.Seek(offset, origin);

        public override void SetLength(long value) => s.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => s.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => s.Write(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing) s.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>読み取りの中で偽の時計を進めてから、データを返すクリップボード(取り込みの操作を受け取ってから読むまでの時間)。</summary>
internal sealed class SlowClipboard(FakeClock clock, TimeSpan readTime, IImportData data) : IClipboardService
{
    public IImportData Read()
    {
        clock.UtcNow += readTime;
        return data;
    }

    public void Place(System.Windows.IDataObject data, bool keepAfterExit) => throw new NotSupportedException("貼り付けのテストでは置かない");
}

internal readonly record struct PerfLine(long T, string K, long V)
{
    public override string ToString() => $"{{\"t\":{T},\"k\":\"{K}\",\"v\":{V}}}";
}

internal static partial class PerfKit
{
    [GeneratedRegex("^\\{\"t\":([0-9]+),\"k\":\"([a-z_]+)\",\"v\":([0-9]+)\\}$")]
    public static partial Regex LinePattern();

    /// <summary>記録を始める(GLINT_PERF=1)。sample は 1 秒ごとの行のタイマー、ui は ui_stall のタイマー(省くと鳴らない偽の時計)。</summary>
    public static PerfRecorder Start(string perfDir, FakeClock clock, IFileSystem? fs = null, ITimerFactory? sample = null, ITimerFactory? ui = null, IProcessStats? process = null) =>
        PerfRecorder.TryStart("1", perfDir, clock, fs ?? RealFileSystem.Instance, sample ?? new FakeClock(), ui ?? new FakeClock(), process ?? new FakeProcessStats())
        ?? throw new CheckFailedException("記録が始まらない");

    /// <summary>フォルダの記録のファイル(1 つだけのはず)。</summary>
    public static string File(string perfDir)
    {
        var files = Directory.GetFiles(perfDir, "perf-*.jsonl");
        Check.Equal(1, files.Length, "記録のファイルの数");
        return files[0];
    }

    /// <summary>書いている途中のファイルも読む(書き手は共有を許して開いている)。</summary>
    public static byte[] Bytes(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>全行を読み、形式(BOM なし・CR なし・各行が正規表現に一致・最後も LF)を確かめて返す。</summary>
    public static List<PerfLine> Lines(string path)
    {
        var bytes = Bytes(path);
        Check.True(bytes.Length < 3 || !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "先頭に BOM がある");
        Check.True(!bytes.Contains((byte)0x0D), "0x0D を含む");
        if (bytes.Length == 0) return [];
        Check.Equal((byte)0x0A, bytes[^1], "最後の行が LF で終わる");
        var text = Encoding.ASCII.GetString(bytes);
        var list = new List<PerfLine>();
        foreach (var line in text[..^1].Split('\n'))
        {
            var m = LinePattern().Match(line);
            Check.True(m.Success, "正規表現に一致しない行: " + line);
            list.Add(new PerfLine(long.Parse(m.Groups[1].Value), m.Groups[2].Value, long.Parse(m.Groups[3].Value)));
        }
        return list;
    }

    public static List<PerfLine> Of(this List<PerfLine> lines, string kind) => lines.Where(l => l.K == kind).ToList();

    public static void Same(IReadOnlyList<string> expected, IReadOnlyList<PerfLine> actual, string what) =>
        Check.SequenceEqual(expected, actual.Select(l => l.ToString()).ToArray(), what);

    public static string L(long t, string k, long v) => new PerfLine(t, k, v).ToString();

    /// <summary>偽の時計を、記録を始めてから ms の時点まで進める(戻さない)。</summary>
    public static void AdvanceTo(FakeClock clock, long ms)
    {
        var target = FakeClock.Phase5Start + TimeSpan.FromMilliseconds(ms);
        Check.True(target >= clock.UtcNow, $"時計を戻せない({ms} ms)");
        clock.Advance(target - clock.UtcNow);
    }
}

/// <summary>記録を差し込んだ画面の外のアプリ(WARP)。記録は別の一時フォルダに書く。</summary>
internal sealed class PerfApp : IDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    public PerfApp(FakeFileSystem? fileSystem = null, bool gpu = true)
    {
        PerfDir = Path.Combine(Dir.Path, "perf");
        Fs = fileSystem ?? new FakeFileSystem();
        Perf = PerfKit.Start(PerfDir, Clock, Fs, process: Process);
        try
        {
            App = TestApp.Start(clock: Clock, gpu: gpu ? GpuAdapterRequest.Warp : null, fileSystem: Fs, perf: Perf);
        }
        catch
        {
            Perf.Dispose();
            Dir.Dispose();
            throw;
        }
    }

    public FakeClock Clock { get; } = FakeClock.ForPhase5();

    public FakeProcessStats Process { get; } = new();

    public TempDir Dir { get; } = new();

    public string PerfDir { get; }

    public FakeFileSystem Fs { get; }

    public PerfRecorder Perf { get; }

    public TestApp App { get; }

    public ImportCoordinator Imports => EditingScreenKit.M<ScreensModule>(App).Imports;

    public CanvasModule Canvas => EditingScreenKit.M<CanvasModule>(App);

    public LooksModule Looks => EditingScreenKit.M<LooksModule>(App);

    public List<PerfLine> Lines() => PerfKit.Lines(PerfKit.File(PerfDir));

    /// <summary>記録を始めてから ms の時点まで偽の時計を進める(UI スレッドで。期限の来たタイマーが鳴る)。</summary>
    public void AdvanceTo(long ms)
    {
        UiHost.Idle();
        App.Ui(() => PerfKit.AdvanceTo(Clock, ms));
        UiHost.Idle();
    }

    /// <summary>貼り付け: 偽のクリップボードの読み取りに readMs かかる。最初の表示が終わって門を出るまで待つ。</summary>
    public ImportOutcome Paste(byte[] png, long readMs = 0)
    {
        App.Clipboard.ReadThrough = new SlowClipboard(Clock, TimeSpan.FromMilliseconds(readMs), new MemoryImportData().With(ImportFormatNames.Png, png));
        var t = App.Ui(() => Imports.PasteAsync());
        Check.True(UiHost.WaitUntil(() => t.IsCompleted, Limit), "取り込みが終わらない");
        UiHost.Idle();
        return t.GetAwaiter().GetResult() ?? throw new CheckFailedException("取り込みが始まらない");
    }

    /// <summary>キャンバスに今の文書の加工後の表示が載るまで待つ。</summary>
    public void WaitPresented() =>
        Check.True(UiHost.WaitUntil(() => !Canvas.Processed.IsBusy && Canvas.Picture.Bitmap is not null
                                          && ReferenceEquals(Canvas.Processed.PresentedDocument, App.Shell.Session.Document), Limit), "キャンバスに表示が載らない");

    /// <summary>ギャラリーの作り直しが終わり、全部のタイルの画像が揃うまで待つ。</summary>
    public void WaitGallery() =>
        Check.True(UiHost.WaitUntil(() => !Looks.IsRebuildPending && !Looks.IsBuilding && Looks.Gallery.Tiles.All(t => t.Thumbnail is not null), Limit), "サムネイルが揃わない");

    /// <summary>ログの全行(Dispose の前に読む)。</summary>
    public List<JsonElement> Log()
    {
        var list = new List<JsonElement>();
        if (!Directory.Exists(App.Paths.LogsDir)) return list;
        foreach (var f in Directory.GetFiles(App.Paths.LogsDir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            foreach (var line in Encoding.UTF8.GetString(PerfKit.Bytes(f)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                list.Add(JsonDocument.Parse(line).RootElement.Clone());
        }
        return list;
    }

    public void Dispose()
    {
        App.Dispose();
        Perf.Dispose();
        Dir.Dispose();
    }
}
