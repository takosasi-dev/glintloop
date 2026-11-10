// ショートカットの振り分けの骨組み(§9.12・FR-100): ウィンドウに届いたキーだけを受け(INV-17)、Shortcuts.Resolve で動作を決めて、登録された処理を呼ぶ。
// 各動作の中身は後の担当が Register で登録する。処理が true を返したときだけ、そのキーを処理済みにする(登録が無ければ部品の既定の動作に任せる)。ダイアログの表示中は Esc だけを振り分ける(FR-2)。
// 部品のフォーカスの種類は ShortcutScope.Kind(無ければ型から推す)。Esc は EscapeChain が §9.3 の順に 1 つだけ取り消す。修飾キーは IModifierKeys から読む。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Platform;

namespace GlintLoop.App.Input;

/// <summary>呼ばれた動作と、そのときのキー・フォーカス。IsRelease は Backslash(押している間の表示)を離したとき。</summary>
public sealed record ShortcutInvocation(ShortcutAction Action, KeyChord Chord, FocusKind Focus, DependencyObject? Source, bool IsRelease);

/// <summary>部品にフォーカスの種類を付ける添付プロパティ。付けた部品の中の子にも効く(近い祖先のものを使う)。</summary>
public static class ShortcutScope
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(FocusKind?), typeof(ShortcutScope), new PropertyMetadata(null));

    public static FocusKind? GetKind(DependencyObject d) => (FocusKind?)d.GetValue(KindProperty);

    public static void SetKind(DependencyObject d, FocusKind? value) => d.SetValue(KindProperty, value);

    /// <summary>フォーカスのある要素の種類。文字入力欄は祖先の指定より優先する(スタックの中の数値入力も文字入力欄)。</summary>
    public static FocusKind Of(DependencyObject? focused)
    {
        if (focused is null) return FocusKind.Other;
        if (focused is TextBoxBase or PasswordBox) return FocusKind.TextInput;
        for (DependencyObject? d = focused; d is not null; d = Parent(d))
        {
            if (GetKind(d) is { } k) return k;
        }
        return focused switch
        {
            ButtonBase => FocusKind.Button,
            ListBoxItem or Selector => FocusKind.List,
            _ => FocusKind.Other,
        };
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}

/// <summary>Esc で取り消すものの順(§9.3 の画面をまたぐ規則 (3))。</summary>
public enum EscapeTarget { NameEdit, Dialog, Eyedropper, HandoffWait, ActualOutput, StackSelection }

public sealed class EscapeChain
{
    private readonly Dictionary<EscapeTarget, (Func<bool> IsOpen, Action Cancel)> _targets = new();

    /// <summary>取り消せるものを登録する(後から登録したものが置き換わる)。</summary>
    public void Register(EscapeTarget target, Func<bool> isOpen, Action cancel) => _targets[target] = (isOpen, cancel);

    /// <summary>target が登録されていて、いま開いているか。</summary>
    public bool IsOpen(EscapeTarget target) => _targets.TryGetValue(target, out var e) && e.IsOpen();

    /// <summary>開いているものを順に調べ、最初のものだけを取り消す。スタックの行の選択はスタックにフォーカスがあるときだけ。取り消したものを返す。</summary>
    public EscapeTarget? Cancel(FocusKind focus)
    {
        foreach (var t in Enum.GetValues<EscapeTarget>())
        {
            if (t == EscapeTarget.StackSelection && focus != FocusKind.Stack) continue;
            if (_targets.TryGetValue(t, out var e) && e.IsOpen())
            {
                e.Cancel();
                return t;
            }
        }
        return null;
    }
}

public sealed class ShortcutRouter
{
    private readonly IModifierKeys _modifiers;
    private readonly Func<bool> _hasDocument;
    private readonly Dictionary<ShortcutAction, Func<ShortcutInvocation, bool>> _handlers = new();
    private bool _originalHeld;

    public ShortcutRouter(IModifierKeys modifiers, Func<bool> hasDocument)
    {
        _modifiers = modifiers;
        _hasDocument = hasDocument;
        Register(ShortcutAction.Cancel, inv => Escape.Cancel(inv.Focus) is not null);
    }

    public EscapeChain Escape { get; } = new();

    /// <summary>直近に振り分けた動作(診断とテスト用)。</summary>
    public ShortcutInvocation? Last { get; private set; }

    /// <summary>動作の処理を登録する(同じ動作は後から登録したものが置き換わる)。true を返すとキーを処理済みにする。</summary>
    public void Register(ShortcutAction action, Func<ShortcutInvocation, bool> handler) => _handlers[action] = handler;

    public void Unregister(ShortcutAction action) => _handlers.Remove(action);

    public bool IsRegistered(ShortcutAction action) => _handlers.ContainsKey(action);

    /// <summary>ウィンドウのキーを受ける(トンネルの PreviewKeyDown・PreviewKeyUp)。</summary>
    public void Attach(UIElement root)
    {
        root.PreviewKeyDown += (_, e) =>
        {
            if (HandleKeyDown(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.FocusedElement as DependencyObject ?? e.OriginalSource as DependencyObject))
                e.Handled = true;
        };
        root.PreviewKeyUp += (_, e) =>
        {
            if (HandleKeyUp(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.FocusedElement as DependencyObject ?? e.OriginalSource as DependencyObject))
                e.Handled = true;
        };
    }

    /// <summary>キーの押下を 1 回振り分ける。処理済みなら true。</summary>
    public bool HandleKeyDown(Key key, DependencyObject? focused)
    {
        var chord = KeyChord.Of(key, _modifiers.Current);
        var focus = ShortcutScope.Of(focused);
        var action = Shortcuts.Resolve(chord, new ShortcutContext(focus, _hasDocument()));
        if (action is not { } a || a == ShortcutAction.MoveFocus) return false;
        // ダイアログの表示中は、ダイアログの中の操作だけが働く(FR-2)。Esc(取り消し)だけを通し、Tab・Enter・矢印などは部品の既定の動作に任せる
        if (a != ShortcutAction.Cancel && Escape.IsOpen(EscapeTarget.Dialog)) return false;
        if (a == ShortcutAction.ShowOriginal && _originalHeld) return true; // キーの自動の繰り返し
        var inv = new ShortcutInvocation(a, chord, focus, focused, IsRelease: false);
        Last = inv;
        if (!_handlers.TryGetValue(a, out var h) || !h(inv)) return false;
        if (a == ShortcutAction.ShowOriginal) _originalHeld = true;
        return true;
    }

    /// <summary>ボタンから動作を呼ぶ(コマンドバーのボタンは、フォーカスの位置にかかわらず働く。FR-2)。処理されたら true。</summary>
    public bool Invoke(ShortcutAction action, DependencyObject? source)
    {
        var inv = new ShortcutInvocation(action, default, ShortcutScope.Of(source), source, IsRelease: false);
        Last = inv;
        return _handlers.TryGetValue(action, out var h) && h(inv);
    }

    /// <summary>キーを離したとき(Backslash の押している間の表示だけが使う)。</summary>
    public bool HandleKeyUp(Key key, DependencyObject? focused)
    {
        if (!_originalHeld || key is not (Key.OemPipe or Key.OemBackslash)) return false;
        _originalHeld = false;
        var inv = new ShortcutInvocation(ShortcutAction.ShowOriginal, KeyChord.Of(key, _modifiers.Current), ShortcutScope.Of(focused), focused, IsRelease: true);
        Last = inv;
        return _handlers.TryGetValue(ShortcutAction.ShowOriginal, out var h) && h(inv);
    }
}
