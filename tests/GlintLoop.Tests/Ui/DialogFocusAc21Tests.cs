// 非アクティブなウィンドウでダイアログが論理フォーカスを決めるとき(DialogHost.FocusElement)、前面にならないことのテスト(AC-21 の (1)、FR-100)。
// 場面は、中身に部品が無く長いダイアログのスクロールの領域へのフォールバックと、上のダイアログを閉じたときの戻し先(下のダイアログの部品)。
// 前面の見張りに加えて、行き先の部品でキーボードフォーカスの移動が取り消されずに通った回数(SetFocus に至る経路)も数える。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Theme;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class DialogFocusAc21Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>e へのキーボードフォーカスの移動のうち、取り消されずに e まで届いた回数を数える(UI スレッドで呼ぶ)。</summary>
    private sealed class LeakCounter
    {
        public int Count;

        public LeakCounter(UIElement e) =>
            e.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, a) =>
            {
                if (!a.Handled && ReferenceEquals(a.NewFocus, e)) Count++;
            }), handledEventsToo: true);
    }

    /// <summary>前面の見張りの下で body を回し、GlintLoop のウィンドウが前面になった回数が 0 であることを確かめる。</summary>
    private static void UnderWatch(Action<TestApp> body)
    {
        int samples, own;
        using (var watch = new ForegroundWatch())
        {
            using (var app = TestApp.Start())
            {
                app.Ui(() => Check.True(!app.Shell.Window.IsActive, "ウィンドウがアクティブ(テストの前提)"));
                body(app);
                UiHost.Idle();
            }
            UiHost.Idle();
            samples = watch.Samples;
            own = watch.OwnHits;
        }
        Check.True(samples >= 10, $"前面の見張りが回っていない({samples} 回)");
        Check.Equal(0, own, $"GlintLoop のウィンドウが前面になった回数({samples} 回のうち)");
    }

    [Test]
    public void AC21_1_非アクティブなウィンドウで部品の無い長いダイアログはスクロールの領域に論理フォーカスだけが当たり前面にならない()
    {
        UnderWatch(app =>
        {
            // 中身に操作できる部品が無く、ダイアログの高さを超える。最初の部品が無いので、スクロールの領域へフォールバックする(DialogHost.FocusElement)
            var (scroll, leak) = app.Ui(() =>
            {
                var content = new StackPanel { Children = { new Border { Height = 4000 } } };
                _ = app.Shell.Dialogs.Show(content, "DialogShortcutsWidth", "DialogShortcutsHeight", MotionCause.Keyboard);
                var s = (ScrollViewer)LogicalTreeHelper.GetParent(content);
                return (s, new LeakCounter(s));
            });
            Check.True(UiHost.WaitUntil(() => ReferenceEquals(FocusManager.GetFocusedElement(app.Shell.Window), scroll), Wait),
                "スクロールの領域に論理フォーカスが当たらない");
            UiHost.Idle();
            app.Ui(() =>
            {
                Check.True(scroll.ScrollableHeight > 0 && scroll.Focusable, "スクロールの領域が止まる部品になっていない(テストの前提)");
                Check.True(!scroll.IsKeyboardFocused, "非アクティブなウィンドウでキーボードフォーカスが動いた");
                Check.Equal(0, leak.Count, "スクロールの領域へのキーボードフォーカスの移動が取り消されずに通った回数");
                Check.True(!app.Shell.Window.IsActive, "ダイアログを開いたらウィンドウがアクティブになった");
                app.Shell.Dialogs.Close();
            });
        });
    }

    [Test]
    public void AC21_1_非アクティブなウィンドウで上のダイアログを閉じると下のダイアログの部品に論理フォーカスだけが戻り前面にならない()
    {
        UnderWatch(app =>
        {
            // 設定の上の「このソフトについて」と同じく、ダイアログを 2 枚重ねる。上を閉じると、開く前の論理フォーカス(下のダイアログの最初の部品)へ戻す(DialogHost.FocusElement)
            var (first, leak) = app.Ui(() =>
            {
                var a = new Button { Content = "下の一つ目" };
                var b = new Button { Content = "下の二つ目" };
                _ = app.Shell.Dialogs.Show(new StackPanel { Children = { a, b } }, "DialogShortcutsWidth", null, MotionCause.Keyboard);
                return (a, new LeakCounter(a));
            });
            Check.True(UiHost.WaitUntil(() => ReferenceEquals(FocusManager.GetFocusedElement(app.Shell.Window), first), Wait),
                "下のダイアログの最初の部品に論理フォーカスが当たらない");
            var upper = app.Ui(() =>
            {
                var c = new Button { Content = "上の一つ目" };
                _ = app.Shell.Dialogs.Show(new StackPanel { Children = { c } }, "DialogShortcutsWidth", null, MotionCause.Keyboard);
                return c;
            });
            Check.True(UiHost.WaitUntil(() => ReferenceEquals(FocusManager.GetFocusedElement(app.Shell.Window), upper), Wait),
                "上のダイアログの部品に論理フォーカスが当たらない");
            UiHost.Idle();
            var before = app.Ui(() => leak.Count);
            app.Ui(() => app.Shell.Dialogs.Close());
            UiHost.Idle();
            app.Ui(() =>
            {
                Check.Equal(1, app.Shell.Dialogs.Count, "閉じたあとのダイアログの数");
                Check.True(ReferenceEquals(FocusManager.GetFocusedElement(app.Shell.Window), first), "上を閉じたあとの論理フォーカスが下のダイアログの最初の部品に戻らない");
                Check.True(!first.IsKeyboardFocused && !upper.IsKeyboardFocused, "非アクティブなウィンドウでキーボードフォーカスが動いた");
                Check.Equal(0, leak.Count - before, "閉じたときの戻し先へのキーボードフォーカスの移動が取り消されずに通った回数");
                Check.Equal(0, before, "下のダイアログの最初の部品へのキーボードフォーカスの移動が取り消されずに通った回数");
                Check.True(!app.Shell.Window.IsActive, "ダイアログを閉じたらウィンドウがアクティブになった");
                app.Shell.Dialogs.Close();
            });
        });
    }
}
