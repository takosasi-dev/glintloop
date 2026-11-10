// テストのフォーカスの確かめ(UI スレッドの上で呼ぶ)。画面の外の非アクティブなウィンドウでは、ダイアログの最初のフォーカスは論理フォーカス(FocusManager)だけになる
// (DialogHost。キーボードフォーカスを動かすと WPF の SetFocus でウィンドウが前面に出る)ので、キーボードフォーカスが無いときはウィンドウの論理フォーカスで見る。
// Tab の巡回の行き先は、MoveFocus のキーボードフォーカスの移動を PreviewGotKeyboardFocus で取り消して求め、論理フォーカスだけを動かす(論理フォーカスを決める間も取り消す)。
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GlintLoop.Tests.Ui;

public static class FocusCheck
{
    /// <summary>いまのフォーカス: キーボードフォーカスがあればそれ、無ければ inWindow のある範囲(ウィンドウ)の論理フォーカス。</summary>
    public static IInputElement? Focused(DependencyObject inWindow) =>
        Keyboard.FocusedElement ?? (FocusManager.GetFocusScope(inWindow) is { } scope ? FocusManager.GetFocusedElement(scope) : null);

    /// <summary>e にフォーカスがある(キーボードフォーカス、またはキーボードフォーカスが無いときの論理フォーカス)。</summary>
    public static bool IsFocused(UIElement e) => ReferenceEquals(Focused(e), e);

    /// <summary>container の中にフォーカスがある。</summary>
    public static bool IsFocusWithin(UIElement container) =>
        container.IsKeyboardFocusWithin || (Keyboard.FocusedElement is null && Focused(container) is Visual v && container.IsAncestorOf(v));

    /// <summary>
    /// from から dir へ Tab の巡回をしたときの行き先へ、論理フォーカスだけを動かす(キーボードフォーカスは動かさない)。行き先を返す(無ければ null)。
    /// </summary>
    public static IInputElement? MoveLogical(UIElement from, FocusNavigationDirection dir)
    {
        var window = Window.GetWindow(from) ?? throw new InvalidOperationException("ウィンドウの中の要素でない");
        IInputElement? next = null;
        KeyboardFocusChangedEventHandler cancel = (_, e) =>
        {
            next ??= e.NewFocus;
            e.Handled = true;
        };
        window.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, cancel, handledEventsToo: true);
        try
        {
            from.MoveFocus(new TraversalRequest(dir));
            // 論理フォーカスを決めると、WPF の FocusManager(FocusedElement の変化)がキーボードフォーカスも動かそうとし、SetFocus でウィンドウが前面になる
            // (ダイアログの中のボタンからスクロールの領域へ動かすときに起きた)。そこで、論理フォーカスを決める間もキーボードフォーカスの移動を取り消したままにする
            if (next is DependencyObject d && FocusManager.GetFocusScope(d) is { } scope) FocusManager.SetFocusedElement(scope, next);
        }
        finally
        {
            window.RemoveHandler(Keyboard.PreviewGotKeyboardFocusEvent, cancel);
        }
        return next;
    }
}
