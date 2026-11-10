// AC-58: システムのエッジケース(E-40・E-41・E-39・E-46・E-47)を重ねた 1 本のシナリオ。偽のデバイス工場・偽の時計・偽のファイルシステム・偽のダイアログ・偽のプロセス終了と、
// 本物の CrashHandler と、本物の名前付きパイプ(テストごとの名前)を使う。退避を書けない実行と書ける実行の 2 回を流す。ログは Dispose の前に読む。
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using GlintLoop.App.Looks;
using GlintLoop.App.MotionTab;
using GlintLoop.App.Platform;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc58Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>入口の呼び出しを数えてから本物へ渡す包み。</summary>
    private sealed class CountingCrash(ICrashHandler inner) : ICrashHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Handle(Exception exception, CrashSource source)
        {
            Interlocked.Increment(ref _calls);
            inner.Handle(exception, source);
        }
    }

    private sealed class CountingForeground : IWindowForeground
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void BringToFront() => Interlocked.Increment(ref _count);
    }

    private static void Paste(TestApp app, byte[] png, string what)
    {
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, png);
        Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), what + ": Ctrl+V が処理されない");
        WaitImports(app);
    }

    /// <summary>タイルにフォーカスを当てて Enter で選ぶ(クリックとともに製品の入口。タイルは InvokePattern を持たない)。</summary>
    private static void PressTile(TestApp app, System.Windows.Controls.ListBoxItem tile, string what) => app.Ui(() =>
    {
        Keyboard.Focus(tile);
        Check.True(UiInput.Key(tile, System.Windows.Input.Key.Enter), what + ": Enter を処理しない");
    });

    private static void WaitShown(TestApp app, CanvasModule canvas) =>
        Check.True(UiHost.WaitUntil(() => !canvas.Processed.IsBusy && ReferenceEquals(canvas.Processed.PresentedDocument, app.Shell.Session.Document), Limit), "表示が更新されない");

    private static void CheckSubsequence(string[] expected, string[] actual, string what)
    {
        var at = 0;
        foreach (var e in actual)
        {
            if (at < expected.Length && e == expected[at]) at++;
        }
        Check.True(at == expected.Length, $"{what}: {string.Join(",", expected)} の順に現れない。実際: {string.Join(",", actual)}");
    }

    /// <summary>シナリオの全部を流す。canSave が偽なら退避の書き込みで例外を投げる。</summary>
    private static void Scenario(bool canSave)
    {
        var what = canSave ? "退避できる" : "退避できない";
        var factory = new FakeDeviceFactory { FailStartupCheck = true };
        var fs = new FakeFileSystem();
        CountingCrash? crash = null;
        using var app = TestApp.Start(clock: FakeClock.ForPhase5(), gpu: GpuAdapterRequest.HighPerformance, deviceFactory: factory, fileSystem: fs,
            crashFactory: s => crash = new CountingCrash(CrashHandler.ForShell(s)));
        Check.True(UiHost.WaitUntil(() => EdgeLog.Count(app, "app_start") == 1, Limit), what + ": app_start が書かれない");
        Check.True(app.Ui(() => app.Shell.RenderReady!.Wait(Limit)), what + ": 描画が始まらない");

        // (1) R16G16B16A16_FLOAT の確認に失敗するデバイスで、WARP で起動する
        Check.Equal("warp", EdgeLog.Last(app, "app_start").GetProperty("adapter").GetString(), what + ": app_start の adapter");
        Check.Equal("float_rt_unsupported", EdgeLog.Last(app, "warp_fallback").GetProperty("reason").GetString(), what + ": warp_fallback の reason");
        Check.True(!app.Ui(() => app.Shell.NoticeBar.IsShowing), what + ": 通知バーは空");

        // (2) 2 枚のレイヤーにして、2 枚目にルックとモーションを選ぶ
        Paste(app, SkiaBytes.Png(1024, 768, Img.Pattern(1024, 768, alpha: false)), "1 枚目");
        Paste(app, SkiaBytes.Png(256, 256, Img.Pattern(256, 256, alpha: false)), "2 枚目");
        var session = app.Shell.Session;
        Check.Equal(2, app.Ui(() => session.Document!.Layers.Count), what + ": 2 枚のレイヤー");
        Check.Equal(Target.OfLayer(1), app.Ui(() => session.Selected), what + ": 2 枚目を選んでいる");
        var looks = M<LooksModule>(app);
        var motion = M<MotionTabModule>(app);
        var vivid = app.Ui(() => looks.Gallery.Tiles.Single(t => t.DisplayName == "ビビッド"));
        PressTile(app, vivid, "ビビッド");
        SelectTab(app, LeftPaneTab.Motion);
        var spin = app.Ui(() => motion.View.Tiles.Single(t => t.DisplayName == "くるくる"));
        PressTile(app, spin, "くるくる");
        UiHost.Idle();
        var canvas = M<CanvasModule>(app);
        WaitShown(app, canvas);
        var stack = app.Ui(() => session.Document!.Layers[1].Stack);
        Check.True(stack.Any(e => e.Origin == EffectOrigin.Look), what + ": 2 枚目のスタックにルック: " + string.Join(",", stack.Select(e => e.Id)));
        Check.True(stack.Any(e => e.Id == "spin" && e.Origin == EffectOrigin.User), what + ": 2 枚目のスタックにモーション");

        // (3) 画面の描画のメモリ不足は、表示を残して render.failed を出し、次の変更で更新する
        var before = app.Ui(() => canvas.Processed.PresentedImage!.Pixels);
        factory.FailRender(FakeDeviceFactory.OutOfMemory, 1, RenderPriority.Display);
        app.Ui(() => session.SetLayerOpacity(1, 80));
        Check.True(UiHost.WaitUntil(() => app.Shell.NoticeBar.Current?.Key == NoticeKeys.RenderFailed, Limit), what + ": render.failed が出ない");
        Check.SequenceEqual(before, app.Ui(() => canvas.Processed.PresentedImage!.Pixels), what + ": 表示が直前のまま");
        Check.Equal(1, EdgeLog.Count(app, "render_fail"), what + ": render_fail");
        app.Ui(() => session.SetLayerOpacity(1, 60));
        WaitShown(app, canvas);
        var after = app.Ui(() => canvas.Processed.PresentedImage!.Pixels);
        Check.True(!before.AsSpan().SequenceEqual(after), what + ": 次の変更で表示が更新される");

        // (4) 描画の途中でデバイスが失われ、1 回目の作り直しも失敗する。2 回目を待つ間に、本物のパイプで 2 つ目の起動の連絡を受ける
        var foreground = new CountingForeground();
        var receiver = app.Ui(() => SecondInstanceReceiver.ForShell(app.Shell, foreground));
        var pipe = "glintloop-ac58-" + Guid.NewGuid().ToString("N");
        using var server = new SingleInstancePipeServer(pipe, receiver.Receive);
        server.Start();
        if (!canSave) fs.FailOn(FileOps.Write, p => p.EndsWith(".glint.tmp", StringComparison.OrdinalIgnoreCase));
        factory.FailNextCreates = 2;
        factory.FailRender(FakeDeviceFactory.DeviceRemoved, 1, RenderPriority.Display);
        app.Ui(() => session.SetLayerOpacity(1, 40));
        var recovery = app.Shell.Recovery!;
        Check.True(UiHost.WaitUntil(() => recovery.IsWaitingForRetry, Limit), what + ": 2 回目を待つ状態にならない");
        var json = app.Ui(() => session.Json);

        var watch = Stopwatch.StartNew();
        var acked = false;
        var client = Task.Run(() => acked = SingleInstanceClient.Send(pipe, null));
        Check.True(UiHost.WaitUntil(() => client.IsCompleted, TimeSpan.FromSeconds(3)), what + ": 3 秒以内に 2 つ目の起動が戻らない");
        watch.Stop();
        Check.True(acked, what + ": 0x06 が返る");
        Check.True(watch.Elapsed <= TimeSpan.FromSeconds(3), what + ": 時間 " + watch.Elapsed);
        Check.True(UiHost.WaitUntil(() => foreground.Count == 1, TimeSpan.FromSeconds(3)), what + ": 前面に出す動作が行われない(UI スレッドが止まっている)");
        Check.True(app.Ui(() => recovery.IsWaitingForRetry), what + ": 連絡のあいだも 2 回目を待っている");
        Check.Equal(0, crash!.Calls, what + ": まだ入口を呼ばない");

        // (5) 2 回目も失敗すると、すでに WARP なので gpu.software は出ず、入口が 1 回だけ呼ばれる
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1000)));
        Check.True(UiHost.WaitUntil(() => app.ProcessExit.Codes.Count > 0, Limit), what + ": 終了の依頼が来ない");
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(10)));
        UiHost.Idle();
        Check.Equal(1, crash.Calls, what + ": 入口は 1 回だけ");
        Check.True(app.Ui(() => app.Shell.NoticeBar.Current?.Key != NoticeKeys.GpuSoftware), what + ": gpu.software は出ない");

        // (6) 退避・ダイアログ・終了
        Check.SequenceEqual([1], app.ProcessExit.Codes, what + ": 終了の依頼は 1 回でコード 1");
        Check.Equal(1, EdgeLog.Last(app, "app_exit").GetProperty("code").GetInt32(), what + ": app_exit の code");
        var dialog = app.CrashDialog.Shown.Single();
        var recoveryFiles = Directory.Exists(app.Paths.RecoveryDir) ? Directory.GetFiles(app.Paths.RecoveryDir) : [];
        if (canSave)
        {
            var file = recoveryFiles.Single();
            Check.True(file.EndsWith(".glint", StringComparison.Ordinal), what + ": 退避の .glint: " + file);
            Check.Equal(file, dialog.Body, what + ": 本文は退避のパス");
            var opened = GlintProjectFile.Open(file, app.Shell.Catalog);
            var fresh = new EditorSession(app.Shell.Catalog);
            new SessionImportTarget(fresh).OpenDocument(opened.Document, opened.Images);
            Check.Equal(json, fresh.Json, what + ": 開いた JSON がクラッシュ直前のものと一致");
            Check.Equal(2, fresh.Document!.Layers.Count, what + ": 2 枚のレイヤー");
            var opendStack = fresh.Document.Layers[1].Stack;
            Check.True(opendStack.Any(e => e.Origin == EffectOrigin.Look) && opendStack.Any(e => e.Id == "spin"), what + ": 2 枚目のスタックにルックとモーション");
        }
        else
        {
            Check.Equal(0, recoveryFiles.Length, what + ": 退避のフォルダにファイルが残らない");
            Check.Equal(NoticeTexts.DialogMessage(NoticeKeys.CrashNoSave), dialog.Body, what + ": 本文は crash.nosave");
        }

        // (7) ログの順(Dispose の前に読む)
        var events = EdgeLog.Events(app);
        CheckSubsequence(["warp_fallback", "app_start", "import_ok", "render_fail", "device_lost", "crash", "app_exit"], events, what + ": ログの順");
        Check.True(!events.Contains("device_recreated"), what + ": device_recreated が無い");
        var text = EdgeLog.Text(app);
        foreach (var bad in new[] { Path.GetFileName(app.Root), ".png", ".glint", "recovery-", "glint-" })
            Check.True(!text.Contains(bad, StringComparison.OrdinalIgnoreCase), what + ": ログにファイルの名前とパスが含まれる: " + bad);
    }

    [Test(Slow = true)]
    public void AC58_1から7_退避を書けない実行は本文がcrash_nosaveでログの順が決まりどおり() => Scenario(canSave: false);

    [Test(Slow = true)]
    public void AC58_1から7_退避を書ける実行は開いたJSONがクラッシュ直前と一致しログの順が決まりどおり() => Scenario(canSave: true);
}
