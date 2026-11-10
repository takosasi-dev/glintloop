// 書き出しの専用スレッドの TaskScheduler(Q-F・AC-39 の (6) の「書き出しのスレッドが BelowNormal」)の検査。
// 仕事が BelowNormal の背景スレッドの上で動くこと、本数が決めた数を超えないこと、取りまとめ(ExportJob.RunAsync)とフレームの取り出しもその上で動くことを見る。
// 同じスケジューラーの上で待つ入れ子の仕事が詰まらないことも確かめる。GPU を使わない。
using System.Collections.Concurrent;
using System.IO;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Exports.ExportTestKit;

namespace GlintLoop.Tests.Exports;

public sealed class ExportSchedulerTests
{
    [Test]
    public void AC39_6_書き出しのスレッドはBelowNormal()
    {
        using var scheduler = new ExportScheduler(3);
        Check.Equal(3, scheduler.MaximumConcurrencyLevel, "本数");
        Check.Equal(1, scheduler.DefaultParallelism, "並列度の既定(取りまとめと受け取りの 2 本を除く)");
        var seen = new ConcurrentDictionary<int, (ThreadPriority Priority, bool Background, bool Pool)>();
        var tasks = Enumerable.Range(0, 40).Select(_ => Task.Factory.StartNew(() =>
        {
            var t = Thread.CurrentThread;
            seen[t.ManagedThreadId] = (t.Priority, t.IsBackground, t.IsThreadPoolThread);
            Thread.SpinWait(20_000);
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler)).ToArray();
        Task.WaitAll(tasks);
        Check.True(seen.Count is >= 1 and <= 3, $"使ったスレッドの数 {seen.Count} が 1〜3");
        foreach (var (id, v) in seen)
        {
            Check.Equal(ThreadPriority.BelowNormal, v.Priority, $"スレッド {id} の優先度");
            Check.True(v.Background && !v.Pool, $"スレッド {id} は専用の背景スレッド");
        }
        Check.Throws<ArgumentOutOfRangeException>(() => new ExportScheduler(2), "2 本は作れない");
        Check.True(ExportScheduler.DefaultThreadCount is >= ExportScheduler.MinThreads and <= ExportScheduler.MaxDefaultThreads, "既定の本数");
    }

    [Test]
    public void AC39_6_取りまとめとフレームの取り出しも専用スレッドで動き入れ子で詰まらない()
    {
        using var scheduler = new ExportScheduler(ExportScheduler.MinThreads);
        var run = new ExportRun(scheduler, scheduler.DefaultParallelism, CancellationToken.None);

        // 入れ子: 外の仕事が同じスケジューラーの仕事を待つ(最少の本数でも詰まらない)
        var outer = Task.Factory.StartNew(() =>
        {
            var inner = Enumerable.Range(0, 8).Select(i => Task.Factory.StartNew(() => i * 2, CancellationToken.None, TaskCreationOptions.None, scheduler)).ToArray();
            return inner.Sum(t => t.GetAwaiter().GetResult());
        }, CancellationToken.None, TaskCreationOptions.None, scheduler);
        Check.True(outer.Wait(10_000), "入れ子の仕事が 10 秒で終わらない");
        Check.Equal(56, outer.Result, "入れ子の結果");

        // APNG の書き出し(フレームの圧縮も run.Scheduler で並列にする)を RunAsync で
        const int w = 40, h = 30, n = 6;
        var frames = Noisy(w, h, n);
        var priorities = new ConcurrentBag<ThreadPriority>();
        var plan = new ExportPlan(Settings("apng", w, h, n), Settings("apng", w, h, n), new(w, h), null, null, false);
        var r = ExportJob.RunAsync(plan, new ApngEncoder(), s => new WatchFrames(new ArrayFrames(w, h, frames), priorities),
            () => new MemoryStream(), s => s.Dispose(), run).GetAwaiter().GetResult();
        Check.True(r.Bytes > 0, "書けた");
        Check.Equal(n, priorities.Count, "フレームを取り出した回数");
        Check.True(priorities.All(p => p == ThreadPriority.BelowNormal), "取りまとめのスレッド(フレームを取り出す)は BelowNormal");
        var p = MiniPngParser.Parse(((MemoryStream)r.Output).ToArray());
        for (var k = 0; k < n; k++) Check.SequenceEqual(frames[k], p.Frames[k], $"専用スレッドで書いたフレーム {k}");
    }

    // Q-F: GIF の集計・ディザ(フレームを放す仕事の中)と受け取り(ストリームへの書き込み)も BelowNormal で動く。最少の 3 本で詰まらず、バイト列は既定のスケジューラーと同じ(INV-8)。
    [Test]
    public void AC39_6_GIFの計算と受け取りもBelowNormalで動きバイト列は変わらない()
    {
        const int w = 40, h = 30, n = 6;
        var frames = Noisy(w, h, n);
        var expected = new MemoryStream();
        GifEncoder.Encode(new WatchSource(w, h, frames, new ConcurrentBag<ThreadPriority>()), expected, new GifEncoderOptions { Parallelism = 2 }, CancellationToken.None);

        using var scheduler = new ExportScheduler(ExportScheduler.MinThreads);
        var worked = new ConcurrentBag<ThreadPriority>();
        var written = new ConcurrentBag<ThreadPriority>();
        var actual = new WatchStream(written);
        var options = new GifEncoderOptions { Parallelism = scheduler.DefaultParallelism, Scheduler = scheduler };
        var job = Task.Factory.StartNew(() => GifEncoder.Encode(new WatchSource(w, h, frames, worked), actual, options, CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler);
        Check.True(job.Wait(30_000), "最少の本数で 30 秒で終わらない");

        Check.True(worked.Count >= n, "フレームを放す仕事が動いた");
        Check.True(worked.All(p => p == ThreadPriority.BelowNormal), "集計とディザは BelowNormal");
        Check.True(written.Count > n, "受け取りが書いた");
        Check.True(written.All(p => p == ThreadPriority.BelowNormal), "受け取りは BelowNormal");
        Check.SequenceEqual(expected.ToArray(), actual.ToArray(), "スケジューラーでバイト列が変わらない");
    }

    private sealed class WatchSource(int w, int h, IReadOnlyList<byte[]> frames, ConcurrentBag<ThreadPriority> released) : IGifFrameSource
    {
        public int Width => w;

        public int Height => h;

        public int FrameCount => frames.Count;

        public byte[] GetFrame(GifPass pass, int index) => frames[index];

        public void ReleaseFrame(GifPass pass, int index, byte[] pixels) => released.Add(Thread.CurrentThread.Priority);
    }

    private sealed class WatchStream(ConcurrentBag<ThreadPriority> seen) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            seen.Add(Thread.CurrentThread.Priority);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            seen.Add(Thread.CurrentThread.Priority);
            base.Write(buffer);
        }
    }

    private sealed class WatchFrames(IExportFrames inner, ConcurrentBag<ThreadPriority> seen) : IExportFrames
    {
        public int Width => inner.Width;

        public int Height => inner.Height;

        public int FrameCount => inner.FrameCount;

        public byte[] GetFrame(int index, CancellationToken ct)
        {
            seen.Add(Thread.CurrentThread.Priority);
            return inner.GetFrame(index, ct);
        }
    }
}
