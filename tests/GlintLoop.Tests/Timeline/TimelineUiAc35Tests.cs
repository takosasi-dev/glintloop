// タイムラインの画面(TimelineModule・TimelineView)のテスト: AC-35 の (2) 部品が UIA で見つかる、(3) Space とボタンで再生と停止・スクラブで止まる、
// AC-22 の Space(ボタン・リスト・文字入力欄では部品が優先)とスクラブバーの左右・Home・End、L・F・ループ・停止時間・振り直しの欄が文書に届くこと、
// 616 DIP 未満で 2 行に折り返すこと(§9.3)、AC-20 の (5) アニメーションの縮小が 0 ms でも再生のフレームが進むこと。GPU を使わない(空のキャンバスの文書)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Input;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.App.Timelines;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Timelines;

public sealed class TimelineUiAc35Tests
{
    /// <summary>空のキャンバスの文書に spin を入れた(N=40)アプリを立てる。</summary>
    private static TestApp StartWithMotion(FakeOsInfo? os = null)
    {
        var app = TestApp.Start(os: os);
        app.Ui(() =>
        {
            app.Shell.Session.NewEmptyCanvas(200, 100);
            Check.True(app.Shell.Session.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を入れられない");
        });
        UiHost.Idle();
        Check.Equal(40, app.Ui(() => app.Shell.Frames.N), "spin の文書の N");
        return app;
    }

    private static TimelineModule Module(TestApp app) => app.Ui(() => app.Shell.Modules.OfType<TimelineModule>().Single());

    private static bool Press(TestApp app, Key key) => app.Ui(() => UiInput.Key(app.Shell.Window, key));

    private static void Focus(TestApp app, Func<IInputElement> target) => app.Ui(() =>
    {
        var e = target();
        Keyboard.Focus(e);
        FocusManager.SetFocusedElement(app.Shell.Window, e);
    });

    private static void Advance(TestApp app, long ms) => app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(ms)));

    [Test]
    public void FR59_動くフレームが無いときはスクラブバーのつまみが左端で操作できない()
    {
        using var app = TestApp.Start();
        var m = Module(app);
        UiHost.Idle();
        void CheckState(string what, bool enabled, double max, double value) => app.Ui(() =>
        {
            var s = m.View.Scrub;
            s.UpdateLayout();
            Check.Equal(enabled, s.IsEnabled, what + ": 操作できるか");
            Check.Equal(max, s.Maximum, what + ": 範囲");
            Check.Equal(value, s.Value, what + ": 位置");
            var thumb = (System.Windows.Controls.Primitives.Track)s.Template.FindName("PART_Track", s);
            var x = thumb.Thumb.TransformToAncestor(s).Transform(new Point(thumb.Thumb.ActualWidth / 2, 0)).X;
            Check.True(x < s.ActualWidth / 2, $"{what}: つまみの中心 {x:F1} が左半分にない(幅 {s.ActualWidth:F1})");
        });
        CheckState("文書なし", false, 1, 0);
        app.Ui(() => app.Shell.Session.NewEmptyCanvas(200, 100));
        UiHost.Idle();
        CheckState("モーションなし", false, 1, 0);
        app.Ui(() => app.Shell.Session.AddEffect(Target.OfLayer(0), "spin"));
        UiHost.Idle();
        CheckState("モーションあり", true, 39, 0);
    }

    [Test]
    public void AC35_2_FR59_タイムラインの部品がUIAで見つかる()
    {
        using var app = StartWithMotion();
        var m = Module(app);
        Check.Equal(650, m.Order, "Order");
        Check.True(app.Ui(() => ReferenceEquals(app.Shell.Window.TimelineHost.Content, m.View)), "TimelineHost に入っている");
        app.Ui(() => app.Shell.Frames.Set(2));
        UiHost.Idle();
        var root = Uia.Root(app.Hwnd);
        foreach (var name in new[] { "再生", "スクラブバー", "3 / 40", "ループの長さ", "フレームレート", "ループ", "最後のフレームの停止時間", "振り直し" })
            Uia.Find(root, name);
        var scrub = Uia.FindById(root, "timeline.scrub");
        Check.Equal(ControlType.Slider, scrub.Current.ControlType, "スクラブバーはスライダー");
        Check.Equal(ControlType.ComboBox, Uia.FindById(root, "timeline.fps").Current.ControlType, "F は選択");
        Check.Equal(ControlType.ComboBox, Uia.FindById(root, "timeline.loop").Current.ControlType, "ループは選択");
        Check.Equal(ControlType.Edit, Uia.FindById(root, "timeline.length").Current.ControlType, "L は入力");
        Check.Equal(ControlType.Edit, Uia.FindById(root, "timeline.hold").Current.ControlType, "停止時間は入力");
        app.Ui(() =>
        {
            var v = m.View;
            Check.SequenceEqual(TimeModel.FpsChoices, v.FpsBox.Items.OfType<ComboBoxItem>().Select(i => (int)i.Tag).ToList(), "F の 11 通り");
            Check.SequenceEqual(["無限", "1 回"], v.LoopBox.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content).ToList(), "ループの 2 通り");
            Check.Equal("2.0", v.LengthBox.Text, "既定の L");
            Check.Equal(20, (int)((ComboBoxItem)v.FpsBox.SelectedItem).Tag, "既定の F");
            Check.Equal(LoopMode.Infinite, (LoopMode)((ComboBoxItem)v.LoopBox.SelectedItem).Tag, "既定のループは無限");
            Check.Equal("0", v.HoldBox.Text, "既定の停止時間は 0");
            Check.Equal("3 / 40", v.FrameLabel.Text, "スクラブバーの表示");
            Check.Equal(39.0, v.Scrub.Maximum, "スクラブバーの範囲");
            Check.Equal(2.0, v.Scrub.Value, "スクラブバーの位置");
            Check.Equal("再生(Space)", (string)v.PlayButton.ToolTip, "再生ボタンのツールチップ");
            Check.Equal("振り直し", (string)v.ReseedButton.ToolTip, "振り直しのツールチップ(ショートカットなし)");
            // 振り直しはタイムラインの右端(§9.3)
            var reseed = v.ReseedButton.TransformToAncestor(v).TransformBounds(new Rect(v.ReseedButton.RenderSize));
            Check.Near(v.ActualWidth, reseed.Right, 0.5, "振り直しの右端");
        });
    }

    [Test]
    public void AC35_3_AC22_Spaceとボタンで再生と停止しスクラブバーで止まる()
    {
        using var app = StartWithMotion();
        var m = Module(app);
        var frames = app.Shell.Frames;
        var root = Uia.Root(app.Hwnd);

        // 再生ボタン
        Uia.Invoke(Uia.FindById(root, "timeline.play"));
        UiHost.Idle();
        Check.True(app.Ui(() => frames.IsPlaying), "再生ボタンで再生");
        Check.Equal("停止", app.Ui(() => AutomationProperties.GetName(m.View.PlayButton)), "再生中のボタンは停止");
        Check.Equal("停止(Space)", app.Ui(() => (string)m.View.PlayButton.ToolTip), "停止のツールチップ");
        Advance(app, 1234);
        Check.Equal(24, app.Ui(() => frames.K), "経過 1234 ms のフレーム");
        Uia.Invoke(Uia.FindById(root, "timeline.play"));
        UiHost.Idle();
        Check.True(app.Ui(() => !frames.IsPlaying), "もう一度押すと停止");
        Check.Equal(24, app.Ui(() => frames.K), "止めたフレームのまま");

        // Space: キャンバス・スクラブバーにフォーカスがあるときは再生と停止
        foreach (var (what, target) in new (string, Func<IInputElement>)[]
                 {
                     ("キャンバス", () => FindFocusable<FrameworkElement>(app.Shell.CanvasView, e => ShortcutScope.GetKind(e) == FocusKind.Canvas)),
                     ("スクラブバー", () => m.View.Scrub),
                 })
        {
            Focus(app, target);
            Check.Equal(what == "キャンバス" ? FocusKind.Canvas : FocusKind.Scrub,
                app.Ui(() => ShortcutScope.Of(Keyboard.FocusedElement as DependencyObject)), $"{what} のフォーカスの種類");
            Check.True(Press(app, Key.Space), $"{what} の Space が処理されない");
            Check.True(app.Ui(() => frames.IsPlaying), $"{what} の Space で再生");
            Check.Equal(0, app.Ui(() => frames.K), $"{what}: 再生は先頭から");
            Advance(app, 500);
            Check.Equal(10, app.Ui(() => frames.K), $"{what}: 500 ms");
            Check.True(Press(app, Key.Space), $"{what} の 2 回目の Space");
            Check.True(app.Ui(() => !frames.IsPlaying), $"{what} の Space で停止");
        }

        // Space: ボタン・リスト・文字入力欄では部品の操作が優先(再生しない)
        foreach (var (what, target, kind) in new (string, Func<IInputElement>, FocusKind)[]
                 {
                     ("振り直し", () => m.View.ReseedButton, FocusKind.Button),
                     ("F の選択", () => m.View.FpsBox, FocusKind.List),
                     ("ループの選択", () => m.View.LoopBox, FocusKind.List),
                     ("L の入力", () => m.View.LengthBox, FocusKind.TextInput),
                     ("停止時間の入力", () => m.View.HoldBox, FocusKind.TextInput),
                 })
        {
            Focus(app, target);
            app.Ui(() =>
            {
                Check.Equal(kind, ShortcutScope.Of((DependencyObject)target()), $"{what} のフォーカスの種類");
                Check.True(Shortcuts.Resolve(KeyChord.Of(Key.Space), new ShortcutContext(kind, true)) is null, $"{what} の Space が動作を返す");
            });
            Press(app, Key.Space);
            UiHost.Idle();
            Check.True(app.Ui(() => !frames.IsPlaying), $"{what} にフォーカスがあるときの Space で再生した");
            app.Ui(() =>
            {
                m.View.FpsBox.IsDropDownOpen = false;
                m.View.LoopBox.IsDropDownOpen = false;
            });
        }

        // スクラブバーの操作で再生が止まり、そのフレームへ移る
        Uia.Invoke(Uia.FindById(root, "timeline.play"));
        UiHost.Idle();
        Advance(app, 300);
        Check.True(app.Ui(() => frames.IsPlaying), "再生中");
        Uia.SetValue(Uia.FindById(root, "timeline.scrub"), 30);
        UiHost.Idle();
        Check.True(app.Ui(() => !frames.IsPlaying), "スクラブで再生が止まる");
        Check.Equal(30, app.Ui(() => frames.K), "スクラブで移ったフレーム");
        Check.Equal("31 / 40", app.Ui(() => m.View.FrameLabel.Text), "表示");
        Advance(app, 1000);
        Check.Equal(30, app.Ui(() => frames.K), "止まったあとは動かない");
        // つまみをつかむと止まる
        app.Ui(() => m.Playback.Play());
        app.Ui(() => m.View.Scrub.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
        {
            RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
        }));
        Check.True(app.Ui(() => !frames.IsPlaying), "つまみをつかむと止まる");
    }

    [Test]
    public void AC22_スクラブバーの左右とHomeとEndでフレームを移し再生を止める()
    {
        using var app = StartWithMotion();
        var m = Module(app);
        var frames = app.Shell.Frames;
        Focus(app, () => m.View.Scrub);
        app.Ui(() => frames.Set(5));
        foreach (var (key, k) in new[] { (Key.Right, 6), (Key.Right, 7), (Key.Left, 6), (Key.End, 39), (Key.Right, 39), (Key.Home, 0), (Key.Left, 0) })
        {
            Check.True(Press(app, key), $"{key} が処理されない");
            Check.Equal(k, app.Ui(() => frames.K), $"{key} のあとのフレーム");
            Check.Equal(k, (int)app.Ui(() => m.View.Scrub.Value), $"{key} のあとのスクラブバー");
        }
        app.Ui(() => m.Playback.Play());
        Advance(app, 200);
        Check.True(Press(app, Key.Right), "再生中の右");
        Check.True(app.Ui(() => !frames.IsPlaying), "キーで移すと再生が止まる");
        Check.Equal(5, app.Ui(() => frames.K), "200 ms のフレーム 4 の次");
        // スクラブバーにフォーカスが無いときの左右はフレームを動かさない
        Focus(app, () => m.View.ReseedButton);
        Press(app, Key.Right);
        Check.Equal(5, app.Ui(() => frames.K), "振り直しのボタンにフォーカスがあるときの右");
    }

    [Test]
    public void FR58_FR61_FR66_欄の入力が文書に届き直した値を表示する()
    {
        using var app = StartWithMotion();
        var m = Module(app);
        var s = app.Shell.Session;
        void Commit(TextBox box, string text)
        {
            Focus(app, () => box);
            app.Ui(() => box.Text = text);
            Check.True(Press(app, Key.Enter), "Enter が処理されない");
        }

        Commit(m.View.LengthBox, "2.05");
        Check.Equal(21, app.Ui(() => s.Time!.Length10), "2.05 は 2.1");
        Check.Equal("2.1", app.Ui(() => m.View.LengthBox.Text), "直した値を表示");
        Check.Equal(42, app.Ui(() => app.Shell.Frames.N), "N=round(2.1×20)");
        Check.Equal("1 / 42", app.Ui(() => m.View.FrameLabel.Text), "N が変わると表示も変わる");
        Commit(m.View.LengthBox, "abc");
        Check.Equal(21, app.Ui(() => s.Time!.Length10), "読めない入力は反映しない");
        Check.Equal("2.1", app.Ui(() => m.View.LengthBox.Text), "読めない入力は直前の値に戻す");
        Commit(m.View.LengthBox, "0.25");
        Check.Equal("0.5", app.Ui(() => m.View.LengthBox.Text), "範囲外は端");
        Commit(m.View.LengthBox, "12");
        Check.Equal("10.0", app.Ui(() => m.View.LengthBox.Text), "範囲外は端");

        Commit(m.View.HoldBox, "149");
        Check.Equal(100L, app.Ui(() => s.Time!.HoldMs), "149 は 100");
        Check.Equal("100", app.Ui(() => m.View.HoldBox.Text), "直した値を表示");
        Commit(m.View.HoldBox, "5001");
        Check.Equal("5000", app.Ui(() => m.View.HoldBox.Text), "5001 は 5000");
        Commit(m.View.HoldBox, "-5");
        Check.Equal("0", app.Ui(() => m.View.HoldBox.Text), "−5 は 0");

        app.Ui(() => m.View.FpsBox.SelectedIndex = 0);
        Check.Equal(5, app.Ui(() => s.Time!.Fps), "F の選択");
        app.Ui(() => m.View.LoopBox.SelectedIndex = 1);
        Check.Equal(LoopMode.Once, app.Ui(() => s.Time!.Loop), "ループの選択");

        // Undo で戻ると欄も戻る
        app.Ui(() => s.Undo());
        UiHost.Idle();
        Check.Equal(LoopMode.Infinite, (LoopMode)app.Ui(() => ((ComboBoxItem)m.View.LoopBox.SelectedItem).Tag), "Undo で欄が戻る");

        var seed = app.Ui(() => s.Document!.Seed);
        Uia.Invoke(Uia.FindById(Uia.Root(app.Hwnd), "timeline.reseed"));
        UiHost.Idle();
        Check.True(app.Ui(() => s.Document!.Seed) != seed, "振り直しのボタンで seed が変わる");
        Check.InRange(0, DocumentRules.MaxSeed, app.Ui(() => s.Document!.Seed), "seed の範囲");
    }

    [Test]
    public void FR59_キャンバスの幅が616DIP未満なら2行に折り返す()
    {
        using var app = StartWithMotion();
        var m = Module(app);
        var w = app.Shell.Window;
        var r = AppResources.LayoutTokens;
        Check.Equal(616.0, TimelineView.OneLineWidth(), "1 行の幅の合計(§9.3)");
        app.Ui(() =>
        {
            Check.True(w.TimelineHost.ActualWidth >= 616, $"既定の大きさのキャンバスの幅 {w.TimelineHost.ActualWidth}");
            Check.True(!w.IsTimelineWrapped && !m.View.IsWrapped, "既定の大きさは 1 行");
            Check.Equal((double)r["TimelineHeight"], w.TimelineRow.Height.Value, "1 行の高さ");
            w.SetPaneWidths((double)r["LeftPaneMaxWidth"], w.RightPaneWidth, PaneSide.Left);
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.True(w.TimelineHost.ActualWidth < 616, $"狭めたキャンバスの幅 {w.TimelineHost.ActualWidth}");
            Check.True(w.IsTimelineWrapped && m.View.IsWrapped, "616 DIP 未満は 2 行");
            Check.Equal(96.0, w.TimelineRow.Height.Value, "2 行の高さ");
            var v = m.View;
            Rect B(FrameworkElement e) => e.TransformToAncestor(w.TimelineHost).TransformBounds(new Rect(e.RenderSize));
            var play = B(v.PlayButton);
            var scrub = B(v.Scrub);
            var length = B(v.LengthBox);
            var reseed = B(v.ReseedButton);
            Check.Near(play.Top, scrub.Top, 1, "1 行目は再生とスクラブバー");
            Check.True(length.Top >= play.Bottom, "2 行目の L は 1 行目の下");
            Check.Near(length.Top, reseed.Top, 1, "2 行目に L から振り直しまで");
            var host = new Rect(w.TimelineHost.RenderSize);
            foreach (var e in new FrameworkElement[] { v.PlayButton, v.Scrub, v.LengthBox, v.FpsBox, v.LoopBox, v.HoldBox, v.ReseedButton })
                Check.True(host.Contains(B(e)), $"{AutomationProperties.GetName(e)} が枠の外: {B(e)} / {host}");
            w.SetPaneWidths((double)r["LeftPaneWidth"], (double)r["RightPaneWidth"], PaneSide.Left);
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.True(!w.IsTimelineWrapped && !m.View.IsWrapped, "広げると 1 行に戻る");
            Check.Equal(56.0, w.TimelineRow.Height.Value, "1 行の高さに戻る");
            var v = m.View;
            Rect B(FrameworkElement e) => e.TransformToAncestor(w.TimelineHost).TransformBounds(new Rect(e.RenderSize));
            Check.Near(B(v.PlayButton).Top, B(v.ReseedButton).Top, 1, "1 行に全部並ぶ");
            Check.True(B(v.Scrub).Width + 0.5 >= (double)r["TimelineScrubMinWidth"] - (double)r["Space2"] - B(v.FrameLabel).Width - 1,
                "スクラブバーの幅");
        });
    }

    [Test]
    public void AC20_5_アニメーションの縮小で時間が0msでもループ再生のフレームは進む()
    {
        (ReduceMotionSetting Setting, bool OsOn)[] zero =
        [
            (ReduceMotionSetting.System, false),
            (ReduceMotionSetting.On, true),
            (ReduceMotionSetting.On, false),
        ];
        var os = new FakeOsInfo();
        using var app = StartWithMotion(os);
        var m = Module(app);
        foreach (var (setting, osOn) in zero)
        {
            app.Ui(() =>
            {
                os.AnimationsEnabled = osOn;
                app.Shell.Settings.ReduceMotion = setting;
                Check.True(Motion.GetReduced(app.Shell.Window), $"({setting}, OS {osOn}) で縮小が有効");
                m.Playback.Play();
            });
            var seen = new HashSet<int>();
            for (var i = 0; i < 40; i++)
            {
                Advance(app, 50);
                seen.Add(app.Ui(() => app.Shell.Frames.K));
            }
            Check.True(seen.Count >= 39, $"({setting}, OS {osOn}) でフレームが進まない({seen.Count} 通り)");
            Check.True(app.Ui(() => app.Shell.Frames.IsPlaying), "再生が止まらない");
            app.Ui(() => m.Playback.Stop());
        }
    }

    private static T FindFocusable<T>(DependencyObject root, Func<T, bool> pick) where T : FrameworkElement =>
        TryFind(root, pick) ?? throw new InvalidOperationException("要素が見つからない");

    private static T? TryFind<T>(DependencyObject root, Func<T, bool> pick) where T : FrameworkElement
    {
        if (root is T t && t.Focusable && pick(t)) return t;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (TryFind(System.Windows.Media.VisualTreeHelper.GetChild(root, i), pick) is { } found) return found;
        }
        return null;
    }
}
