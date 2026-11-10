// ダイアログの表示中のキー操作(FR-2・FR-100・§9.3 の規則 (4))のテスト。
// (a) 長い中身(ショートカットの一覧など)は、中のスクロールの領域が Tab で止まり、キーだけで送れる(名前とフォーカスの輪を持つ)。
// (b) ダイアログの表示中は、後ろの画面のショートカットが働かず(ダイアログの中の操作だけが働く)、Esc・Tab はダイアログの中で働く。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Input;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Ui.LayersTestKit;

namespace GlintLoop.Tests.Ui;

public sealed class DialogKeysTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static void ShowList(TestApp app)
    {
        app.Ui(() => app.Shell.Dialogs.Show(new ShortcutListView(), "DialogShortcutsWidth", "DialogShortcutsHeight", MotionCause.Keyboard));
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is ShortcutListView l && FocusCheck.IsFocusWithin(l), Wait), "ダイアログの中にフォーカスが移らない");
        UiHost.Idle();
    }

    private static ScrollViewer ScrollOf(TestApp app) => app.Ui(() =>
    {
        DependencyObject? d = app.Shell.Dialogs.Top;
        while (d is not null and not ScrollViewer) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return (ScrollViewer)(d ?? throw new InvalidOperationException("ダイアログのスクロールの領域が無い"));
    });

    [Test]
    public void FR100_長いダイアログの中身はTabで止まるスクロールの領域でキーだけで送れる()
    {
        // テストの間ずっと前面の見張りを回し、GlintLoop のウィンドウが前面にならないことも確かめる(AC-21 の (1))
        int samples, own;
        using (var watch = new ForegroundWatch())
        {
            using (var app = TestApp.Start()) LongDialogKeys(app);
            UiHost.Idle();
            samples = watch.Samples;
            own = watch.OwnHits;
        }
        Check.True(samples >= 10, $"前面の見張りが回っていない({samples} 回)");
        Check.Equal(0, own, $"GlintLoop のウィンドウが前面になった回数({samples} 回のうち)");
    }

    private static void LongDialogKeys(TestApp app)
    {
        // ダイアログの出現の拡大(0.96 → 1)と不透明度の途中で輪を描いて撮らないよう、アニメーションの縮小を有効にする(輪の描き方は変わらない)
        app.Ui(() => app.Shell.Settings.ReduceMotion = GlintLoop.Core.Settings.ReduceMotionSetting.On);
        ShowList(app);
        var scroll = ScrollOf(app);
        app.Ui(() =>
        {
            Check.True(scroll.ScrollableHeight > 0, "一覧が 1 画面に収まっている(テストの前提)");
            Check.True(scroll.Focusable, "スクロールの領域にフォーカスが当たらない");
            Check.True(KeyboardNavigation.GetIsTabStop(scroll), "スクロールの領域が Tab で止まらない");
            Check.True(!string.IsNullOrEmpty(AutomationProperties.GetName(scroll)), "スクロールの領域の名前が空");
            Check.True(FocusRing.GetIsEnabled(scroll), "スクロールの領域にフォーカスの輪が無い");
        });

        // Tab の巡回(MoveFocus の行き先)でスクロールの領域に止まる。非アクティブなウィンドウなので、キーボードフォーカスは動かさず論理フォーカスでたどる
        var reached = app.Ui(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                if (FocusCheck.IsFocused(scroll)) return true;
                if (FocusCheck.Focused(app.Shell.Window) is UIElement from) FocusCheck.MoveLogical(from, FocusNavigationDirection.Next);
            }
            return FocusCheck.IsFocused(scroll);
        });
        Check.True(reached, "Tab でスクロールの領域に止まらない");
        UiHost.Idle();

        // 下矢印・PageDown で送り、PageUp・上矢印で戻す
        app.Ui(() => UiInput.Key(app.Shell.Window, System.Windows.Input.Key.Down));
        UiHost.Idle();
        var afterDown = app.Ui(() => scroll.VerticalOffset);
        Check.True(afterDown > 0, "下矢印で送れない");
        app.Ui(() => UiInput.Key(app.Shell.Window, System.Windows.Input.Key.PageDown));
        UiHost.Idle();
        Check.True(app.Ui(() => scroll.VerticalOffset) > afterDown, "PageDown で送れない");
        var afterPage = app.Ui(() => scroll.VerticalOffset);
        app.Ui(() => UiInput.Key(app.Shell.Window, System.Windows.Input.Key.PageUp));
        UiHost.Idle();
        Check.True(app.Ui(() => scroll.VerticalOffset) < afterPage, "PageUp で戻せない");
        for (var i = 0; i < 5; i++) app.Ui(() => UiInput.Key(app.Shell.Window, System.Windows.Input.Key.Up));
        UiHost.Idle();
        app.Ui(() => Check.Equal(0.0, scroll.VerticalOffset, "上矢印で先頭に戻らない"));

        // UIA からも名前つきで見つかり、キーボードで操作できる部品として数えられる
        var name = app.Ui(() => AutomationProperties.GetName(scroll));
        var e = Uia.Find(Uia.Root(app.Hwnd), name);
        Check.True(e.Current.IsKeyboardFocusable, "UIA で操作できる部品になっていない");

        // フォーカスの輪: FocusRing はキーボードフォーカス(IsKeyboardFocused)の変化でだけ輪を出し、非アクティブなウィンドウの部品にキーボードフォーカスを当てると
        // WPF の SetFocus でウィンドウが前面になる。そこでキーボードフォーカスは当てず、テスト用の入口(キーボードフォーカスの変化と同じ処理を通す)で輪を出し、
        // ウィンドウを RenderTargetBitmap に描いて、輪の位置(部品の外側 3 DIP の 4 辺の中央)の画素が focus-ring の色に各チャンネルの差 4 以内で一致することを確かめる(AC-21 の (5) と同じ基準)
        app.Ui(() =>
        {
            Check.True(!scroll.IsKeyboardFocused && !FocusRing.IsShown(scroll), "キーボードフォーカスを当てていないのに、キーボードフォーカスか輪がある");
            FocusRing.RaiseKeyboardFocusForTest(scroll, true);
        });
        UiHost.Idle();
        Point[] RingPoints(Window w)
        {
            var b = scroll.TransformToAncestor(w).TransformBounds(new Rect(scroll.RenderSize));
            return
            [
                new Point(b.Left + b.Width / 2, b.Top - 3), new Point(b.Left + b.Width / 2, b.Bottom + 3),
                new Point(b.Left - 3, b.Top + b.Height / 2), new Point(b.Right + 3, b.Top + b.Height / 2),
            ];
        }
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            Check.True(FocusRing.IsShown(scroll), "スクロールの領域に輪を出していない");
            var color = ((SolidColorBrush)w.FindResource("FocusRingBrush")).Color;
            var snap = Snapshot.Of(w, w.RootLayer);
            var failures = RingPoints(w).Where(p => !snap.Near(p, color, 4)).Select(p => $"({p.X:F1},{p.Y:F1}) が {snap.Describe(p)}").ToList();
            Check.True(failures.Count == 0, $"スクロールの領域の輪の位置の画素が focus-ring の色(#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2})でない: " + string.Join(" / ", failures));
            // 輪を消すと、同じ位置は focus-ring の色でなくなる(上の画素が輪のものであること)
            FocusRing.RaiseKeyboardFocusForTest(scroll, false);
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            Check.True(!FocusRing.IsShown(scroll), "輪が消えない");
            var color = ((SolidColorBrush)w.FindResource("FocusRingBrush")).Color;
            var snap = Snapshot.Of(w, w.RootLayer);
            Check.True(RingPoints(w).All(p => !snap.Near(p, color, 4)), "輪を消したのに、輪の位置が focus-ring の色のまま");
            Check.True(!scroll.IsKeyboardFocused, "キーボードフォーカスが動いた");
            Check.True(!app.Shell.Window.IsActive, "ウィンドウがアクティブになった");
        });
    }

    [Test]
    public void FR100_短いダイアログの中身ではスクロールの領域はTabで止まらない()
    {
        using var app = TestApp.Start();
        app.Ui(() => app.Shell.Dialogs.Show(new Button { Content = "短い" }, "DialogShortcutsWidth", null, MotionCause.Keyboard));
        UiHost.Idle();
        var scroll = ScrollOf(app);
        app.Ui(() =>
        {
            Check.Equal(0.0, scroll.ScrollableHeight, "短い中身がスクロールする(テストの前提)");
            Check.True(!scroll.Focusable, "スクロールしない領域にフォーカスが当たる");
        });
    }

    [Test]
    public void FR2_ダイアログの表示中は後ろの画面のショートカットが働かずEscで閉じたあとは働く()
    {
        using var app = TestApp.Start();
        Open(app, Doc(Empty("A", 64, 64)));
        Check.True(Key(app, System.Windows.Input.Key.N, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+N が処理されない(テストの前提)");
        UiHost.Idle();
        var layers = app.Ui(() => app.Shell.Session.Document!.Layers.Count);
        var undo = app.Ui(() => app.Shell.Session.UndoCount);
        Check.Equal(2, layers, "空のレイヤーを足したあとの枚数");
        Check.Equal(1, undo, "履歴の件数");
        var offset = app.Ui(() => app.CanvasTransform.Offset);
        var scale = app.Ui(() => app.CanvasTransform.Scale);

        ShowList(app);
        // フォーカスはダイアログの中のボタン(閉じる)。後ろの画面のショートカットは、どれも処理されない
        var blocked = new (System.Windows.Input.Key Key, ModifierKeys Mods, string Name)[]
        {
            (System.Windows.Input.Key.Z, ModifierKeys.Control, "Ctrl+Z"),
            (System.Windows.Input.Key.Y, ModifierKeys.Control, "Ctrl+Y"),
            (System.Windows.Input.Key.N, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+N"),
            (System.Windows.Input.Key.N, ModifierKeys.Control, "Ctrl+N"),
            (System.Windows.Input.Key.V, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+V"),
            (System.Windows.Input.Key.D0, ModifierKeys.Control, "Ctrl+0"),
            (System.Windows.Input.Key.OemPlus, ModifierKeys.Control, "Ctrl+プラス"),
            (System.Windows.Input.Key.F1, ModifierKeys.None, "F1"),
            (System.Windows.Input.Key.OemComma, ModifierKeys.Control, "Ctrl+カンマ"),
            (System.Windows.Input.Key.D2, ModifierKeys.Alt, "Alt+2"),
        };
        foreach (var (k, mods, name) in blocked)
        {
            var handled = Key(app, k, mods);
            UiHost.Idle();
            Check.True(!handled, $"ダイアログの表示中に {name} がショートカットとして処理された");
            Check.True(app.Ui(() => app.Shell.Session.HasDocument), $"{name} で文書が閉じた");
            Check.Equal(layers, app.Ui(() => app.Shell.Session.Document!.Layers.Count), $"{name} でレイヤーの枚数が変わった");
            Check.Equal(undo, app.Ui(() => app.Shell.Session.UndoCount), $"{name} で履歴が動いた");
            Check.Equal(1, app.Ui(() => app.Shell.Dialogs.Count), $"{name} でダイアログが増えた・閉じた");
            Check.Equal(offset, app.Ui(() => app.CanvasTransform.Offset), $"{name} で表示位置が変わった");
            Check.Equal(scale, app.Ui(() => app.CanvasTransform.Scale), $"{name} で表示の倍率が変わった");
        }
        Check.True(app.Ui(() => app.Shell.Dialogs.Top is ShortcutListView l && FocusCheck.IsFocusWithin(l)), "フォーカスがダイアログの外に出た");
        // Tab は判定で MoveFocus になり、部品(WPF の既定の動作)に任せる
        Check.True(!app.Ui(() => app.Shell.Router.HandleKeyDown(System.Windows.Input.Key.Tab, FocusCheck.Focused(app.Shell.Window) as DependencyObject)), "Tab を処理済みにした");

        // Esc はダイアログの中で働き(閉じる)、閉じたあとは後ろの画面のショートカットが働く
        Check.True(Key(app, System.Windows.Input.Key.Escape), "Esc が処理されない");
        Check.True(UiHost.WaitUntil(() => !app.Shell.Dialogs.IsOpen, Wait), "Esc でダイアログが閉じない");
        UiHost.Idle();
        Check.True(Key(app, System.Windows.Input.Key.Z, ModifierKeys.Control), "閉じたあとの Ctrl+Z が処理されない");
        UiHost.Idle();
        Check.Equal(0, app.Ui(() => app.Shell.Session.UndoCount), "閉じたあとの Ctrl+Z で元に戻らない");
    }
}
