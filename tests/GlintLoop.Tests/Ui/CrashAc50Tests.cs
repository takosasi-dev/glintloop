// AC-50: クラッシュの処理(FR-104・E-47・INV-15・INV-16)。入口の順序・退避のファイル・ダイアログ・ログの中身・失敗しても終わること・再入・退避の個数を、偽の口で調べる。
// 製品のダイアログの中身と、Dispatcher の未処理例外の発生元は、別のスレッドの実物で調べる(ダイアログは画面の外に非アクティブで出す)。
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GlintLoop.App.Screens;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Ui;

public sealed class CrashAc50Tests
{
    private const string RecoveryName = "recovery-20261014-203805.glint";
    private const string Secret = @"C:\Users\テスト\画像.png を読めない";

    private static readonly byte[] PngA = SkiaBytes.Png(40, 30, Img.Solid(40, 30, 250, 10, 10));
    private static readonly byte[] PngB = SkiaBytes.Png(20, 10, Img.Solid(20, 10, 10, 250, 10));

    // ---------------------------------------------------------------- 例外の作り方(25 段の再帰の最も深いところの Boom)

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Boom() => throw new InvalidOperationException(Secret);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int n)
    {
        if (n == 0)
        {
            Boom();
            return 0;
        }
        var r = Recurse(n - 1);
        return r + 1;
    }

    private static Exception MakeBoom()
    {
        try
        {
            Recurse(25);
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
        throw new InvalidOperationException("例外が起きなかった");
    }

    // ---------------------------------------------------------------- 組み立て

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly CallOrder Order = new();
        public readonly FakeFileSystem Fs = new();
        public readonly FakeClock Clock = FakeClock.ForPhase5();
        public readonly FakeCrashDialog Dialog;
        public readonly FakeProcessExit Exit;
        public readonly RecordingLog Log;
        public readonly JsonlLogWriter Writer;
        public readonly EditorSession Session = SessionTestKit.NewSession();
        public readonly SessionImportTarget Target;
        public CrashRecoverySaver? Saver;

        public Rig(bool twoLayers = true)
        {
            Dialog = new FakeCrashDialog(Order);
            Exit = new FakeProcessExit(Order);
            Writer = new JsonlLogWriter(LogsDir, Clock, "9.9.9");
            Log = new RecordingLog(Order, Writer);
            Target = new SessionImportTarget(Session);
            Fs.Observer = (op, path) =>
            {
                if (op == "Create" && path.EndsWith(".glint.tmp", StringComparison.Ordinal)) Order.Add("fs:tmp");
                if (op == "Move" && path.EndsWith(".glint", StringComparison.Ordinal)) Order.Add("fs:rename");
            };
            if (!twoLayers) return;
            new ImageImporter(Target, new MemoryLogSink()).Open([Dir.Write("a.png", PngA), Dir.Write("b.png", PngB)]).CompleteDisplay();
            Session.SetLayerOpacity(1, 80);
            Session.SetLayerPlacement(1, 10, 20, 50);
        }

        public string LogsDir => Dir.PathOf("logs");

        public string RecoveryDir => Dir.PathOf("recovery");

        public string RecoveryPath => Path.Combine(RecoveryDir, RecoveryName);

        public CrashHandler Handler() => new(Log, Fs, Dialog, Exit, Clock, RecoveryDir, "9.9.9",
            () => Session.Document is { } d ? new CrashSnapshot(d, Target.Images) : null, Saver);

        public string LogText => string.Concat(Directory.EnumerateFiles(LogsDir).Select(File.ReadAllText));

        public IReadOnlyList<string> Steps(params string[] prefixes) =>
            Order.Steps.Where(s => prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal))).ToList();

        public string[] RecoveryFiles => Directory.Exists(RecoveryDir) ? Directory.GetFiles(RecoveryDir) : [];

        public void Dispose() => Dir.Dispose();
    }

    private static string NoSaveText => NoticeTexts.DialogMessage(NoticeKeys.CrashNoSave);

    /// <summary>退避が無かったときの共通の確かめ(E-47)。</summary>
    private static void ExpectNoSave(Rig rig, string what)
    {
        Check.Equal(0, rig.RecoveryFiles.Length, what + ": 退避のフォルダにファイルが残らない(.tmp も)");
        Check.Equal(1, rig.Dialog.Shown.Count, what + ": ダイアログ");
        Check.Equal(NoSaveText, rig.Dialog.Shown[0].Body, what + ": 本文");
        var steps = rig.Steps("log:");
        Check.SequenceEqual(["log:crash", "log:app_exit"], steps, what + ": ログ");
        Check.SequenceEqual([1], rig.Exit.Codes, what + ": 終了の依頼");
    }

    // ---------------------------------------------------------------- (1) 登録

    private sealed class CountingCrash : ICrashHandler
    {
        public readonly List<(Exception Exception, CrashSource Source)> Calls = [];

        public void Handle(Exception exception, CrashSource source) => Calls.Add((exception, source));
    }

    private sealed class RegistrationApp(FakeExceptionSource source, CountingCrash crash, ManualResetEventSlim running, ManualResetEventSlim stop) : IRunningApp
    {
        public ICrashHandler Crash => crash;

        public IUnhandledExceptionSource Exceptions => source;

        public void ReceiveFromSecondInstance(string? path)
        {
        }

        public int Run()
        {
            running.Set();
            stop.Wait(TimeSpan.FromSeconds(20));
            return 0;
        }

        public void Dispose()
        {
        }
    }

    [Test]
    public void AC50_1_起動処理が発生元ごとに1個ずつ登録し入口を発生元付きで呼ぶ()
    {
        var source = new FakeExceptionSource();
        var crash = new CountingCrash();
        using var running = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        var ports = new StartupPorts(Guid.NewGuid().ToString("N"), _ => new RegistrationApp(source, crash, running, stop));
        var code = -1;
        var t = new Thread(() => code = AppStartup.Run([], ports)) { IsBackground = true };
        t.Start();
        Check.True(running.Wait(TimeSpan.FromSeconds(10)), "起動処理がメッセージのループまで進まない");

        Check.Equal(1, source.UiThreadHandlers, "UI スレッド用のハンドラー");
        Check.Equal(1, source.AppDomainHandlers, "AppDomain 用のハンドラー");
        var ui = new InvalidOperationException("ui");
        var domain = new InvalidOperationException("domain");
        source.RaiseUiThread(ui);
        source.RaiseAppDomain(domain);
        Check.Equal(2, crash.Calls.Count, "入口の呼び出し");
        Check.True(ReferenceEquals(ui, crash.Calls[0].Exception) && crash.Calls[0].Source == CrashSource.UiThread, "UI スレッドの発生元");
        Check.True(ReferenceEquals(domain, crash.Calls[1].Exception) && crash.Calls[1].Source == CrashSource.AppDomain, "AppDomain の発生元");

        stop.Set();
        Check.True(t.Join(TimeSpan.FromSeconds(10)), "起動処理が戻らない");
        Check.Equal(0, code, "終了コード");
    }

    [Test]
    public void AC50_1_製品の発生元はDispatcherの未処理例外を処理済みにしてイベントへ渡す()
    {
        Exception? seen = null;
        var handled = false;
        var thrown = new InvalidOperationException("ui");
        var t = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            var source = new WpfUnhandledExceptionSource(d);
            source.UiThreadException += e => seen = e;
            d.UnhandledException += (_, e) => handled = e.Handled;
            d.BeginInvoke(() => throw thrown);
            d.BeginInvoke(() => d.InvokeShutdown(), DispatcherPriority.ApplicationIdle);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Check.True(t.Join(TimeSpan.FromSeconds(10)), "スレッドが終わらない");
        Check.True(ReferenceEquals(thrown, seen), "イベントに渡らない");
        Check.True(handled, "処理済みにしていない(WPF が投げ直してしまう)");
    }

    // ---------------------------------------------------------------- (2) 順序

    [Test]
    public void AC50_2_順序はcrash_退避_ダイアログ_app_exit_終了で発生元によらない()
    {
        foreach (var source in new[] { CrashSource.UiThread, CrashSource.AppDomain })
        {
            using var rig = new Rig();
            var existedAtDialog = false;
            rig.Dialog.OnShow = () => existedAtDialog = File.Exists(rig.RecoveryPath);
            rig.Handler().Handle(MakeBoom(), source);
            Check.SequenceEqual(["log:crash", "fs:tmp", "fs:rename", "dialog", "log:app_exit", "exit:1"], rig.Order.Steps, $"{source} の順");
            Check.True(existedAtDialog, $"{source}: ダイアログの時点で退避が最終的な名前になっている");
        }
    }

    // ---------------------------------------------------------------- (3) 退避のファイル

    [Test]
    public void AC50_3_退避を製品の開く処理で開くとJSONとソースのバイト列が一致する()
    {
        using var rig = new Rig();
        var before = rig.Session.Json;
        Check.True(before!.Contains("\"opacity\":80", StringComparison.Ordinal) || before.Contains("\"opacity\": 80", StringComparison.Ordinal), "不透明度 80 の文書で調べる");
        rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);

        Check.SequenceEqual([rig.RecoveryPath], rig.RecoveryFiles, "退避のファイルの名前と個数(.tmp が残らない)");
        var opened = GlintProjectFile.Open(rig.RecoveryPath, SessionTestKit.Catalog);
        var fresh = SessionTestKit.NewSession(seed: 1);
        var target = new SessionImportTarget(fresh);
        fresh.NewEmptyCanvas(64, 48);
        Check.Equal(ReplaceResult.Replaced, target.OpenDocument(opened.Document, opened.Images), "開く");
        Check.Equal(before, fresh.Json, "JSON");
        Check.Equal(2, opened.Images.Count, "ソースの枚数");
        var bytes = opened.Images.Select(i => i.Encoded).ToList();
        Check.True(bytes.Any(b => b.SequenceEqual(PngA)) && bytes.Any(b => b.SequenceEqual(PngB)), "ソースのバイト列が取り込んだ画像と一致する");
    }

    // ---------------------------------------------------------------- (4) ダイアログ

    [Test]
    public void AC50_4_ダイアログはタイトルと終了ボタン1個で本文に退避の絶対パス()
    {
        using var rig = new Rig();
        rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
        var shown = rig.Dialog.Shown.Single();
        Check.Equal("エラーが起きました", shown.Title, "タイトル");
        Check.SequenceEqual(["終了"], shown.Buttons, "ボタン");
        Check.True(Path.IsPathFullyQualified(rig.RecoveryPath) && shown.Body.Contains(rig.RecoveryPath, StringComparison.Ordinal), "本文に退避の絶対パス");
        Check.True(!shown.Body.Contains(NoSaveText, StringComparison.Ordinal), "crash.nosave を含まない");
    }

    [Test]
    public void AC50_4_製品のダイアログは自分のスレッドで作れて資源が解決される()
    {
        // 製品と同じく Application がある状態で調べる(pack の URI は Application が登録する)
        _ = UiHost.Dispatcher;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.HighContrast })
        {
            string? title = null;
            List<string>? buttons = null;
            string? text = null;
            double width = 0;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    var w = WpfCrashDialog.Build("エラーが起きました", @"C:\退避\recovery.glint", ["終了"], theme);
                    w.Left = AppShell.OffscreenLeft;
                    w.Top = 0;
                    w.ShowActivated = false;
                    w.ShowInTaskbar = false;
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(() =>
                    {
                        title = w.Title;
                        var found = Descendants(w).OfType<Button>().ToList();
                        buttons = found.Select(b => (string)b.Content).ToList();
                        text = string.Concat(Descendants(w).OfType<TextBlock>().Select(b => b.Text));
                        width = found.Single().ActualWidth;
                        found.Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }, DispatcherPriority.ApplicationIdle);
                    w.ShowDialog();
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            })
            {
                IsBackground = true,
            };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Check.True(t.Join(TimeSpan.FromSeconds(20)), $"{theme}: ボタンで閉じない");
            Check.True(failure is null, $"{theme}: {failure}");
            Check.Equal("エラーが起きました", title, $"{theme}: タイトル");
            Check.SequenceEqual(["終了"], buttons!, $"{theme}: ボタン");
            Check.True(text!.Contains(@"C:\退避\recovery.glint", StringComparison.Ordinal), $"{theme}: 本文");
            Check.True(width > 0, $"{theme}: ボタンが描かれている");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    // ---------------------------------------------------------------- (5) ログ

    private static readonly Regex ForbiddenInStack = new(@"[\s()\\/:]|\.cs", RegexOptions.Compiled);

    [Test]
    public void AC50_5_crashは型とスタック20個で禁止の文字もメッセージも無くapp_exitは整数の1()
    {
        using var rig = new Rig();
        rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
        var lines = rig.LogText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check.Equal(2, lines.Length, "ログの行数");
        using var crash = System.Text.Json.JsonDocument.Parse(lines[0]);
        Check.Equal("crash", crash.RootElement.GetProperty("event").GetString(), "1 行目");
        Check.Equal("System.InvalidOperationException", crash.RootElement.GetProperty("type").GetString(), "type");
        var stack = crash.RootElement.GetProperty("stack").EnumerateArray().Select(e => e.GetString()!).ToList();
        Check.Equal(20, stack.Count, "stack の個数");
        Check.True(stack[0].EndsWith("Boom", StringComparison.Ordinal), "先頭が Boom で終わる: " + stack[0]);
        foreach (var s in stack) Check.True(!ForbiddenInStack.IsMatch(s), "禁止の文字: " + s);

        var all = rig.LogText;
        Check.True(!all.Contains("読めない", StringComparison.Ordinal) && !all.Contains(@"C:\Users", StringComparison.Ordinal) && !all.Contains(@"C:\\Users", StringComparison.Ordinal), "メッセージとパスが無い");
        Check.True(!Regex.IsMatch(all, @"[\u3040-\u30FF\u4E00-\u9FFF]"), "日本語の文字列が無い");

        using var exit = System.Text.Json.JsonDocument.Parse(lines[1]);
        Check.Equal("app_exit", exit.RootElement.GetProperty("event").GetString(), "2 行目");
        Check.Equal(System.Text.Json.JsonValueKind.Number, exit.RootElement.GetProperty("code").ValueKind, "code は数値");
        Check.Equal(1, exit.RootElement.GetProperty("code").GetInt32(), "code");
    }

    private sealed class GenericHolder<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw<U>() => throw new GenericFailure<T>();
    }

    private sealed class GenericFailure<T> : Exception;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void 日本語の名前のメソッド() => GenericHolder<string>.Throw<int>();

    [Test]
    public void AC50_5_総称型と日本語の名前のフレームでもstackとtypeに禁止の文字が入らない()
    {
        Exception caught;
        try
        {
            日本語の名前のメソッド();
            throw new InvalidOperationException("例外が起きなかった");
        }
        catch (GenericFailure<string> e)
        {
            caught = e;
        }
        var ev = LogEvents.Crash(caught);
        var type = (string)ev.Get("type")!;
        var stack = (IReadOnlyList<string>)ev.Get("stack")!;
        Check.True(stack.Count >= 2, "フレームがある");
        foreach (var s in stack.Append(type))
        {
            Check.True(!ForbiddenInStack.IsMatch(s), "禁止の文字: " + s);
            Check.True(!Regex.IsMatch(s, @"[^\x00-\x7F]"), "ASCII の外の文字: " + s);
            Check.True(!s.Contains(',') && !s.Contains('['), "型の引数の並びを残さない: " + s);
        }
        Check.True(type.Contains("GenericFailure", StringComparison.Ordinal), "型の名前が残る: " + type);
    }

    // ---------------------------------------------------------------- (6) 退避できないとき

    [Test]
    public void AC50_6_文書が無いときは退避を作らずnosave()
    {
        using var rig = new Rig(twoLayers: false);
        rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
        ExpectNoSave(rig, "取り込む前");
        Check.Equal(0, rig.Fs.Count("Create"), "ファイルを作らない");

        using var closed = new Rig();
        closed.Session.CloseDocument();
        closed.Handler().Handle(MakeBoom(), CrashSource.AppDomain);
        ExpectNoSave(closed, "新規のあとのウェルカム画面");
    }

    [Test]
    public void AC50_6_退避の書き込みの例外はnosaveになり何も残らない()
    {
        foreach (var op in new[] { "CreateDirectory", "Create", "Move" })
        {
            using var rig = new Rig();
            rig.Fs.FailOn(op, p => p.Contains("recovery", StringComparison.Ordinal));
            rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
            ExpectNoSave(rig, op);
            Check.True(rig.Fs.Count(op) >= 1, op + ": 失敗させた操作が呼ばれている");
        }
    }

    [Test]
    public void AC50_6_退避のJSONの作成の例外と画像の欠けはnosaveになる()
    {
        using (var rig = new Rig())
        {
            var called = 0;
            rig.Saver = (_, _) =>
            {
                called++;
                throw new InvalidOperationException("JSON を作れない");
            };
            rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
            ExpectNoSave(rig, "JSON の作成");
            Check.Equal(1, called, "作成が呼ばれる");
        }
        using (var rig = new Rig())
        {
            // 製品の Save の経路: 文書が参照する画像が無いと書く途中で例外になり、.tmp を残さない
            var handler = new CrashHandler(rig.Log, rig.Fs, rig.Dialog, rig.Exit, rig.Clock, rig.RecoveryDir, "9.9.9",
                () => new CrashSnapshot(rig.Session.Document!, new Dictionary<string, ImportedImage>()));
            handler.Handle(MakeBoom(), CrashSource.UiThread);
            ExpectNoSave(rig, "画像の欠け");
        }
        using (var rig = new Rig())
        {
            var handler = new CrashHandler(rig.Log, rig.Fs, rig.Dialog, rig.Exit, rig.Clock, rig.RecoveryDir, "9.9.9",
                () => throw new InvalidOperationException("文書を読めない"));
            handler.Handle(MakeBoom(), CrashSource.UiThread);
            ExpectNoSave(rig, "文書の読み取りの例外");
        }
    }

    // ---------------------------------------------------------------- (7) 失敗しても終わる

    [Test]
    public void AC50_7_ログが投げても退避とダイアログと終了まで進む()
    {
        using var rig = new Rig();
        rig.Log.Throw = new IOException("満杯");
        rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
        Check.SequenceEqual(["log:crash", "fs:tmp", "fs:rename", "dialog", "log:app_exit", "exit:1"], rig.Order.Steps, "順");
        Check.True(File.Exists(rig.RecoveryPath), "退避がある");
    }

    [Test]
    public void AC50_7_ダイアログが投げても終了の依頼が1回出る()
    {
        using var rig = new Rig();
        rig.Dialog.Throw = new InvalidOperationException("開けない");
        rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
        Check.SequenceEqual([1], rig.Exit.Codes, "終了の依頼");
        Check.Equal(1, rig.Steps("log:app_exit").Count, "app_exit も書く");
    }

    // ---------------------------------------------------------------- (8) 繰り返さない

    [Test]
    public void AC50_8_ダイアログの表示中の呼び出しは同じスレッドでも別のスレッドでも何もせず戻る()
    {
        using var rig = new Rig();
        var handler = rig.Handler();
        var otherReturned = false;
        rig.Dialog.OnShow = () =>
        {
            handler.Handle(new InvalidOperationException("同じスレッド"), CrashSource.UiThread);
            var t = new Thread(() =>
            {
                handler.Handle(new InvalidOperationException("別のスレッド"), CrashSource.AppDomain);
                otherReturned = true;
            })
            {
                IsBackground = true,
            };
            t.Start();
            Check.True(t.Join(TimeSpan.FromSeconds(10)), "別のスレッドの呼び出しが戻らない(ダイアログを待っている)");
        };
        handler.Handle(MakeBoom(), CrashSource.UiThread);
        Check.True(otherReturned, "別のスレッドの呼び出しが戻る");
        Check.Equal(1, rig.Steps("log:crash").Count, "crash の行");
        Check.Equal(1, rig.Dialog.Shown.Count, "ダイアログ");
        Check.SequenceEqual([1], rig.Exit.Codes, "終了の依頼");
        Check.Equal(1, rig.RecoveryFiles.Length, "退避");
    }

    [Test]
    public void AC50_8_終わったあとの呼び出しも何もしない()
    {
        using var rig = new Rig();
        var handler = rig.Handler();
        handler.Handle(MakeBoom(), CrashSource.UiThread);
        handler.Handle(MakeBoom(), CrashSource.AppDomain);
        Check.Equal(1, rig.Dialog.Shown.Count, "ダイアログ");
        Check.SequenceEqual([1], rig.Exit.Codes, "終了の依頼");
    }

    // ---------------------------------------------------------------- (9) 退避の個数

    private static string RecoveryFile(int hour) => $"recovery-20261014-{hour:00}0000.glint";

    [Test]
    public void AC50_9_起動時の整理は降順で5個を残し2個を消す()
    {
        using var dir = new TempDir();
        var paths = AppPaths.UnderOneRoot(dir.Path);
        Directory.CreateDirectory(paths.RecoveryDir);
        for (var h = 1; h <= 7; h++) File.WriteAllBytes(Path.Combine(paths.RecoveryDir, RecoveryFile(h)), [1]);
        StartupMaintenance.Run(paths);
        var left = Directory.GetFiles(paths.RecoveryDir).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check.SequenceEqual(Enumerable.Range(3, 5).Select(RecoveryFile).ToList(), left, "新しい 5 個が残る");
    }

    [Test]
    public void AC50_9_クラッシュの処理の中では消さず5個あれば6個になる()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(rig.RecoveryDir);
        for (var h = 1; h <= 5; h++) File.WriteAllBytes(Path.Combine(rig.RecoveryDir, RecoveryFile(h)), [1]);
        rig.Handler().Handle(MakeBoom(), CrashSource.UiThread);
        Check.Equal(6, rig.RecoveryFiles.Length, "6 個になる");
        Check.Equal(0, rig.Fs.Count("Delete"), "消さない");
    }

    // ---------------------------------------------------------------- シェルにつないだ形(担当 gpu と AC-58 が使う入口)

    [Test]
    public void AC50_シェルの入口は取り込んだ文書を退避しログをシェルの書き先に書く()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.png", PngA);
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        var screens = app.Ui(() => app.Shell.Modules.OfType<ScreensModule>().Single());
        app.Ui(() => { _ = screens.ImportStartupArgs([a]); });
        Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Session.HasDocument) && !screens.Imports.IsImporting, TimeSpan.FromSeconds(10)), "取り込みが終わらない");
        var json = app.Ui(() => app.Shell.Session.Json);

        app.Shell.Crash.Handle(MakeBoom(), CrashSource.AppDomain);

        var path = Path.Combine(app.Paths.RecoveryDir, RecoveryName);
        Check.True(File.Exists(path), "退避が Paths の recovery にできる");
        Check.Equal(path, app.CrashDialog.Shown.Single().Body, "本文は退避の絶対パス");
        Check.SequenceEqual([1], app.ProcessExit.Codes, "終了の依頼");
        var opened = GlintProjectFile.Open(path, app.Shell.Catalog);
        var fresh = new EditorSession(app.Shell.Catalog);
        var target = new SessionImportTarget(fresh);
        target.OpenDocument(opened.Document, opened.Images);
        Check.Equal(json, fresh.Json, "JSON");
        var log = File.ReadAllText(app.Shell.Log.CurrentPath!, Encoding.UTF8);
        Check.True(log.Contains("\"event\":\"crash\"", StringComparison.Ordinal) && log.Contains("\"event\":\"app_exit\"", StringComparison.Ordinal), "シェルのログに書く");
    }

    [Test]
    public void AC50_包みを渡すとシェルの入口がそれになる()
    {
        var counting = new CountingCrash();
        using var app = TestApp.Start(show: false, crashFactory: _ => counting);
        app.Shell.Crash.Handle(new InvalidOperationException("x"), CrashSource.UiThread);
        Check.Equal(1, counting.Calls.Count, "入口の呼び出し");
    }
}
