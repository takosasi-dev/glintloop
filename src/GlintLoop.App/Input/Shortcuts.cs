// §9.12 のショートカットの表(37 行)と、キーと修飾キーの組(KeyChord)・フォーカスの種類・文書の有無から動作を 1 つ決める判定(FR-100・AC-22 の (1))。
// 判定は純粋な関数で、修飾キーの状態は呼び出し側が渡す(§11 Phase 3 の前書きの (c))。表に無い組と、条件を満たさない組は null を返す。
// キーの対応: Backslash は OemPipe と OemBackslash、Ctrl+プラスは OemPlus と Add(Shift は問わない)、Ctrl+マイナスは OemMinus と Subtract、Ctrl+カンマは OemComma。
using System.Windows.Input;

namespace GlintLoop.App.Input;

/// <summary>キーと修飾キーの組。</summary>
public readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    public bool Ctrl => (Modifiers & ModifierKeys.Control) != 0;

    public bool Shift => (Modifiers & ModifierKeys.Shift) != 0;

    public bool Alt => (Modifiers & ModifierKeys.Alt) != 0;

    public static KeyChord Of(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers & ~ModifierKeys.Windows);

    public override string ToString() => Modifiers == ModifierKeys.None ? Key.ToString() : $"{Modifiers}+{Key}";
}

/// <summary>フォーカスのある部品の種類(判定の入力)。部品には ShortcutScope.Kind を付ける(付いていなければ型から決める)。</summary>
public enum FocusKind
{
    /// <summary>テキスト入力・数値入力・検索欄・色の 16 進入力。</summary>
    TextInput,
    Button,
    List,
    Stack,
    LayerList,
    Scrub,
    /// <summary>タイムラインの、スクラブバー以外の部分。</summary>
    Timeline,
    Canvas,
    /// <summary>分割比較の縦線。</summary>
    SplitLine,
    /// <summary>左右のペインの境界(左・右の矢印で動く)。</summary>
    PaneBoundaryHorizontal,
    /// <summary>右ペインの縦の 2 つの境界(上・下の矢印で動く)。</summary>
    PaneBoundaryVertical,
    LookTile,
    UserPresetItem,
    Other,
}

/// <summary>表の行が起こす動作。同じキーでも対象が違えば別の値(StackDuplicate と LayerDuplicate など)。</summary>
public enum ShortcutAction
{
    PasteImage, Open, New, AddEmptyLayer, SaveExport, SaveProject, Copy, Undo, Redo,
    StackDuplicate, StackDelete, StackMoveUp, StackMoveDown,
    LayerSelectUp, LayerSelectDown, LayerDuplicate, LayerDelete, LayerMoveFront, LayerMoveBack, LayerRename,
    FocusSearch, LeftTabLooks, LeftTabEffects, LeftTabMotion, PlayPause,
    FramePrev, FrameNext, FrameFirst, FrameLast, SplitLineLeft, SplitLineRight,
    ShowOriginal, ActualOutput, ZoomFit, Zoom100, ZoomIn, ZoomOut,
    PanUp, PanDown, PanLeft, PanRight,
    PaneBoundaryLeft, PaneBoundaryRight, PaneBoundaryUp, PaneBoundaryDown,
    OpenBatch, OpenSettings, ContextMenu, ShowShortcutList, Cancel, MoveFocus,
}

/// <summary>判定に渡す、そのときの状態。</summary>
public readonly record struct ShortcutContext(FocusKind Focus, bool HasDocument);

/// <summary>F1 の一覧と同じ内容の 1 行(§9.12 の表のキー・動作・条件の文字)。</summary>
public sealed record ShortcutRow(int Number, string Keys, string Action, string Condition);

public static class Shortcuts
{
    /// <summary>表の Space の行の「上の注」(§9.12 の表の上の文のまま)。F1 の一覧で表の上に出す。</summary>
    public const string SpaceNote = "Space は、ボタン・リスト・文字入力欄にフォーカスがあるときはその部品の操作(押下・入力)を優先し、それ以外(キャンバスとタイムライン)にあるときに再生と停止を行う。";

    /// <summary>§9.12 の表の 37 行(表の順。文字は表のとおり)。</summary>
    public static IReadOnlyList<ShortcutRow> Table { get; } =
    [
        new(1, "Ctrl+V", "画像を貼り付け(FR-1)", "文字入力欄にフォーカスが無いとき"),
        new(2, "Ctrl+Shift+V", "画像を貼り付け", "どこにフォーカスがあっても"),
        new(3, "Ctrl+O", "開く", ""),
        new(4, "Ctrl+N", "新規(現在の文書を閉じてウェルカム画面へ戻る。FR-43)", ""),
        new(5, "Ctrl+Shift+N", "空のレイヤーを足す(FR-45)", "文書があるとき"),
        new(6, "Ctrl+S", "保存(完成したファイルを保存。FR-84)", ""),
        new(7, "Ctrl+Shift+S", "プロジェクトを保存(FR-89)", ""),
        new(8, "Ctrl+C", "コピー(FR-80)", "文字入力欄にフォーカスが無いとき"),
        new(9, "Ctrl+Z", "元に戻す(FR-30)", "文字入力欄にフォーカスが無いとき"),
        new(10, "Ctrl+Y、Ctrl+Shift+Z", "やり直す(FR-30)", "文字入力欄にフォーカスが無いとき"),
        new(11, "Ctrl+D", "選んでいるエフェクトを複製", "スタックにフォーカス"),
        new(12, "Delete", "選んでいるエフェクトを削除", "スタックにフォーカス"),
        new(13, "Alt+上、Alt+下", "選んでいるエフェクトを同じステージの中で上・下へ(FR-25)", "スタックにフォーカス"),
        new(14, "上、下", "レイヤーの一覧で、1 つ上・1 つ下の行(「全体」の行かレイヤーの行)を選ぶ。端では何も起きない(FR-46)", "レイヤーの一覧にフォーカス"),
        new(15, "Ctrl+D", "選んでいるレイヤーを複製(FR-53)", "レイヤーの一覧にフォーカス"),
        new(16, "Delete", "選んでいるレイヤーを削除(FR-53)", "レイヤーの一覧にフォーカス"),
        new(17, "Alt+上、Alt+下", "選んでいるレイヤーを手前・奥へ(FR-52)", "レイヤーの一覧にフォーカス"),
        new(18, "F2", "選んでいるレイヤーの名前を変える(FR-54)", "レイヤーの一覧にフォーカス"),
        new(19, "Ctrl+F", "エフェクトの検索欄にフォーカスを移す", ""),
        new(20, "Alt+1、Alt+2、Alt+3", "左ペインのタブ(ルック・エフェクト・モーション)", ""),
        new(21, "Space", "再生・停止(FR-60)", "上の注のとおり"),
        new(22, "左、右", "前・次のフレーム", "スクラブバーにフォーカス"),
        new(23, "Home、End", "最初・最後のフレーム", "スクラブバーにフォーカス"),
        new(24, "左、右", "分割比較の縦線を 2% ずつ動かす(FR-18)", "縦線にフォーカス(Tab で止まる別の部品。キャンバスのパンは働かない)"),
        new(25, "Backslash(\\)", "押している間、加工前を表示(FR-17)", "文字入力欄にフォーカスが無いとき"),
        new(26, "F5", "実出力の表示(FR-19)", ""),
        new(27, "Ctrl+0、Ctrl+1", "フィット、100%(FR-15)", ""),
        new(28, "Ctrl+プラス、Ctrl+ホイール上", "拡大", ""),
        new(29, "Ctrl+マイナス、Ctrl+ホイール下", "縮小", ""),
        new(30, "矢印キー(上・下・左・右)", "キャンバスの画像を、押した向きに 32 DIP ずつ動かす(Shift を押しているときは 128 DIP ずつ。FR-16)", "キャンバスにフォーカス"),
        new(31, "矢印キー", "ペインの境界を 8 DIP ずつ動かす(Shift を押しているときは 32 DIP ずつ)。左右の境界は左・右、縦の 2 つの境界(レイヤーの一覧とスタックの間、スタックとパラメータ欄の間)は上・下で動き、最小と最大で止まる(§9.3)", "ペインの境界にフォーカス"),
        new(32, "Ctrl+B", "バッチ画面を開く(FR-87)", ""),
        new(33, "Ctrl+カンマ", "設定を開く(FR-90)", ""),
        new(34, "Shift+F10、アプリケーションキー", "コンテキストメニューを開く", "ルックのタイル、ユーザーのプリセットの項目にフォーカス"),
        new(35, "F1", "ショートカットの一覧", ""),
        new(36, "Esc", "取り消す。1 回の押下で取り消すのは 1 つだけで、開いているものを、一覧の行の名前の編集(FR-54)、ダイアログ、スポイト、コピー・保存の待機、実出力の表示、スタックの行の選択(スタックにフォーカスがあるときだけ。行を選んでいない状態に戻す。FR-48)の順に調べて、最初のものを取り消す(FR-83)。バッチの実行中は、実行を取り消す(FR-87)", ""),
        new(37, "Tab、Shift+Tab", "フォーカスの移動", ""),
    ];

    /// <summary>動作から、表の行の番号(F1 の一覧と、ツールチップの表記に使う)。</summary>
    public static int RowOf(ShortcutAction a) => a switch
    {
        ShortcutAction.PasteImage => 1,
        ShortcutAction.Open => 3,
        ShortcutAction.New => 4,
        ShortcutAction.AddEmptyLayer => 5,
        ShortcutAction.SaveExport => 6,
        ShortcutAction.SaveProject => 7,
        ShortcutAction.Copy => 8,
        ShortcutAction.Undo => 9,
        ShortcutAction.Redo => 10,
        ShortcutAction.StackDuplicate => 11,
        ShortcutAction.StackDelete => 12,
        ShortcutAction.StackMoveUp or ShortcutAction.StackMoveDown => 13,
        ShortcutAction.LayerSelectUp or ShortcutAction.LayerSelectDown => 14,
        ShortcutAction.LayerDuplicate => 15,
        ShortcutAction.LayerDelete => 16,
        ShortcutAction.LayerMoveFront or ShortcutAction.LayerMoveBack => 17,
        ShortcutAction.LayerRename => 18,
        ShortcutAction.FocusSearch => 19,
        ShortcutAction.LeftTabLooks or ShortcutAction.LeftTabEffects or ShortcutAction.LeftTabMotion => 20,
        ShortcutAction.PlayPause => 21,
        ShortcutAction.FramePrev or ShortcutAction.FrameNext => 22,
        ShortcutAction.FrameFirst or ShortcutAction.FrameLast => 23,
        ShortcutAction.SplitLineLeft or ShortcutAction.SplitLineRight => 24,
        ShortcutAction.ShowOriginal => 25,
        ShortcutAction.ActualOutput => 26,
        ShortcutAction.ZoomFit or ShortcutAction.Zoom100 => 27,
        ShortcutAction.ZoomIn => 28,
        ShortcutAction.ZoomOut => 29,
        ShortcutAction.PanUp or ShortcutAction.PanDown or ShortcutAction.PanLeft or ShortcutAction.PanRight => 30,
        ShortcutAction.PaneBoundaryLeft or ShortcutAction.PaneBoundaryRight or ShortcutAction.PaneBoundaryUp or ShortcutAction.PaneBoundaryDown => 31,
        ShortcutAction.OpenBatch => 32,
        ShortcutAction.OpenSettings => 33,
        ShortcutAction.ContextMenu => 34,
        ShortcutAction.ShowShortcutList => 35,
        ShortcutAction.Cancel => 36,
        _ => 37,
    };

    /// <summary>ツールチップに書くショートカットの表記: 表のキーの表記の最初の 1 つ(FR-102)。</summary>
    public static string FirstKeyText(int row)
    {
        var keys = Table[row - 1].Keys;
        var at = keys.IndexOf('、');
        return at < 0 ? keys : keys[..at];
    }

    /// <summary>アイコンボタンのツールチップの文(「名前(ショートカット)」。ショートカットが無ければ名前だけ)。</summary>
    public static string ToolTipText(string name, ShortcutAction? action) =>
        action is { } a ? name + "(" + FirstKeyText(RowOf(a)) + ")" : name;

    private static bool IsBackslash(Key k) => k is Key.OemPipe or Key.OemBackslash;

    private static bool IsPlus(Key k) => k is Key.OemPlus or Key.Add;

    private static bool IsMinus(Key k) => k is Key.OemMinus or Key.Subtract;

    private static bool IsArrow(Key k) => k is Key.Up or Key.Down or Key.Left or Key.Right;

    /// <summary>
    /// 1 回の押下の動作を決める。条件を満たさない組と表に無い組は null。Space は、ボタン・リスト・文字入力欄にあるときは部品の操作が優先(null)。
    /// 矢印キーは、フォーカスのある部品の行だけが受け取る。Tab と Shift+Tab は MoveFocus(実際の移動は WPF に任せる)。
    /// </summary>
    public static ShortcutAction? Resolve(KeyChord chord, ShortcutContext ctx)
    {
        var k = chord.Key;
        var m = chord.Modifiers;
        var text = ctx.Focus == FocusKind.TextInput;
        const ModifierKeys C = ModifierKeys.Control, S = ModifierKeys.Shift, A = ModifierKeys.Alt, N = ModifierKeys.None;

        // Ctrl+プラス・Ctrl+マイナスは Shift の有無を問わない
        if (IsPlus(k) && (m == C || m == (C | S))) return ShortcutAction.ZoomIn;
        if (IsMinus(k) && (m == C || m == (C | S))) return ShortcutAction.ZoomOut;
        if (IsBackslash(k) && m == N) return text ? null : ShortcutAction.ShowOriginal;

        if (m == C)
        {
            switch (k)
            {
                case Key.V: return text ? null : ShortcutAction.PasteImage;
                case Key.O: return ShortcutAction.Open;
                case Key.N: return ShortcutAction.New;
                case Key.S: return ShortcutAction.SaveExport;
                case Key.C: return text ? null : ShortcutAction.Copy;
                case Key.Z: return text ? null : ShortcutAction.Undo;
                case Key.Y: return text ? null : ShortcutAction.Redo;
                case Key.D:
                    return ctx.Focus switch
                    {
                        FocusKind.Stack => ShortcutAction.StackDuplicate,
                        FocusKind.LayerList => ShortcutAction.LayerDuplicate,
                        _ => null,
                    };
                case Key.F: return ShortcutAction.FocusSearch;
                case Key.D0: case Key.NumPad0: return ShortcutAction.ZoomFit;
                case Key.D1: case Key.NumPad1: return ShortcutAction.Zoom100;
                case Key.B: return ShortcutAction.OpenBatch;
                case Key.OemComma: return ShortcutAction.OpenSettings;
            }
            return null;
        }
        if (m == (C | S))
        {
            return k switch
            {
                Key.V => ShortcutAction.PasteImage,
                Key.N => ctx.HasDocument ? ShortcutAction.AddEmptyLayer : null,
                Key.S => ShortcutAction.SaveProject,
                Key.Z => text ? null : ShortcutAction.Redo,
                _ => null,
            };
        }
        if (m == A)
        {
            switch (k)
            {
                case Key.Up:
                case Key.Down:
                    var up = k == Key.Up;
                    return ctx.Focus switch
                    {
                        FocusKind.Stack => up ? ShortcutAction.StackMoveUp : ShortcutAction.StackMoveDown,
                        FocusKind.LayerList => up ? ShortcutAction.LayerMoveFront : ShortcutAction.LayerMoveBack,
                        _ => null,
                    };
                case Key.D1: case Key.NumPad1: return ShortcutAction.LeftTabLooks;
                case Key.D2: case Key.NumPad2: return ShortcutAction.LeftTabEffects;
                case Key.D3: case Key.NumPad3: return ShortcutAction.LeftTabMotion;
            }
            return null;
        }
        if (m == S)
        {
            if (k == Key.F10) return ContextMenuFor(ctx.Focus);
            if (k == Key.Tab) return ShortcutAction.MoveFocus;
            if (IsArrow(k)) return Arrow(k, ctx.Focus, shift: true);
            return null;
        }
        if (m != N) return null;

        switch (k)
        {
            case Key.Delete:
                return ctx.Focus switch
                {
                    FocusKind.Stack => ShortcutAction.StackDelete,
                    FocusKind.LayerList => ShortcutAction.LayerDelete,
                    _ => null,
                };
            case Key.F2: return ctx.Focus == FocusKind.LayerList ? ShortcutAction.LayerRename : null;
            case Key.Space:
                return ctx.Focus is FocusKind.Canvas or FocusKind.Timeline or FocusKind.Scrub ? ShortcutAction.PlayPause : null;
            case Key.Home: return ctx.Focus == FocusKind.Scrub ? ShortcutAction.FrameFirst : null;
            case Key.End: return ctx.Focus == FocusKind.Scrub ? ShortcutAction.FrameLast : null;
            case Key.F5: return ShortcutAction.ActualOutput;
            case Key.Apps: return ContextMenuFor(ctx.Focus);
            case Key.F1: return ShortcutAction.ShowShortcutList;
            case Key.Escape: return ShortcutAction.Cancel;
            case Key.Tab: return ShortcutAction.MoveFocus;
        }
        return IsArrow(k) ? Arrow(k, ctx.Focus, shift: false) : null;
    }

    private static ShortcutAction? ContextMenuFor(FocusKind f) => f is FocusKind.LookTile or FocusKind.UserPresetItem ? ShortcutAction.ContextMenu : null;

    /// <summary>矢印キー(Shift つきは、キャンバスのパンとペインの境界だけが大きく動く)。</summary>
    private static ShortcutAction? Arrow(Key k, FocusKind f, bool shift)
    {
        switch (f)
        {
            case FocusKind.Canvas:
                return k switch
                {
                    Key.Up => ShortcutAction.PanUp,
                    Key.Down => ShortcutAction.PanDown,
                    Key.Left => ShortcutAction.PanLeft,
                    _ => ShortcutAction.PanRight,
                };
            case FocusKind.PaneBoundaryHorizontal:
                return k switch { Key.Left => ShortcutAction.PaneBoundaryLeft, Key.Right => ShortcutAction.PaneBoundaryRight, _ => null };
            case FocusKind.PaneBoundaryVertical:
                return k switch { Key.Up => ShortcutAction.PaneBoundaryUp, Key.Down => ShortcutAction.PaneBoundaryDown, _ => null };
        }
        if (shift) return null;
        return f switch
        {
            FocusKind.LayerList => k switch { Key.Up => ShortcutAction.LayerSelectUp, Key.Down => ShortcutAction.LayerSelectDown, _ => null },
            FocusKind.Scrub => k switch { Key.Left => ShortcutAction.FramePrev, Key.Right => ShortcutAction.FrameNext, _ => null },
            FocusKind.SplitLine => k switch { Key.Left => ShortcutAction.SplitLineLeft, Key.Right => ShortcutAction.SplitLineRight, _ => null },
            _ => null,
        };
    }
}
