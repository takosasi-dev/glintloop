// GPU を呼ぶ専用の 1 スレッド(仕様書 D-37・INV-5)。仕事を受け取り、そのスレッドの上で 1 つずつ直列に実行する。
// 待ち行列は優先度ごとに 3 本(§9.9 の 8: 0 表示・1 サムネイル・2 書き出し)で、小さい番号を先に取り出す。同じ優先度の中は先着順。実行中の仕事は中断しない。
// UI スレッドからは InvokeAsync で依頼し、結果は Task で受ける(UI スレッドで待たない)。
namespace GlintLoop.Render;

/// <summary>描画の依頼の優先度(§9.9 の 8・NFR-4)。小さい番号が先に処理される。</summary>
public enum RenderPriority
{
    /// <summary>画面の表示(プロキシ。キャンバス・スポイト)。</summary>
    Display = 0,

    /// <summary>サムネイル(ルックのギャラリー・レイヤーの行・モーションのタイル)。</summary>
    Thumbnail = 1,

    /// <summary>書き出し。</summary>
    Export = 2,
}

public sealed class RenderThread : IDisposable
{
    private const int PriorityCount = 3;

    private readonly Queue<Action>[] _queues = [new(), new(), new()];
    private readonly object _lock = new();
    private readonly Thread _thread;
    private bool _completed;
    private bool _disposed;

    public RenderThread(string name = "GlintLoop.Render")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>専用スレッドの番号(Environment.CurrentManagedThreadId と比べる)。</summary>
    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>いま専用スレッドの上で動いているか。</summary>
    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>専用スレッドで work を実行し、その結果を返す Task。例外は Task に入る。priority の待ち行列に積む(既定は表示)。</summary>
    public Task<T> InvokeAsync<T>(Func<T> work, RenderPriority priority = RenderPriority.Display)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var index = (int)priority;
        if (index is < 0 or >= PriorityCount) throw new ArgumentOutOfRangeException(nameof(priority));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }
        lock (_lock)
        {
            if (_completed) throw new ObjectDisposedException(nameof(RenderThread));
            _queues[index].Enqueue(Run);
            Monitor.Pulse(_lock);
        }
        return tcs.Task;
    }

    public Task InvokeAsync(Action work, RenderPriority priority = RenderPriority.Display)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(() =>
        {
            work();
            return true;
        }, priority);
    }

    /// <summary>専用スレッドで work を実行して終わるまで待つ(UI スレッドから呼ばないこと。テストと測定用)。</summary>
    public T Invoke<T>(Func<T> work) => IsCurrent ? work() : InvokeAsync(work).GetAwaiter().GetResult();

    public void Invoke(Action work)
    {
        if (IsCurrent) work();
        else InvokeAsync(work).GetAwaiter().GetResult();
    }

    /// <summary>待ち行列に積まれている仕事の数(優先度ごと。テスト用)。</summary>
    internal int Pending(RenderPriority priority)
    {
        lock (_lock) return _queues[(int)priority].Count;
    }

    private void Loop()
    {
        while (true)
        {
            Action? work = null;
            lock (_lock)
            {
                while (true)
                {
                    foreach (var q in _queues)
                    {
                        if (q.Count > 0)
                        {
                            work = q.Dequeue();
                            break;
                        }
                    }
                    if (work is not null || _completed) break;
                    Monitor.Wait(_lock);
                }
            }
            if (work is null) return; // 受け付けを閉じ、積まれた仕事も無い
            work();
        }
    }

    /// <summary>受け付けを閉じ、積まれた仕事を(優先度の順に)終えてからスレッドを止める。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (IsCurrent) throw new InvalidOperationException("RenderThread は自分のスレッドの上では破棄できない");
        _disposed = true;
        lock (_lock)
        {
            _completed = true;
            Monitor.PulseAll(_lock);
        }
        _thread.Join();
    }
}
