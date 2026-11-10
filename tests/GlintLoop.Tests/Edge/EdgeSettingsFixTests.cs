// AC-56 の (3)(5)(6) と AC-55 の (7) の、製品を直した分の周りの確かめ。壊れた設定の改名(E-42)、cacheLimitMB のファイルの値(E-42)、保存の失敗の両方の出し先(E-43)、
// ユーザーのルックの未知の id の印(E-24)、IHDR を自分で読む PNG のヘッダー(E-5・E-8)を調べる。主な場面のテストは EdgeAc55RejectTests と EdgeAc56Tests にある。
using System.IO;
using System.Text;
using System.Windows.Automation;
using System.Windows.Input;
using GlintLoop.App.Looks;
using GlintLoop.App.Screens;
using GlintLoop.Codec;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Stack;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeSettingsFixTests
{
    private static AppPaths TempPaths(TempDir dir)
    {
        var paths = AppPaths.UnderOneRoot(dir.Path);
        Directory.CreateDirectory(paths.RoamingRoot);
        return paths;
    }

    // ---------------------------------------------------------------- (5) 改名

    [Test]
    public void AC56_5_壊れた設定は前の回のbadを置き換えて新しいバイト列を残す()
    {
        using var dir = new TempDir();
        var paths = TempPaths(dir);
        var broken = Encoding.UTF8.GetBytes("{ 今回の壊れた設定");
        File.WriteAllBytes(paths.SettingsFile + ".bad", Encoding.UTF8.GetBytes("前の回"));
        File.WriteAllBytes(paths.SettingsFile, broken);
        Check.Equal(SettingsLoadStatus.Broken, new SettingsStore(paths, new FakeClock(), new MemoryLogSink()).Load(), "読み込みの結果");
        Check.SequenceEqual(broken, File.ReadAllBytes(paths.SettingsFile + ".bad"), "settings.json.bad のバイト列");
        Check.True(!File.Exists(paths.SettingsFile), "元の settings.json が残っている");
    }

    [Test]
    public void AC56_5_JSONの配列やnullのsettingsも壊れたものとして改名する()
    {
        foreach (var text in new[] { "[1,2]", "null", "3", "" })
        {
            using var dir = new TempDir();
            var paths = TempPaths(dir);
            var bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(paths.SettingsFile, bytes);
            Check.Equal(SettingsLoadStatus.Broken, new SettingsStore(paths, new FakeClock(), new MemoryLogSink()).Load(), text + " の読み込みの結果");
            Check.SequenceEqual(bytes, File.ReadAllBytes(paths.SettingsFile + ".bad"), text + " の settings.json.bad");
        }
    }

    [Test]
    public void AC56_5_読み取りの失敗は壊れたファイルでないので改名しない()
    {
        using var dir = new TempDir();
        var paths = TempPaths(dir);
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":1,\"theme\":\"dark\"}");
        File.WriteAllBytes(paths.SettingsFile, bytes);
        var fs = new FakeFileSystem();
        fs.FailOn(FileOps.ReadAllBytes, p => Path.GetFileName(p) == "settings.json");
        var log = new MemoryLogSink();
        Check.Equal(SettingsLoadStatus.Broken, new SettingsStore(paths, new FakeClock(), log, fs).Load(), "読み込みの結果");
        Check.Equal("file", (string?)log.Events.Single().Get("key"), "settings_invalid の key");
        Check.SequenceEqual(bytes, File.ReadAllBytes(paths.SettingsFile), "settings.json はそのまま");
        Check.True(!File.Exists(paths.SettingsFile + ".bad"), "改名された");
    }

    [Test]
    public void AC56_5_設定の読み込みはcacheLimitMBの不正を既定に直し範囲の中はそのまま読む()
    {
        foreach (var (value, want, bad) in new[]
                 {
                     ("256", 256, false),
                     ("4096", 4096, false),
                     ("2048", 2048, false),
                     ("255", 1024, true),
                     ("4097", 1024, true),
                     ("100", 1024, true),
                     ("9000", 1024, true),
                     ("-1", 1024, true),
                     ("1024.5", 1024, true),
                     ("\"abc\"", 1024, true),
                     ("null", 1024, true),
                 })
        {
            var into = new AppSettings();
            var log = new MemoryLogSink();
            var status = SettingsJson.Read(Encoding.UTF8.GetBytes("{\"schema\":1,\"cacheLimitMB\":" + value + "}"), into, log);
            Check.Equal(SettingsLoadStatus.Ok, status, value + " の読み込みの結果");
            Check.Equal(want, into.CacheLimitMB, value + " の値");
            var keys = log.Events.Where(e => e.Name == "settings_invalid").Select(e => (string?)e.Get("key")).ToList();
            Check.SequenceEqual(bad ? ["cacheLimitMB"] : Array.Empty<string?>(), keys, value + " の settings_invalid");
        }
    }

    [Test]
    public void AC30_1_設定のモデルへの代入は範囲の端に直す_ファイルの読み込みとは別の扱い()
    {
        var s = new AppSettings { CacheLimitMB = 100 };
        Check.Equal(256, s.CacheLimitMB, "256 未満");
        s.CacheLimitMB = 9000;
        Check.Equal(4096, s.CacheLimitMB, "4096 超");
    }

    // ---------------------------------------------------------------- (6) 保存の失敗

    [Test]
    public void AC56_6_設定画面を開いているときの保存の失敗は通知バーと上端の帯の両方に出る()
    {
        var fs = new FakeFileSystem();
        using var app = TestApp.Start(fileSystem: fs);
        var m = EditingScreenKit.M<ScreensModule>(app);
        fs.FailOn(FileOps.Create, p => Path.GetFileName(p).StartsWith("settings.json", StringComparison.Ordinal));
        Check.True(EditingScreenKit.Key(app, System.Windows.Input.Key.OemComma, ModifierKeys.Control), "Ctrl+カンマが処理されない");
        Check.True(UiHost.WaitUntil(() => m.Settings is not null, TimeSpan.FromSeconds(20)), "設定の画面が出ない");
        UiHost.Idle();
        app.Ui(() =>
        {
            app.Shell.Settings.Theme = ThemeSetting.Dark;
            app.Clock.Advance(TimeSpan.FromMilliseconds(500));
        });
        foreach (var (notice, what) in new[] { (app.Ui(() => app.Shell.NoticeBar.Current), "通知バー"), (app.Ui(() => m.Settings!.SaveFailedBand.Current), "設定画面の上端の帯") })
        {
            Check.Equal(NoticeKeys.SettingsSaveFailed, notice?.Key, what);
            Check.Equal(NoticeLevel.Error, notice!.Level, what + " の重さ");
        }
    }

    [Test]
    public void AC56_6_閉じるときの最後の保存の失敗は閉じたあとの通知バーに出さない()
    {
        var fs = new FakeFileSystem();
        var app = TestApp.Start(fileSystem: fs);
        fs.FailOn(FileOps.Create, p => Path.GetFileName(p).StartsWith("settings.json", StringComparison.Ordinal));
        app.Ui(() => app.Shell.Settings.Theme = ThemeSetting.Dark);
        app.Dispose();
        Check.True(app.Ui(() => app.Shell.NoticeBar.Current) is null, "閉じたあとに通知を出した");
    }

    // ---------------------------------------------------------------- (3) ユーザーのルック

    private static byte[] LookJson(params string[] ids) => Encoding.UTF8.GetBytes("{\"schema\":1,\"name\":\"印\",\"effects\":["
        + string.Join(",", ids.Select(id => "{\"id\":\"" + id + "\",\"amount\":100,\"params\":{}}")) + "]}");

    [Test]
    public void AC56_3_未知のidの印はカタログに無いidだけで立ちルックに使えないステージでは立たない()
    {
        var store = new UserLookStore("unused", StackTestKit.Catalog);
        Check.True(store.Read(LookJson("exposure", "no_such_effect"), "a.json")!.HasUnknownEffects, "カタログに無い id");
        Check.True(!store.Read(LookJson("exposure", "contrast"), "a.json")!.HasUnknownEffects, "すべて知っている id");
        var motion = store.Read(LookJson("exposure", "spin"), "a.json")!;
        Check.True(!motion.HasUnknownEffects, "ルックに使えないステージは未知でない");
        Check.SequenceEqual(["exposure"], motion.Effects.Select(e => e.Id).ToList(), "ルックに使えないステージの行は入らない");
    }

    [Test]
    public void AC56_3_名前を変えると未知の行の無いファイルに書き直すので印が消える()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "look-12345678.json"), LookJson("exposure", "no_such_effect"));
        var store = new UserLookStore(dir.Path, StackTestKit.Catalog);
        store.Load();
        Check.True(store.Looks.Single().HasUnknownEffects, "前提: 印がある");
        var renamed = store.Rename(store.Looks.Single(), "新しい名前");
        Check.True(!renamed.HasUnknownEffects, "名前の変更のあとも印が残る");
        store.Load();
        Check.True(!store.Looks.Single().HasUnknownEffects, "読み直しても印が無い");
    }

    private static void ApplyLook(TestApp app, string key) => app.Ui(() =>
    {
        var tile = EditingScreenKit.M<LooksModule>(app).Gallery.Tiles.Single(t => t.Key == key);
        Keyboard.Focus(tile);
        Check.True(UiInput.Key(tile, System.Windows.Input.Key.Enter), "タイルの Enter を処理しない");
    });

    [Test]
    public void AC56_3_未知の無いルックは通知を出さず未知のあるルックは適用のたびに出る()
    {
        using var app = TestApp.Start(paths =>
        {
            Directory.CreateDirectory(paths.LooksDir);
            File.WriteAllBytes(Path.Combine(paths.LooksDir, "look-11111111.json"), Encoding.UTF8.GetBytes("{\"schema\":1,\"name\":\"未知なし\",\"effects\":[{\"id\":\"exposure\",\"amount\":100,\"params\":{}}]}"));
            File.WriteAllBytes(Path.Combine(paths.LooksDir, "look-22222222.json"), Encoding.UTF8.GetBytes("{\"schema\":1,\"name\":\"未知あり\",\"effects\":[{\"id\":\"no_such_effect\",\"amount\":100,\"params\":{}},{\"id\":\"contrast\",\"amount\":100,\"params\":{}}]}"));
        });
        StackTestKit.Import(app, 64, 48, Img.Solid(64, 48, 10, 20, 30), "base");
        app.Ui(() => app.Shell.NoticeBar.Close());
        EditingScreenKit.SelectTab(app, LeftPaneTab.Looks);
        ApplyLook(app, "look-11111111");
        Check.True(app.Ui(() => app.Shell.NoticeBar.Current) is null, "未知の無いルックで通知が出た");
        ApplyLook(app, "look-22222222");
        Check.Equal(NoticeKeys.StackUnknownEffect, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "1 回目の適用");
        app.Ui(() => app.Shell.NoticeBar.Close());
        ApplyLook(app, "look-11111111");
        Check.True(app.Ui(() => app.Shell.NoticeBar.Current) is null, "別のルックで通知が出た");
        ApplyLook(app, "look-22222222");
        Check.Equal(NoticeKeys.StackUnknownEffect, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "2 回目の適用");
    }

    // ---------------------------------------------------------------- AC-55 の (7): PNG のヘッダー

    [Test]
    public void AC55_7_PNGのヘッダーはIHDRの幅と高さを読み不正な値はSKCodecの判定に任せて読めない()
    {
        var ok = ImageDecoder.ReadHeader(EdgeBytes.PngHeader(16385, 7), out _);
        Check.Equal(new ImageHeader(ImageFormat.Png, 16385, 7), ok, "33 バイトの幅と高さ");
        foreach (var (w, h, what) in new[] { (0, 1, "幅が 0"), (1, 0, "高さが 0"), (-1, 1, "幅の最上位のビットが立つ"), (1, -1, "高さの最上位のビットが立つ") })
        {
            Check.True(ImageDecoder.ReadHeader(EdgeBytes.PngHeader(w, h), out var why) is null, what + " を読めた");
            Check.Equal(ImportFailReason.DecodeError, why, what + " の理由");
        }
        var cut = EdgeBytes.PngHeader(16, 16)[..32];
        Check.True(ImageDecoder.ReadHeader(cut, out var cutWhy) is null, "IHDR の途中で切れた PNG を読めた");
        Check.Equal(ImportFailReason.DecodeError, cutWhy, "切れた PNG の理由");
    }

    [Test]
    public void AC55_7_E5_上限の内の33バイトのPNGはデコードへ進んでimport_decode_errorになる()
    {
        using var r = EdgeRig.Start();
        var small = r.Dir.Write("small.png", EdgeBytes.PngHeader(64, 64));
        r.Reject("8 ビット", () => r.Open(small), NoticeKeys.ImportDecodeError, NoticeLevel.Error, ["import_fail:decode_error"], decodes: 1);
        var sixteen = EdgeBytes.PngHeader(64, 64);
        sixteen[24] = 16; // ビット深度。CRC は見ない
        var path = r.Dir.Write("sixteen.png", sixteen);
        r.Reject("16 ビット", () => r.Open(path), NoticeKeys.ImportDecodeError, NoticeLevel.Error, ["import_fail:decode_error"], decodes: 1);
    }
}
