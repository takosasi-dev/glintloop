// AC-21: メインウィンドウを画面の外に非アクティブで出し、UIA の木を調べる。(1) 静けさ、(2) 名前、(3) 大きさ、(4) Tab の巡回、(5) フォーカスの輪、(6) 通知バーと文言。
// 対象は U0 の時点の部品(コマンドバー・ペインの境界・通知バーと、13 行の部品を並べた ComponentGallery)。(7) の DPI は U0 の範囲外。
// ペインの境界は幅が space-2(8 DIP)の隙間なので、(3) の hit-min の検査から外す(仕様の食い違いとして報告した)。
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Shell;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Notices;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Static;

namespace GlintLoop.Tests.Ui;

public sealed class ShellAc21Tests
{
    // 前面のウィンドウを変える Win32 と WPF の呼び出し(検査の語。INV-18 のテストの語の検査に当たらないよう分けて書く)
    private static readonly string[] ForegroundTerms =
    [
        "SetForeground" + "Window", "BringWindow" + "ToTop", "SetActive" + "Window", "SwitchToThis" + "Window",
        "AllowSetForeground" + "Window", "LockSetForeground" + "Window", ".Acti" + "vate(", "ShowActivated = " + "true", "Topmost",
    ];

    // FR-92 の「前面に出す」を置く製品の 1 ファイルだけを、前面の関数の検査から外す(仕様書 AC-21 の 2026-11-05 の注記。テストはこのファイルを呼ばず、偽の実装を使う)
    private static readonly string[] ForegroundExcluded = ["src/GlintLoop.App/Platform/WindowForeground.cs"];

    // マウスカーソルを動かす(または入力を合成してカーソルを動かしうる)Win32 と .NET の呼び出し(検査の語。この検査と INV-18 の検査に当たらないよう、
    // コメントの中でも語を分けて書く)。Win32: カーソルの位置を決める 2 つ(論理の座標・物理の座標)、カーソルを範囲に閉じ込める関数(範囲の外のカーソルを中へ動かす)、
    // キーとマウスの入力の合成 2 つ、ポインター(ペン・タッチ)の合成 2 つ。WinForms: Cursor の Position と Clip への代入。WPF の Mouse には位置を決める API が無い
    private static readonly string[] CursorTerms =
    [
        "SetCursor" + "Pos", "SetPhysicalCursor" + "Pos", "Clip" + "Cursor", "Send" + "Input", "mouse" + "_event",
        "InjectSyntheticPointer" + "Input", "InjectTouch" + "Input", "Cursor.Position " + "=", "Cursor.Clip " + "=",
    ];

    /// <summary>実際のデータフォルダ(%APPDATA%\GlintLoop と %LOCALAPPDATA%\GlintLoop)のファイルの一覧と最終更新時刻。無ければ「無し」。</summary>
    private static string RealDataFolders()
    {
        var parts = new List<string>();
        foreach (var env in new[] { "APPDATA", "LOCALAPPDATA" })
        {
            var baseDir = Environment.GetEnvironmentVariable(env);
            Check.True(!string.IsNullOrEmpty(baseDir), $"環境変数 {env} が無い");
            var dir = Path.Combine(baseDir!, "GlintLoop");
            if (!Directory.Exists(dir))
            {
                parts.Add(env + ": 無し");
                continue;
            }
            foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                parts.Add($"{env}: {Path.GetRelativePath(dir, f)} {File.GetLastWriteTimeUtc(f):O}");
        }
        return string.Join("\n", parts);
    }

    /// <summary>ギャラリーをキャンバスの領域に差し込んだアプリ(ウェルカム画面の無効表示を外す)。</summary>
    private static (TestApp App, ComponentGallery Gallery) StartWithGallery()
    {
        var app = TestApp.Start();
        var gallery = app.Ui(() =>
        {
            app.Shell.Window.SetWelcome(false);
            var g = new ComponentGallery();
            app.Shell.Window.CanvasHost.Content = g;
            return g;
        });
        UiHost.Idle();
        return (app, gallery);
    }

    [Test]
    public void AC21_1_前面がGlintLoopのウィンドウにならずカーソルを動かす呼び出しが無く実際のデータフォルダが変わらない()
    {
        // 前面のウィンドウ: テストの間ずっと GlintLoop のウィンドウ(このプロセスのウィンドウ)が前面にならず、
        // src に前面を変える関数の呼び出しが無い(AC-21 の (1) の 2026-10-28 の注記。テストの外の操作で前面が変わっても落ちない)
        NoForegroundCalls();
        // カーソル: 位置の前後の比較は、テストの間に人がマウスを動かすと落ちるので行わない。代わりに、src と tests にカーソルを動かす関数の呼び出しが無いことを確かめる
        NoCursorCalls();
        var data = RealDataFolders();
        int samples, own;
        using (var watch = new ForegroundWatch())
        {
            var (app, gallery) = StartWithGallery();
            using (app)
            {
                // 部品の操作は UIA の提供側(AutomationPeer のパターン)を UI スレッドで直接呼ぶ。UIA のクライアント(TogglePattern.Toggle など)から
                // 呼ぶと、UIA の仕組みがウィンドウを前面に出す(素の WPF のウィンドウでも起きる。報告済み)ので、この検査では使わない
                var root = Uia.Root(app.Hwnd);
                Check.True(Uia.TryFind(root, "コピー") is not null && Uia.TryFind(root, "トグル") is not null, "UIA で部品が見つからない");
                app.Ui(() =>
                {
                    var buttons = AllOf<ButtonBase>(gallery);
                    ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(buttons.OfType<Button>().First(b => Equals(b.Content, "コピー"))).GetPattern(PatternInterface.Invoke)).Invoke();
                    ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(buttons.OfType<ToggleButton>().First(b => Equals(b.Content, "トグル"))).GetPattern(PatternInterface.Toggle)).Toggle();
                });
                UiHost.Idle();
                // キーはウィンドウへ直接発生させる(Ctrl+0 のフィット)。キーボードフォーカスは動かさない: 非アクティブなウィンドウの部品に
                // キーボードフォーカスを当てる(Keyboard.Focus、ダイアログを開いたときの最初のフォーカス)と、WPF の SetFocus でウィンドウが前面になる(報告済み)
                app.Ui(() =>
                {
                    app.Modifiers.Current = ModifierKeys.Control;
                    UiInput.Key(app.Shell.Window, Key.D0);
                    app.Modifiers.Current = ModifierKeys.None;
                    app.Shell.Settings.Theme = GlintLoop.Core.Settings.ThemeSetting.Light;
                    app.Clock.Advance(TimeSpan.FromSeconds(1));
                });
                UiHost.Idle();
                Check.True(File.Exists(app.Paths.SettingsFile), "設定を一時フォルダに保存していない");
            }
            UiHost.Idle();
            samples = watch.Samples;
            own = watch.OwnHits;
        }
        Check.True(samples >= 10, $"前面の見張りが回っていない({samples} 回)");
        Check.Equal(0, own, $"GlintLoop のウィンドウが前面になった回数({samples} 回のうち)");
        Check.Equal(data, RealDataFolders(), "実際のデータフォルダ");
    }

    [Test]
    public void AC21_1_テストのUIAのInvokeとToggleは前面を変えず対応する部品に届く()
    {
        // Uia.Invoke・Uia.Toggle は、UIA の要素から木の中の位置で AutomationPeer を探し、提供側を UI スレッドで呼ぶ(UIA のクライアントから呼ぶと前面に出る)
        int samples, own;
        using (var watch = new ForegroundWatch())
        {
            var (app, gallery) = StartWithGallery();
            using (app)
            {
                var clicks = 0;
                var (copy, toggle) = app.Ui(() =>
                {
                    var buttons = AllOf<ButtonBase>(gallery);
                    var c = buttons.OfType<Button>().First(b => Equals(b.Content, "コピー"));
                    c.Click += (_, _) => clicks++;
                    return (c, buttons.OfType<ToggleButton>().First(b => Equals(b.Content, "トグル")));
                });
                var before = app.Ui(() => toggle.IsChecked == true);
                var root = Uia.Root(app.Hwnd);
                Uia.Invoke(Uia.Find(root, "コピー"));
                Uia.Toggle(Uia.Find(root, "トグル"));
                UiHost.Idle();
                Check.Equal(1, app.Ui(() => clicks), "「コピー」のクリックの回数");
                Check.Equal(!before, app.Ui(() => toggle.IsChecked == true), "「トグル」が切り替わらない");
                Check.Equal(before ? ToggleState.Off : ToggleState.On, Uia.ToggleStateOf(Uia.Find(root, "トグル")), "UIA の ToggleState");
                Check.True(app.Ui(() => !copy.IsKeyboardFocused), "押しただけでキーボードフォーカスが移った");
            }
            UiHost.Idle();
            samples = watch.Samples;
            own = watch.OwnHits;
        }
        Check.True(samples >= 10, $"前面の見張りが回っていない({samples} 回)");
        Check.Equal(0, own, $"GlintLoop のウィンドウが前面になった回数({samples} 回のうち)");
    }

    [Test]
    public void AC21_1_テストのUIAのSelectとSetValueは前面を変えず対応する部品に届く()
    {
        // Uia.Select・Uia.SetValue も Invoke・Toggle と同じく、提供側(ISelectionItemProvider・IRangeValueProvider)を UI スレッドで呼ぶ
        // (UIA のクライアントの SelectionItemPattern.Select・RangeValuePattern.SetValue から呼ぶと前面に出うる)
        int samples, own;
        using (var watch = new ForegroundWatch())
        {
            var (app, gallery) = StartWithGallery();
            using (app)
            {
                var (item, slider) = app.Ui(() =>
                {
                    var seg = AllOf<ListBox>(gallery).First(l => AutomationProperties.GetName(l) == "ディザ");
                    var i = seg.Items.OfType<ListBoxItem>().First(x => Equals(x.Content, "誤差拡散"));
                    return (i, AllOf<Slider>(gallery).First(s => AutomationProperties.GetName(s) == "強さ"));
                });
                Check.True(app.Ui(() => !item.IsSelected && slider.Value != 73), "テストの前提(項目が選ばれていない・スライダーが 73 でない)");
                var root = Uia.Root(app.Hwnd);
                var list = Uia.Find(root, "ディザ");
                Uia.Select(list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "誤差拡散"))
                    ?? throw new InvalidOperationException("UIA で「誤差拡散」が見つからない"));
                Uia.SetValue(Uia.Find(root, "強さ"), 73);
                UiHost.Idle();
                Check.True(app.Ui(() => item.IsSelected), "「誤差拡散」が選ばれない");
                Check.Equal(73.0, app.Ui(() => slider.Value), "スライダーの値");
                Check.True(app.Ui(() => !item.IsKeyboardFocused && !slider.IsKeyboardFocused), "選んだだけでキーボードフォーカスが移った");
            }
            UiHost.Idle();
            samples = watch.Samples;
            own = watch.OwnHits;
        }
        Check.True(samples >= 10, $"前面の見張りが回っていない({samples} 回)");
        Check.Equal(0, own, $"GlintLoop のウィンドウが前面になった回数({samples} 回のうち)");
    }

    [Test]
    public void AC21_1_非アクティブなウィンドウでダイアログを開いても前面にならず最初の部品に論理フォーカスだけが当たる()
    {
        // DialogHost はウィンドウがアクティブなときだけキーボードフォーカスを動かす。そうでなければ FocusManager の論理フォーカスだけを決め、
        // 閉じたら開く前の論理フォーカスに戻す(ウィンドウがあとでアクティブになったとき、WPF がそこへキーボードフォーカスを戻す)
        int samples, own;
        using (var watch = new ForegroundWatch())
        {
            using (var app = TestApp.Start())
            {
                var (first, second, before) = app.Ui(() =>
                {
                    var w = app.Shell.Window;
                    Check.True(!w.IsActive, "ウィンドウがアクティブ(テストの前提)");
                    var a = new Button { Content = "一つ目" };
                    var b = new Button { Content = "二つ目" };
                    var before = FocusManager.GetFocusedElement(w);
                    _ = app.Shell.Dialogs.Show(new StackPanel { Children = { a, b } }, "DialogShortcutsWidth", null, MotionCause.Keyboard);
                    return (a, b, before);
                });
                Check.True(UiHost.WaitUntil(() => ReferenceEquals(FocusManager.GetFocusedElement(app.Shell.Window), first), TimeSpan.FromSeconds(5)),
                    "ダイアログの最初の部品に論理フォーカスが当たらない");
                UiHost.Idle();
                app.Ui(() =>
                {
                    Check.True(!first.IsKeyboardFocused && !second.IsKeyboardFocused, "非アクティブなウィンドウでキーボードフォーカスが動いた");
                    Check.True(!app.Shell.Window.IsActive, "ダイアログを開いたらウィンドウがアクティブになった");
                    app.Shell.Dialogs.Close();
                    Check.Equal(before, FocusManager.GetFocusedElement(app.Shell.Window), "閉じたあとの論理フォーカスが開く前に戻らない");
                });
                UiHost.Idle();
            }
            UiHost.Idle();
            samples = watch.Samples;
            own = watch.OwnHits;
        }
        Check.True(samples >= 10, $"前面の見張りが回っていない({samples} 回)");
        Check.Equal(0, own, $"GlintLoop のウィンドウが前面になった回数({samples} 回のうち)");
    }

    /// <summary>root の下の T をすべて(ビジュアルの木)。</summary>
    private static List<T> AllOf<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) found.Add(t);
            for (var i = VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return found;
    }

    /// <summary>src(製品のコード)に、前面のウィンドウを変える関数の呼び出しが無い。</summary>
    private static void NoForegroundCalls()
    {
        var files = RepoFiles.Under(["src"], RepoFiles.SourceExtensions);
        var hits = SourceScan.FindTermsInFiles(files, ForegroundTerms, ForegroundExcluded);
        Check.True(hits.Count == 0, $"src に前面を変える呼び出しが {hits.Count} 件: {string.Join(", ", hits.Take(20))}");
    }

    [Test]
    public void AC21_1_前面を変える呼び出しの検査は語を見つける()
    {
        foreach (var t in ForegroundTerms)
            Check.True(SourceScan.FindTerms("x.cs", "w" + t + "x", ForegroundTerms).Count >= 1, $"「{t}」を見つけない");
        Check.Equal(1, SourceScan.FindTerms("x.cs", "window.Acti" + "vate();", ForegroundTerms).Count, "Window.Activate() を見つけない");
        Check.Equal(0, SourceScan.FindTerms("x.cs", "ShowActivated = false;", ForegroundTerms).Count, "ShowActivated = false を当てた");
    }

    [Test]
    public void AC21_1_前面を変える呼び出しの検査は決めた1ファイルだけを外す()
    {
        // 外したファイルは中身を読まない(ファイルがまだ無くても、できて語を含んでも 0 件)。綴りが違えば読みに行って落ちる
        var path = Path.Combine(RepoFiles.Root, "src", "GlintLoop.App", "Platform", "WindowForeground.cs");
        Check.Equal(0, SourceScan.FindTermsInFiles([path], ForegroundTerms, ForegroundExcluded).Count, "外したファイル");
        var other = Directory.CreateTempSubdirectory("glintloop-scan-").FullName;
        try
        {
            var file = Path.Combine(other, "x.cs");
            File.WriteAllText(file, "SetForeground" + "Window");
            Check.Equal(1, SourceScan.FindTermsInFiles([file], ForegroundTerms, ForegroundExcluded).Count, "ほかのファイルは調べる");
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    /// <summary>src(製品のコード)と tests(テストのコード)に、マウスカーソルを動かす関数の呼び出しが無い。</summary>
    private static void NoCursorCalls()
    {
        var files = RepoFiles.Under(["src", "tests"], RepoFiles.SourceExtensions);
        Check.True(files.Count > 0, "src と tests のソースが無い");
        var hits = SourceScan.FindTermsInFiles(files, CursorTerms, []);
        Check.True(hits.Count == 0, $"src と tests にカーソルを動かす呼び出しが {hits.Count} 件: {string.Join(", ", hits.Take(20))}");
    }

    [Test]
    public void AC21_1_カーソルを動かす呼び出しの検査は語を見つけ関係の無い語を当てない()
    {
        foreach (var t in CursorTerms)
            Check.True(SourceScan.FindTerms("x.cs", "w" + t + "x", CursorTerms).Count >= 1, $"「{t}」を見つけない");
        Check.Equal(1, SourceScan.FindTerms("x.cs", "[DllImport(\"user32.dll\")] static extern bool " + "SetCursor" + "Pos(int x, int y);", CursorTerms).Count, "カーソルの位置を決める関数の宣言を見つけない");
        Check.Equal(1, SourceScan.FindTerms("x.cs", "System.Windows.Forms." + "Cursor.Position " + "= new Point(0, 0);", CursorTerms).Count, "Cursor.Position への代入を見つけない");
        // 読むだけの呼び出しと、カーソルの形を変えるだけの呼び出しは当てない
        foreach (var line in new[] { "GetCursorPos(out var p);", "Mouse.SetCursor(Cursors.Arrow);", "var p = Mouse.GetPosition(w);", "var p = Cursor.Position;", "Mouse.OverrideCursor = null;", "this.Cursor = Cursors.Hand;" })
            Check.Equal(0, SourceScan.FindTerms("x.cs", line, CursorTerms).Count, $"「{line}」を当てた");
    }

    [Test]
    public void AC21_2_3_操作できる部品は名前を持ち当たり判定が32DIP以上()
    {
        var (app, _) = StartWithGallery();
        using (app)
        {
            var scale = app.DpiScale;
            var root = Uia.Root(app.Hwnd);
            var focusables = Uia.Focusables(root);
            Check.True(focusables.Count >= 25, $"操作できる部品が少ない: {focusables.Count}");
            var splitterNames = app.Ui(() => new[] { app.Shell.Window.LeftSplitter, app.Shell.Window.RightSplitter, app.Shell.Window.LayersStackSplitter, app.Shell.Window.StackParamsSplitter }
                .Select(AutomationProperties.GetName).ToHashSet());
            var noName = new List<string>();
            var small = new List<string>();
            foreach (var e in focusables)
            {
                var name = e.Current.Name;
                if (string.IsNullOrWhiteSpace(name)) noName.Add(Uia.Describe(e));
                if (splitterNames.Contains(name)) continue;
                var r = Uia.RectDip(e, scale);
                if (r.Width < 32 - 0.5 || r.Height < 32 - 0.5) small.Add($"{Uia.Describe(e)} {r.Width:F1}×{r.Height:F1}");
            }
            Check.True(noName.Count == 0, "名前の無い部品: " + string.Join(", ", noName));
            Check.True(small.Count == 0, "32 DIP 未満の部品: " + string.Join(", ", small));
            // アイコンだけのボタンの名前は §9.1 のアイコンの一覧の名前
            var iconNames = Icons.All.Select(i => i.Name).ToHashSet();
            foreach (var c in CommandBarView.Commands)
                Check.True(iconNames.Contains(c.Icon.Name) && Uia.TryFind(root, c.Icon.Name) is not null, $"コマンドバーの「{c.Icon.Name}」");
            Check.Equal(29, Icons.All.Count, "§9.1 のアイコンの数");
            var toggle = Uia.Find(root, "分割比較");
            Check.Equal(ToggleState.Off, Uia.ToggleStateOf(toggle), "分割比較のトグルの状態");
        }
    }

    [Test]
    public void AC21_4_Tabの巡回は全部を1回ずつ通って最初に戻りPreviousは逆順()
    {
        var (app, _) = StartWithGallery();
        using (app)
        {
            app.Ui(() =>
            {
                var w = app.Shell.Window;
                var first = w.CommandBar.Button(Icons.PasteImage);
                List<IInputElement> Walk(FocusNavigationDirection dir)
                {
                    Keyboard.Focus(first);
                    var seen = new List<IInputElement> { Keyboard.FocusedElement };
                    for (var i = 0; i < 300; i++)
                    {
                        ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(dir));
                        if (Keyboard.FocusedElement == first) return seen;
                        seen.Add(Keyboard.FocusedElement);
                    }
                    throw new CheckFailedException("300 回で最初に戻らない");
                }
                var next = Walk(FocusNavigationDirection.Next);
                Check.Equal(next.Count, next.Distinct().Count(), "Next で同じ部品を 2 回通った");
                // Tab で止まるべき部品(リストの中の項目とタブは、一覧ごとに 1 つ)
                var expected = new List<UIElement>();
                void Collect(DependencyObject d)
                {
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                    {
                        var c = VisualTreeHelper.GetChild(d, i);
                        if (c is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } u && KeyboardNavigation.GetIsTabStop(u) &&
                            u is ButtonBase or TextBox or Slider or PaneSplitter or ComboBox) expected.Add(u);
                        Collect(c);
                    }
                }
                Collect(w);
                var missing = expected.Where(e => !next.Contains(e)).Select(e => e.GetType().Name + " " + AutomationProperties.GetName(e)).ToList();
                Check.True(missing.Count == 0, "Tab で通らない部品: " + string.Join(", ", missing));
                foreach (var list in new[] { "ディザ", "ルックの一覧", "スタック" })
                    Check.True(next.OfType<ListBoxItem>().Count(i => AutomationProperties.GetName(ItemsControl.ItemsControlFromItemContainer(i)) == list) == 1,
                        $"一覧「{list}」の項目に 1 回だけ止まらない");
                Check.True(next.OfType<TabItem>().Count() == 1, "タブに 1 回だけ止まらない");
                var prev = Walk(FocusNavigationDirection.Previous);
                Check.SequenceEqual(next.Skip(1).Reverse().Prepend(next[0]).ToList(), prev, "Previous の順");
            });
        }
    }

    [Test]
    public void AC21_5_13行の部品のフォーカスの輪が外側3DIPの4辺の中央に出る()
    {
        var (app, gallery) = StartWithGallery();
        using (app)
        {
            var rows = app.Ui(() => gallery.Rows.ToList());
            Check.Equal(13, rows.Select(r => r.Row.Split("(")[0]).Distinct().Count(), "表の行の数");
            Check.Equal(14, rows.Count, "見本の数(副ボタンの行はドロップダウンを含めて 2 つ)");
            var failures = new List<string>();
            foreach (var (row, focus, ring) in rows)
            {
                app.Ui(() => Keyboard.Focus(focus));
                UiHost.Idle();
                app.Ui(() =>
                {
                    var w = app.Shell.Window;
                    var color = ((SolidColorBrush)w.FindResource("FocusRingBrush")).Color;
                    var snap = Snapshot.Of(w, w.RootLayer);
                    var b = ring.TransformToAncestor(w).TransformBounds(new Rect(ring.RenderSize));
                    // 輪は外側に間隔 2・幅 2。外側 3 DIP の位置は輪の幅の中央
                    var points = new[]
                    {
                        new Point(b.Left + b.Width / 2, b.Top - 3), new Point(b.Left + b.Width / 2, b.Bottom + 3),
                        new Point(b.Left - 3, b.Top + b.Height / 2), new Point(b.Right + 3, b.Top + b.Height / 2),
                    };
                    foreach (var p in points)
                        if (!snap.Near(p, color, 4)) failures.Add($"{row}: ({p.X:F1},{p.Y:F1}) が {snap.Describe(p)}");
                    if (!FocusRing.IsShown(ring)) failures.Add($"{row}: 輪を出していない");
                });
            }
            Check.True(failures.Count == 0, "フォーカスの輪: " + string.Join(" / ", failures));
        }
    }

    // §9.12 の通知バーとダイアログの文言の表(40 行)を書き写したもの
    private static readonly (string Key, string Kind, string Text)[] NoticeTable =
    [
        ("paste.empty", "warn", "クリップボードに画像がありません"),
        ("import.multi", "info", "{N} 個のうち {M} 個を取り込みました"),
        ("import.web_url", "warn", "画像のデータがありません。ブラウザで画像をコピーして、貼り付けてください"),
        ("import.unsupported", "error", "この形式には対応していません(PNG・JPEG・WebP・BMP・ICO・GIF)"),
        ("import.animated", "info", "アニメーションの 1 枚目だけを使っています"),
        ("import.too_large_file", "error", "ファイルが大きすぎます({size} MB。上限は 100 MB)"),
        ("import.too_large_side", "error", "画像の縦または横が大きすぎます({w}×{h} px。上限は 16384 px)"),
        ("import.too_many_pixels", "error", "画素数が多すぎます({mp} メガピクセル。上限は 64 メガピクセル)"),
        ("import.decode_error", "error", "画像を読み込めませんでした。ファイルが壊れている可能性があります"),
        ("import.read_error", "error", "ファイルを開けませんでした"),
        ("import.gpu_shrunk", "info", "GPU の上限のため縮小して取り込みました"),
        ("layer.added", "info", "レイヤーとして追加しました"),
        ("layer.full", "warn", "レイヤーは 32 枚までです"),
        ("layer.budget", "warn", "画像の合計が大きすぎます(上限は 300 MB または 100 メガピクセル)"),
        ("layer.render_shrunk", "info", "大きいレイヤーは、縮小して重ねました"),
        ("stack.full", "warn", "エフェクトは 64 個までです"),
        ("batch.full", "warn", "バッチは 200 個までです"),
        ("batch.layers_ignored", "常時表示", "いちばん下のレイヤーと全体のスタックだけを適用します。ほかのレイヤーは使いません"),
        ("stack.unknown_effect", "warn", "未対応のエフェクトを外しました"),
        ("font.missing", "warn", "フォントが見つからないため置き換えました"),
        ("copy.done", "info", "コピーしました({size})"),
        ("save.done", "info", "保存しました"),
        ("save.failed", "error", "保存できませんでした。保存先を変えて試してください"),
        ("clip.failed", "error", "クリップボードを使えませんでした。もう一度試してください"),
        ("export.size_fail", "warn", "上限({limit} バイト)に収まりませんでした。最小の設定で {size} バイトです"),
        ("export.confirm_over", "ダイアログ", "上限を超えています。それでも渡しますか(ボタン:渡す、やめる。既定は「やめる」)"),
        ("export.drag_again", "info", "もう一度ドラッグすると渡します"),
        ("export.gif_too_big", "error", "{fmt} が大きすぎます。サイズかフレーム数を減らしてください"),
        ("export.failed", "error", "書き出せませんでした。もう一度試してください"),
        ("export.condition", "warn", "条件:{内容}を満たしていません"),
        ("render.failed", "error", "表示を更新できませんでした。もう一度試してください"),
        ("gpu.software", "warn", "ソフトウェア描画で動いています(遅くなります)"),
        ("settings.save_failed", "error", "設定を保存できません"),
        ("project.saved", "info", "プロジェクトには元の画像の情報(撮影場所を含むことがあります)が入っています。人に渡すときは書き出したファイルを使ってください"),
        ("project.too_new", "error", "新しい版のファイルです。このソフトでは開けません"),
        ("project.invalid", "error", "プロジェクトを開けませんでした。ファイルが壊れています"),
        ("replace.confirm", "ダイアログ", "現在の編集内容を置き換えますか(ボタン:保存して置き換え、保存せず置き換え、キャンセル。既定は「キャンセル」)"),
        ("name.overwrite", "ダイアログ", "同じ名前のものがあります。上書きしますか(ボタン:上書き、キャンセル。既定は「キャンセル」)"),
        ("crash.title", "ダイアログ", "エラーが起きました(本文に退避した場所を表示し、ボタンは「終了」)"),
        ("crash.nosave", "ダイアログ", "編集内容を退避できませんでした(退避した場所の代わりに、本文にこの文言を表示する)"),
    ];

    private static string KindName(NoticeKind k) => k switch
    {
        NoticeKind.Info => "info",
        NoticeKind.Warn => "warn",
        NoticeKind.Error => "error",
        NoticeKind.Dialog => "ダイアログ",
        _ => "常時表示",
    };

    [Test]
    public void AC21_6_文言のリソースが表の40行と一致しリソースのファイル以外に無い()
    {
        Check.Equal(40, NoticeTable.Length, "書き写した表の行数");
        var rows = NoticeTexts.Rows;
        Check.Equal(NoticeTable.Length, rows.Count, "リソースの行数");
        for (var i = 0; i < NoticeTable.Length; i++)
        {
            Check.Equal(NoticeTable[i].Key, rows[i].Key, $"{i + 1} 行目のキー");
            Check.Equal(NoticeTable[i].Kind, KindName(rows[i].Kind), $"{NoticeTable[i].Key} の種別");
            Check.Equal(NoticeTable[i].Text, rows[i].Text, $"{NoticeTable[i].Key} の文言");
        }
        Check.SequenceEqual(NoticeTable.Select(r => r.Key).ToList(), NoticeKeys.All, "Core の NoticeKeys.All");

        // 表の文言の文字列が、リソースのファイル(notices.tsv)以外の src のソースに無い
        var hits = new List<string>();
        foreach (var f in RepoFiles.Under(["src"], RepoFiles.SourceExtensions))
        {
            var text = RepoFiles.ReadText(f);
            foreach (var row in NoticeTable)
            {
                var body = row.Kind == "ダイアログ" ? row.Text[..row.Text.LastIndexOf('(')] : row.Text;
                if (text.Contains(body, StringComparison.Ordinal)) hits.Add($"{RepoFiles.Relative(f)}: {row.Key}");
            }
        }
        Check.True(hits.Count == 0, "リソースの外の文言: " + string.Join(", ", hits));
    }

    [Test]
    public void AC21_6_置き換えの関数が表の書式どおり()
    {
        Check.Equal("123.5", NoticeFormat.MegabytesOneDecimal(123_456_789), "import.too_large_file の size");
        Check.Equal("67.1", NoticeFormat.MegapixelsOneDecimal(67_108_865), "import.too_many_pixels の mp");
        Check.Equal("12.3 KB", NoticeFormat.KilobytesOneDecimal(12_345), "copy.done の 12,345 バイト");
        Check.Equal("1.0 KB", NoticeFormat.KilobytesOneDecimal(999), "copy.done の 999 バイト");
        Check.Equal("2000", NoticeFormat.Integer(2000), "export.size_fail の limit");
        Check.Equal("5 個のうち 3 個を取り込みました", NoticeTexts.Format(NoticeKeys.ImportMulti, ("N", "5"), ("M", "3")), "import.multi");
        Check.Equal("コピーしました(12.3 KB)", NoticeTexts.Format(NoticeKeys.CopyDone, ("size", NoticeFormat.KilobytesOneDecimal(12_345))), "copy.done の文");
        Check.Equal("現在の編集内容を置き換えますか", NoticeTexts.DialogMessage(NoticeKeys.ReplaceConfirm), "ダイアログの本文");
        Check.Equal(NoticeLevel.Warn, NoticeTexts.LevelOf(NoticeKeys.PasteEmpty), "paste.empty の種別");
        Check.True(NoticeTexts.LevelOf(NoticeKeys.ReplaceConfirm) is null, "ダイアログの行に通知バーの種別がある");
    }

    [Test]
    public void AC21_6_通知バーは1つだけ高さ40でキャンバスに重なりinfoは5秒で消えwarnとerrorは閉じるまで残る()
    {
        using var app = TestApp.Start();
        var (canvasBefore, imageBefore) = app.Ui(() => (app.Shell.Window.CanvasHost.RenderSize, app.Shell.Window.CanvasHost.TranslatePoint(new Point(), app.Shell.Window)));
        app.Ui(() => app.Shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.LayerAdded)));
        // notice-in(上から滑り降りる 200 ms)が終わるまで待つ
        Check.True(UiHost.WaitUntil(() =>
        {
            var w = app.Shell.Window;
            return Math.Abs(app.Shell.NoticeBar.TranslatePoint(new Point(), w).Y - w.CanvasHost.TranslatePoint(new Point(), w).Y) < 0.01 &&
                   app.Shell.NoticeBar.Opacity == 1;
        }, TimeSpan.FromSeconds(2)), "通知バーの出現の動きが終わらない");
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            var bar = app.Shell.NoticeBar;
            Check.True(bar.IsVisible && bar.IsShowing, "info が出ない");
            Check.Near(40.0, bar.ActualHeight, 0.5, "通知バーの高さ(DPI の丸めで 1 画素未満ずれうる)");
            Check.Equal("レイヤーとして追加しました", bar.Text, "文");
            var top = bar.TranslatePoint(new Point(), w);
            var canvasTop = w.CanvasHost.TranslatePoint(new Point(), w);
            Check.Near(canvasTop.Y, top.Y, 0.01, "通知バーの上端がキャンバスの上端");
            Check.Near(w.CanvasHost.ActualWidth, bar.ActualWidth, 0.01, "通知バーの幅がキャンバスと同じ");
            Check.Equal(canvasBefore, w.CanvasHost.RenderSize, "通知バーを出してもキャンバスの大きさは同じ");
            Check.Equal(imageBefore, w.CanvasHost.TranslatePoint(new Point(), w), "通知バーを出してもキャンバスの位置は同じ");
            app.Clock.Advance(TimeSpan.FromSeconds(4.7));
            Check.True(bar.IsShowing, "4.7 秒で info が消えた");
            app.Clock.Advance(TimeSpan.FromSeconds(0.6));
            Check.True(!bar.IsShowing, "5.3 秒で info が消えない");

            bar.Show(NoticeTexts.Notice(NoticeKeys.PasteEmpty));
            bar.Show(NoticeTexts.Notice(NoticeKeys.ImportReadError));
            Check.Equal("ファイルを開けませんでした", bar.Text, "新しいメッセージに置き換わる");
            Check.Equal(1, FindAll<NoticeBar>(w).Count, "通知バーの数");
            app.Clock.Advance(TimeSpan.FromMinutes(5));
            Check.True(bar.IsShowing, "error が 5 分で消えた");
        });
        // 閉じるボタンを UIA で押す
        var root = Uia.Root(app.Hwnd);
        var bar = Uia.FindById(root, "NoticeBar");
        var close = bar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "閉じる"));
        Check.True(close is not null, "通知バーの閉じるボタンが UIA で見つからない");
        Uia.Invoke(close!);
        Check.True(UiHost.WaitUntil(() => !app.Shell.NoticeBar.IsShowing && !app.Shell.NoticeBar.IsVisible, TimeSpan.FromSeconds(2)), "閉じるボタンで消えない");
        app.Ui(() =>
        {
            app.Shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.PasteEmpty));
            app.Clock.Advance(TimeSpan.FromMinutes(5));
            Check.True(app.Shell.NoticeBar.IsShowing, "warn が 5 分で消えた");
        });
    }

    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var list = new List<T>();
        void Walk(DependencyObject d)
        {
            if (d is T t) list.Add(t);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return list;
    }
}
