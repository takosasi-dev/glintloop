// AC-21 の (2)〜(5)・(7) を、本物の部品がそろった編集画面(画像を取り込み、空のレイヤーとぼかしを足した文書)で調べる。U0 の見本(ComponentGallery)の検査は ShellAc21Tests。
// 状態は 3 つ(左ペインのルックのタブ、エフェクトのタブ、スタックの行を選んでパラメータ欄にぼかしのパラメータを出した状態)で調べる。ペインの境界 4 か所は hit-min の対象外(§9.3 の注)。
// (7) の DPI は VisualTreeHelper.SetRootDpi で倍率を変え、表示中の部品の矩形(DIP)を TransformToAncestor で求めて、親の内側と兄弟の重なりを調べる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Looks;
using GlintLoop.App.Shell;
using GlintLoop.App.Stack;
using GlintLoop.App.Theme;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Ui;

public sealed class ShellAc21EditingTests
{
    private static HashSet<string> SplitterNames(TestApp app) => app.Ui(() =>
        new[] { app.Shell.Window.LeftSplitter, app.Shell.Window.RightSplitter, app.Shell.Window.LayersStackSplitter, app.Shell.Window.StackParamsSplitter }
            .Select(AutomationProperties.GetName).ToHashSet());

    /// <summary>名前の無い部品と、32 DIP 未満の部品(ペインの境界を除く)。</summary>
    private static (List<string> NoName, List<string> Small, int Count) NamesAndSizes(TestApp app)
    {
        var scale = app.DpiScale;
        var splitters = SplitterNames(app);
        var noName = new List<string>();
        var small = new List<string>();
        var focusables = Uia.Focusables(Uia.Root(app.Hwnd));
        foreach (var e in focusables)
        {
            var name = e.Current.Name;
            if (string.IsNullOrWhiteSpace(name)) noName.Add(Uia.Describe(e) + " " + e.Current.ClassName + " " + Uia.RectDip(e, scale));
            if (splitters.Contains(name)) continue;
            var r = Uia.RectDip(e, scale);
            if (r.Width < 32 - 0.5 || r.Height < 32 - 0.5) small.Add($"{Uia.Describe(e)} {r.Width:F1}×{r.Height:F1}");
        }
        return (noName, small, focusables.Count);
    }

    [Test]
    public void AC21_2_3_編集画面の操作できる部品は名前を持ち当たり判定が32DIP以上()
    {
        using var app = Start();
        var stack = M<StackModule>(app);
        var problems = new List<string>();
        void Check3(string state)
        {
            var (noName, small, count) = NamesAndSizes(app);
            if (count < 30) problems.Add($"{state}: 操作できる部品が少ない {count}");
            if (noName.Count > 0) problems.Add($"{state}: 名前の無い部品 " + string.Join(", ", noName));
            if (small.Count > 0) problems.Add($"{state}: 32 DIP 未満 " + string.Join(", ", small));
        }
        SelectTab(app, LeftPaneTab.Looks);
        Check3("ルックのタブ・レイヤーの設定");
        SelectTab(app, LeftPaneTab.Effects);
        Check3("エフェクトのタブ・レイヤーの設定");
        app.Ui(() => stack.SelectRow(0));
        UiHost.Idle();
        Check3("ぼかしの行を選んだ状態");
        Check.True(problems.Count == 0, string.Join(" / ", problems));

        // レイヤーの一覧の行は、レイヤーの名前(固定の行は「全体」)を Name に持ち、中の 3 つのボタンは「表示」・「複製」・「削除」
        var root = Uia.Root(app.Hwnd);
        var list = Uia.Find(root, GlintLoop.App.Layers.LayerTexts.List);
        var rows = list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToList();
        var names = app.Ui(() => app.Shell.Session.Document!.Layers.Select(l => l.Name).Reverse().Prepend("全体").ToList());
        Check.SequenceEqual(names, rows.Select(r => r.Current.Name).ToList(), "一覧の行の名前");
        foreach (var r in rows.Skip(1))
        {
            foreach (var b in new[] { "表示", "複製", "削除" })
                Check.True(r.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, b)) is not null, $"行「{r.Current.Name}」に「{b}」が無い");
            var eye = r.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "表示"));
            Check.Equal(ToggleState.On, Uia.ToggleStateOf(eye), $"行「{r.Current.Name}」の表示のトグル");
        }
        // アイコンだけのボタン(コマンドバー)の名前は §9.1 のアイコンの一覧の名前
        var iconNames = Icons.All.Select(i => i.Name).ToHashSet();
        foreach (var c in CommandBarView.Commands)
            Check.True(iconNames.Contains(c.Icon.Name) && Uia.TryFind(root, c.Icon.Name) is not null, $"コマンドバーの「{c.Icon.Name}」");
    }

    /// <summary>first から MoveFocus(dir) を繰り返し、first に戻るまでに通った要素(first を含む)。</summary>
    private static List<IInputElement> Walk(IInputElement first, FocusNavigationDirection dir)
    {
        Keyboard.Focus(first);
        var seen = new List<IInputElement> { Keyboard.FocusedElement };
        for (var i = 0; i < 2000; i++)
        {
            ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(dir));
            if (Keyboard.FocusedElement == first) return seen;
            seen.Add(Keyboard.FocusedElement);
        }
        throw new CheckFailedException("2000 回で最初に戻らない");
    }

    /// <summary>Tab で止まるべき部品(見えていて有効で、フォーカスを受けるボタン・入力欄・スライダー・境界・ドロップダウン)。リストの項目とタブは一覧ごとに 1 つなので別に数える。</summary>
    private static List<UIElement> TabStops(DependencyObject root)
    {
        var expected = new List<UIElement>();
        void Collect(DependencyObject d)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } u && KeyboardNavigation.GetIsTabStop(u) &&
                    u is ButtonBase or TextBox or Slider or PaneSplitter or ComboBox && ItemsControl.ItemsControlFromItemContainer(u) is null) expected.Add(u);
                Collect(c);
            }
        }
        Collect(root);
        return expected;
    }

    [Test]
    public void AC21_4_編集画面のTabの巡回は全部を1回ずつ通って最初に戻りPreviousは逆順()
    {
        using var app = Start();
        var stack = M<StackModule>(app);
        foreach (var (state, list0, prepare) in new (string, string, Action)[]
                 {
                     ("ルックのタブ・レイヤーの設定", LooksText.Get("looks.list"), () => SelectTab(app, LeftPaneTab.Looks)),
                     ("エフェクトのタブ・ぼかしの行", StackTexts.EffectsTab + " " + StackTexts.StageName(GlintLoop.Core.Effects.EffectStage.Finish), () =>
                     {
                         SelectTab(app, LeftPaneTab.Effects);
                         app.Ui(() => stack.SelectRow(0));
                         UiHost.Idle();
                     }),
                 })
        {
            prepare();
            app.Ui(() =>
            {
                var w = app.Shell.Window;
                var first = w.CommandBar.Button(Icons.New);
                var next = Walk(first, FocusNavigationDirection.Next);
                Check.Equal(next.Count, next.Distinct().Count(), $"{state}: Next で同じ部品を 2 回通った");
                var missing = TabStops(w).Where(e => !next.Contains(e)).Select(e => e.GetType().Name + "「" + AutomationProperties.GetName(e) + "」").ToList();
                Check.True(missing.Count == 0, $"{state}: Tab で通らない部品: " + string.Join(", ", missing));
                // 一覧の項目には一覧ごとに 1 回だけ止まる。レイヤーの一覧は行の中に 3 つのボタンがあるので、行ごとに止まる(TabNavigation=Continue。各行 1 回)
                var layerList = GlintLoop.App.Layers.LayerTexts.List;
                var byList = next.OfType<ListBoxItem>().GroupBy(i => ItemsControl.ItemsControlFromItemContainer(i))
                    .Where(g => g.Count() > 1 && AutomationProperties.GetName(g.Key!) != layerList)
                    .Select(g => AutomationProperties.GetName(g.Key!)).ToList();
                var layerRows = app.Shell.Session.Document!.Layers.Count + 1;
                Check.Equal(layerRows, next.OfType<ListBoxItem>().Count(i => AutomationProperties.GetName(ItemsControl.ItemsControlFromItemContainer(i)) == layerList),
                    $"{state}: レイヤーの一覧の行に止まる回数");
                Check.True(byList.Count == 0, $"{state}: 一覧の項目に 2 回以上止まる: " + string.Join(", ", byList));
                foreach (var list in new[] { list0, layerList })
                    Check.True(next.OfType<ListBoxItem>().Any(i => AutomationProperties.GetName(ItemsControl.ItemsControlFromItemContainer(i)) == list),
                        $"{state}: 一覧「{list}」の項目に止まらない");
                Check.Equal(1, next.OfType<TabItem>().Count(), $"{state}: タブに 1 回だけ止まらない");
                var prev = Walk(first, FocusNavigationDirection.Previous);
                Check.SequenceEqual(next.Skip(1).Reverse().Prepend(next[0]).ToList(), prev, $"{state}: Previous の順");
            });
        }
    }
    /// <summary>root の下で AutomationId が id の最初の要素(論理の木と表示の木の両方を辿る。UI スレッドで呼ぶ)。</summary>
    private static T? FindById<T>(DependencyObject root, string id) where T : DependencyObject
    {
        var seen = new HashSet<DependencyObject>();
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (!seen.Add(d)) continue;
            if (d is T t && AutomationProperties.GetAutomationId(d) == id) return t;
            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) queue.Enqueue(c);
        }
        return null;
    }

    /// <summary>focus にキーボードフォーカスを当て、ring の外側 3 DIP の 4 辺の中央が focus-ring の色か調べる。違えば failures に足す。</summary>
    private static void CheckRing(TestApp app, string row, UIElement focus, FrameworkElement ring, List<string> failures)
    {
        app.Ui(() => Keyboard.Focus(focus));
        UiHost.Idle();
        app.Ui(() =>
        {
            if (!focus.IsKeyboardFocused)
            {
                failures.Add($"{row}: フォーカスが当たらない");
                return;
            }
            var w = app.Shell.Window;
            var color = ((SolidColorBrush)w.FindResource("FocusRingBrush")).Color;
            var snap = Snapshot.Of(w, w.RootLayer);
            var b = ring.TransformToAncestor(w).TransformBounds(new Rect(ring.RenderSize));
            var points = new[]
            {
                new Point(b.Left + b.Width / 2, b.Top - 3), new Point(b.Left + b.Width / 2, b.Bottom + 3),
                new Point(b.Left - 3, b.Top + b.Height / 2), new Point(b.Right + 3, b.Top + b.Height / 2),
            };
            foreach (var p in points)
                if (!snap.Near(p, color, 4)) failures.Add($"{row}: ({p.X:F1},{p.Y:F1}) が {snap.Describe(p)}");
        });
    }

    [Test]
    public void AC21_5_編集画面の13行の本物の部品のフォーカスの輪が外側3DIPの4辺の中央に出る()
    {
        using var app = Start();
        // ダイアログの出現の拡大(0.96 → 1)の途中で撮らないよう、アニメーションの縮小を有効にする(輪の描き方は変わらない)
        app.Ui(() => app.Shell.Settings.ReduceMotion = GlintLoop.Core.Settings.ReduceMotionSetting.On);
        var stack = M<StackModule>(app);
        var layers = Layers(app);
        var looks = M<LooksModule>(app);
        SelectTab(app, LeftPaneTab.Looks);
        var failures = new List<string>();
        var rows = new List<string>();
        void Ring(string row, Func<UIElement> focus, Func<FrameworkElement>? ring = null)
        {
            rows.Add(row);
            var (f, r) = app.Ui(() =>
            {
                var e = focus();
                return (e, ring?.Invoke() ?? (FrameworkElement)e);
            });
            CheckRing(app, row, f, r, failures);
        }
        var settings = app.Ui(() => layers.Settings);
        Ring("副ボタン", () => layers.Header.ResetAllButton);
        Ring("副ボタン(ドロップダウン)", () => settings.BlendBox);
        Ring("アイコンボタン", () => app.Shell.Window.CommandBar.Button(Icons.Fit));
        Ring("スライダー", () => settings.OpacitySlider);
        Ring("トグル", () => layers.List.Rows.First(r => !r.IsAll && r.LayerIndex == 1).VisibleToggle!);
        Ring("テキスト入力", () => settings.NameBox);
        Ring("色の四角", () => settings.FillSwatch);
        // 「ルックなし」を Enter で適用して選ぶ(スタックは変わらない)
        app.Ui(() => Keyboard.Focus(looks.Gallery.Tiles.First()));
        Check.True(Key(app, System.Windows.Input.Key.Enter), "タイルの Enter が処理されない");
        UiHost.Idle();
        Ring("タイル", () => looks.Gallery.Tiles.First(t => !t.IsSelected));
        Ring("選択中のタイル・行", () => looks.Gallery.Tiles.First(t => t.IsSelected));
        Ring("選択中のタイル・行(レイヤーの行)", () => layers.List.Rows.First(r => r.IsSelected));
        Ring("スタックの行", () => stack.StackView.RowItems[0]);
        Ring("タブ", () => looks.LeftPane.EffectsTab);
        Ring("選択中のタブ", () => looks.LeftPane.LooksTab);
        // 横並びの切替ボタン: ぼかしの「種類」(選択肢 2 つ)。輪は切替ボタンの全体の外側
        app.Ui(() => stack.SelectRow(0));
        UiHost.Idle();
        Ring("横並びの切替ボタン", () =>
        {
            var seg = FindById<ListBox>(stack.ParamsView, "param.kind.choice") ?? throw new CheckFailedException("ぼかしの種類の切替ボタンが無い");
            return (UIElement)seg.ItemContainerGenerator.ContainerFromIndex(1);
        }, () => FindById<ListBox>(stack.ParamsView, "param.kind.choice")!);
        // 主ボタン: 「ルックを保存」の名前の入力の「保存」(編集画面の主ボタンはダイアログの中にある。コピーは Phase 4)
        app.Ui(() => looks.Gallery.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is NameInputDialog, Wait), "名前の入力が開かない");
        var dialog = app.Ui(() => (NameInputDialog)app.Shell.Dialogs.Top!);
        app.Ui(() => dialog.Input.Text = "見本");
        UiHost.Idle();
        Ring("主ボタン", () => dialog.Confirm);
        app.Ui(() => dialog.Cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        Check.Equal(13, rows.Select(r => r.Split("(")[0]).Distinct().Count(), "表の行の数");
        Check.True(failures.Count == 0, "フォーカスの輪: " + string.Join(" / ", failures));
    }
    private static T? Descendant<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (Descendant<T>(c) is { } f) return f;
        }
        return null;
    }

    [Test]
    public void AC21_5_スクロールの外へ出た部分の輪は枠の縁で切れて隣の領域に浮かない()
    {
        using var app = Start();
        var stack = M<StackModule>(app);
        var settings = app.Ui(() => Layers(app).Settings);
        app.Ui(() => Keyboard.Focus(settings.OpacitySlider));
        UiHost.Idle();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            var scroll = Descendant<ScrollViewer>(stack.ParamsView) ?? throw new CheckFailedException("パラメータ欄のスクロールが無い");
            var presenter = Descendant<ScrollContentPresenter>(scroll)!;
            var view = presenter.TransformToAncestor(w).TransformBounds(new Rect(presenter.RenderSize));
            var slider = settings.OpacitySlider;
            var b = slider.TransformToAncestor(w).TransformBounds(new Rect(slider.RenderSize));
            // つまみの行の上半分が枠の上へ出るまで送る
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + (b.Top - view.Top) + b.Height / 2);
            w.UpdateLayout();
            b = slider.TransformToAncestor(w).TransformBounds(new Rect(slider.RenderSize));
            Check.True(b.Top < view.Top - 8 && b.Bottom > view.Top, $"スライダーが枠の縁にかかっていない: {b} / {view}");
            var color = ((SolidColorBrush)w.FindResource("FocusRingBrush")).Color;
            var snap = Snapshot.Of(w, w.RootLayer);
            // 枠の外(上の輪の線)には描かず、枠の中(左の輪の線)には描く
            Check.True(!snap.Near(new Point(b.Left + b.Width / 2, b.Top - 3), color, 4), "枠の外へ出た部分に輪が描かれる");
            Check.True(snap.Near(new Point(b.Left - 3, (view.Top + b.Bottom) / 2), color, 4), $"枠の中の輪が無い: {snap.Describe(new Point(b.Left - 3, (view.Top + b.Bottom) / 2))}");
        });
    }
    /// <summary>
    /// 表示中の操作できる部品(フォーカスを受けられて、見えていて有効な要素)の矩形(ウィンドウの DIP)の検査: 最寄りの Panel の祖先(親)の内側に収まり、
    /// 親が違うものも含めて部品どうしが重ならない。スクロールの枠の表示の範囲に収まっていない部品(スクロールの外の行)は対象にしない。Grid の同じ升目に重ねた部品は重なりの対象外。
    /// </summary>
    private static List<string> LayoutProblems(Window w) => LayoutProblems(w, out _);

    private static List<string> LayoutProblems(Window w, out int count)
    {
        const double tol = 0.01;
        var problems = new List<string>();
        var parts = new List<(FrameworkElement E, Panel Parent, Rect R)>();
        string Name(FrameworkElement e) => e.GetType().Name + "「" + AutomationProperties.GetName(e) + "」";
        void Collect(DependencyObject d)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is UIElement { IsVisible: false }) continue;
                if (c is FrameworkElement { Focusable: true, IsEnabled: true } fe && c is Control or PaneSplitter && !(c is ScrollViewer))
                {
                    var r = fe.TransformToAncestor(w).TransformBounds(new Rect(fe.RenderSize));
                    var visible = true;
                    Panel? parent = null;
                    for (var p = VisualTreeHelper.GetParent(fe); p is not null; p = VisualTreeHelper.GetParent(p))
                    {
                        if (parent is null && p is Panel panel) parent = panel;
                        if (p is ScrollContentPresenter sp)
                        {
                            var v = sp.TransformToAncestor(w).TransformBounds(new Rect(sp.RenderSize));
                            if (!Contains(v, r, tol)) visible = false;
                        }
                    }
                    if (visible && parent is not null && r.Width > 0 && r.Height > 0) parts.Add((fe, parent, r));
                }
                Collect(c);
            }
        }
        Collect(w);
        count = parts.Count;
        foreach (var (e, parent, r) in parts)
        {
            var pr = parent.TransformToAncestor(w).TransformBounds(new Rect(parent.RenderSize));
            if (!Contains(pr, r, tol)) problems.Add($"{Name(e)} {Fmt(r)} が親 {parent.GetType().Name} {Fmt(pr)} の外");
        }
        for (var i = 0; i < parts.Count; i++)
        for (var j = i + 1; j < parts.Count; j++)
        {
            var (a, pa, ra) = parts[i];
            var (b, pb, rb) = parts[j];
            // 親のパネルが違っても、ウィンドウの座標で重なれば見つける(祖先と子孫の関係は中に入っているだけなので除く)
            if (a.IsAncestorOf(b) || b.IsAncestorOf(a)) continue;
            if (ReferenceEquals(pa, pb) && pa is Grid && Grid.GetRow(a) == Grid.GetRow(b) && Grid.GetColumn(a) == Grid.GetColumn(b)) continue;
            var x = Rect.Intersect(ra, rb);
            if (!x.IsEmpty && x.Width > tol && x.Height > tol) problems.Add($"{Name(a)} {Fmt(ra)} と {Name(b)} {Fmt(rb)} が重なる");
        }
        return problems;
    }

    private static bool Contains(Rect outer, Rect inner, double tol) =>
        inner.Left >= outer.Left - tol && inner.Top >= outer.Top - tol && inner.Right <= outer.Right + tol && inner.Bottom <= outer.Bottom + tol;

    private static string Fmt(Rect r) => $"({r.X:F2},{r.Y:F2} {r.Width:F2}×{r.Height:F2})";

    [Test]
    public void AC21_7_検査自身の確認_親の外と兄弟の重なりを見つける()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            w.SetWelcome(false);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var a = new Button { Content = "A", Width = 40 };
            var b = new Button { Content = "B", Width = 40, Margin = new Thickness(-10, 0, 0, 0) };
            var c = new Button { Content = "C", Width = 40, Margin = new Thickness(0, -20, 0, 0) };
            foreach (var (e, n) in new[] { (a, "A"), (b, "B"), (c, "C") })
            {
                AutomationProperties.SetName(e, n);
                panel.Children.Add(e);
            }
            app.Shell.Window.TimelineHost.Content = panel;
            w.UpdateLayout();
            var problems = LayoutProblems(w);
            Check.True(problems.Any(p => p.Contains("「A」") && p.Contains("「B」") && p.Contains("重なる")), "重なりを見つけない: " + string.Join(" / ", problems));
            Check.True(problems.Any(p => p.Contains("「C」") && p.Contains("の外")), "親の外を見つけない: " + string.Join(" / ", problems));
            app.Shell.Window.TimelineHost.Content = null;
        });
    }

    [Test]
    public void AC21_7_検査自身の確認_親のパネルが違う部品の重なりを見つける()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            w.SetWelcome(false);
            var grid = new Grid();
            var left = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            var a = new Button { Content = "甲", Width = 40 };
            var b = new Button { Content = "乙", Width = 40 };
            AutomationProperties.SetName(a, "甲");
            AutomationProperties.SetName(b, "乙");
            left.Children.Add(a);
            right.Children.Add(b);
            grid.Children.Add(left);
            grid.Children.Add(right);
            var old = w.TimelineHost.Content;
            w.TimelineHost.Content = grid;
            w.UpdateLayout();
            var problems = LayoutProblems(w);
            w.TimelineHost.Content = old;
            Check.True(problems.Any(p => p.Contains("「甲」") && p.Contains("「乙」") && p.Contains("重なる")), "親が違う重なりを見つけない: " + string.Join(" / ", problems));
        });
    }

    [Test(Slow = true)]
    public void AC21_7_FR99_DPIの倍率と最小と既定の大きさで部品が親に収まり兄弟と重ならない()
    {
        var problems = new List<string>();
        foreach (var layers in new[] { 1, 32 })
        {
            using var app = Start(layers);
            Check.Equal(layers, app.Ui(() => app.Shell.Session.Document!.Layers.Count), "レイヤーの枚数");
            foreach (var (cw, ch) in new[] { (1120.0, 640.0), (1280.0, 800.0) })
            {
                Resize(app, cw, ch);
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0, 3.0 })
                {
                    app.Ui(() =>
                    {
                        var w = app.Shell.Window;
                        VisualTreeHelper.SetRootDpi(w, new DpiScale(scale, scale));
                        w.InvalidateMeasure();
                        w.UpdateLayout();
                        Check.Equal(scale, VisualTreeHelper.GetDpi(w).DpiScaleX, "SetRootDpi のあとの倍率");
                        // 倍率を変えると、ウィンドウの実際の画素の大きさ(変わらない)を新しい倍率の画素の格子に丸めるので、2 DIP 未満ずれる
                        Check.True(Math.Abs(w.RootLayer.ActualWidth - cw) < 2 && Math.Abs(w.RootLayer.ActualHeight - ch) < 2, $"クライアント領域が {cw}×{ch} でない: {w.RootLayer.ActualWidth}×{w.RootLayer.ActualHeight} 窓 {w.ActualWidth}×{w.ActualHeight} 最小 {w.MinWidth}×{w.MinHeight}");
                        var found = LayoutProblems(w, out var count);
                        foreach (var p in found) problems.Add($"{layers} 枚・{cw}×{ch}・{scale * 100}%: {p}");
                        Check.True(count >= 30, $"調べた部品が少ない: {count}");
                    });
                }
                if (layers == 32)
                {
                    // 32 枚のとき、一覧は縦にスクロールする
                    var list = app.Ui(() => Layers(app).List.List);
                    var scroll = app.Ui(() => Descendant<ScrollViewer>(list));
                    Check.True(app.Ui(() => scroll is not null && scroll.ScrollableHeight > 0), "32 枚の一覧がスクロールしない");
                }
            }
        }
        Check.True(problems.Count == 0, $"{problems.Count} 件: " + string.Join(" / ", problems.Take(30)));
    }
}
