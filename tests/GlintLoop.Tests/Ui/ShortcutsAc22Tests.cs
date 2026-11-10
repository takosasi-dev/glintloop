// AC-22 の (2) 画面の配線と (3) キーボードだけで行えることを、本物の部品がそろった編集画面で調べる(キーはウィンドウの中へ直接発生させ、修飾キーは偽物)。
// (1) の判定の関数は ShellU0Tests の U0_ショートカットの表は37行で判定の骨組みが条件を守る と U0_Escは開いているものを順に1つだけ取り消す、(4) は U0_F1で…、(5) は ScreensTests.AC22_5。
// Space(再生)・コピー・保存・スクラブ・ドラッグ欄は Phase 4・5 の機能なので、その機能を実装したフェーズから検査する。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Controls;
using GlintLoop.App.Input;
using GlintLoop.App.Layers;
using GlintLoop.App.Screens;
using GlintLoop.App.Shell;
using GlintLoop.App.Stack;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Ui.EditingScreenKit;
using K = System.Windows.Input.Key;

namespace GlintLoop.Tests.Ui;

public sealed class ShortcutsAc22Tests
{
    private const ModifierKeys C = ModifierKeys.Control, S = ModifierKeys.Shift, A = ModifierKeys.Alt, N = ModifierKeys.None;

    private static int Count(TestApp app) => app.Ui(() => app.Shell.Session.Document?.Layers.Count ?? 0);

    private static int Undo(TestApp app) => app.Ui(() => app.Shell.Session.UndoCount);

    [Test]
    public void AC22_2_取り込みと空のレイヤーと履歴とF1とEscとフィットとパンのキーが実際の動作になる()
    {
        using var app = Start(layers: 2);
        var canvas = M<CanvasModule>(app);
        void FocusCanvas() => app.Ui(() => Keyboard.Focus(canvas.Surface));

        // Ctrl+V: 2 枚目の画像をレイヤーとして足す
        FocusCanvas();
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(120, 80, Img.Solid(120, 80, 200, 40, 40)));
        Check.True(Key(app, K.V, C), "Ctrl+V");
        WaitImports(app);
        Check.Equal(3, Count(app), "Ctrl+V のあとの枚数");
        // Ctrl+Shift+N: 空のレイヤーを足す
        FocusCanvas();
        Check.True(Key(app, K.N, C | S), "Ctrl+Shift+N");
        Check.Equal(4, Count(app), "Ctrl+Shift+N のあとの枚数");
        Check.Equal("空のレイヤー", app.Ui(() => app.Shell.Session.Document!.Layers[3].Name), "空のレイヤーの名前");
        // Ctrl+Z・Ctrl+Y・Ctrl+Shift+Z
        FocusCanvas();
        var undo = Undo(app);
        Check.True(Key(app, K.Z, C), "Ctrl+Z");
        Check.Equal((3, undo - 1), (Count(app), Undo(app)), "Ctrl+Z で空のレイヤーが消える");
        Check.True(Key(app, K.Y, C), "Ctrl+Y");
        Check.Equal((4, undo), (Count(app), Undo(app)), "Ctrl+Y で戻る");
        Key(app, K.Z, C);
        Check.True(Key(app, K.Z, C | S), "Ctrl+Shift+Z");
        Check.Equal((4, undo), (Count(app), Undo(app)), "Ctrl+Shift+Z で戻る");

        // F1 で一覧、Esc で閉じる
        Check.True(Key(app, K.F1), "F1");
        Check.True(app.Ui(() => app.Shell.Dialogs.Top is ShortcutListView), "F1 で一覧が出ない");
        UiHost.Idle();
        Check.True(Key(app, K.Escape), "一覧の Esc");
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "Esc で一覧が閉じない");

        // Ctrl+1(100%)のあと Ctrl+0 でフィット
        FocusCanvas();
        Check.True(Key(app, K.D1, C), "Ctrl+1");
        Check.True(app.Ui(() => !canvas.Viewport.IsFit && Math.Abs(canvas.Viewport.Zoom - 1) < 1e-9), "Ctrl+1 で 100% にならない");
        Check.True(Key(app, K.D0, C), "Ctrl+0");
        Check.True(app.Ui(() => canvas.Viewport.IsFit), "Ctrl+0 でフィットにならない");

        // 矢印キー(キャンバス): 画像がキャンバスより大きいとき、押した向きへ 32 DIP(Shift で 128 DIP)
        for (var i = 0; i < 5; i++) Key(app, K.OemPlus, C);
        var before = app.Ui(() => app.CanvasTransform.Offset);
        Check.True(Key(app, K.Right), "右の矢印キー");
        Check.Equal(before.X + 32, app.Ui(() => app.CanvasTransform.Offset.X), "右の矢印キーで画像が右へ 32 DIP");
        Check.True(Key(app, K.Down, S), "Shift+下の矢印キー");
        Check.Equal(before.Y + 128, app.Ui(() => app.CanvasTransform.Offset.Y), "Shift+下の矢印キーで画像が下へ 128 DIP");
    }

    [Test]
    public void AC22_2_スタックとレイヤーの一覧のキーとEscと数値入力欄のCtrlZ()
    {
        using var app = Start(layers: 3);
        var stack = M<StackModule>(app);
        var layers = Layers(app);
        var s = app.Shell.Session;

        // Alt+下(スタックにフォーカス): ルックのステージの中で 1 つ下へ
        app.Ui(() =>
        {
            stack.AddEffect("exposure");
            stack.AddEffect("contrast");
        });
        UiHost.Idle();
        string Ids() => app.Ui(() => string.Join(",", s.Document!.StackOf(s.Selected).Select(e => e.Id)));
        var row = app.Ui(() => s.Document!.StackOf(s.Selected).ToList().FindIndex(e => e.Id == "exposure"));
        app.Ui(() =>
        {
            stack.SelectRow(row);
            Keyboard.Focus(stack.StackView.RowItems[row]);
        });
        var ids = Ids();
        Check.True(Key(app, K.Down, A), "スタックの Alt+下");
        Check.True(Ids() != ids && Ids().IndexOf("contrast", StringComparison.Ordinal) < Ids().IndexOf("exposure", StringComparison.Ordinal), $"Alt+下で並びが変わらない: {ids} → {Ids()}");
        // Esc(スタックにフォーカス): 行の選択を外す
        app.Ui(() => Keyboard.Focus(stack.StackView.RowItems[stack.SelectedRow!.Row]));
        Check.True(Key(app, K.Escape), "スタックの Esc");
        Check.True(app.Ui(() => stack.SelectedRow is null), "Esc で行の選択が外れない");
        Check.True(app.Ui(() => layers.ShowsSettings && layers.Settings.IsVisible), "Esc のあとレイヤーの設定が出ない");

        // レイヤーの一覧にフォーカス: 下(1 つ下の行)・Ctrl+D・Alt+上・F2・Delete
        app.Ui(() =>
        {
            s.Select(Target.OfLayer(2));
            layers.List.FocusSelected();
        });
        Check.True(Key(app, K.Down), "一覧の下");
        Check.Equal(Target.OfLayer(1), app.Ui(() => s.Selected), "下で 1 つ下の行");
        Check.True(Key(app, K.D, C), "一覧の Ctrl+D");
        Check.Equal(4, Count(app), "Ctrl+D で複製");
        Check.Equal(Target.OfLayer(2), app.Ui(() => s.Selected), "コピーが選ばれる");
        var name = app.Ui(() => s.Document!.Layers[2].Name);
        Check.True(app.Ui(() => layers.List.List.IsKeyboardFocusWithin && Keyboard.FocusedElement is LayerRowItem { IsSelected: true }), "Ctrl+D のあと選んだ行にフォーカスが無い");
        Check.True(Key(app, K.Up, A), "一覧の Alt+上");
        Check.Equal(name, app.Ui(() => s.Document!.Layers[3].Name), "Alt+上で手前へ");
        Check.True(Key(app, K.F2), "一覧の F2");
        UiHost.Idle();
        Check.True(app.Ui(() => layers.List.EditingRow is not null), "F2 で名前の編集にならない");
        Check.True(Key(app, K.Escape), "名前の編集の Esc");
        app.Ui(() => layers.List.FocusSelected());
        Check.True(Key(app, K.Delete), "一覧の Delete");
        Check.Equal(3, Count(app), "Delete で削除");

        // 数値入力欄にフォーカスがあるまま Ctrl+Z を発生させても履歴は動かない(FR-30)
        app.Ui(() => s.Select(Target.OfLayer(1)));
        UiHost.Idle();
        var json = app.Ui(() => s.Json);
        var undo = Undo(app);
        app.Ui(() => Keyboard.Focus(layers.Settings.XBox));
        Check.True(app.Ui(() => layers.Settings.XBox.IsKeyboardFocused), "位置 X の欄にフォーカスが無い");
        Key(app, K.Z, C);
        Check.Equal(undo, Undo(app), "数値入力欄の Ctrl+Z で履歴の件数が変わった");
        Check.Equal(json, app.Ui(() => s.Json), "数値入力欄の Ctrl+Z で文書が変わった");
    }

    [Test]
    public void AC22_2_編集していない文書のCtrlNはウェルカム画面へ戻りウェルカム画面のCtrlNは何もしない()
    {
        using var app = TestApp.Start(gpu: GlintLoop.Render.GpuAdapterRequest.Warp);
        var screens = M<ScreensModule>(app);
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(64, 48, Img.Solid(64, 48, 9, 9, 9)));
        Check.True(Key(app, K.V, C), "Ctrl+V");
        WaitImports(app);
        Check.True(app.Ui(() => app.Shell.Session.HasDocument && !app.Shell.Window.WelcomeHost.IsVisible), "取り込んでも編集画面にならない");
        Check.True(Key(app, K.N, C), "Ctrl+N");
        UiHost.Idle();
        Check.True(app.Ui(() => !app.Shell.Session.HasDocument && !app.Shell.Dialogs.IsOpen), "編集していない文書の Ctrl+N で確認なしに閉じない");
        Check.True(app.Ui(() => screens.Welcome.IsVisible), "ウェルカム画面に戻らない");
        Key(app, K.N, C);
        UiHost.Idle();
        Check.True(app.Ui(() => !app.Shell.Session.HasDocument && !app.Shell.Dialogs.IsOpen && screens.Welcome.IsVisible), "ウェルカム画面の Ctrl+N で何かが起きた");
    }

    [Test]
    public void AC22_3_マウスのドラッグが主の操作とそのほかの操作にキーボードの同じ操作がある()
    {
        using var app = Start(layers: 2);
        var r = app.Shell.Router;
        // 動作の登録(ファイルのドロップ → Ctrl+V・Ctrl+O、行のドラッグ → Alt+上下、並べ替え・パン・ズーム・縦線・境界・レイヤーの操作)
        ShortcutAction[] actions =
        [
            ShortcutAction.PasteImage, ShortcutAction.Open, ShortcutAction.New, ShortcutAction.AddEmptyLayer,
            ShortcutAction.StackMoveUp, ShortcutAction.StackMoveDown, ShortcutAction.StackDuplicate, ShortcutAction.StackDelete,
            ShortcutAction.LayerMoveFront, ShortcutAction.LayerMoveBack, ShortcutAction.LayerDuplicate, ShortcutAction.LayerDelete,
            ShortcutAction.LayerRename, ShortcutAction.LayerSelectUp, ShortcutAction.LayerSelectDown,
            ShortcutAction.ZoomIn, ShortcutAction.ZoomOut, ShortcutAction.ZoomFit, ShortcutAction.Zoom100,
            ShortcutAction.PanUp, ShortcutAction.PanDown, ShortcutAction.PanLeft, ShortcutAction.PanRight,
            ShortcutAction.SplitLineLeft, ShortcutAction.SplitLineRight,
            ShortcutAction.PaneBoundaryLeft, ShortcutAction.PaneBoundaryRight, ShortcutAction.PaneBoundaryUp, ShortcutAction.PaneBoundaryDown,
            ShortcutAction.Undo, ShortcutAction.Redo, ShortcutAction.FocusSearch, ShortcutAction.LeftTabLooks, ShortcutAction.LeftTabEffects,
            ShortcutAction.LeftTabMotion, ShortcutAction.ContextMenu, ShortcutAction.ShowShortcutList, ShortcutAction.OpenSettings,
            ShortcutAction.ShowOriginal, ShortcutAction.Cancel,
        ];
        var missing = app.Ui(() => actions.Where(a => !r.IsRegistered(a)).ToList());
        Check.True(missing.Count == 0, "登録の無い動作: " + string.Join(", ", missing));
        // 枠のドラッグ → レイヤーの設定の位置 X・位置 Y・拡縮の数値入力、スポイト → 16 進数の入力
        var layers = Layers(app);
        app.Ui(() =>
        {
            var v = layers.Settings;
            Check.True(v.IsVisible, "レイヤーの設定が出ていない");
            foreach (var (box, what) in new[] { (v.XBox, "位置 X"), (v.YBox, "位置 Y"), (v.ScaleBox, "拡縮") })
                Check.True(box is NumberBox { IsVisible: true, IsEnabled: true }, what + " の数値入力が無い");
            var undo = app.Shell.Session.UndoCount;
            v.XBox.Text = "12";
            v.XBox.Commit();
            Check.Equal(12.0, app.Shell.Session.Document!.Layers[1].Place.X, "位置 X の数値入力が枠のドラッグと同じ結果にならない");
            Check.Equal(undo + 1, app.Shell.Session.UndoCount, "位置 X の数値入力の件数");
            Check.True(v.FillHex is TextBox { IsVisible: true }, "塗りの色の 16 進数の入力が無い");
        });
        var stack = M<StackModule>(app);
        app.Ui(() => Check.True(stack.AddEffect("chroma").Changed, "色抜きを足せない"));
        UiHost.Idle();
        app.Ui(() =>
        {
            var hex = FindByIdLogical(stack.ParamsView, "param.key.hex");
            Check.True(hex is TextBox { IsVisible: true }, "色抜きの key の 16 進数の入力が無い");
        });
    }

    private static DependencyObject? FindByIdLogical(DependencyObject root, string id)
    {
        if (System.Windows.Automation.AutomationProperties.GetAutomationId(root) == id) return root;
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindByIdLogical(c, id) is { } f) return f;
        return null;
    }
}
