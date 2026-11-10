// 多重起動(FR-92・E-46・INV-1): ミューテックスとパイプの名前、判定、2 つ目の起動のクライアント、1 つ目の起動のサーバー。通信は System.IO.Pipes だけを使う。
// 名前付きパイプには片側だけを閉じる操作が無いので、仕様書の「書き込み側を閉じる」は、メッセージの区切りで表す(パスが無いときは接続して何も書かずに閉じる)。
// パイプのアクセス制御は現在のユーザーの SID だけ。受け取った内容の検査(FR-92 の条件)は Accept にまとめる。
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GlintLoop.App.Platform;

/// <summary>多重起動のミューテックスとパイプの名前。</summary>
public sealed record SingleInstanceNames(string Mutex, string Pipe)
{
    public static SingleInstanceNames ForSid(string sid) => new(@"Local\GlintLoop." + sid, "GlintLoop." + sid);

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }
}

/// <summary>多重起動の判定。取れたら持ち続け、Dispose で放す。</summary>
public sealed class SingleInstanceMutex : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceMutex(Mutex mutex) => _mutex = mutex;

    /// <summary>保持している起動が無ければ取って返す(1 つ目)。ほかが保持していれば null(2 つ目)。クラッシュで放棄されたものは取れる。</summary>
    public static SingleInstanceMutex? TryAcquire(string name)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, name);
        }
        catch (UnauthorizedAccessException)
        {
            // 同じ名前を、開く権限の無い相手が持っている(管理者として動いている 1 つ目など)。動いている起動があるものとして扱う
            return null;
        }
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (acquired) return new SingleInstanceMutex(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>放して閉じる(取ったスレッドで呼ぶ。別のスレッドからは閉じるだけで、放すのはプロセスの終わりになる)。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        _mutex.Dispose();
    }
}

/// <summary>2 つ目の起動の側。</summary>
public static class SingleInstanceClient
{
    /// <summary>接続から返事までを合わせて待つ時間(E-46)。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// path を送り、0x06 を待つ。onConnected は接続できた直後(書く前)に呼ぶ(前面に出す許可を渡すため)。
    /// 0x06 を受け取ったら true。パスが無いときは接続して閉じるだけで、接続できたら true。失敗も待ち切れなくても例外にせず false。
    /// </summary>
    public static bool Send(string pipeName, string? path, Action<SafePipeHandle>? onConnected = null, TimeSpan? timeout = null)
    {
        var budget = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect((int)budget.TotalMilliseconds);
            onConnected?.Invoke(pipe.SafePipeHandle);
            // パイプには空のメッセージを送れないので、パスが無いときは何も書かずに閉じる(サーバーは「閉じられた 0 バイト」として受ける)
            if (string.IsNullOrEmpty(path)) return true;
            var left = budget - clock.Elapsed;
            if (left <= TimeSpan.Zero) return false;
            using var cts = new CancellationTokenSource(left);
            pipe.WriteAsync(Encoding.UTF8.GetBytes(path), cts.Token).AsTask().GetAwaiter().GetResult();
            var ack = new byte[1];
            return pipe.ReadAsync(ack, cts.Token).AsTask().GetAwaiter().GetResult() == 1 && ack[0] == SingleInstancePipeServer.Ack;
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>1 つ目の起動の側。接続を 1 本ずつ、閉じられるまで繰り返し受ける。</summary>
public sealed class SingleInstancePipeServer : IDisposable
{
    /// <summary>読むのをやめるバイト数(FR-92 の 98,302 バイト目)。</summary>
    public const int ReadLimit = 98_302;

    /// <summary>受け取ってよい内容の上限(バイト)。</summary>
    public const int MaxContentBytes = 98_301;

    /// <summary>受け取ってよいパスの上限(string.Length)。</summary>
    public const int MaxPathLength = 32_767;

    /// <summary>受け取りを終えたことを示す 1 バイト。</summary>
    public const byte Ack = 0x06;

    /// <summary>内容を読むのを待つ上限の既定。クライアントが待つのは 3 秒までなので、それより長く待っても受け取る相手がいない。閉じないクライアントで次の起動を塞がないために置く(仮の決め)。</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(3);

    // 0x06 を相手が読むのを待つ上限。読まない相手でも、内容の読み終わりから 1 秒以内に接続を閉じるため(AC-51 の (6))
    private static readonly TimeSpan AckDrainTimeout = TimeSpan.FromMilliseconds(500);

    // 作り直しに失敗したときに次を試すまでの間(空回りしない)
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    // ERROR_NO_DATA(232。パイプを閉じている途中)の HRESULT
    private const int ErrorNoData = unchecked((int)0x800700E8);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _pipeName;
    private readonly Action<string?> _received;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>received は接続ごとに 1 回、パイプのスレッドで呼ぶ。引数は検査を通ったパス(通らなければ null)。readTimeout はテストが短くする。</summary>
    public SingleInstancePipeServer(string pipeName, Action<string?> received, TimeSpan? readTimeout = null)
    {
        _pipeName = pipeName;
        _received = received;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
    }

    /// <summary>パイプのインスタンスを 1 つ作る(アクセス制御は現在のユーザーの SID だけ)。</summary>
    public static NamedPipeServerStream CreatePipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // 0x06 が読み手を待たずに書けるよう、出力のバッファだけを持つ
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 0, 16, security);
    }

    /// <summary>FR-92 の (2) の条件を満たす内容ならパスを返す。満たさなければ null。</summary>
    public static string? Accept(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > MaxContentBytes) return null;
        string path;
        try
        {
            path = StrictUtf8.GetString(content);
        }
        catch (ArgumentException)
        {
            return null;
        }
        return path.Length <= MaxPathLength && path.IndexOfAny(['\0', '\r', '\n']) < 0 ? path : null;
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        NamedPipeServerStream? pipe = null;
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    pipe ??= CreatePipe(_pipeName);
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    await ServeAsync(pipe, token).ConfigureAwait(false);
                    pipe.Disconnect();
                }
                catch (IOException e) when (e.HResult == ErrorNoData)
                {
                    // 接続を待ち始める前に相手が接続して閉じた(パスが無い起動)。0 バイトの連絡として受け、インスタンスを作り直す
                    pipe?.Dispose();
                    pipe = null;
                    _received(null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // 1 本の接続の失敗でサーバーを終わらせない(§9.11 のログの表に行が無いので書かない)。インスタンスを作り直して次を受ける
                    pipe?.Dispose();
                    pipe = null;
                    await Task.Delay(RetryDelay, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        _received(await ReadPathAsync(pipe, token).ConfigureAwait(false));
        try
        {
            await pipe.WriteAsync(new[] { Ack }, token).ConfigureAwait(false);
            // Disconnect は読まれていない応答を捨てるので、読まれるのを待つ(読まない相手は待ち切って閉じる。閉じると Drain は例外で終わる)
            var drain = Task.Run(pipe.WaitForPipeDrain, token);
            _ = drain.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await Task.WhenAny(drain, Task.Delay(AckDrainTimeout, token)).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 相手がもう閉じている(パスが無いときの普通の形)
        }
    }

    // 1 通のメッセージの終わりか、相手が閉じるか、98,302 バイトまで読む。途中で切れた内容は渡さない
    private async Task<string?> ReadPathAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var buffer = new byte[ReadLimit];
        var total = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_readTimeout);
        try
        {
            while (total < buffer.Length)
            {
                var n = await pipe.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (n == 0) return null;
                total += n;
                if (pipe.IsMessageComplete) return Accept(buffer.AsSpan(0, total));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }
}

/// <summary>動いているウィンドウを前面に出す動作(製品は WindowForeground.cs、テストは偽物)。UI スレッドで呼ぶ。</summary>
public interface IWindowForeground
{
    void BringToFront();
}
