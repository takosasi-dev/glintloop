// AC-51: 多重起動(FR-92・E-46・INV-1)を、実際の名前付きミューテックスとパイプで調べる。名前の SID の部分は実行ごとの GUID で、利用者の実物に触れない。
// 前面に出す動作・取り込みの入口・ウィンドウと GPU の作成は偽物で、回数と引数を記録する。(9) の時間の範囲は仕様書のとおり実時間で測る。
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using GlintLoop.App.Platform;
using GlintLoop.App.Screens;
using GlintLoop.App.Startup;
using GlintLoop.Core.Notices;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Static;

namespace GlintLoop.Tests.Ui;

public sealed class SingleInstanceAc51Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string HardPath = @"C:\テスト 用\画像 😀.png";

    // ---------------------------------------------------------------- 組み立て

    /// <summary>1 つ目の起動で動いているアプリの偽物。前面に出す動作と取り込みの入口の回数を記録する。</summary>
    private sealed class FakeFirst : IRunningApp
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _stop = new();

        public readonly ManualResetEventSlim Running = new();
        public readonly List<string> Imports = [];
        public int Foreground;

        public ICrashHandler Crash { get; } = new NoCrash();

        public IUnhandledExceptionSource Exceptions { get; } = new FakeExceptionSource();

        public void ReceiveFromSecondInstance(string? path)
        {
            lock (_gate)
            {
                Foreground++;
                if (path is not null) Imports.Add(path);
            }
        }

        public (int Foreground, List<string> Imports) Snapshot()
        {
            lock (_gate) return (Foreground, Imports.ToList());
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
    }

    private sealed class NoCrash : ICrashHandler
    {
        public void Handle(Exception exception, CrashSource source)
        {
        }
    }

    /// <summary>実際のミューテックスとパイプで動く 1 つ目の起動(別のスレッドで AppStartup.Run を回す)。</summary>
    private sealed class FirstInstance : IDisposable
    {
        public readonly string Sid = "test-" + Guid.NewGuid().ToString("N");
        public readonly FakeFirst App = new();
        private readonly Thread _thread;

        public FirstInstance(TimeSpan? readTimeout = null)
        {
            var ports = new StartupPorts(Sid, _ => App) { PipeReadTimeout = readTimeout };
            _thread = new Thread(() => AppStartup.Run([], ports)) { IsBackground = true };
            _thread.Start();
            Check.True(App.Running.Wait(Wait), "1 つ目の起動が始まらない");
        }

        public string PipeName => SingleInstanceNames.ForSid(Sid).Pipe;

        public void Dispose()
        {
            App.Stop();
            _thread.Join(Wait);
        }
    }

    /// <summary>2 つ目の起動を別のスレッドで行う(ミューテックスはスレッドに結びつくので、1 つ目と別のスレッドで呼ぶ)。</summary>
    private sealed record SecondResult(int Code, int StartApp, int Connected, TimeSpan Elapsed);

    private static SecondResult RunSecond(string sid, string[] args, string? workingDirectory = null)
    {
        var started = 0;
        var connected = 0;
        var ports = new StartupPorts(sid, _ =>
        {
            Interlocked.Increment(ref started);
            throw new InvalidOperationException("2 つ目の起動がアプリを作った");
        })
        {
            OnConnectedToFirstInstance = _ => Interlocked.Increment(ref connected),
            WorkingDirectory = () => workingDirectory ?? Environment.CurrentDirectory,
        };
        var code = -1;
        var sw = Stopwatch.StartNew();
        var t = new Thread(() => code = AppStartup.Run(args, ports)) { IsBackground = true };
        t.Start();
        Check.True(t.Join(Wait), "2 つ目の起動が戻らない");
        sw.Stop();
        return new SecondResult(code, started, connected, sw.Elapsed);
    }

    /// <summary>クライアントを使わずにパイプへ直接書く。全部を 1 回の書き込みで送り、応答(無ければ null)を返す。</summary>
    private static byte? WriteRaw(string pipeName, byte[] content)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(3000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ack = new byte[1];
        var reading = pipe.ReadAsync(ack, cts.Token).AsTask();
        try
        {
            pipe.WriteAsync(content, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            // サーバーが読むのをやめて閉じた(1 MB のとき)
        }
        try
        {
            return reading.GetAwaiter().GetResult() == 1 ? ack[0] : null;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>テストが実際のパイプで待ち受ける偽のサーバー。</summary>
    private static NamedPipeServerStream RawServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);

    // ---------------------------------------------------------------- (1) 名前

    [Test]
    public void AC51_1_名前はSIDから作る()
    {
        var names = SingleInstanceNames.ForSid("S-1-5-21-1-2-3-1001");
        Check.Equal(@"Local\GlintLoop.S-1-5-21-1-2-3-1001", names.Mutex, "ミューテックス");
        Check.Equal("GlintLoop.S-1-5-21-1-2-3-1001", names.Pipe, "パイプ");
        Check.Equal(WindowsIdentity.GetCurrent().User!.Value, SingleInstanceNames.CurrentUserSid(), "現在のユーザーの SID");
    }

    // ---------------------------------------------------------------- (2) 判定

    [Test]
    public void AC51_2_別のスレッドが保持していれば2つ目で保持していなければ1つ目になって取る()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var name = SingleInstanceNames.ForSid(sid).Mutex;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(false, name);
            m.WaitOne();
            held.Set();
            release.Wait(Wait);
            m.ReleaseMutex();
        })
        {
            IsBackground = true,
        };
        holder.Start();
        Check.True(held.Wait(Wait), "保持できない");
        var second = RunSecond(sid, []);
        Check.Equal(0, second.Code, "2 つ目の終了コード");
        Check.Equal(0, second.StartApp, "2 つ目はアプリを作らない");
        release.Set();
        Check.True(holder.Join(Wait), "保持のスレッドが終わらない");

        using var first = new FirstInstance();
        // FirstInstance は固有の SID なので、ここでは「保持していない名前」で 1 つ目になることを確かめたことになる。取ったことは別のスレッドから確かめる
        var taken = false;
        var other = new Thread(() =>
        {
            using var again = SingleInstanceMutex.TryAcquire(SingleInstanceNames.ForSid(first.Sid).Mutex);
            taken = again is not null;
        })
        {
            IsBackground = true,
        };
        other.Start();
        Check.True(other.Join(Wait), "判定が戻らない");
        Check.True(!taken, "1 つ目の起動がミューテックスを取っている");
    }

    [Test]
    public void AC51_2_名前があっても保持する者がいなければ1つ目で放棄されたものも1つ目()
    {
        var name = SingleInstanceNames.ForSid("test-" + Guid.NewGuid().ToString("N")).Mutex;
        // ハンドルだけが開いている(保持はしていない)
        using var open = new Mutex(false, name);
        Bool(name, true, "保持していない");

        // クラッシュの終了と同じく、保持したまま持ち主のスレッドが終わる
        var name2 = SingleInstanceNames.ForSid("test-" + Guid.NewGuid().ToString("N")).Mutex;
        using var keepAlive = new Mutex(false, name2);
        var abandon = new Thread(() =>
        {
            var m = new Mutex(false, name2);
            m.WaitOne();
        })
        {
            IsBackground = true,
        };
        abandon.Start();
        Check.True(abandon.Join(Wait), "スレッドが終わらない");
        Bool(name2, true, "放棄された");

        static void Bool(string n, bool expected, string what)
        {
            var result = false;
            var t = new Thread(() =>
            {
                using var acquired = SingleInstanceMutex.TryAcquire(n);
                result = acquired is not null;
            })
            {
                IsBackground = true,
            };
            t.Start();
            Check.True(t.Join(Wait), what + ": 戻らない");
            Check.Equal(expected, result, what);
        }
    }

    [Test]
    public void AC51_2_開く権限の無いミューテックスは2つ目として扱い例外にしない()
    {
        // 管理者として動いている 1 つ目と、ふつうの権限の 2 つ目のときに起きる形を、拒否の規則で作る
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Deny));
        using var denied = MutexAcl.Create(false, SingleInstanceNames.ForSid(sid).Mutex, out var created, security);
        Check.True(created, "テスト用のミューテックスを作れた");
        var r = RunSecond(sid, []);
        Check.Equal(0, r.Code, "終了コード");
        Check.Equal(0, r.StartApp, "アプリを作らない");
        Check.True(r.Elapsed >= TimeSpan.FromSeconds(2.9), "パイプを 3 秒待つ: " + r.Elapsed);
    }

    // ---------------------------------------------------------------- (3) アクセス

    [Test]
    public void AC51_3_パイプの規則は現在のユーザーのSIDだけ()
    {
        using var pipe = SingleInstancePipeServer.CreatePipe("test-" + Guid.NewGuid().ToString("N"));
        var rules = pipe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User!;
        Check.True(rules.Count >= 1, "規則がある(既定のアクセス制御ではない)");
        Check.True(rules.All(r => r.AccessControlType == AccessControlType.Allow), "許可だけ");
        Check.Equal(0, rules.Count(r => (SecurityIdentifier)r.IdentityReference != me), "現在のユーザー以外の規則");
        foreach (var type in new[] { WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.BuiltinUsersSid })
        {
            var sid = new SecurityIdentifier(type, null);
            Check.Equal(0, rules.Count(r => (SecurityIdentifier)r.IdentityReference == sid), type + " の規則");
        }
    }

    // ---------------------------------------------------------------- (4) 受け渡し

    [Test]
    public void AC51_4_日本語と絵文字と空白のパスが届き2つ目はアプリを作らず3秒未満で0で戻る()
    {
        using var first = new FirstInstance();
        var r = RunSecond(first.Sid, [HardPath]);
        Check.Equal(0, r.Code, "終了コード");
        Check.Equal(0, r.StartApp, "新しいウィンドウと GPU のデバイスの作成");
        Check.Equal(1, r.Connected, "接続した直後の処理(前面に出す許可)");
        Check.True(r.Elapsed < TimeSpan.FromSeconds(3), "3 秒未満: " + r.Elapsed);
        var (fg, imports) = first.App.Snapshot();
        Check.Equal(1, fg, "前面に出す動作");
        Check.SequenceEqual([HardPath], imports, "取り込みの入口");
    }

    [Test]
    public void AC51_4_相対パスは2つ目の作業フォルダを基準に絶対パスになり2個目の引数は使わない()
    {
        using var first = new FirstInstance();
        var cwd = Path.Combine(Path.GetTempPath(), "glint-作業");
        RunSecond(first.Sid, [@"sub\a.png", @"sub\b.png"], cwd);
        Check.SequenceEqual([Path.Combine(cwd, "sub", "a.png")], first.App.Snapshot().Imports, "1 個目だけが絶対パスで渡る");
    }

    // ---------------------------------------------------------------- (5) パスなし

    [Test]
    public void AC51_5_引数が無いときは前面に出すだけで終了コード0()
    {
        using var first = new FirstInstance();
        var r = RunSecond(first.Sid, []);
        Check.Equal(0, r.Code, "終了コード");
        Check.Equal(1, r.Connected, "接続した直後の処理");
        Check.True(UiHost.WaitUntil(() => first.App.Snapshot().Foreground == 1, Wait), "前面に出す動作が 1 回");
        Check.Equal(0, first.App.Snapshot().Imports.Count, "取り込みの入口");
    }

    [Test]
    public void AC51_5_接続してすぐ閉じる起動が続いても取りこぼさない()
    {
        // サーバーが接続を待ち始める前にクライアントが閉じると、接続の待ちが「閉じている途中」の失敗で返る。これも 0 バイトの連絡として受ける
        const int rounds = 40;
        using var first = new FirstInstance();
        for (var i = 0; i < rounds; i++) Check.True(SingleInstanceClient.Send(first.PipeName, null), "接続できる");
        Check.True(UiHost.WaitUntil(() => first.App.Snapshot().Foreground == rounds, Wait), "前面に出す動作の回数: " + first.App.Snapshot().Foreground);
    }

    // ---------------------------------------------------------------- (6) 受け取る内容の検査

    private static void ExpectDelivered(byte[] content, string expected, string what)
    {
        using var first = new FirstInstance();
        Check.Equal<byte?>(0x06, WriteRaw(first.PipeName, content), what + ": 0x06");
        var (fg, imports) = first.App.Snapshot();
        Check.Equal(1, fg, what + ": 前面");
        Check.Equal(1, imports.Count, what + ": 渡る");
        Check.True(imports[0] == expected, what + ": 内容が一致する");
    }

    private static void ExpectDiscarded(byte[] content, string what)
    {
        using var first = new FirstInstance();
        Check.Equal<byte?>(0x06, WriteRaw(first.PipeName, content), what + ": 0x06");
        var (fg, imports) = first.App.Snapshot();
        Check.Equal(1, fg, what + ": 前面は 1 回行う");
        Check.Equal(0, imports.Count, what + ": 渡らない");
    }

    [Test]
    public void AC51_6_32767文字は渡り32768文字は渡らない()
    {
        var ok = new string('a', 32_767);
        ExpectDelivered(Encoding.UTF8.GetBytes(ok), ok, "32,767 文字");
        ExpectDiscarded(Encoding.UTF8.GetBytes(new string('a', 32_768)), "32,768 文字");
    }

    [Test]
    public void AC51_6_98301バイトちょうどは渡りそれを超えると渡らない()
    {
        var ok = new string('あ', 32_767);
        var bytes = Encoding.UTF8.GetBytes(ok);
        Check.Equal(98_301, bytes.Length, "内容のバイト数");
        ExpectDelivered(bytes, ok, "98,301 バイト");
        ExpectDiscarded(Encoding.UTF8.GetBytes(new string('あ', 32_767) + "a"), "98,302 バイト");
    }

    [Test]
    public void AC51_6_1MBは読むのをやめて1秒以内に閉じ続けて受け渡しが成功する()
    {
        using var first = new FirstInstance();
        var sw = Stopwatch.StartNew();
        using (var pipe = new NamedPipeClientStream(".", first.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            pipe.Connect(3000);
            // 読まないクライアントでも、応答の書き込みでサーバーが止まらない
            var threw = false;
            try
            {
                pipe.WriteAsync(new byte[1_000_000]).AsTask().Wait(Wait);
            }
            catch (AggregateException e) when (e.InnerException is IOException)
            {
                threw = true;
            }
            Check.True(threw, "サーバーが接続を閉じて書き込みが失敗する(1 MB を読み切っていない)");
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(1), "1 秒以内に閉じる: " + sw.Elapsed);
        }
        var (fg, imports) = first.App.Snapshot();
        Check.Equal(1, fg, "前面は 1 回行う");
        Check.Equal(0, imports.Count, "渡らない");

        var r = RunSecond(first.Sid, [HardPath]);
        Check.Equal(0, r.Code, "続けての受け渡しの終了コード");
        Check.True(r.Elapsed < TimeSpan.FromSeconds(3), "続けての受け渡しの時間: " + r.Elapsed);
        Check.SequenceEqual([HardPath], first.App.Snapshot().Imports, "続けての受け渡し");
    }

    [Test]
    public void AC51_6_1MBの書き手が同時に読むと0x06を受け取れる()
    {
        using var first = new FirstInstance();
        Check.Equal<byte?>(0x06, WriteRaw(first.PipeName, new byte[1_000_000]), "0x06");
        Check.Equal(0, first.App.Snapshot().Imports.Count, "渡らない");
    }

    [Test]
    public void AC51_6_不正なUTF8とNUL_CR_LFを含む内容は渡らない()
    {
        ExpectDiscarded([0xFF, 0xFE], "不正な UTF-8");
        ExpectDiscarded(Encoding.UTF8.GetBytes("a.png\nb.png"), "LF");
        ExpectDiscarded(Encoding.UTF8.GetBytes("a.png\rb.png"), "CR");
        ExpectDiscarded(Encoding.UTF8.GetBytes("a.png\0b.png"), "NUL");
    }

    [Test]
    public void AC51_6_0バイトの内容は渡らず前面だけ行う_閉じる形と黙って待つ形()
    {
        // 閉じる形(製品のクライアント): 閉じたあとなので 0x06 は受け取れない
        using (var first = new FirstInstance())
        {
            using (var pipe = new NamedPipeClientStream(".", first.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(3000);
            }
            Check.True(UiHost.WaitUntil(() => first.App.Snapshot().Foreground == 1, Wait), "前面に出す動作が 1 回");
            Check.Equal(0, first.App.Snapshot().Imports.Count, "渡らない");
        }
        // 黙って待つ形: 読む時間の上限のあとに 0x06 が返る
        using (var first = new FirstInstance(readTimeout: TimeSpan.FromMilliseconds(200)))
        {
            using var pipe = new NamedPipeClientStream(".", first.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(3000);
            var ack = new byte[1];
            using var cts = new CancellationTokenSource(Wait);
            Check.Equal(1, pipe.ReadAsync(ack, cts.Token).AsTask().GetAwaiter().GetResult(), "応答のバイト数");
            Check.Equal((byte)0x06, ack[0], "0x06");
            var (fg, imports) = first.App.Snapshot();
            Check.Equal(1, fg, "前面は 1 回行う");
            Check.Equal(0, imports.Count, "渡らない");
        }
    }

    [Test]
    public void AC51_6_閉じないクライアントがいても次の受け渡しが通る()
    {
        using var first = new FirstInstance(readTimeout: TimeSpan.FromMilliseconds(200));
        using var silent = new NamedPipeClientStream(".", first.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        silent.Connect(3000);
        var r = RunSecond(first.Sid, [HardPath]);
        Check.Equal(0, r.Code, "終了コード");
        Check.SequenceEqual([HardPath], first.App.Snapshot().Imports, "次の受け渡し");
    }

    // ---------------------------------------------------------------- (7) 取り込みの失敗の表示(本物のシェル)

    [Test]
    public void AC51_7_存在しないパスと画像でないファイルは通知バーに出る()
    {
        using var dir = new TempDir();
        var text = dir.Write("text.png", "画像ではない"u8.ToArray());
        Check.Equal(NoticeKeys.ImportReadError, ThroughPipe(dir.PathOf("無い.png")), "存在しないパス");
        Check.Equal(NoticeKeys.ImportUnsupported, ThroughPipe(text), "画像でないファイル");
    }

    [Test]
    public void AC51_6_本物のシェルでも捨てた内容は通知もログも無い()
    {
        using var app = TestApp.Start();
        var foreground = new CountingForeground();
        var receiver = SecondInstanceReceiver.ForShell(app.Shell, foreground);
        var name = "test-" + Guid.NewGuid().ToString("N");
        using var server = new SingleInstancePipeServer(name, receiver.Receive);
        server.Start();
        var logBefore = LogText(app);
        Check.Equal<byte?>(0x06, WriteRaw(name, Encoding.UTF8.GetBytes("a.png\nb.png")), "0x06");
        Check.True(UiHost.WaitUntil(() => foreground.Count == 1, Wait), "前面に出す動作");
        UiHost.Idle();
        Check.Equal<string?>(null, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "通知");
        Check.Equal(logBefore, LogText(app), "ログ");
        Check.True(!app.Ui(() => app.Shell.Session.HasDocument), "文書ができない");
    }

    private static string LogText(TestApp app)
    {
        var path = app.Shell.Log.CurrentPath;
        if (path is null || !File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private sealed class CountingForeground : IWindowForeground
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void BringToFront()
        {
            Check.True(UiHost.Dispatcher.CheckAccess(), "前面に出す動作は UI スレッドで行う");
            Interlocked.Increment(ref _count);
        }
    }

    private static string? ThroughPipe(string path)
    {
        using var app = TestApp.Start();
        var foreground = new CountingForeground();
        var screens = app.Ui(() => app.Shell.Modules.OfType<ScreensModule>().Single());
        var receiver = SecondInstanceReceiver.ForShell(app.Shell, foreground);
        var name = "test-" + Guid.NewGuid().ToString("N");
        using var server = new SingleInstancePipeServer(name, receiver.Receive);
        server.Start();
        Check.True(SingleInstanceClient.Send(name, path), "0x06 を受け取る");
        Check.True(UiHost.WaitUntil(() => foreground.Count == 1, Wait), "前面に出す動作");
        Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.NoticeBar.Current is not null) && !screens.Imports.IsImporting, Wait), "通知が出ない");
        return app.Ui(() => app.Shell.NoticeBar.Current?.Key);
    }

    [Test]
    public void AC51_7_読める画像はパイプから取り込まれて文書になる()
    {
        using var dir = new TempDir();
        var png = dir.Write("画像 😀.png", SkiaBytes.Png(8, 8, Img.Solid(8, 8, 200, 10, 10)));
        using var app = TestApp.Start();
        var receiver = SecondInstanceReceiver.ForShell(app.Shell, new CountingForeground());
        var name = "test-" + Guid.NewGuid().ToString("N");
        using var server = new SingleInstancePipeServer(name, receiver.Receive);
        server.Start();
        Check.True(SingleInstanceClient.Send(name, png), "0x06 を受け取る");
        Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Session.HasDocument), Wait), "取り込まれない");
    }

    // ---------------------------------------------------------------- (8) 続けて 3 回

    [Test]
    public void AC51_8_続けて3回でも同じ結果になる()
    {
        using var first = new FirstInstance();
        for (var i = 1; i <= 3; i++)
        {
            var path = $@"C:\a\{i}.png";
            var r = RunSecond(first.Sid, [path]);
            Check.Equal(0, r.Code, $"{i} 回目の終了コード");
            Check.True(r.Elapsed < TimeSpan.FromSeconds(3), $"{i} 回目の時間: {r.Elapsed}");
            var (fg, imports) = first.App.Snapshot();
            Check.Equal(i, fg, $"{i} 回目の前面");
            Check.Equal(i, imports.Count, $"{i} 回目の取り込み");
            Check.Equal(path, imports[^1], $"{i} 回目のパス");
        }
    }

    // ---------------------------------------------------------------- (9) 応答なし

    private sealed class HeldMutex : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public HeldMutex(string name)
        {
            using var held = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var m = new Mutex(false, name);
                m.WaitOne();
                held.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
                m.ReleaseMutex();
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            Check.True(held.Wait(Wait), "保持できない");
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(Wait);
        }
    }

    [Test]
    public void AC51_9_パイプが1秒で現れると1秒の付近で受け渡しが終わる()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var names = SingleInstanceNames.ForSid(sid);
        using var mutex = new HeldMutex(names.Mutex);
        string? got = null;
        using var received = new ManualResetEventSlim();
        var starter = Task.Run(async () =>
        {
            await Task.Delay(1000);
            return new SingleInstancePipeServer(names.Pipe, p =>
            {
                got = p;
                received.Set();
            });
        }).ContinueWith(t =>
        {
            t.Result.Start();
            return t.Result;
        }, TaskScheduler.Default);
        var r = RunSecond(sid, [HardPath]);
        using var server = starter.Result;
        Check.Equal(0, r.Code, "終了コード");
        Check.Equal(0, r.StartApp, "アプリを作らない");
        Check.True(received.Wait(Wait), "届かない");
        Check.Equal(HardPath, got, "パス");
        Check.True(r.Elapsed >= TimeSpan.FromSeconds(0.9) && r.Elapsed < TimeSpan.FromSeconds(1.8), "1.0 秒の付近: " + r.Elapsed);
    }

    [Test]
    public void AC51_9_パイプが現れなければ3秒で何もせず0で戻る()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        using var mutex = new HeldMutex(SingleInstanceNames.ForSid(sid).Mutex);
        var r = RunSecond(sid, [HardPath]);
        Check.Equal(0, r.Code, "終了コード");
        Check.Equal(0, r.StartApp, "新しいウィンドウと GPU のデバイスの作成");
        Check.Equal(0, r.Connected, "接続していない");
        Check.True(r.Elapsed >= TimeSpan.FromSeconds(2.9) && r.Elapsed <= TimeSpan.FromSeconds(3.5), "3.0 秒: " + r.Elapsed);
    }

    [Test]
    public void AC51_9_0x06を返さないサーバーでも接続から3秒で0で戻る()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var names = SingleInstanceNames.ForSid(sid);
        using var mutex = new HeldMutex(names.Mutex);
        using var server = RawServer(names.Pipe);
        var accept = server.WaitForConnectionAsync();
        var drain = Task.Run(async () =>
        {
            await accept;
            var buf = new byte[4096];
            try
            {
                while (await server.ReadAsync(buf) > 0)
                {
                }
            }
            catch (IOException)
            {
            }
        });
        var r = RunSecond(sid, [HardPath]);
        Check.Equal(0, r.Code, "終了コード");
        Check.Equal(0, r.StartApp, "アプリを作らない");
        Check.Equal(1, r.Connected, "接続している");
        Check.True(r.Elapsed >= TimeSpan.FromSeconds(2.9) && r.Elapsed <= TimeSpan.FromSeconds(3.5), "3.0 秒: " + r.Elapsed);
    }

    [Test]
    public void AC51_9_読まないサーバーでも書き込みで止まらず3秒で戻る()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 0, 0);
        var accept = server.WaitForConnectionAsync();
        var sw = Stopwatch.StartNew();
        var acked = SingleInstanceClient.Send(name, new string('a', 1000));
        sw.Stop();
        Check.True(!acked, "0x06 は来ない");
        Check.True(sw.Elapsed >= TimeSpan.FromSeconds(2.9) && sw.Elapsed <= TimeSpan.FromSeconds(3.5), "3.0 秒: " + sw.Elapsed);
    }

    // ---------------------------------------------------------------- (10) ネットワーク

    private static string J(params string[] parts) => string.Concat(parts);

    [Test]
    public void AC51_10_多重起動のコードにネットワークの語が無く通信はPipesだけ()
    {
        string[] terms =
        [
            J("Sys", "tem.", "Net"), J("Http", "Client"), J("Web", "Client"), J("Web", "Request"), J("Web", "Soc", "ket"),
            J("Tcp", "Client"), J("Udp", "Client"), J("Soc", "ket"), J("Dn", "s."), J("local", "host"), J("127", ".0.0.1"),
        ];
        string[] files =
        [
            "src/GlintLoop.App/Program.cs", "src/GlintLoop.App/Platform/SingleInstance.cs", "src/GlintLoop.App/Platform/WindowForeground.cs",
            "src/GlintLoop.App/Startup/AppStartup.cs", "src/GlintLoop.App/Startup/ProductionStartup.cs",
        ];
        var paths = files.Select(f => Path.Combine(RepoFiles.Root, f)).ToList();
        foreach (var p in paths) Check.True(File.Exists(p), "ファイルが無い: " + p);
        var hits = SourceScan.FindTermsInFiles(paths, terms, []);
        Check.True(hits.Count == 0, $"ネットワークの語が {hits.Count} 件: {string.Join(", ", hits.Take(10))}");
        var usings = File.ReadAllLines(paths[1]).Where(l => l.StartsWith("using ", StringComparison.Ordinal)).ToList();
        Check.True(usings.Contains("using System.IO.Pipes;"), "System.IO.Pipes を使う");
    }

    [Test]
    public void AC51_2つ目の入口のProgramは起動処理を呼ぶだけ()
    {
        var text = File.ReadAllText(Path.Combine(RepoFiles.Root, "src/GlintLoop.App/Program.cs"));
        Check.True(text.Contains("AppStartup.Run(", StringComparison.Ordinal), "起動処理を呼ぶ");
        Check.True(!text.Contains("new Application", StringComparison.Ordinal), "判定の前に Application を作らない");
    }
}
