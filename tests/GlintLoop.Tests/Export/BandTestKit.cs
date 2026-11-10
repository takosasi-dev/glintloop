// 書き出し帯(担当 band)のテストの道具: 偽の書き出しの中身(FakePipeline)・UI スレッドの代わりの待ち行列(QueuePost)・BelowNormal の偽のスケジューラー・偽のキーの環境・
// テスト用の小さな APNG の書き手(実出力のデコードの検査用。エンコーダーの担当 export のものとは別に、ここで最小の形だけ書く)。
// 本物のエンコーダー(担当 export)はまだ無いので、FakePipeline はフレームごとに決まったバイト列を書き、フレームの切れ目で取り消しを見る。
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;

namespace GlintLoop.Tests.Exports;

/// <summary>UI スレッドへの post の代わり。積んだものを Pump で順に動かす。</summary>
internal sealed class QueuePost
{
    private readonly ConcurrentQueue<Action> _q = new();

    public void Post(Action a) => _q.Enqueue(a);

    public int Pump()
    {
        var n = 0;
        while (_q.TryDequeue(out var a))
        {
            a();
            n++;
        }
        return n;
    }
}

/// <summary>
/// 偽の書き出しの中身。FrameCount 枚のフレームを 1 枚ずつ書き(中身は Content、既定は文書の JSON から作ったバイト列を等分したもの)、フレームの切れ目で取り消しを見る。
/// Gate を入れると、各フレームの前でその門が開くのを待つ(取り消しのテストで途中に止めるため)。
/// </summary>
internal sealed class FakePipeline : IExportPipeline
{
    private int _calls;
    private int _framesDone;

    public int FrameCount { get; init; } = 4;

    public Func<ExportRequest, byte[]>? Content { get; init; }

    public SemaphoreSlim? Gate { get; init; }

    public Exception? Throw { get; init; }

    public ExportPipelineResult? Result { get; init; }

    public ExportCheck CheckResult { get; init; } = new(null, null);

    public int Calls => Volatile.Read(ref _calls);

    public int FramesDone => Volatile.Read(ref _framesDone);

    public ConcurrentQueue<ExportRequest> Jobs { get; } = new();

    public ConcurrentQueue<ThreadPriority> Priorities { get; } = new();

    /// <summary>フレームの前で門を待っている(Gate があるとき)。</summary>
    public ManualResetEventSlim Waiting { get; } = new();

    public static byte[] DefaultContent(ExportRequest job) =>
        Encoding.UTF8.GetBytes("fake:" + job.FrameK + ":" + DocumentJson.Write(job.Document));

    public ExportPipelineResult Run(ExportRequest job, Stream output, ExportRun run)
    {
        Interlocked.Increment(ref _calls);
        Jobs.Enqueue(job);
        Priorities.Enqueue(Thread.CurrentThread.Priority);
        if (Throw is not null) throw Throw;
        var bytes = (Content ?? DefaultContent)(job);
        var n = Math.Max(1, FrameCount);
        for (var i = 0; i < n; i++)
        {
            // フレームの切れ目で取り消しを見る(§9.9 の 8)
            run.Cancel.ThrowIfCancellationRequested();
            if (Gate is not null)
            {
                Waiting.Set();
                Gate.Wait();
                Waiting.Reset();
            }
            var from = bytes.Length * i / n;
            var to = bytes.Length * (i + 1) / n;
            output.Write(bytes, from, to - from);
            output.Flush();
            Interlocked.Increment(ref _framesDone);
            run.Progress?.Report(new ExportProgress(i + 1, n, 0, 0));
        }
        run.Cancel.ThrowIfCancellationRequested();
        return Result ?? new ExportPipelineResult(48, 32, n, job.Document.Time.Fps, null, false, null);
    }

    public ExportCheck Check(ExportRequest job, ExportWritten written) => CheckResult;
}

/// <summary>BelowNormal の専用スレッド 1 本で仕事を動かす偽のスケジューラー(担当 export の本物が来るまでの代わり。Q-F)。</summary>
internal sealed class BelowNormalTestScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread _thread;

    public BelowNormalTestScheduler()
    {
        _thread = new Thread(() =>
        {
            foreach (var t in _tasks.GetConsumingEnumerable()) TryExecuteTask(t);
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "band-test-export",
        };
        _thread.Start();
    }

    protected override void QueueTask(Task task) => _tasks.Add(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        Thread.CurrentThread == _thread && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

    public void Dispose()
    {
        _tasks.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _tasks.Dispose();
    }
}

internal static class BandTestKit
{
    public static readonly Guid FakeMvid = new("11111111-2222-3333-4444-555555555555");

    /// <summary>偽のキーの環境(版・アセンブリの ModuleVersionId はテストの値。AC-39 の (3) の (h))。</summary>
    public static ExportEnvironment Env(string version = "0.4.0-test", Guid? mvid = null, bool warp = true, string adapter = "Microsoft Basic Render Driver",
        string? blueNoise = null) =>
        new(new BuildIdentity(version, [new("GlintLoop", mvid ?? FakeMvid), new("GlintLoop.Core", FakeMvid)]), warp, adapter,
            blueNoise ?? ExportEnvironment.BlueNoiseTileSha256);

    // ---------------------------------------------------------------- 最小の APNG(テスト用)

    /// <summary>1 フレーム分(枠と画素と遅延と重ね方)。Rgba は枠の大きさの straight RGBA。</summary>
    public sealed record ApngFrame(int X, int Y, int W, int H, byte[] Rgba, ushort DelayNum, ushort DelayDen, byte Dispose = 0, byte Blend = 0);

    /// <summary>width×height の RGBA の APNG を書く(最初のフレームを IDAT に置く。plays は 0 で無限)。</summary>
    public static byte[] Apng(int width, int height, IReadOnlyList<ApngFrame> frames, int plays = 0)
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Count);
        BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(4), (uint)plays);
        Chunk(ms, "acTL", actl);
        uint seq = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var fc = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fc, seq++);
            BinaryPrimitives.WriteUInt32BigEndian(fc.AsSpan(4), (uint)f.W);
            BinaryPrimitives.WriteUInt32BigEndian(fc.AsSpan(8), (uint)f.H);
            BinaryPrimitives.WriteUInt32BigEndian(fc.AsSpan(12), (uint)f.X);
            BinaryPrimitives.WriteUInt32BigEndian(fc.AsSpan(16), (uint)f.Y);
            BinaryPrimitives.WriteUInt16BigEndian(fc.AsSpan(20), f.DelayNum);
            BinaryPrimitives.WriteUInt16BigEndian(fc.AsSpan(22), f.DelayDen);
            fc[24] = f.Dispose;
            fc[25] = f.Blend;
            Chunk(ms, "fcTL", fc);
            var z = Deflate(f.W, f.H, f.Rgba);
            if (i == 0)
            {
                Chunk(ms, "IDAT", z);
            }
            else
            {
                var fd = new byte[4 + z.Length];
                BinaryPrimitives.WriteUInt32BigEndian(fd, seq++);
                z.CopyTo(fd, 4);
                Chunk(ms, "fdAT", fd);
            }
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>単色の straight RGBA。</summary>
    public static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }
        return px;
    }

    /// <summary>straight RGBA を straight BGRA に並べ替える(WPF の Bgra32 と比べるため)。</summary>
    public static byte[] ToBgra(byte[] rgba)
    {
        var o = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            o[i] = rgba[i + 2];
            o[i + 1] = rgba[i + 1];
            o[i + 2] = rgba[i];
            o[i + 3] = rgba[i + 3];
        }
        return o;
    }

    private static byte[] Deflate(int w, int h, byte[] rgba)
    {
        var raw = new byte[h * (1 + w * 4)];
        for (var y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, raw, y * (1 + w * 4) + 1, w * 4);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head);
        s.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(head.AsSpan(4), data));
        s.Write(crc);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        void Add(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                c ^= b;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }
        Add(type);
        Add(data);
        return ~c;
    }
}
