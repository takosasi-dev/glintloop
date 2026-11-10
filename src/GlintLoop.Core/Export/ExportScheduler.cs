// 書き出しの専用スレッドの TaskScheduler(Q-F)。BelowNormal の背景スレッドを数本持ち、取りまとめ・受け取り・エンコードの並列の全部をここで動かす。
// 取りまとめと受け取りの仕事はスレッドを 1 本ずつふさいで待つので、スレッドは 3 本以上にする(残りがフレームの計算を進める)。
// 同じスケジューラーの上で待つときに詰まらないよう、自分のスレッドからの依頼だけはその場で実行してよい。
using System.Collections.Concurrent;

namespace GlintLoop.Core.Export;

public sealed class ExportScheduler : TaskScheduler, IDisposable
{
    /// <summary>スレッドの最少の本数(取りまとめの 1 本、受け取りの 1 本、計算の 1 本以上)。</summary>
    public const int MinThreads = 3;

    /// <summary>スレッドの本数の既定の上限。</summary>
    public const int MaxDefaultThreads = 8;

    private readonly BlockingCollection<Task> _queue = new(new ConcurrentQueue<Task>());
    private readonly Thread[] _threads;

    [ThreadStatic]
    private static ExportScheduler? _current;

    /// <summary>threads 本(3 以上)の BelowNormal の専用スレッドを作る。</summary>
    public ExportScheduler(int threads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, MinThreads);
        _threads = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            var t = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "GlintLoop.Export." + i,
            };
            _threads[i] = t;
            t.Start();
        }
    }

    /// <summary>既定の本数: 論理プロセッサの数(3〜8)。</summary>
    public static int DefaultThreadCount => Math.Clamp(Environment.ProcessorCount, MinThreads, MaxDefaultThreads);

    /// <summary>エンコードの並列度の既定(取りまとめと受け取りの 2 本を除いた本数)。</summary>
    public int DefaultParallelism => _threads.Length - 2;

    public override int MaximumConcurrencyLevel => _threads.Length;

    private void Loop()
    {
        _current = this;
        try
        {
            foreach (var task in _queue.GetConsumingEnumerable()) TryExecuteTask(task);
        }
        catch (ObjectDisposedException)
        {
            // Dispose のあとの終わり
        }
    }

    protected override void QueueTask(Task task) => _queue.Add(task);

    // 自分のスレッドの上で、まだ待ち行列に入れていない仕事だけをその場で実行する(入れた仕事は取り出しの側が実行する)。
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        !taskWasPreviouslyQueued && ReferenceEquals(_current, this) && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    /// <summary>待ち行列を閉じ、実行中の仕事が終わるのを待ってスレッドを終える。</summary>
    public void Dispose()
    {
        if (_queue.IsAddingCompleted) return;
        _queue.CompleteAdding();
        foreach (var t in _threads)
        {
            if (t != Thread.CurrentThread) t.Join();
        }
        _queue.Dispose();
    }
}
