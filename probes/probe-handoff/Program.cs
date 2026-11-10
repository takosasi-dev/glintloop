// probe-handoff: 仕様書 §8.4 の測定 4(コピーとドラッグ)。動く GIF を 3 通りの載せ方でクリップボードに置き、ドラッグ欄から渡す。
// 置いた直後に、クリップボードにある形式の一覧(WPF と Win32 の両方)を標準出力に出す(Q-16・INV-10 の材料)。
// 引数: [GIF のパス] [--smoke](--smoke は窓を出して数秒で閉じるだけで、クリップボードに触らない)。
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlintLoop.Probes.Shared;

namespace GlintLoop.Probes.Handoff;

static class Program
{
    static bool smoke;
    static string gifSource = "";
    static byte[] gifBytes = Array.Empty<byte>();
    static string? handoffFile;
    static FirstFrame? frame;
    static TextBox? logBox;
    static Window? window;

    static readonly StringBuilder earlyLog = new();

    // 標準出力と窓の欄の両方に書く。欄ができる前の行は取っておき、欄ができたときに入れる。
    static void W(string s = "")
    {
        Console.WriteLine(s);
        if (logBox is null) { earlyLog.AppendLine(s); return; }
        logBox.AppendText(s + Environment.NewLine);
        logBox.ScrollToEnd();
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        smoke = args.Contains("--smoke");
        string? argPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        W("===== probe-handoff (測定 4: コピーとドラッグ) =====");
        W($"  OS: {Environment.OSVersion.VersionString} / .NET {Environment.Version} / smoke={smoke}");
        try { PrepareGif(argPath); }
        catch (Exception e) { W($"  GIF の準備で例外: {e.GetType().FullName}: {e.Message}"); }
        PrintFormatNames();
        PrintFrameCheck();
        PrintDryRun();

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        window = BuildWindow();
        if (smoke)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => { t.Stop(); W("  smoke: 窓を出して閉じた(クリップボードには触っていない)"); window.Close(); };
            t.Start();
        }
        else
        {
            PrintInstructions();
        }
        app.Run(window);
        W("===== probe-handoff ここまで =====");
        return 0;
    }

    // ---------- GIF の準備 ----------
    static void PrepareGif(string? argPath)
    {
        string? path = null;
        if (argPath is not null)
        {
            if (File.Exists(argPath)) path = Path.GetFullPath(argPath);
            else W($"  指定のパスが見つからない: {argPath}");
        }
        path ??= FindProbeGifOutput();
        if (path is not null)
        {
            gifBytes = File.ReadAllBytes(path);
            gifSource = $"ファイル {path}";
        }
        else
        {
            gifBytes = MiniGif.TwoFrameAnimated(128);
            gifSource = "probe-gif の loop_inf.gif が見つからないので、自分で組み立てた 2 フレームの GIF(128×128、遅延 50、ループ 0)";
        }
        W($"  使う GIF: {gifSource}({gifBytes.Length} バイト)");

        // FileDrop に載せる実体は一時フォルダにコピーしたもの(名前は半角英数字とハイフンだけ)
        string dir = Path.Combine(Path.GetTempPath(), "GlintLoop-probe-handoff");
        Directory.CreateDirectory(dir);
        handoffFile = Path.Combine(dir, $"glint-probe-{DateTime.Now:yyyyMMdd-HHmmss}.gif");
        string tmp = handoffFile + ".tmp";
        File.WriteAllBytes(tmp, gifBytes);
        File.Move(tmp, handoffFile, overwrite: true);
        W($"  渡す実体: {handoffFile}");

        frame = HandoffData.FromGif(gifBytes);
        W($"  先頭フレーム: {frame.Width}×{frame.Height} / PNG {frame.Png.Length} バイト / DIB {frame.Dib.Length} バイト / DIBV5 {frame.DibV5.Length} バイト");
    }

    // probe-gif の出力(loop_inf.gif)を、作業フォルダ・実行ファイルの場所・リポジトリの probes/probe-gif の下から探す。
    static string? FindProbeGifOutput()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "loop_inf.gif"),
            Path.Combine(AppContext.BaseDirectory, "loop_inf.gif"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = new DirectoryInfo(start);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlintLoop.slnx"))) d = d.Parent;
            if (d is null) continue;
            string gifDir = Path.Combine(d.FullName, "probes", "probe-gif");
            if (!Directory.Exists(gifDir)) continue;
            var found = Directory.EnumerateFiles(gifDir, "loop_inf.gif", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found.FullName;
        }
        return null;
    }

    static void PrintFormatNames()
    {
        W("## WPF の形式名(Q-16 の材料)");
        void Try(string label, Func<string> f) { try { W($"  {label}: {f()}"); } catch (Exception e) { W($"  {label}: 例外 {e.GetType().Name}: {e.Message}"); } }
        Try("DataFormats.FileDrop", () => DataFormats.FileDrop);
        Try("DataFormats.Dib", () => DataFormats.Dib);
        Try("DataFormats.Bitmap", () => DataFormats.Bitmap);
        Try("DataFormats.GetDataFormat(8)(CF_DIB)", () => { var f = DataFormats.GetDataFormat(8); return $"Name={f.Name} Id={f.Id}"; });
        Try("DataFormats.GetDataFormat(15)(CF_HDROP)", () => { var f = DataFormats.GetDataFormat(15); return $"Name={f.Name} Id={f.Id}"; });
        Try("DataFormats.GetDataFormat(17)(CF_DIBV5)", () => { var f = DataFormats.GetDataFormat(17); return $"Name={f.Name} Id={f.Id}"; });
        Try("DataFormats.GetDataFormat(\"PNG\")", () => { var f = DataFormats.GetDataFormat("PNG"); return $"Name={f.Name} Id=0x{f.Id:X4}(登録された形式)"; });
        W();
    }

    // DIB・DIBV5 のヘッダーを読み直して、§9.10 の表の値になっているかを出す。
    static void PrintFrameCheck()
    {
        if (frame is null) return;
        W("## 作ったバイト列の点検(§9.10 の表)");
        var d = frame.Dib;
        W($"  DIB: biSize={BitConverter.ToInt32(d, 0)} biWidth={BitConverter.ToInt32(d, 4)} biHeight={BitConverter.ToInt32(d, 8)} biPlanes={BitConverter.ToInt16(d, 12)} biBitCount={BitConverter.ToInt16(d, 14)} biCompression={BitConverter.ToInt32(d, 16)} biSizeImage={BitConverter.ToInt32(d, 20)} 全体={d.Length}(期待 {40 + (frame.Width * 3 + 3) / 4 * 4 * frame.Height})");
        var v = frame.DibV5;
        W($"  DIBV5: bV5Size={BitConverter.ToInt32(v, 0)} bV5Width={BitConverter.ToInt32(v, 4)} bV5Height={BitConverter.ToInt32(v, 8)} bV5BitCount={BitConverter.ToInt16(v, 14)} bV5Compression={BitConverter.ToInt32(v, 16)} bV5SizeImage={BitConverter.ToInt32(v, 20)} マスク R=0x{BitConverter.ToUInt32(v, 40):X8} G=0x{BitConverter.ToUInt32(v, 44):X8} B=0x{BitConverter.ToUInt32(v, 48):X8} A=0x{BitConverter.ToUInt32(v, 52):X8} CSType=0x{BitConverter.ToUInt32(v, 56):X8} Intent={BitConverter.ToUInt32(v, 108)} 全体={v.Length}(期待 {124 + frame.Width * 4 * frame.Height})");
        W($"  PNG の先頭: {BitConverter.ToString(frame.Png, 0, 8)}");
        W();
    }

    // クリップボードに置く前の、データオブジェクトそのものが持つ形式(クリップボードには触らない)。
    static void PrintDryRun()
    {
        W("## データオブジェクトの形式(クリップボードに置く前。WPF が自分で足す形式があるかを見る)");
        foreach (var m in new[] { Mode.File, Mode.Image, Mode.Both })
        {
            W($"  {ModeName(m)}: " + Safe(() =>
            {
                var obj = HandoffData.Build(m, handoffFile, frame, out var set);
                return $"SetData={string.Join(", ", set)} / GetFormats(false)={string.Join(", ", obj.GetFormats(false))} / GetFormats(true)={string.Join(", ", obj.GetFormats(true))}";
            }));
        }
        W("  (参考) ファイルだけ・string[] で autoConvert=false: " + Safe(() =>
        {
            var obj = HandoffData.Build(Mode.File, handoffFile, frame, out var set, fileDropNoConvert: true);
            return $"SetData={string.Join(", ", set)} / GetFormats(false)={string.Join(", ", obj.GetFormats(false))} / GetFormats(true)={string.Join(", ", obj.GetFormats(true))}";
        }));
        W();
    }

    // ---------- 窓 ----------
    static Window BuildWindow()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "GlintLoop probe-handoff(測定 4)", FontSize = 18, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = gifSource, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });

        var buttons = new WrapPanel();
        Button B(string text, Action act)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6) };
            b.Click += (_, _) => act();
            buttons.Children.Add(b);
            return b;
        }
        B("(a) ファイル(FileDrop)だけを置く", () => Place(Mode.File));
        B("(b) 画像(PNG・DIB・DIBV5)だけを置く", () => Place(Mode.Image));
        B("(c) 両方を置く", () => Place(Mode.Both));
        B("(参考) ファイルだけ・string[]・自動変換なし", () => Place(Mode.File, fileDropNoConvert: true));
        B("今のクリップボードの形式を表示", () => DumpClipboard("ボタンで表示"));
        top.Children.Add(buttons);

        var drag = new Border
        {
            Height = 110,
            Margin = new Thickness(0, 4, 0, 8),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.SteelBlue,
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFA)),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "ドラッグ欄: ここを押したまま、渡し先のアプリの入力欄へドラッグして落とす(FileDrop だけ・コピーのみ)",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12),
                Foreground = Brushes.Black,
            },
        };
        Point? down = null;
        drag.MouseLeftButtonDown += (_, e) => down = e.GetPosition(drag);
        drag.MouseLeftButtonUp += (_, _) => down = null;
        drag.MouseMove += (_, e) =>
        {
            if (down is not Point p || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(drag);
            if (Math.Abs(now.X - p.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - p.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
            down = null;
            StartDrag(drag);
        };
        top.Children.Add(drag);
        root.Children.Add(top);

        logBox = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Yu Gothic UI"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Text = earlyLog.ToString(),
        };
        root.Children.Add(logBox);

        return new Window { Title = "GlintLoop probe-handoff", Width = 900, Height = 640, Content = root };
    }

    // ---------- 置く ----------
    static void Place(Mode mode, bool fileDropNoConvert = false)
    {
        if (smoke) { W("  smoke ではクリップボードに置かない"); return; }
        W($"## 置く: {ModeName(mode)}{(fileDropNoConvert ? "(参考: string[]・自動変換なし)" : "")}({DateTime.Now:HH:mm:ss})");
        try
        {
            var obj = HandoffData.Build(mode, handoffFile, frame, out var set, fileDropNoConvert);
            W("  SetData に渡した形式: " + string.Join(", ", set));
            W("  このデータオブジェクトの GetFormats(false): " + string.Join(", ", obj.GetFormats(false)));
            W("  このデータオブジェクトの GetFormats(true): " + string.Join(", ", obj.GetFormats(true)));
            Clipboard.SetDataObject(obj, true);
            W("  Clipboard.SetDataObject(obj, true): 成功");
        }
        catch (COMException e) { W($"  置けなかった: COMException HRESULT=0x{e.HResult:X8}({unchecked((uint)e.HResult)}) {e.Message}"); return; }
        catch (Exception e) { W($"  置けなかった: {e.GetType().FullName} HRESULT=0x{e.HResult:X8} {e.Message}"); return; }
        DumpClipboard("置いた直後");
    }

    static string ModeName(Mode m) => m switch { Mode.File => "(a) ファイルだけ", Mode.Image => "(b) 画像だけ", _ => "(c) 両方" };

    static void DumpClipboard(string when)
    {
        if (smoke) return;
        W($"  [{when}] Clipboard.GetDataObject().GetFormats(false): " + Safe(() => string.Join(", ", Clipboard.GetDataObject()?.GetFormats(false) ?? Array.Empty<string>())));
        W($"  [{when}] Clipboard.GetDataObject().GetFormats(true): " + Safe(() => string.Join(", ", Clipboard.GetDataObject()?.GetFormats(true) ?? Array.Empty<string>())));
        W($"  [{when}] Win32 の EnumClipboardFormats(番号と名前): " + Safe(EnumWin32Formats));
        W($"  [{when}] 読み戻し: FileDrop={Safe(() => Clipboard.ContainsFileDropList() ? string.Join(";", Clipboard.GetFileDropList().Cast<string>()) : "なし")}");
        foreach (var name in new[] { "PNG", DataFormats.Dib, HandoffData.DibV5FormatName })
        {
            string fmt = name;
            W($"  [{when}] 読み戻し: Clipboard.GetData(\"{fmt}\") = " + Safe(() =>
            {
                if (!Clipboard.ContainsData(fmt)) return "なし";
                var o = Clipboard.GetData(fmt);
                return o switch
                {
                    null => "null",
                    MemoryStream ms => $"MemoryStream {ms.Length} バイト 先頭 {BitConverter.ToString(ms.ToArray(), 0, (int)Math.Min(8, ms.Length))}",
                    _ => o.GetType().FullName ?? "?",
                };
            }));
        }
        W();
    }

    static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return $"例外 {e.GetType().Name} HRESULT=0x{e.HResult:X8}: {e.Message}"; }
    }

    // ---------- ドラッグ ----------
    static void StartDrag(DependencyObject source)
    {
        if (smoke) return;
        try
        {
            var obj = HandoffData.Build(Mode.File, handoffFile, null, out var set);
            W($"## ドラッグ開始({DateTime.Now:HH:mm:ss}) 形式: {string.Join(", ", set)}");
            var result = DragDrop.DoDragDrop(source, obj, DragDropEffects.Copy);
            W($"  DoDragDrop の戻り値: {result}{(result == DragDropEffects.None ? "(取り消し、または落とし先が受け取らなかった)" : "")}");
            W();
        }
        catch (Exception e) { W($"  ドラッグで例外: {e.GetType().FullName}: {e.Message}"); }
    }

    // ---------- Win32 のクリップボードの形式の一覧 ----------
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern int GetClipboardFormatNameW(uint format, StringBuilder lpszFormatName, int cchMaxCount);

    static readonly Dictionary<uint, string> Standard = new()
    {
        [1] = "CF_TEXT", [2] = "CF_BITMAP", [3] = "CF_METAFILEPICT", [4] = "CF_SYLK", [5] = "CF_DIF", [6] = "CF_TIFF",
        [7] = "CF_OEMTEXT", [8] = "CF_DIB", [9] = "CF_PALETTE", [10] = "CF_PENDATA", [11] = "CF_RIFF", [12] = "CF_WAVE",
        [13] = "CF_UNICODETEXT", [14] = "CF_ENHMETAFILE", [15] = "CF_HDROP", [16] = "CF_LOCALE", [17] = "CF_DIBV5",
    };

    static string EnumWin32Formats()
    {
        IntPtr hwnd = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        bool opened = false;
        for (int i = 0; i < 10 && !opened; i++)
        {
            opened = OpenClipboard(hwnd);
            if (!opened) Thread.Sleep(100);
        }
        if (!opened) return $"OpenClipboard に失敗(GetLastError={Marshal.GetLastWin32Error()})";
        try
        {
            var list = new List<string>();
            uint f = 0;
            while ((f = EnumClipboardFormats(f)) != 0)
            {
                string name;
                if (Standard.TryGetValue(f, out var s)) name = s;
                else
                {
                    var sb = new StringBuilder(256);
                    name = GetClipboardFormatNameW(f, sb, sb.Capacity) > 0 ? sb.ToString() : "(名前なし)";
                }
                list.Add($"{f}={name}");
            }
            return list.Count == 0 ? "(空)" : string.Join(", ", list);
        }
        finally { CloseClipboard(); }
    }

    // ---------- 手順 ----------
    static void PrintInstructions()
    {
        W("## 測定 4 の手順");
        W("  1. 渡し先のアプリ(Discord デスクトップ版・LINE PC 版・Slack デスクトップ版・ブラウザの X)を開いておく。");
        W("     入れていないアプリの列は「該当なし」と書く(推測で埋めない)。");
        W("  2. この窓の (a) を押す → 各アプリの入力欄で Ctrl+V → 送る前のプレビューと、送ったあとの表示を見る。");
        W("     各セルに「動く」「静止画」「貼れない」「その他(内容)」のどれかを書く。");
        W("     X はブラウザの投稿欄に貼る。");
        W("     ペイントの列は §8.1 に無いので「該当なし」と書く(測らない)。");
        W("  3. (b)、(c) でも同じことをする。押すたびに、この下(標準出力にも)にクリップボードの形式の一覧が出る。");
        W("     (参考) のボタンは表に入れなくてよい。押したときの形式の一覧だけ貼る(FileName・FileNameW が消えるかを見る。INV-10 の材料)。");
        W("  4. ドラッグ欄から各アプリの入力欄へドラッグして落とし、同じように書く。");
        W("  5. この窓の文字(標準出力)を全部コピーして、表と一緒に貼る(GetFormats の一覧は Q-16 の材料)。");
        W("  補足: Discord は Nitro 加入済みなので添付の上限は気にしなくてよい。見るのは、貼った GIF がアニメのまま動くかどうか。");
        W("  補足: Slack・Discord は貼ったあと送信してから表示が変わることがあるので、送信後の表示で判定する(送信先は自分だけのチャンネルや DM にする)。");
        W();
        W("| 載せ方 | Discord(デスクトップ) | LINE(PC) | Slack(デスクトップ) | X(ブラウザ) | ペイント |");
        W("|---|---|---|---|---|---|");
        W("| ファイル(FileDrop)だけ |  |  |  |  |  |");
        W("| 画像(PNG・DIB・DIBV5)だけ |  |  |  |  |  |");
        W("| ファイルと画像の両方 |  |  |  |  |  |");
        W("| ドラッグ欄からドラッグ |  |  |  |  |  |");
        W();
    }
}
