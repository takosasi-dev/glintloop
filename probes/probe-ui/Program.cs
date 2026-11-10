// probe-ui: 仕様書 §8.4 の測定 3(Mica・ダークの属性・.NET の Fluent テーマ(ThemeMode)・モニターごとの DPI)の入口と操作。
// 起動の既定は WPF の Fluent と同じ手順(ThemeMode=System・Mica・ダークはテーマに合わせる・透明な背景)。ボタンと --press で組み合わせを変える。
// 引数: --smoke、--theme none|light|dark|system、--backdrop auto|none|mica|acrylic|tabbed、--dark on|off|auto、--opaque、--press a,b,…、--snap 画像.png。
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlintLoop.Probes.Ui;

static partial class Program
{
    static Window? window;
    static IntPtr hwnd;
    static bool smoke;
    static string themeArg = "system", backdropArg = "mica", darkArg = "auto";
    static string? snapPath;
    static readonly List<string> presses = [];

    // 今の選択(ボタンと --press で変わる)
    static int backdropSel = Native.DWMSBT_MAINWINDOW;
    static bool glassOn = true;
    static string marginsSet = "(まだ呼んでいない)";

    static void W(string s = "") => Console.WriteLine(s);

    static string Hr(int hr) => $"0x{hr:X8}{(hr == 0 ? "(S_OK)" : "")}";

    static string? ArgRaw(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static string Arg(string[] args, string name, string def) => ArgRaw(args, name)?.ToLowerInvariant() ?? def;

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        smoke = args.Contains("--smoke");
        glassOn = !args.Contains("--opaque");
        themeArg = Arg(args, "--theme", "system");
        backdropArg = Arg(args, "--backdrop", "mica");
        darkArg = Arg(args, "--dark", "auto");
        snapPath = ArgRaw(args, "--snap");
        presses.AddRange(Arg(args, "--press", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        backdropSel = BackdropValue(backdropArg);

        W("===== probe-ui (測定 3: Mica とテーマ) =====");
        PrintOs();
        W($"  引数: theme={themeArg} backdrop={backdropArg} dark={darkArg} 透明な背景={(glassOn ? "on" : "off(--opaque)")} press=[{string.Join(",", presses)}] snap={snapPath ?? "なし"} smoke={smoke}");
        W($"  スレッドの DPI 認識(窓を作る前): {Awareness(Native.GetThreadDpiAwarenessContext())}");
        W();

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        SetTheme(app, themeArg, "起動時");

        window = BuildWindow();
        if (smoke)
        {
            // smoke は画面から色を読むので、置き場所を主モニターの左上に決める(モニターの境目をまたがない)
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 40;
            window.Top = 40;
        }
        window.SourceInitialized += (_, _) => OnSourceInitialized();
        window.Loaded += (_, _) => { RefreshColors(); PrintState("Loaded のあと"); };
        window.ContentRendered += (_, _) => OnContentRendered();
        window.DpiChanged += (_, e) => W($"  DpiChanged: {e.OldDpi.PixelsPerInchX:F0} → {e.NewDpi.PixelsPerInchX:F0} dpi(拡大率 {e.NewDpi.DpiScaleX * 100:F0}%)");
        app.Run(window);
        W("===== probe-ui ここまで =====");
        return 0;
    }

    // ---------- 起動の流れ ----------
    static void OnSourceInitialized()
    {
        hwnd = new WindowInteropHelper(window!).Handle;
        W("## SourceInitialized(HWND ができた直後。このプローブが何かを設定する前)");
        W($"  窓の DPI 認識: {Awareness(Native.GetWindowDpiAwarenessContext(hwnd))} GetDpiForWindow={Native.GetDpiForWindow(hwnd)}");
        W($"  38 SYSTEMBACKDROP_TYPE: {ReadBackdrop()}(0 以外なら ThemeMode の Fluent が先に設定した)");
        W($"  20 USE_IMMERSIVE_DARK_MODE: {ReadDark()}");
        W($"  Window.Background: {window!.Background?.ToString() ?? "null"} / CompositionTarget.BackgroundColor: {CompositionColor()}");
        W("## 起動の引数どおりに設定する(ダーク → 背景 → 素材 の順。WPF の Fluent と同じ順)");
        ApplyDark(DarkFromArg());
        ApplyGlass(glassOn);
        ApplyBackdrop(backdropSel);
    }

    static void OnContentRendered()
    {
        RefreshColors();
        PrintState("ContentRendered のあと");
        if (smoke)
        {
            // 画面から色を読むので、ほかの窓に隠れないよう最前面にする
            window!.Topmost = true;
            window.Activate();
        }
        var steps = new List<Action> { () => { }, () => { }, () => Measure("起動直後") };
        foreach (var p in presses)
        {
            string id = p;
            steps.Add(() => Do(id, "--press " + id));
            steps.Add(() => { });
            steps.Add(() => Measure(id + " のあと"));
        }
        if (smoke)
        {
            if (snapPath is not null) steps.Add(() => Snap(snapPath));
            steps.Add(() => { W("  smoke: 窓を出して閉じた"); window!.Close(); });
        }
        else steps.Add(PrintInstructions);

        // 1 歩ずつ 0.35 秒おきに進める(DWM が合成し直す時間を取る)
        int next = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (_, _) =>
        {
            if (next >= steps.Count) { t.Stop(); return; }
            steps[next++]();
        };
        t.Start();
    }

    // ---------- 操作(ボタンと --press で共通) ----------
    // id: theme:none|light|dark|system、backdrop:none|mica|acrylic|tabbed|auto、dark:on|off、glass:on|off、reapply、measure、move:横の量(DIP)
    static void Do(string id, string label)
    {
        W($"## 操作: {label}");
        int colon = id.IndexOf(':');
        string kind = colon < 0 ? id : id[..colon], val = colon < 0 ? "" : id[(colon + 1)..];
        switch (kind)
        {
            case "theme": SetTheme(Application.Current, val, "操作"); break;
            case "backdrop": backdropSel = BackdropValue(val); ApplyBackdrop(backdropSel); break;
            case "dark": ApplyDark(val == "on"); break;
            case "glass": ApplyGlass(val == "on"); break;
            case "reapply": ReapplyAll(); break;
            case "measure": Measure("「背景を測る」"); return;
            case "move" when double.TryParse(val, out double dx):
                // 窓を横に dx(DIP)動かす(Mica が壁紙の場所で色を変えるかを smoke で確かめる用)
                window!.Left += dx;
                W($"  窓を横に {dx} 動かした: Left={window.Left}");
                return;
            default: W($"  知らない操作: {id}(使えるのは theme:… backdrop:… dark:on|off glass:on|off reapply measure move:横の量)"); return;
        }
        RefreshColors();
        PrintState(label + " のあと");
    }

    // ボタンから: 操作して、0.6 秒後に(DWM が描き直してから)のぞき窓を測る
    static void Click(string id, string label)
    {
        Do(id, "ボタン「" + label + "」");
        if (id == "measure") return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) => { t.Stop(); Measure(label + " のあと"); };
        t.Start();
    }

    // 「全部かけ直す」: ThemeMode を変えたあとに WPF が上書きした属性を、今の選択で設定し直す
    static void ReapplyAll()
    {
        ApplyDark(DarkFromArg());
        ApplyGlass(true);
        ApplyBackdrop(backdropSel);
    }

    // ---------- テーマ ----------
    static void SetTheme(Application app, string mode, string when)
    {
        try
        {
#pragma warning disable WPF0001 // ThemeMode は実験的 API(測定のため、この数行だけ警告を抑える)
            ThemeMode tm = mode switch
            {
                "none" => ThemeMode.None,
                "light" => ThemeMode.Light,
                "dark" => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
            app.ThemeMode = tm;
            W($"  [{when}] Application.ThemeMode = {tm.Value} を設定: 例外なし。読み戻し={app.ThemeMode.Value}");
#pragma warning restore WPF0001
            W($"  [{when}] Application.Resources.MergedDictionaries: " +
              (app.Resources.MergedDictionaries.Count == 0 ? "(空。Fluent の辞書は外れた)" :
               string.Join(", ", app.Resources.MergedDictionaries.Select(d => d.Source?.ToString() ?? "(Source なし)"))));
        }
        catch (Exception e) { W($"  [{when}] ThemeMode の設定で例外: {e.GetType().FullName}: {e.Message}"); }
    }

#pragma warning disable WPF0001 // ThemeMode の読み取り(測定のため)
    static string AppTheme() => (Application.Current?.ThemeMode.Value ?? "None").ToLowerInvariant();
    static string WindowTheme() => window?.ThemeMode.Value ?? "?";
#pragma warning restore WPF0001

    // ThemeMode から見た「暗い色の画面か」。none(WPF の既定テーマ)はダークの属性(20)の読み戻しに合わせる
    static bool ThemeDark() => AppTheme() switch
    {
        "light" => false,
        "dark" => true,
        "system" => OsDark(),
        _ => ReadDarkValue() ?? OsDark(),
    };

    // --dark auto はテーマに合わせる(none のときは OS のアプリモード)
    static bool DarkFromArg() => darkArg switch
    {
        "on" => true,
        "off" => false,
        _ => AppTheme() == "none" ? OsDark() : ThemeDark(),
    };

    // ---------- DWM と背景 ----------
    static int BackdropValue(string s) => s switch
    {
        "auto" => Native.DWMSBT_AUTO,
        "none" => Native.DWMSBT_NONE,
        "acrylic" => Native.DWMSBT_TRANSIENTWINDOW,
        "tabbed" => Native.DWMSBT_TABBEDWINDOW,
        _ => Native.DWMSBT_MAINWINDOW,
    };

    static string BackdropName(int v) => v switch { 0 => "AUTO", 1 => "NONE", 2 => "MAINWINDOW=Mica", 3 => "TRANSIENTWINDOW=Acrylic", 4 => "TABBEDWINDOW=Mica Alt", _ => "?" };

    static void ApplyDark(bool on)
    {
        int v = on ? 1 : 0;
        int hr = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        W($"  DwmSetWindowAttribute(20 USE_IMMERSIVE_DARK_MODE, {v}): {Hr(hr)} / 読み戻し {ReadDark()}");
    }

    static void ApplyGlass(bool on)
    {
        glassOn = on;
        try
        {
            var src = HwndSource.FromHwnd(hwnd);
            if (on)
            {
                window!.Background = Brushes.Transparent;
                if (src?.CompositionTarget is not null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new Native.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                int hr = Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
                marginsSet = "-1,-1,-1,-1(窓全体)";
                W($"  透明な背景 on: Window.Background=Transparent、CompositionTarget.BackgroundColor={CompositionColor()}、DwmExtendFrameIntoClientArea(-1,-1,-1,-1): {Hr(hr)}");
            }
            else
            {
                PaintOpaque();
                var m = new Native.MARGINS();
                int hr = Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
                marginsSet = "0,0,0,0(広げない)";
                W($"  透明な背景 off: Window.Background={window!.Background}、CompositionTarget.BackgroundColor={CompositionColor()}、DwmExtendFrameIntoClientArea(0,0,0,0): {Hr(hr)}");
            }
        }
        catch (Exception e) { W($"  透明な背景の設定で例外: {e.GetType().FullName}: {e.Message}"); }
    }

    // 不透明のときの塗り。ThemeMode の明暗に合わせる(Fluent の SolidBackgroundFillColorBase 相当の灰色。probe の中だけの直書き)
    static void PaintOpaque()
    {
        var c = ThemeDark() ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        window!.Background = new SolidColorBrush(c);
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget is not null) src.CompositionTarget.BackgroundColor = c;
    }

    static void ApplyBackdrop(int v)
    {
        int val = v;
        int hr = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref val, sizeof(int));
        W($"  DwmSetWindowAttribute(38 SYSTEMBACKDROP_TYPE, {v}={BackdropName(v)}): {Hr(hr)} / 読み戻し {ReadBackdrop()}");
    }
}
