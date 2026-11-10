// AC-30: 設定のモデルと保存(FR-90・§9.11 の 2)・テーマの追従(FR-95)・Mica の背景(FR-96)・ハイコントラスト(FR-98)・ウィンドウの状態の復元(FR-106)。
// 設定画面そのもの(9 項目の UIA の確認)は設定画面の担当が検査する。ここは偽の時計・偽の OS の情報・一時フォルダで調べる。
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Platform;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class SettingsAc30Tests
{
    private static void WithPaths(Action<AppPaths> body)
    {
        var dir = Directory.CreateTempSubdirectory("glintloop-settings-");
        try
        {
            body(AppPaths.UnderOneRoot(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // §9.11 の 2 の表(キーの順と既定値)を書き写したもの
    private const string DefaultJson =
        "{\n  \"schema\": 1,\n  \"theme\": \"system\",\n  \"copyMode\": \"both\",\n  \"defaultFormat\": \"gif\",\n  \"defaultSaveDir\": \"\",\n" +
        "  \"lastOpenDir\": \"\",\n  \"lastSaveDir\": \"\",\n  \"reduceMotion\": \"system\",\n  \"cacheLimitMB\": 1024,\n" +
        "  \"bgPreview\": {\n    \"mode\": \"checker\",\n    \"color\": \"#808080\"\n  },\n  \"window\": {},\n  \"lastExport\": {}\n}\n";

    [Test]
    public void AC30_1_settings_jsonの既定とBOMなしとインデント2()
    {
        WithPaths(paths =>
        {
            var store = new SettingsStore(paths, new FakeClock(), NullLogSink.Instance);
            Check.True(store.SaveNow(), "保存できない");
            var bytes = File.ReadAllBytes(paths.SettingsFile);
            Check.True(!(bytes[0] == 0xEF && bytes[1] == 0xBB), "BOM がある");
            Check.Equal(DefaultJson, Encoding.UTF8.GetString(bytes), "既定の settings.json");
        });
    }

    [Test]
    public void AC30_1_最後の変更から500ms後に1回だけ保存する()
    {
        WithPaths(paths =>
        {
            var clock = new FakeClock();
            var store = new SettingsStore(paths, clock, NullLogSink.Instance);
            store.Settings.Theme = ThemeSetting.Dark;
            clock.Advance(TimeSpan.FromMilliseconds(150));
            store.Settings.CopyMode = CopyModeSetting.File;
            clock.Advance(TimeSpan.FromMilliseconds(150));
            store.Settings.CacheLimitMB = 2048;
            // 300 ms の間に 3 回変えた。最後の変更から 349 ms ではまだ書かない(500 − 150)
            clock.Advance(TimeSpan.FromMilliseconds(349));
            Check.Equal(0, store.SaveCount, "350 ms 前に書いた");
            Check.True(!File.Exists(paths.SettingsFile), "350 ms 前にファイルがある");
            clock.Advance(TimeSpan.FromMilliseconds(151));
            Check.Equal(1, store.SaveCount, "最後の変更から 500 ms で 1 回");
            clock.Advance(TimeSpan.FromSeconds(5));
            Check.Equal(1, store.SaveCount, "そのあとも 1 回だけ");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(paths.SettingsFile));
            Check.Equal("dark", doc.RootElement.GetProperty("theme").GetString(), "theme");
            Check.Equal("file", doc.RootElement.GetProperty("copyMode").GetString(), "copyMode");
            Check.Equal(2048, doc.RootElement.GetProperty("cacheLimitMB").GetInt32(), "cacheLimitMB");
            Check.Equal(SettingsStore.SaveDelay, TimeSpan.FromMilliseconds(500), "保存までの時間");
        });
    }

    [Test]
    public void AC30_1_キャッシュの上限は範囲の端に直り初期化は6項目だけを戻す()
    {
        var s = new AppSettings();
        s.CacheLimitMB = 100;
        Check.Equal(256, s.CacheLimitMB, "256 未満");
        s.CacheLimitMB = 99999;
        Check.Equal(4096, s.CacheLimitMB, "4096 超");
        s.CacheLimitMB = 777;
        Check.Equal(777, s.CacheLimitMB, "範囲内");

        s.Theme = ThemeSetting.Light;
        s.CopyMode = CopyModeSetting.Image;
        s.DefaultSaveDir = "D:\\out";
        s.ReduceMotion = ReduceMotionSetting.On;
        s.LastOpenDir = "D:\\open";
        s.LastSaveDir = "D:\\save";
        s.BgPreview = new BgPreview(BgPreviewMode.Custom, 0x123456);
        s.Window = new WindowSettings(10, 20, 1300, 820, false, 300, 330);
        s.LastExport = new JsonObject { ["format"] = "gif", ["size"] = new JsonObject { ["mode"] = "auto" } };
        s.SetDefaultFormatFromSettingsScreen(ExportFormatSetting.Webp);
        Check.Equal("webp", (string?)s.LastExport!["format"], "設定画面で既定の形式を変えたときの lastExport.format");

        s.ResetToDefaults();
        Check.Equal(ThemeSetting.System, s.Theme, "theme");
        Check.Equal(CopyModeSetting.Both, s.CopyMode, "copyMode");
        Check.Equal(ExportFormatSetting.Gif, s.DefaultFormat, "defaultFormat");
        Check.Equal("", s.DefaultSaveDir, "defaultSaveDir");
        Check.Equal(ReduceMotionSetting.System, s.ReduceMotion, "reduceMotion");
        Check.Equal(1024, s.CacheLimitMB, "cacheLimitMB");
        Check.Equal("D:\\open", s.LastOpenDir, "lastOpenDir は変えない");
        Check.Equal("D:\\save", s.LastSaveDir, "lastSaveDir は変えない");
        Check.Equal(new BgPreview(BgPreviewMode.Custom, 0x123456), s.BgPreview, "bgPreview は変えない");
        Check.Equal(new WindowSettings(10, 20, 1300, 820, false, 300, 330), s.Window, "window は変えない");
        Check.Equal("webp", (string?)s.LastExport!["format"], "初期化で lastExport.format は変わらない");
    }

    [Test]
    public void AC30_1_不正な値は既定に直してログに書きschemaが違えばbadに改名する()
    {
        WithPaths(paths =>
        {
            Directory.CreateDirectory(paths.RoamingRoot);
            File.WriteAllText(paths.SettingsFile,
                "{\"schema\":1,\"theme\":\"blue\",\"copyMode\":\"file\",\"cacheLimitMB\":9000,\"bgPreview\":{\"mode\":\"x\"},\"unknown\":3,\"lastExport\":5}");
            var log = new MemoryLogSink();
            var store = new SettingsStore(paths, new FakeClock(), log);
            Check.Equal(SettingsLoadStatus.Ok, store.Load(), "読み込みの結果");
            Check.Equal(ThemeSetting.System, store.Settings.Theme, "不正な theme");
            Check.Equal(CopyModeSetting.File, store.Settings.CopyMode, "正しい copyMode");
            Check.Equal(1024, store.Settings.CacheLimitMB, "ファイルの範囲外の cacheLimitMB は既定に直す");
            Check.Equal(BgPreview.Default, store.Settings.BgPreview, "不正な bgPreview");
            Check.True(store.Settings.LastExport is null, "不正な lastExport");
            var keys = log.Events.Where(e => e.Name == "settings_invalid").Select(e => (string)e.Get("key")!).ToList();
            Check.SequenceEqual(["theme", "cacheLimitMB", "bgPreview", "lastExport"], keys, "settings_invalid の key");

            File.WriteAllText(paths.SettingsFile, "{\"schema\":2,\"theme\":\"dark\"}");
            var log2 = new MemoryLogSink();
            var store2 = new SettingsStore(paths, new FakeClock(), log2);
            Check.Equal(SettingsLoadStatus.WrongSchema, store2.Load(), "schema が 2");
            Check.Equal(ThemeSetting.System, store2.Settings.Theme, "schema が違うときは既定");
            Check.True(File.Exists(paths.SettingsFile + ".bad") && !File.Exists(paths.SettingsFile), "settings.json.bad に改名しない");
            Check.Equal("schema", (string?)log2.Events.Single().Get("key"), "schema の settings_invalid");

            File.WriteAllText(paths.SettingsFile, "{ 壊れた");
            var log3 = new MemoryLogSink();
            Check.Equal(SettingsLoadStatus.Broken, new SettingsStore(paths, new FakeClock(), log3).Load(), "壊れた JSON");
            Check.Equal("file", (string?)log3.Events.Single().Get("key"), "壊れた JSON の settings_invalid");
        });
    }

    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    [Test]
    public void AC30_2_システムに合わせるはOSのアプリのテーマに追従しライトとダークは固定()
    {
        var os = new FakeOsInfo { AppsUseLightTheme = false };
        using var app = TestApp.Start(os: os);
        app.Ui(() =>
        {
            Color Dark(string k) => (Color)AppResources.DarkTokens[k];
            Color Light(string k) => (Color)AppResources.LightTokens[k];
            Check.Equal(Dark("BgBase"), BrushColor("BgBaseBrush"), "起動(OS はダーク)");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            os.AppsUseLightTheme = true;
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(1), "切り替えに 1 秒以上かかった");
            Check.Equal(Light("BgBase"), BrushColor("BgBaseBrush"), "OS をライトにしたあとの BgBaseBrush");
            Check.Equal(Light("Surface1"), BrushColor("Surface1Brush"), "OS をライトにしたあとの Surface1Brush");
            Check.Equal(ThemeMode.Light, Application.Current.ThemeMode, "Fluent の ThemeMode");
            os.AppsUseLightTheme = false;
            Check.Equal(Dark("Surface1"), BrushColor("Surface1Brush"), "OS をダークに戻したあと");

            app.Shell.Settings.Theme = ThemeSetting.Light;
            os.AppsUseLightTheme = false;
            Check.Equal(Light("BgBase"), BrushColor("BgBaseBrush"), "ライトに固定");
            os.AppsUseLightTheme = true;
            app.Shell.Settings.Theme = ThemeSetting.Dark;
            Check.Equal(Dark("BgBase"), BrushColor("BgBaseBrush"), "ダークに固定(OS はライト)");
            Check.Equal(Dark("Surface1"), BrushColor("Surface1Brush"), "ダークに固定の Surface1Brush");
        });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [Test]
    public void AC30_3_ビルド22621以上はMicaで背景が透明_22620以下は不透明のbg_base()
    {
        using (var app = TestApp.Start(os: new FakeOsInfo { BuildNumber = 22621 }))
        {
            var hr = DwmGetWindowAttribute(app.Hwnd, 38, out var backdrop, sizeof(int));
            Check.Equal(0, hr, "DwmGetWindowAttribute の HRESULT");
            Check.Equal(2, backdrop, "DWMWA_SYSTEMBACKDROP_TYPE(DWMSBT_MAINWINDOW = 2)");
            app.Ui(() =>
            {
                var bg = app.Shell.Window.Background as SolidColorBrush;
                Check.True(bg is not null && bg.Color.A == 0, $"背景が透明でない: {bg?.Color}");
            });
        }
        using (var app = TestApp.Start(os: new FakeOsInfo { BuildNumber = 22620 }))
        {
            app.Ui(() =>
            {
                var bg = (SolidColorBrush)app.Shell.Window.Background;
                Check.Equal((Color)AppResources.DarkTokens["BgBase"], bg.Color, "22620 の背景");
                Check.Equal((byte)255, bg.Color.A, "22620 の背景の不透明度");
            });
        }
    }

    [Test]
    public void AC30_4_ハイコントラストではシステムの色になりフォーカスの輪が残る()
    {
        var os = new FakeOsInfo { BuildNumber = 26100 };
        using var app = TestApp.Start(os: os);
        app.Ui(() =>
        {
            os.HighContrast = true;
            var w = app.Shell.Window;
            Check.Equal(SystemColors.WindowColor, ((SolidColorBrush)w.Background).Color, "ウィンドウの背景");
            Check.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)w.Foreground).Color, "文字の色");
            Check.Equal(SystemColors.WindowTextColor, BrushColor("FocusRingBrush"), "フォーカスの輪の色");
            var b = w.CommandBar.Button(Icons.Settings);
            Keyboard.Focus(b);
            Check.True(b.IsKeyboardFocused && FocusRing.IsShown(b), $"ハイコントラストでフォーカスの輪が出ない(focused={b.IsKeyboardFocused} shown={FocusRing.IsShown(b)} dev={InputManager.Current.MostRecentInputDevice?.GetType().Name} layer={System.Windows.Documents.AdornerLayer.GetAdornerLayer(b) is not null} ring={FocusRing.GetIsEnabled(b)})");
            os.HighContrast = false;
            Check.Equal((Color)AppResources.DarkTokens["TextPrimary"], ((SolidColorBrush)w.Foreground).Color, "ハイコントラストを切ったあとの文字");
        });
    }

    [Test]
    public void AC30_5_ウィンドウの状態が保存され復元される_重ならない矩形はプライマリの中央()
    {
        // 純粋な関数(E-49)
        MonitorInfo[] mons = [new(new Rect(0, 0, 1920, 1040), true), new(new Rect(1920, 0, 1280, 1024), false)];
        var size = new Size(1280, 800);
        Check.Equal(new Rect(100, 50, 1200, 700), WindowPlacement.Restore(new WindowSettings(100, 50, 1200, 700, false, null, null), mons, size), "見える矩形");
        Check.Equal(new Rect(3000, 900, 1200, 700), WindowPlacement.Restore(new WindowSettings(3000, 900, 1200, 700, false, null, null), mons, size), "2 枚目と 200×124 で重なる");
        var center = new Rect(320, 120, 1280, 800);
        Check.Equal(center, WindowPlacement.Restore(new WindowSettings(5000, 100, 1200, 700, false, null, null), mons, size), "どのモニターとも重ならない");
        Check.Equal(center, WindowPlacement.Restore(new WindowSettings(3150, 950, 1200, 700, false, null, null), mons, size), "重なりが 50×74(100 未満)");
        Check.Equal(center, WindowPlacement.Restore(WindowSettings.Empty, mons, size), "保存が無い");

        // 起動し直して復元する(画面の外に出しているので、位置は起動時に決めた値を保存する)
        var os = new FakeOsInfo { Monitors = mons };
        var app = TestApp.Start(paths =>
        {
            var s = new SettingsStore(paths, new FakeClock(), NullLogSink.Instance);
            s.Settings.Window = new WindowSettings(200, 100, 1300, 820, false, null, null);
            s.SaveNow();
        }, os: os);
        try
        {
            Check.Equal(new Rect(200, 100, 1300, 820), app.Ui(() => app.Shell.InitialPlacement), "保存した矩形で開く");
            app.Ui(() =>
            {
                app.Shell.Window.SetPaneWidths(300, 360, PaneSide.Right);
                app.Shell.SaveWindowState();
            });
            var next = app.Restart();
            app = next;
            var (placement, left, right) = app.Ui(() => (app.Shell.InitialPlacement, app.Shell.Window.LeftPaneWidth, app.Shell.Window.RightPaneWidth));
            Check.Equal(new Point(200, 100), placement.TopLeft, "次の起動の位置");
            // 大きさはクライアント領域。DPI の丸めで 1 DIP 未満ずれうる
            Check.Near(1300, placement.Width, 1, "次の起動の幅");
            Check.Near(820, placement.Height, 1, "次の起動の高さ");
            Check.Equal(300.0, left, "左ペインの幅");
            Check.Equal(360.0, right, "右ペインの幅");
        }
        finally
        {
            app.Dispose();
        }
        var far = TestApp.Start(paths =>
        {
            var s = new SettingsStore(paths, new FakeClock(), NullLogSink.Instance);
            s.Settings.Window = new WindowSettings(-9000, -9000, 1300, 820, false, null, null);
            s.SaveNow();
        }, os: os);
        using (far)
            Check.Equal(center, far.Ui(() => far.Shell.InitialPlacement), "重ならない保存の矩形");
    }
}
