// フレームを順に取り出して並列に処理し、結果を番号の順に受け取る小さな流れ(仕様書 §9.8 の 1・D-29)。
// 取り出しは呼んだスレッドで番号の順に行い、取り出してから結果を受け取り終えるまでのフレームを slots 枚までに抑える。
// 計算は同時に parallelism 個までのスケジューラーで走らせる。結果の順序は完了の順に依らない。
using System.Runtime.ExceptionServices;

namespace GlintLoop.Core.Gif;

internal static class GifPipeline
{
    /// <summary>
    /// k=0..count−1 について start(k) をこのスレッドで順に呼び(フレームの取り出しを含む)、返った作業の結果を consume(k, r) で k の順に受け取る。
    /// 失敗や取り消しがあれば、始めた作業がすべて終わるのを待ってから最初の原因を投げる。
    /// 受け取りは scheduler(null は TaskScheduler.Default)のスレッドを 1 本ふさぐ。呼ぶスレッドも同じスケジューラーなら、計算の分とあわせて 3 本以上が要る。
    /// </summary>
    internal static void RunOrdered<T>(int count, int slots, TaskScheduler? scheduler, CancellationToken ct,
        Func<int, CancellationToken, Task<T>> start, Action<int, T> consume)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(slots, slots);
        var started = new TaskCompletionSource<Task<T>>[count];
        for (var k = 0; k < count; k++) started[k] = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var works = new List<Task>(count);
        var token = cts.Token;

        var consumer = Task.Factory.StartNew(() =>
        {
            try
            {
                for (var k = 0; k < count; k++)
                {
                    var work = started[k].Task.GetAwaiter().GetResult();
                    var r = work.GetAwaiter().GetResult();
                    consume(k, r);
                    gate.Release();
                }
            }
            catch
            {
                cts.Cancel();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler ?? TaskScheduler.Default);

        Exception? producerError = null;
        try
        {
            for (var k = 0; k < count; k++)
            {
                gate.Wait(token);
                var work = start(k, token);
                works.Add(work);
                started[k].SetResult(work);
            }
        }
        catch (Exception e)
        {
            producerError = e;
            cts.Cancel();
            for (var k = 0; k < count; k++) started[k].TrySetCanceled(token);
        }

        // 始めた作業がすべて終わるまで待つ(失敗のあとも裏で動き続けないように)。
        foreach (var w in works)
        {
            try { w.Wait(CancellationToken.None); }
            catch (Exception) { /* 原因は下で選ぶ */ }
        }
        Exception? consumerError = null;
        try { consumer.GetAwaiter().GetResult(); }
        catch (Exception e) { consumerError = e; }

        var first = Pick(producerError, consumerError);
        if (first is not null)
        {
            ct.ThrowIfCancellationRequested();
            ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    // 取り消しの例外より、本当の原因を優先する。
    private static Exception? Pick(Exception? a, Exception? b)
    {
        if (a is not null && a is not OperationCanceledException) return a;
        if (b is not null && b is not OperationCanceledException) return b;
        return a ?? b;
    }
}
