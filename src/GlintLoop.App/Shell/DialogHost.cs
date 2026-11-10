// ダイアログを重ねて出す仕組み(§9.3 の規則 (4)): ウィンドウの中の重ね表示で、背後に scrim を敷く。ダイアログの上にダイアログを重ねられる(設定の上の「このソフトについて」)。
// 面は surface-1・radius-card・内側の余白 space-4。高さがウィンドウの高さ − 64 DIP を超えるときは中をスクロールし、スクロールできる間はその領域も Tab で止まる(FR-100)。
// 出現は dialog-in(不透明度と拡大率 0.96 → 1、原点は中央、200 ms・ease-drawer。scrim も 200 ms)、消失は dialog-out(140 ms)。Esc は一番上を閉じる(EscapeChain)。最初のフォーカスは、ウィンドウがアクティブなときだけキーボードフォーカスを動かし、そうでなければ論理フォーカスだけを決める(前面を取らない)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Shell;

/// <summary>Esc を自分で扱うダイアログの中身(バッチの実行中など)。true を返すとダイアログを閉じない。</summary>
public interface IDialogEscape
{
    bool HandleEscape();
}

public sealed class DialogHost : Grid
{
    private sealed class Entry
    {
        public required Rectangle Scrim;
        public required Border Card;
        public required ScrollViewer Scroll;
        public bool Ready;
        public required TaskCompletionSource<object?> Done;
        public IInputElement? FocusBefore;
    }

    private readonly List<Entry> _stack = new();

    public DialogHost()
    {
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "DialogHost");
    }

    public int Count => _stack.Count;

    public bool IsOpen => _stack.Count > 0;

    /// <summary>一番上のダイアログの中身。</summary>
    public FrameworkElement? Top => _stack.Count == 0 ? null : (FrameworkElement)((ScrollViewer)_stack[^1].Card.Child).Content;

    /// <summary>
    /// content をダイアログとして重ねて出す。width は §9.3 の画面の表の幅(リソースのキー。例 DialogSettingsWidth)、height は決まっていれば高さのキー。
    /// 閉じたときの結果を返す。
    /// </summary>
    public Task<object?> Show(FrameworkElement content, string widthKey, string? heightKey = null, MotionCause cause = MotionCause.Programmatic)
    {
        var scrim = new Rectangle();
        scrim.SetResourceReference(Shape.FillProperty, "ScrimBrush");
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        // 長い中身(設定・このソフトについて・ショートカットの一覧)をキーボードだけで送れるよう、スクロールできる間だけ Tab で止める(FR-100)
        AutomationProperties.SetName(scroll, ScrollName);
        AutomationProperties.SetAutomationId(scroll, "DialogScroll");
        FocusRing.SetIsEnabled(scroll, true);
        var card = new Border
        {
            Child = scroll,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface1Brush");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        card.SetResourceReference(Border.PaddingProperty, "Space4Thickness");
        card.SetResourceReference(WidthProperty, widthKey);
        if (heightKey is not null) card.SetResourceReference(HeightProperty, heightKey);
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(card, KeyboardNavigationMode.Contained);
        AutomationProperties.SetAutomationId(card, "Dialog");

        var entry = new Entry
        {
            Scrim = scrim, Card = card, Scroll = scroll, Done = new TaskCompletionSource<object?>(),
            FocusBefore = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(FocusManager.GetFocusScope(this) ?? this),
        };
        scroll.ScrollChanged += (_, _) => UpdateScrollFocusable(entry);
        _stack.Add(entry);
        Children.Add(scrim);
        Children.Add(card);
        Visibility = Visibility.Visible;
        UpdateMaxHeight();
        UpdateEnabled();

        var d = Motion.Duration(this, MotionToken.DialogIn, cause);
        var ease = Motion.Easing(this, MotionToken.DialogIn);
        var from = Motion.ScaleFrom(this, MotionToken.DialogIn);
        if (Motion.DoubleTo(1, d, ease, from: 0) is { } fade)
        {
            scrim.BeginAnimation(OpacityProperty, fade);
            card.BeginAnimation(OpacityProperty, fade);
            var grow = Motion.DoubleTo(1, d, ease, from: from)!;
            var s = (ScaleTransform)card.RenderTransform;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        card.Loaded += (_, _) =>
        {
            // 最初のフォーカスは中身の最初の部品。スクロールの領域は、そのあとで止まるかどうかを決める(中身に部品が無いときはスクロールの領域へ)。
            // ウィンドウがアクティブでないときは論理フォーカスだけを決める(キーボードフォーカスを動かすと WPF の SetFocus でウィンドウが前面に出る)
            FocusFirst(card);
            entry.Ready = true;
            UpdateScrollFocusable(entry);
            if (!IsFocusWithin(card) && scroll.Focusable) FocusElement(scroll);
        };
        return entry.Done.Task;
    }

    /// <summary>一番上のダイアログを閉じる。</summary>
    public void Close(object? result = null, MotionCause cause = MotionCause.Programmatic)
    {
        if (_stack.Count == 0) return;
        var e = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        UpdateEnabled();
        var d = Motion.Duration(this, MotionToken.DialogOut, cause);
        void Remove()
        {
            Children.Remove(e.Scrim);
            Children.Remove(e.Card);
            if (_stack.Count == 0) Visibility = Visibility.Collapsed;
        }
        if (Motion.DoubleTo(0, d, Motion.Easing(this, MotionToken.DialogOut)) is { } fade)
        {
            fade.Completed += (_, _) => Remove();
            e.Scrim.BeginAnimation(OpacityProperty, fade);
            e.Card.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            Remove();
        }
        if (e.FocusBefore is { } before && before is UIElement { IsVisible: true }) FocusElement(before);
        else if (!IsWindowActive && FocusScope is { } scope && FocusManager.GetFocusedElement(scope) is Visual v && e.Card.IsAncestorOf(v))
            WithKeyboardFocusHeld(() => FocusManager.SetFocusedElement(scope, null)); // 閉じたダイアログの部品に論理フォーカスを残さない
        e.Done.TrySetResult(result);
    }

    /// <summary>Esc の取り消し(中身が IDialogEscape で自分で扱うときは閉じない)。</summary>
    public void CancelTop()
    {
        if (Top is IDialogEscape esc && esc.HandleEscape()) return;
        Close(null, MotionCause.Keyboard);
    }

    /// <summary>スクロールの領域の名前(ui.tsv の dialog.scroll。無ければ仮の文言)。</summary>
    private static string ScrollName =>
        GlintLoop.App.Text.NoticeTexts.Ui.TryGetValue("dialog.scroll", out var s) && !string.IsNullOrEmpty(s) ? s : "ダイアログの内容";

    /// <summary>スクロールできる間だけ、スクロールの領域にフォーカスが当たる(Tab で止まる)。できなくなったら、当たっていたフォーカスは中身の最初の部品へ移す。</summary>
    private void UpdateScrollFocusable(Entry e)
    {
        if (!e.Ready) return;
        var can = e.Scroll.ScrollableHeight > 0;
        if (e.Scroll.Focusable == can) return;
        var had = e.Scroll.IsKeyboardFocused || (FocusScope is { } scope && ReferenceEquals(FocusManager.GetFocusedElement(scope), e.Scroll));
        e.Scroll.Focusable = can;
        e.Scroll.IsTabStop = can; // ScrollViewer の既定は Tab で止まらない
        if (had) FocusFirst(e.Card);
    }

    /// <summary>このダイアログの層のあるウィンドウがアクティブか(キーボードフォーカスを動かしてよいか)。</summary>
    private bool IsWindowActive => Window.GetWindow(this) is { IsActive: true };

    /// <summary>論理フォーカスの範囲(ウィンドウ)。</summary>
    private DependencyObject? FocusScope => FocusManager.GetFocusScope(this);

    /// <summary>
    /// el にフォーカスを当てる。ウィンドウがアクティブなときはキーボードフォーカス、そうでなければ論理フォーカスだけ
    /// (FocusManager.SetFocusedElement。ウィンドウがあとでアクティブになったとき、WPF がそこへキーボードフォーカスを戻す)。
    /// </summary>
    private void FocusElement(IInputElement el)
    {
        if (IsWindowActive)
        {
            Keyboard.Focus(el);
            return;
        }
        // 論理フォーカスを決めると、FocusManager がキーボードフォーカスも動かそうとし(Keyboard.Focus)、SetFocus でウィンドウが前面に出うるので、その間は取り消す
        if (el is DependencyObject d && FocusManager.GetFocusScope(d) is { } scope) WithKeyboardFocusHeld(() => FocusManager.SetFocusedElement(scope, el));
    }

    /// <summary>
    /// container の中の最初の部品(MoveFocus(First) の行き先)へフォーカスを当てる。ウィンドウがアクティブでないときは、
    /// 行き先を WPF の巡回で求め(キーボードフォーカスの移動は取り消す)、論理フォーカスだけを決める。
    /// </summary>
    private void FocusFirst(FrameworkElement container)
    {
        if (IsWindowActive)
        {
            container.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }
        var first = WithKeyboardFocusHeld(() => container.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        if (first is not null) FocusElement(first);
    }

    /// <summary>
    /// action の間、キーボードフォーカスの移動を PreviewGotKeyboardFocus で取り消す(取り消すと WPF は SetFocus を呼ばず、ウィンドウが前面に出ない)。
    /// 取り消した移動のうち最初の行き先を返す(無ければ null)。取り消しはビジュアルの木の根(ウィンドウ)で受け、ダイアログの外の部品(閉じたときの戻し先)への移動も止める。
    /// </summary>
    private IInputElement? WithKeyboardFocusHeld(Action action)
    {
        DependencyObject root = this;
        while (VisualTreeHelper.GetParent(root) is { } parent) root = parent;
        var target = root as UIElement ?? this;
        IInputElement? first = null;
        KeyboardFocusChangedEventHandler cancel = (_, e) =>
        {
            first ??= e.NewFocus;
            e.Handled = true;
        };
        target.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, cancel, handledEventsToo: true);
        try
        {
            action();
        }
        finally
        {
            target.RemoveHandler(Keyboard.PreviewGotKeyboardFocusEvent, cancel);
        }
        return first;
    }

    /// <summary>container の中にフォーカスがあるか(アクティブでないウィンドウでは論理フォーカスで見る)。</summary>
    private bool IsFocusWithin(FrameworkElement container)
    {
        if (container.IsKeyboardFocusWithin) return true;
        if (IsWindowActive || FocusScope is not { } scope) return false;
        return FocusManager.GetFocusedElement(scope) is Visual v && container.IsAncestorOf(v);
    }

    /// <summary>UIA に見せる(AutomationId は DialogHost)。</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateMaxHeight();
    }

    private void UpdateMaxHeight()
    {
        var margin = (double)(TryFindResource("DialogWindowMargin") ?? 0.0);
        foreach (var e in _stack) e.Card.MaxHeight = Math.Max(0, ActualHeight - margin);
    }

    /// <summary>一番上のダイアログだけを操作できるようにする(下のダイアログは見えるが触れない)。</summary>
    private void UpdateEnabled()
    {
        for (var i = 0; i < _stack.Count; i++) _stack[i].Card.IsHitTestVisible = i == _stack.Count - 1;
        for (var i = 0; i < _stack.Count; i++) KeyboardNavigation.SetIsTabStop(_stack[i].Card, false);
    }
}
