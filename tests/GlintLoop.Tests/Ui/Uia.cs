// UI オートメーションの補助(テストのスレッドから呼ぶ。UiHost.Invoke の中から呼ばない)。
// Root(hwnd) から、Name で探す・操作できる部品(IsKeyboardFocusable で WPF のもの)を集める・矩形を DIP で読む(BoundingRectangle ÷ DPI の倍率)・Toggle・Invoke・Select・SetValue(提供側を UI スレッドで直接呼ぶ。ウィンドウを前面に出さない)。
// ウィンドウの枠(タイトルバーのシステムメニューなど、WPF でない部品)は対象から外す。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;

namespace GlintLoop.Tests.Ui;

public static class Uia
{
    public static AutomationElement Root(IntPtr hwnd) => AutomationElement.FromHandle(hwnd);

    /// <summary>Name が name の最初の要素。無ければ例外。</summary>
    public static AutomationElement Find(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
        ?? throw new InvalidOperationException($"UIA で「{name}」が見つからない");

    public static AutomationElement? TryFind(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    public static AutomationElement FindById(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"UIA で AutomationId「{automationId}」が見つからない");

    /// <summary>操作できる部品(キーボードでフォーカスできて、有効で、見えている WPF の要素)。</summary>
    public static IReadOnlyList<AutomationElement> Focusables(AutomationElement root) =>
        root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new PropertyCondition(AutomationElement.FrameworkIdProperty, "WPF")))
            .Cast<AutomationElement>()
            .ToList();

    /// <summary>当たり判定の矩形(DIP)。</summary>
    public static Rect RectDip(AutomationElement e, double dpiScale)
    {
        var r = e.Current.BoundingRectangle;
        return new Rect(r.X / dpiScale, r.Y / dpiScale, r.Width / dpiScale, r.Height / dpiScale);
    }

    public static ToggleState ToggleStateOf(AutomationElement e) => ((TogglePattern)e.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    /// <summary>
    /// 切り替える。UIA のクライアントの TogglePattern.Toggle はウィンドウを前面に出す(AC-21 の (1)。報告済み)ので、
    /// 対応する WPF の要素の AutomationPeer の提供側(IToggleProvider.Toggle)を UI スレッドの上で直接呼ぶ。
    /// </summary>
    public static void Toggle(AutomationElement e) => CallProvider(e, PatternInterface.Toggle, p => ((IToggleProvider)p).Toggle());

    /// <summary>押す。Toggle と同じ理由で、提供側(IInvokeProvider.Invoke)を UI スレッドの上で直接呼ぶ(ボタンのクリックは WPF が Input の優先度で後から起こす)。</summary>
    public static void Invoke(AutomationElement e) => CallProvider(e, PatternInterface.Invoke, p => ((IInvokeProvider)p).Invoke());

    /// <summary>選ぶ(一覧の項目・横並びの切替)。Toggle と同じ理由で、提供側(ISelectionItemProvider.Select)を UI スレッドの上で直接呼ぶ。</summary>
    public static void Select(AutomationElement e) => CallProvider(e, PatternInterface.SelectionItem, p => ((ISelectionItemProvider)p).Select());

    /// <summary>範囲の値を決める(スライダー)。Toggle と同じ理由で、提供側(IRangeValueProvider.SetValue)を UI スレッドの上で直接呼ぶ。</summary>
    public static void SetValue(AutomationElement e, double value) => CallProvider(e, PatternInterface.RangeValue, p => ((IRangeValueProvider)p).SetValue(value));

    /// <summary>木の 1 段(親の子の中の番号と、照合に使う AutomationId・Name・ControlType)。</summary>
    private readonly record struct Step(int Index, string AutomationId, string Name, string ControlType);

    /// <summary>
    /// e に対応する AutomationPeer を木の中の位置で探し、そのパターンの提供側で call を呼ぶ。
    /// テストのスレッドで、e から上へ RawView の親をたどり、ウィンドウ(NativeWindowHandle を持つ要素)までの各段の「親の WPF の子の中の番号」を記録する。
    /// UI スレッドで、そのウィンドウの HwndSource の RootVisual の AutomationPeer から GetChildren() を同じ番号でたどる
    /// (WPF の UIA の木の子は AutomationPeer の GetChildren() の順そのもの。ウィンドウの枠の部品は WPF でないので番号に数えない)。
    /// 各段で AutomationId・Name・ControlType が UIA の要素と一致することを確かめ、違えば推測せず例外にする。
    /// </summary>
    private static void CallProvider(AutomationElement e, PatternInterface pattern, Action<object> call)
    {
        var walker = TreeWalker.RawViewWalker;
        var steps = new List<Step>();
        var cur = e;
        IntPtr hwnd;
        while (true)
        {
            hwnd = new IntPtr(cur.Current.NativeWindowHandle);
            if (hwnd != IntPtr.Zero) break;
            var parent = walker.GetParent(cur) ?? throw new InvalidOperationException($"UIA で {Describe(e)} の親がたどれない");
            var index = 0;
            var found = false;
            for (var c = walker.GetFirstChild(parent); c is not null; c = walker.GetNextSibling(c))
            {
                if (Automation.Compare(c, cur))
                {
                    found = true;
                    break;
                }
                if (c.Current.FrameworkId == "WPF" && c.Current.NativeWindowHandle == 0) index++;
            }
            if (!found) throw new InvalidOperationException($"UIA で {Describe(cur)} が親の子の中に見つからない");
            steps.Add(new Step(index, cur.Current.AutomationId, cur.Current.Name, cur.Current.ControlType.ProgrammaticName));
            cur = parent;
        }
        if (steps.Count == 0) throw new InvalidOperationException($"{Describe(e)} はウィンドウそのもの(操作の対象にならない)");
        steps.Reverse();
        UiHost.Invoke(() =>
        {
            var root = HwndSource.FromHwnd(hwnd)?.RootVisual as UIElement
                ?? throw new InvalidOperationException($"{Describe(e)} のウィンドウ(0x{hwnd:X})が WPF のウィンドウでない");
            var peer = UIElementAutomationPeer.CreatePeerForElement(root)
                ?? throw new InvalidOperationException("ウィンドウの AutomationPeer が無い");
            foreach (var s in steps)
            {
                var children = peer.GetChildren();
                if (children is null || s.Index >= children.Count)
                    throw new InvalidOperationException($"AutomationPeer の木で「{s.Name}」({s.AutomationId})の位置 {s.Index} に子が無い({children?.Count ?? 0} 個)");
                peer = children[s.Index];
                var type = "ControlType." + peer.GetAutomationControlType();
                if (peer.GetAutomationId() != s.AutomationId || peer.GetName() != s.Name || type != s.ControlType)
                    throw new InvalidOperationException(
                        $"AutomationPeer の木の対応が合わない: UIA は {s.ControlType}「{s.Name}」({s.AutomationId})、peer は {type}「{peer.GetName()}」({peer.GetAutomationId()})");
            }
            var provider = peer.GetPattern(pattern) ?? throw new InvalidOperationException($"{Describe(e)} は {pattern} を持たない");
            call(provider);
        });
    }

    public static string Describe(AutomationElement e) => $"{e.Current.ControlType.ProgrammaticName}「{e.Current.Name}」({e.Current.AutomationId})";
}
