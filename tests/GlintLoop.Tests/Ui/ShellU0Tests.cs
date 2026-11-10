// U0 の受け口の確かめ(AC-22 の検査そのものは後の担当): ショートカットの表と判定の骨組み・振り分けの配線・数値入力(FR-27)・ペインの寸法の規則(§9.3)・
// ダイアログの重ね表示と Esc・F1 の一覧。後の担当が同じ書き方でテストを足せるように、補助(TestApp・UiInput・FakeModifierKeys)の使い方の見本も兼ねる。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Controls;
using GlintLoop.App.Input;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class ShellU0Tests
{
    private static ShortcutAction? R(Key k, ModifierKeys m, FocusKind f, bool doc = true) => Shortcuts.Resolve(KeyChord.Of(k, m), new ShortcutContext(f, doc));

    [Test]
    public void U0_ショートカットの表は37行で判定の骨組みが条件を守る()
    {
        Check.Equal(37, Shortcuts.Table.Count, "表の行数");
        Check.SequenceEqual(Enumerable.Range(1, 37).ToList(), Shortcuts.Table.Select(r => r.Number).ToList(), "行の番号");
        const ModifierKeys C = ModifierKeys.Control, S = ModifierKeys.Shift, A = ModifierKeys.Alt, N = ModifierKeys.None;
        // 文字入力欄では Ctrl+V・Ctrl+C・Ctrl+Z・Ctrl+Y・Ctrl+Shift+Z・Backslash が欄の編集になる
        foreach (var (k, m) in new[] { (Key.V, C), (Key.C, C), (Key.Z, C), (Key.Y, C), (Key.Z, C | S), (Key.OemPipe, N), (Key.OemBackslash, N) })
            Check.True(R(k, m, FocusKind.TextInput) is null, $"文字入力欄で {m}+{k} が動作になった");
        Check.Equal(ShortcutAction.PasteImage, R(Key.V, C | S, FocusKind.TextInput), "Ctrl+Shift+V はどこでも");
        Check.Equal(ShortcutAction.ShowOriginal, R(Key.OemPipe, N, FocusKind.Other), "OemPipe");
        Check.Equal(ShortcutAction.ShowOriginal, R(Key.OemBackslash, N, FocusKind.Other), "OemBackslash");
        foreach (var k in new[] { Key.OemPlus, Key.Add })
        foreach (var m in new[] { C, C | S })
            Check.Equal(ShortcutAction.ZoomIn, R(k, m, FocusKind.Other), $"{m}+{k}");
        foreach (var k in new[] { Key.OemMinus, Key.Subtract }) Check.Equal(ShortcutAction.ZoomOut, R(k, C, FocusKind.Other), $"Ctrl+{k}");
        Check.Equal(ShortcutAction.OpenSettings, R(Key.OemComma, C, FocusKind.Other), "Ctrl+カンマ");
        Check.True(R(Key.N, C | S, FocusKind.Other, doc: false) is null, "文書が無いときの Ctrl+Shift+N");
        Check.Equal(ShortcutAction.StackDuplicate, R(Key.D, C, FocusKind.Stack), "スタックの Ctrl+D");
        Check.Equal(ShortcutAction.LayerDuplicate, R(Key.D, C, FocusKind.LayerList), "レイヤーの一覧の Ctrl+D");
        Check.True(R(Key.D, C, FocusKind.Other) is null && R(Key.Delete, N, FocusKind.Canvas) is null && R(Key.Up, A, FocusKind.Button) is null, "対象が無いときの Ctrl+D・Delete・Alt+上");
        Check.True(R(Key.F2, N, FocusKind.Stack) is null, "レイヤーの一覧の外の F2");
        Check.Equal(ShortcutAction.LayerMoveFront, R(Key.Up, A, FocusKind.LayerList), "レイヤーの一覧の Alt+上");
        Check.Equal(ShortcutAction.StackMoveDown, R(Key.Down, A, FocusKind.Stack), "スタックの Alt+下");
        foreach (var f in new[] { FocusKind.Button, FocusKind.List, FocusKind.TextInput, FocusKind.Stack, FocusKind.LayerList, FocusKind.Other })
            Check.True(R(Key.Space, N, f) is null, $"{f} の Space が再生になった");
        foreach (var f in new[] { FocusKind.Canvas, FocusKind.Timeline, FocusKind.Scrub })
            Check.Equal(ShortcutAction.PlayPause, R(Key.Space, N, f), $"{f} の Space");
        Check.Equal(ShortcutAction.PanRight, R(Key.Right, N, FocusKind.Canvas), "キャンバスの右");
        Check.Equal(ShortcutAction.PanRight, R(Key.Right, S, FocusKind.Canvas), "キャンバスの Shift+右");
        Check.Equal(ShortcutAction.PaneBoundaryLeft, R(Key.Left, N, FocusKind.PaneBoundaryHorizontal), "左右の境界の左");
        Check.True(R(Key.Up, N, FocusKind.PaneBoundaryHorizontal) is null, "左右の境界の上");
        Check.Equal(ShortcutAction.PaneBoundaryDown, R(Key.Down, S, FocusKind.PaneBoundaryVertical), "縦の境界の Shift+下");
        Check.Equal(ShortcutAction.LayerSelectDown, R(Key.Down, N, FocusKind.LayerList), "レイヤーの一覧の下");
        Check.Equal(ShortcutAction.FramePrev, R(Key.Left, N, FocusKind.Scrub), "スクラブバーの左");
        Check.Equal(ShortcutAction.SplitLineRight, R(Key.Right, N, FocusKind.SplitLine), "縦線の右");
        Check.True(R(Key.Left, N, FocusKind.Other) is null && R(Key.Left, N, FocusKind.Stack) is null, "対象の無い矢印キー");
        Check.Equal(ShortcutAction.ContextMenu, R(Key.F10, S, FocusKind.LookTile), "タイルの Shift+F10");
        Check.Equal(ShortcutAction.ContextMenu, R(Key.Apps, N, FocusKind.UserPresetItem), "プリセットのアプリケーションキー");
        Check.True(R(Key.F10, S, FocusKind.Button) is null, "ボタンの Shift+F10");
        Check.True(R(Key.Q, C, FocusKind.Other) is null && R(Key.F3, N, FocusKind.Other) is null, "表に無いキー");
        Check.Equal("新規(Ctrl+N)", Shortcuts.ToolTipText("新規", ShortcutAction.New), "ツールチップの文");
        Check.Equal("やり直す(Ctrl+Y)", Shortcuts.ToolTipText("やり直す", ShortcutAction.Redo), "複数あるときは最初の 1 つ");
        Check.Equal("分割比較", Shortcuts.ToolTipText("分割比較", null), "ショートカットの無いボタン");
    }

    [Test]
    public void U0_ドロップダウンのテンプレートが開いても例外を出さず選んだ項目を出す()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            app.Shell.Window.SetWelcome(false);
            var g = new ComponentGallery();
            app.Shell.Window.CanvasHost.Content = g;
            g.UpdateLayout();
            var cb = g.DropDown;
            Check.True(cb.Template?.FindName("PART_Popup", cb) is System.Windows.Controls.Primitives.Popup, "自前のテンプレートの Popup が無い");
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            var g = (ComponentGallery)app.Shell.Window.CanvasHost.Content;
            g.DropDown.SelectedIndex = 1;
            Check.Equal("GIF", ((ComboBoxItem)g.DropDown.SelectedItem).Content, "選んだ項目");
        });
        UiHost.Idle();
    }

    [Test]
    public void U0_Escは開いているものを順に1つだけ取り消す()
    {
        var chain = new EscapeChain();
        var open = new Dictionary<EscapeTarget, bool>();
        foreach (var t in Enum.GetValues<EscapeTarget>())
        {
            open[t] = true;
            var target = t;
            chain.Register(t, () => open[target], () => open[target] = false);
        }
        var order = new List<EscapeTarget?>();
        for (var i = 0; i < 7; i++) order.Add(chain.Cancel(FocusKind.Stack));
        Check.SequenceEqual(new EscapeTarget?[]
        {
            EscapeTarget.NameEdit, EscapeTarget.Dialog, EscapeTarget.Eyedropper, EscapeTarget.HandoffWait, EscapeTarget.ActualOutput,
            EscapeTarget.StackSelection, null,
        }, order, "取り消しの順");
        open[EscapeTarget.StackSelection] = true;
        Check.True(chain.Cancel(FocusKind.Canvas) is null, "スタックにフォーカスが無いのに行の選択を取り消した");
    }

    [Test]
    public void U0_振り分けは偽の修飾キーで動き文字入力欄のCtrl_Zは履歴を動かさない()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            var calls = new List<ShortcutAction>();
            app.Shell.Router.Register(ShortcutAction.Undo, inv => { calls.Add(inv.Action); return true; });
            app.Shell.Router.Register(ShortcutAction.ZoomFit, inv => { calls.Add(inv.Action); return true; });
            var b = w.CommandBar.Button(Icons.Settings);
            Keyboard.Focus(b);
            app.Modifiers.Current = ModifierKeys.Control;
            Check.True(UiInput.Key(w, Key.Z), "Ctrl+Z が処理されない");
            Check.True(UiInput.Key(w, Key.D0), "Ctrl+0 が処理されない");
            Check.SequenceEqual([ShortcutAction.Undo, ShortcutAction.ZoomFit], calls, "呼ばれた動作");

            w.SetWelcome(false);
            var box = new NumberBox();
            AutomationPropertiesName(box, "テスト");
            w.LeftPaneHost.Content = box;
            w.UpdateLayout();
            Keyboard.Focus(box);
            calls.Clear();
            UiInput.Key(w, Key.Z);
            Check.True(calls.Count == 0, "数値入力欄の Ctrl+Z が元に戻すになった");
            Check.Equal(0, app.Shell.Session.UndoCount, "履歴の件数");
            app.Modifiers.Current = ModifierKeys.None;
        });
    }

    private static void AutomationPropertiesName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    [Test]
    public void U0_F1でショートカットの一覧が37行で出てEscで閉じる()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            Keyboard.Focus(w.CommandBar.Button(Icons.Settings));
            Check.True(UiInput.Key(w, Key.F1), "F1 が処理されない");
            Check.True(app.Shell.Dialogs.Top is ShortcutListView, "一覧が出ない");
            var list = (ShortcutListView)app.Shell.Dialogs.Top!;
            Check.Equal(37 * 3, list.Rows.Children.Count, "一覧の行(キーと動作と条件)");
            Check.Equal("Ctrl+V", ((TextBlock)list.Rows.Children[0]).Text, "1 行目のキー");
            // Space の行の「上の注のとおり」が指す文(§9.12 の表の上の文のまま)
            Check.Equal("Space は、ボタン・リスト・文字入力欄にフォーカスがあるときはその部品の操作(押下・入力)を優先し、それ以外(キャンバスとタイムライン)にあるときに再生と停止を行う。", list.Note.Text, "表の上の Space の注");
        });
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.True(UiInput.Key(app.Shell.Window, Key.Escape), "Esc が処理されない");
            Check.True(!app.Shell.Dialogs.IsOpen, "Esc でダイアログが閉じない");
        });
    }

    [Test]
    public void U0_数値入力はEnterとフォーカスを失ったときに反映し範囲の端に直す()
    {
        Check.Equal(10.0, NumberBox.Parse("12", 0, 10, 0), "最大より大きい");
        Check.Equal(0.0, NumberBox.Parse("-3", 0, 10, 0), "最小より小さい");
        Check.Equal(2.5, NumberBox.Parse(" 2.5 ", 0, 10, 1), "小数");
        Check.True(NumberBox.Parse("", 0, 10, 0) is null && NumberBox.Parse("abc", 0, 10, 0) is null, "数として読めない入力");
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            w.SetWelcome(false);
            var box = new NumberBox { Minimum = 1, Maximum = 100, Value = 50 };
            var other = new Button { Content = "他" };
            w.LeftPaneHost.Content = new StackPanel { Children = { box, other } };
            w.UpdateLayout();
            var committed = new List<double>();
            box.ValueCommitted += (_, v) => committed.Add(v);
            Keyboard.Focus(box);
            box.Text = "500";
            Check.Equal(50.0, box.Value, "Enter の前に反映した");
            UiInput.Key(box, Key.Enter);
            Check.Equal(100.0, box.Value, "Enter で端に直して反映");
            Check.Equal("100", box.Text, "欄の文字");
            box.Text = "あ";
            Keyboard.Focus(other);
            Check.Equal(100.0, box.Value, "読めない入力で値が変わった");
            Check.Equal("100", box.Text, "読めない入力で欄が直前の値に戻らない");
            Keyboard.Focus(box);
            box.Text = "7";
            Keyboard.Focus(other);
            Check.Equal(7.0, box.Value, "フォーカスを失ったときに反映しない");
            Check.SequenceEqual([100.0, 7.0], committed, "反映の通知");
        });
    }

    [Test]
    public void U0_ペインの幅と右ペインの縦の配分が9_3の規則どおり()
    {
        var m = UiHost.Invoke(() => PaneLayout.Metrics.From(AppResources.LayoutTokens));
        // ウィンドウの最小の幅 1120: 合計は 1120 − 448 − 16 = 656 まで
        Check.Equal((312.0, 344.0), PaneLayout.ClampWidths(m, 312, 480, 1120, PaneSide.Right), "右を縮める");
        Check.Equal((240.0, 416.0), PaneLayout.ClampWidths(m, 400, 416, 1120, PaneSide.Left), "左を縮める");
        Check.Equal((240.0, 280.0), PaneLayout.ClampWidths(m, 100, 100, 1280, PaneSide.Right), "最小で止まる");
        Check.Equal((420.0, 480.0), PaneLayout.ClampWidths(m, 999, 999, 3000, PaneSide.Right), "最大で止まる");
        // 最小の高さ 640 のときの右ペインの高さ 504: レイヤーの一覧 136、スタックとパラメータ欄 160 ずつ
        Check.Equal(new RightPaneHeights(136, 160, 160), PaneLayout.Initial(m, 504), "高さ 504 の配分");
        // 既定の高さ 800 のときの右ペインの高さ 664: 176、残り 440 を 220 ずつ。奇数の余りはスタック
        Check.Equal(new RightPaneHeights(176, 220, 220), PaneLayout.Initial(m, 664), "高さ 664 の配分");
        Check.Equal(new RightPaneHeights(176, 221, 220), PaneLayout.Initial(m, 665), "奇数の余り");
        var h = PaneLayout.Initial(m, 664);
        Check.Equal(new RightPaneHeights(176, 220, 320), PaneLayout.Resize(m, h, 764), "高くするとパラメータ欄が受ける");
        Check.Equal(new RightPaneHeights(136, 160, 160), PaneLayout.Resize(m, h, 504), "低くするとパラメータ欄・スタック・一覧の順に縮める");
        Check.Equal(new RightPaneHeights(236, 160, 220), PaneLayout.MoveBoundary(m, h, 0, 100), "上の境界はスタックの最小で止まる");
        Check.Equal(new RightPaneHeights(176, 160, 280), PaneLayout.MoveBoundary(m, h, 1, -100), "下の境界はスタックの最小で止まる");
        Check.Equal(new RightPaneHeights(128, 268, 220), PaneLayout.MoveBoundary(m, h, 0, -100), "一覧の最小で止まる");
    }

    [Test]
    public void U0_ペインの境界は矢印キーで8DIP_Shiftで32DIP動く()
    {
        using var app = TestApp.Start();
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            var before = w.LeftPaneWidth;
            Keyboard.Focus(w.LeftSplitter);
            Check.True(UiInput.Key(w, Key.Right), "右の矢印が処理されない");
            Check.Equal(before + 8, w.LeftPaneWidth, "8 DIP");
            app.Modifiers.Current = ModifierKeys.Shift;
            UiInput.Key(w, Key.Left);
            app.Modifiers.Current = ModifierKeys.None;
            Check.Equal(before - 24, w.LeftPaneWidth, "Shift で 32 DIP");
            for (var i = 0; i < 50; i++) UiInput.Key(w, Key.Left);
            Check.Equal(240.0, w.LeftPaneWidth, "最小で止まる");
        });
    }
}
