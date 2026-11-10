// probe-ui の読み戻しと切り分け: DWM の属性(38・20)、WPF の背景・Fluent の状態、OS の透明効果・省電力などを標準出力に出す。
// のぞき窓の画面上の色(DWM が合成したあとの絵)を読み、Mica が描かれているか(真っ黒か、色が付いているか)を数で示す。
// --snap では窓の範囲の画面を PNG に保存する(文字が読めるかを目で確かめる用)。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

namespace GlintLoop.Probes.Ui;

static partial class Program
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // ---------- OS ----------
    static void PrintOs()
    {
        W("## OS");
        W($"  Environment.OSVersion.Version: {Environment.OSVersion.Version}");
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            W($"  ProductName={k?.GetValue("ProductName")} DisplayVersion={k?.GetValue("DisplayVersion")} CurrentBuild={k?.GetValue("CurrentBuild")} UBR={k?.GetValue("UBR")}(ProductName は Windows 11 でも「Windows 10」と書かれていることがある。版は CurrentBuild で見る)");
        }
        catch (Exception e) { W($"  レジストリ(版)を読めない: {e.Message}"); }
        int build = Environment.OSVersion.Version.Build;
        W($"  Build {build}: 20 USE_IMMERSIVE_DARK_MODE は 22000 以上で対応={build >= 22000}、38 SYSTEMBACKDROP_TYPE は 22621 以上で対応={build >= 22621}(FR-96)");
        W($"  {OsSettings()}");
        W($"  SystemParameters.HighContrast={SystemParameters.HighContrast} / リモート デスクトップのセッション={(Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0 ? "はい" : "いいえ")}");
        W($"  描画: RenderCapability.Tier={RenderCapability.Tier >> 16}(2 ならハードウェア描画)");
        try
        {
            var mons = new List<string>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d) =>
            {
                int hr = Native.GetDpiForMonitor(h, 0, out uint dx, out uint _);
                mons.Add($"[{r.Left},{r.Top} {r.Right - r.Left}×{r.Bottom - r.Top} {(hr == 0 ? $"{dx}dpi={dx * 100.0 / 96:F1}%" : $"GetDpiForMonitor {Hr(hr)}")}]");
                return true;
            }, IntPtr.Zero);
            W($"  モニター {mons.Count} 台(実効 DPI): {string.Join(" ", mons)}");
        }
        catch (Exception e) { W($"  モニターを数えられない: {e.Message}"); }
    }

    static int? PersonalizeValue(string name)
    {
        try
        {
            using var p = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return p?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }

    static bool OsDark() => PersonalizeValue("AppsUseLightTheme") == 0;

    static string OsSettings()
    {
        int? apps = PersonalizeValue("AppsUseLightTheme"), tr = PersonalizeValue("EnableTransparency");
        string power;
        if (Native.GetSystemPowerStatus(out var ps))
        {
            string ac = ps.ACLineStatus switch { 0 => "電池", 1 => "電源につないでいる", _ => "不明" };
            power = $"バッテリー節約={(ps.SystemStatusFlag == 1 ? "オン(Mica は単色になる)" : "オフ")}(SystemStatusFlag={ps.SystemStatusFlag})、電源={ac}";
        }
        else power = "GetSystemPowerStatus に失敗";
        return $"OS の設定: AppsUseLightTheme={apps?.ToString() ?? "無し"}({(apps == 0 ? "アプリはダーク" : "アプリはライト")})、" +
               $"EnableTransparency={tr?.ToString() ?? "無し"}({(tr == 0 ? "透明効果オフ → Mica は単色になる" : "透明効果オン")})、{power}";
    }

    static string Awareness(IntPtr ctx)
    {
        int a = Native.GetAwarenessFromDpiAwarenessContext(ctx);
        bool v2 = Native.AreDpiAwarenessContextsEqual(ctx, Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        string name = a switch { 0 => "Unaware", 1 => "SystemAware", 2 => "PerMonitorAware", _ => $"不明({a})" };
        return $"{name}{(v2 ? "(Per-Monitor V2)" : "")}";
    }

    // ---------- DWM の読み戻し ----------
    static int? ReadBackdropValue()
    {
        int hr = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, out int v, sizeof(int));
        return hr == 0 ? v : null;
    }

    static string ReadBackdrop()
    {
        int hr = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, out int v, sizeof(int));
        return $"{v}({BackdropName(v)}) HRESULT={Hr(hr)}";
    }

    static bool? ReadDarkValue()
    {
        if (hwnd == IntPtr.Zero) return null;
        int hr = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, out int v, sizeof(int));
        return hr == 0 ? v != 0 : null;
    }

    static string ReadDark()
    {
        int hr = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, out int v, sizeof(int));
        return $"{v}({(v != 0 ? "ダーク" : "ライト")}) HRESULT={Hr(hr)}";
    }

    static string CompositionColor()
    {
        var src = HwndSource.FromHwnd(hwnd);
        return src?.CompositionTarget?.BackgroundColor.ToString() ?? "(取れない)";
    }

    // ---------- 状態の表示(操作のたびに出す) ----------
    static void PrintState(string when)
    {
        if (window is null || hwnd == IntPtr.Zero) return;
        W($"## 状態: {when}");
        int hrc = Native.DwmIsCompositionEnabled(out bool comp);
        int? bd = ReadBackdropValue();
        bool? dark = ReadDarkValue();
        var src = HwndSource.FromHwnd(hwnd);
        Color? compColor = src?.CompositionTarget?.BackgroundColor;
        var chrome = WindowChrome.GetWindowChrome(window);
        bool fg = Native.GetForegroundWindow() == hwnd;
        W($"  [DWM] 38 SYSTEMBACKDROP_TYPE={ReadBackdrop()} / 20 USE_IMMERSIVE_DARK_MODE={ReadDark()} / DwmIsCompositionEnabled={comp}(HRESULT={Hr(hrc)})");
        W($"  [WPF] Application.ThemeMode={AppTheme()} Window.ThemeMode={WindowTheme()} / Window.Background={window.Background?.ToString() ?? "null"} / CompositionTarget.BackgroundColor={compColor?.ToString() ?? "(取れない)"}");
        W($"  [WPF] Window.AllowsTransparency={window.AllowsTransparency} / WindowChrome={(chrome is null ? "なし" : $"あり(GlassFrameThickness={chrome.GlassFrameThickness})")} / HwndTarget.RenderMode={src?.CompositionTarget?.RenderMode.ToString() ?? "?"}");
        W($"  [WPF] DwmExtendFrameIntoClientArea の余白: このプローブが最後に渡した値={marginsSet}(DWM から読み戻す関数は無い。ThemeMode を変えると WPF が自分で -1 か 0 にかけ直す)");
        W($"  [WPF] {ControlsReport()}");
        W($"  [OS] {OsSettings()} / 窓が前面(アクティブ)={(fg ? "はい" : "いいえ")} / IsActive={window.IsActive}");
        W($"  [文字] {ContrastReport()}");

        var notes = new List<string>();
        if (!glassOn) notes.Add("透明な背景が off: 窓は不透明に塗っている。Mica は塗りの下に隠れて見えない(正常)");
        if (glassOn && NoMaterial(bd)) notes.Add($"透明な背景が on なのに 38 が {BackdropName(bd!.Value)}({bd}): 素材が描かれず、透明な部分はダークの属性 on なら真っ黒、off なら真っ白になる。「背景の素材」の mica か「全部かけ直す」を押す");
        if (glassOn && bd is int b0 && !NoMaterial(b0) && b0 != backdropSel) notes.Add($"38 の読み戻し({BackdropName(b0)})が、このプローブの選択({BackdropName(backdropSel)})と違う: ThemeMode を変えたときに WPF がかけ直した");
        if (glassOn && compColor is Color cc && cc.A != 0) notes.Add($"CompositionTarget.BackgroundColor が透明でない({cc}): WPF の下地が素材を隠す");
        if (glassOn && window.Background is SolidColorBrush wb && wb.Color.A != 0) notes.Add($"Window.Background が透明でない({wb.Color}): 窓の塗りが素材を隠す");
        if (glassOn && marginsSet.StartsWith("0", StringComparison.Ordinal)) notes.Add("余白が 0: 素材はタイトルバーにしか出ない");
        if (window.AllowsTransparency) notes.Add("AllowsTransparency=True: WPF はこの窓に素材を付けない");
        if (chrome is not null) notes.Add("WindowChrome がある: 余白と素材の範囲が変わる");
        if (PersonalizeValue("EnableTransparency") == 0) notes.Add("OS の透明効果がオフ: Mica は単色になる(Microsoft Learn)");
        if (Native.GetSystemPowerStatus(out var ps) && ps.SystemStatusFlag == 1) notes.Add("バッテリー節約がオン: Mica は単色になる(Microsoft Learn)");
        if (!fg) notes.Add("窓が前面でない: Mica は非アクティブの窓では単色になる。窓をクリックしてから測る");
        if (Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0) notes.Add("リモート デスクトップのセッション: 素材が描かれないことがある");
        if (SystemParameters.HighContrast) notes.Add("ハイコントラスト: Mica は使われない");
        if (dark is bool dk && glassOn && dk != OsDark()) notes.Add($"20 の読み戻し({(dk ? "ダーク" : "ライト")})が OS のアプリモードと違う: 素材とタイトルバーは 20 の明暗で描かれる");
        W(notes.Count == 0 ? "  [切り分け] 気になる値は無い" : "  [切り分け] " + string.Join("\n  [切り分け] ", notes));

        var dpi = VisualTreeHelper.GetDpi(window);
        W($"  [DPI] VisualTreeHelper.GetDpi: {dpi.PixelsPerInchX:F0} dpi(拡大率 {dpi.DpiScaleX * 100:F0}%) / GetDpiForWindow={Native.GetDpiForWindow(hwnd)} / 窓の DPI 認識 {Awareness(Native.GetWindowDpiAwarenessContext(hwnd))}");
        W();

        statusText.Text = $"38={bd?.ToString() ?? "?"}({(bd is int bv ? BackdropName(bv) : "?")})  20={(dark == true ? "1(ダーク)" : dark == false ? "0(ライト)" : "?")}  透明な背景={(glassOn ? "on" : "off")}\n" +
                          $"ThemeMode={AppTheme()}  前面={(fg ? "はい" : "いいえ")}  透明効果={(PersonalizeValue("EnableTransparency") == 0 ? "オフ" : "オン")}" +
                          (glassOn && NoMaterial(bd) ? $"\n注意: 38 が {BackdropName(bd!.Value)} なので素材が描かれない(透明な部分は{(dark == true ? "真っ黒" : "真っ白")})" : "");
    }

    // 38 が NONE(1)か AUTO(0)なら、この窓には素材が描かれない(この PC で、透明な背景のとき画面が真っ黒・真っ白になるのを測って確かめた)
    static bool NoMaterial(int? backdrop) => backdrop is Native.DWMSBT_NONE or Native.DWMSBT_AUTO;

    // コントロールが Fluent のスタイルで描かれているか、実際の色は何か
    static string ControlsReport()
    {
        var src = DependencyPropertyHelper.GetValueSource(sampleButton, FrameworkElement.StyleProperty).BaseValueSource;
        string style = src == BaseValueSource.ImplicitStyleReference ? "Fluent の辞書の暗黙のスタイル" : $"WPF の既定テーマ(Aero2。明るい色だけで、ダークは無い)(Style の出どころ={src})";
        object? textRes = Application.Current?.TryFindResource("TextFillColorPrimaryBrush");
        return $"見本の Button: スタイル={style}、Foreground={sampleButton.Foreground}、Background={sampleButton.Background}" +
               $" / 資源 TextFillColorPrimaryBrush={(textRes?.ToString() ?? "無し(Fluent の辞書が無い)")} / コントロールの明暗(カードの色)={(ControlsDark() ? "ダーク" : "ライト")}";
    }

    // 板の上の文字の対比(後ろが黒のとき・白のときの両方。WCAG の式。4.5 以上が目安)
    static string ContrastReport()
    {
        bool pd = PlateDark();
        Color plateC = pd ? PlateDarkColor : PlateLightColor, text = pd ? TextOnDark : TextOnLight, sub = pd ? SubOnDark : SubOnLight;
        double Ratio(Color fg, Color under) { var bg = Over(plateC, under); double a = Lum(fg), b = Lum(bg); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05); }
        double minText = Math.Min(Ratio(text, Colors.Black), Ratio(text, Colors.White));
        double minSub = Math.Min(Ratio(sub, Colors.Black), Ratio(sub, Colors.White));
        bool cd = ControlsDark();
        var cardC = cd ? CardDarkColor : CardLightColor;
        double cardRatio = (Math.Max(Lum(cd ? TextOnDark : TextOnLight), Lum(cardC)) + 0.05) / (Math.Min(Lum(cd ? TextOnDark : TextOnLight), Lum(cardC)) + 0.05);
        return $"板={(pd ? "暗い" : "明るい")}({plateC}) 見出し・行の名前={text} 対比 {minText:F1}、説明={sub} 対比 {minSub:F1}(後ろが黒でも白でも下回らない値) / カード={(cd ? "暗い" : "明るい")} 対比 {cardRatio:F1}";
    }

    static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        byte M(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(M(top.R, under.R), M(top.G, under.G), M(top.B, under.B));
    }

    static double Lum(Color c)
    {
        static double L(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    // ---------- のぞき窓の色を画面から読む ----------
    static byte[]? CaptureScreen(int x, int y, int w, int h)
    {
        IntPtr sdc = Native.GetDC(IntPtr.Zero);
        if (sdc == IntPtr.Zero) return null;
        IntPtr mdc = Native.CreateCompatibleDC(sdc), bmp = Native.CreateCompatibleBitmap(sdc, w, h);
        try
        {
            IntPtr old = Native.SelectObject(mdc, bmp);
            bool ok = Native.BitBlt(mdc, 0, 0, w, h, sdc, x, y, Native.SRCCOPY);
            Native.SelectObject(mdc, old);
            if (!ok) return null;
            var bs = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, new Int32Rect(0, 0, w, h), BitmapSizeOptions.FromEmptyOptions());
            var conv = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0);
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            return px;
        }
        finally
        {
            Native.DeleteObject(bmp);
            Native.DeleteDC(mdc);
            Native.ReleaseDC(IntPtr.Zero, sdc);
        }
    }

    static void Measure(string when)
    {
        if (window is null || peep is null || peep.ActualWidth < 40 || peep.ActualHeight < 40) return;
        try
        {
            // PointToScreen は物理画素の画面座標(この窓は Per-Monitor V2)。点線を避けて 8 画素内側を測る
            Point a = peep.PointToScreen(new Point(0, 0)), b = peep.PointToScreen(new Point(peep.ActualWidth, peep.ActualHeight));
            int x = (int)Math.Round(Math.Min(a.X, b.X)) + 8, y = (int)Math.Round(Math.Min(a.Y, b.Y)) + 8;
            int w = (int)Math.Round(Math.Abs(b.X - a.X)) - 16, h = (int)Math.Round(Math.Abs(b.Y - a.Y)) - 16;
            var px = CaptureScreen(x, y, w, h);
            if (px is null) { W($"## のぞき窓の色({when}): 画面を読めなかった"); return; }
            long n = (long)w * h, black = 0;
            double sr = 0, sg = 0, sb = 0, sl = 0, sl2 = 0;
            for (long i = 0; i < n; i++)
            {
                byte B0 = px[i * 4], G0 = px[i * 4 + 1], R0 = px[i * 4 + 2];
                sr += R0; sg += G0; sb += B0;
                double l = 0.2126 * R0 + 0.7152 * G0 + 0.0722 * B0;
                sl += l; sl2 += l * l;
                if (R0 <= 2 && G0 <= 2 && B0 <= 2) black++;
            }
            double mr = sr / n, mg = sg / n, mb = sb / n, ml = sl / n, sd = Math.Sqrt(Math.Max(0, sl2 / n - ml * ml));
            double blackPct = black * 100.0 / n, spread = Math.Max(mr, Math.Max(mg, mb)) - Math.Min(mr, Math.Min(mg, mb));
            bool fg = Native.GetForegroundWindow() == hwnd;
            string verdict, shortV;
            int? bdNow = ReadBackdropValue();
            if (!glassOn) { verdict = "不透明に塗っている(この設定では Mica は塗りの下で見えない)"; shortV = "不透明の塗り"; }
            else if (NoMaterial(bdNow)) { verdict = $"38 が {BackdropName(bdNow!.Value)}({bdNow})なので素材は描かれていない(ダークの属性 on なら真っ黒、off なら真っ白)。Mica は効いていない"; shortV = $"38={BackdropName(bdNow!.Value)}: 素材なし(Mica は効いていない)"; }
            else if (blackPct >= 95) { verdict = "真っ黒 = 素材(Mica など)が描かれていない。Mica は効いていない"; shortV = "真っ黒: Mica は効いていない"; }
            else if (sd < 0.6 && spread < 1.5) { verdict = "むらの無い無彩色の単色 = Mica の代わりの単色の可能性(非アクティブ・透明効果オフ・バッテリー節約)。窓をクリックして前面にし、動かしてから測り直す"; shortV = "無彩色の単色: 代わりの単色かも"; }
            else { verdict = "黒ではなく色が付いている = 素材が描かれている。窓を壁紙の色の違う場所へ動かして測り直し、平均が変われば Mica(壁紙を写している)"; shortV = "色が付いている: 素材が描かれている"; }
            W($"## のぞき窓の画面上の色({when}): 範囲 x={x} y={y} {w}×{h}(物理画素) / 平均 R{mr:F0} G{mg:F0} B{mb:F0} / 明るさの標準偏差 {sd:F1} / 色味の幅 {spread:F1} / 真っ黒の画素 {blackPct:F1}% / 前面={(fg ? "はい" : "いいえ")}");
            W($"  判定: {verdict}");
            W();
            measureText.Text = $"平均の色 R{mr:F0} G{mg:F0} B{mb:F0}(標準偏差 {sd:F1}、真っ黒 {blackPct:F0}%、前面={(fg ? "はい" : "いいえ")})\n{shortV}";
        }
        catch (Exception e) { W($"## のぞき窓の色({when}): 例外 {e.GetType().FullName}: {e.Message}"); }
    }

    // --snap: 窓の範囲(DWMWA_EXTENDED_FRAME_BOUNDS)の画面を PNG に保存する
    static void Snap(string path)
    {
        try
        {
            int hr = Native.DwmGetWindowAttributeRect(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>());
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            var px = hr == 0 && w > 0 && h > 0 ? CaptureScreen(r.Left, r.Top, w, h) : null;
            if (px is null) { W($"  snap: 画面を読めなかった(HRESULT={Hr(hr)})"); return; }
            for (int i = 3; i < px.Length; i += 4) px[i] = 0xFF;
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var fs = File.Create(path);
            enc.Save(fs);
            W($"  snap: 窓の範囲 {w}×{h} を {path} に保存した");
        }
        catch (Exception e) { W($"  snap: 例外 {e.GetType().FullName}: {e.Message}"); }
    }

    // ---------- 手順 ----------
    static void PrintInstructions()
    {
        W("## 測定 3 の手順(目で見て、下の表に書く)");
        W("  0. 準備: 設定 > 個人用設定 > 色 の「透明効果」をオン、バッテリー節約(省エネ)をオフにする。窓は最大化しない。");
        W("  1. 起動したまま(ThemeMode=System・背景の素材=Mica・ダークはテーマに合わせる・透明な背景 on)で、窓のどこかを 1 回クリックして前面にする。");
        W("     これは WPF の Fluent が自分で行う手順と同じ(DwmExtendFrameIntoClientArea(-1)・CompositionTarget を透明・DWMWA_SYSTEMBACKDROP_TYPE=2)。");
        W("  2. 右の「のぞき窓」(点線の枠の中。何も描いていない)を見る。");
        W("     ・Mica が見えた: 枠の中が真っ黒ではなく、灰色(ダークなら暗い灰色)に壁紙の色がうっすら混ざっている。タイトルバーも同じ色。");
        W("     ・Mica が見えない: 枠の中が真っ黒(R0 G0 B0)か真っ白(R255 G255 B255)。または窓の後ろのほかの窓がそのまま見える。");
        W("  3. 「背景を測る」を押し、枠の上に出る「平均の色」を控える。窓を壁紙の色の違う場所(画面の左端と右端、別のモニター など)へドラッグし、");
        W("     クリックしてからもう一度「背景を測る」を押す。平均の色が場所で変われば Mica(壁紙を写している)。真っ黒・真っ白のまま、または全く変わらなければ Mica ではない。");
        W("     測るのは画面に映っている色なので、のぞき窓にほかの窓(最前面に出るツールなど)が重なっていると値がずれる。重ならない場所で測る。");
        W("     Mica は壁紙だけを写し、後ろのほかのアプリは写さない。比べるなら「背景の素材」の acrylic を押す: 後ろの窓がぼけて透ければ、素材の仕組み自体は動いている。");
        W("  4. ほかのアプリをクリックして、この窓を非アクティブにする。Mica なら、のぞき窓とタイトルバーが模様の無い単色の灰色に変わる(Microsoft Learn: 非アクティブの窓では単色に戻る)。");
        W("  5. ThemeMode の none・light・dark・system を順に押し、それぞれで 2〜3 を見る。押したあと、標準出力の「[DWM] 38 SYSTEMBACKDROP_TYPE」と「[切り分け]」を見る。");
        W("     ThemeMode を押すと、WPF が 20(ダークの属性)を変えることがある(none ならライト=0。WPF は自分が前に設定した値と違うときだけ設定し直すので、「ダークの属性」を手で変えたあとは変わらないこともある)。38 が NONE(1)か AUTO(0)だと素材が描かれず、");
        W("     20 が 1 なら真っ黒、0 なら真っ白になる(「背景の素材」の none・auto を押したとき)。おかしくなったら「全部かけ直す」を押してから 2〜3 を見る(Q-19)。");
        W("  6. 「ダークの属性」の on・off で、タイトルバーとのぞき窓の明るさが変わるかを見る。");
        W("  7. 文字: どの組み合わせでも、左の板の見出し・説明・行の名前と、カードの中の文字が読めるかを見る。読めない組み合わせがあれば書く。");
        W("  8. DPI: 設定 > システム > ディスプレイ > 拡大/縮小 を 150% にして、カードの文字と細い線・市松模様がぼやけないかを見る。次に 200% でも見る。");
        W("     モニターが 2 台なら、拡大率の違うモニターへ窓をドラッグし、DpiChanged の行が出て、移った直後もくっきりかを見る。");
        W("  9. この標準出力を全部コピーして、下の表と一緒に貼る。");
        W();
        W("| 項目 | 結果(見えた・見えない、平均の色の数など) |");
        W("|---|---|");
        W("| Mica が見えたか(起動したまま。のぞき窓の平均の色を 2 か所で。例: 左端 R27 G33 B41、右端 R40 G31 B36) |  |");
        W("| 非アクティブにしたとき単色の灰色になったか |  |");
        W("| acrylic で後ろの窓がぼけて透けたか |  |");
        W("| ThemeMode=None で Mica が見えたか(「全部かけ直す」の前と後。38 の値も) |  |");
        W("| ThemeMode=Light・Dark で Mica が見えたか |  |");
        W("| ダークの属性が効いたか(タイトルバー) |  |");
        W("| Fluent テーマが動いたか(ボタンの角が丸く、light・dark で色が変わる) |  |");
        W("| ThemeMode と Mica を一緒に使えたか(Q-19) |  |");
        W("| 文字が読めなかった組み合わせ |  |");
        W("| 150% でぼやけなかったか |  |");
        W("| 200% でぼやけなかったか |  |");
        W("| モニターをまたいだときにぼやけなかったか |  |");
        W();
    }
}
