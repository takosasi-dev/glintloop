// キャンバスの入力の面: 画像の層のいちばん下に全面で置く透明な部品。キーボードのフォーカス(矢印キーのパン。ShortcutScope は Canvas)と左ドラッグのパンを受ける。
// 重ね描きの層(ハンドル・レイヤーの枠)は上にあるので、そちらに当たったポインターはここに来ない(FR-16 の「ハンドルにもレイヤーの移動にも当たっていないとき」)。
// 縦線(SplitLine)も同じく Tab で止まる部品で、UIA の名前を持つ。どちらも見た目は持たない(縦線は 2 DIP の線だけ)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GlintLoop.App.Input;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Viewing;

public sealed class CanvasSurface : Border
{
    /// <summary>UIA の名前(ui.tsv の canvas.surface)。</summary>
    public static string AutomationName => NoticeTexts.UiText("canvas.surface");

    public CanvasSurface()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        KeyboardNavigationEx.MakeTabStop(this);
        ShortcutScope.SetKind(this, FocusKind.Canvas);
        FocusRing.SetIsEnabled(this, true);
        AutomationProperties.SetName(this, AutomationName);
        // フォーカスの輪(外側 2+2 DIP)がキャンバスの領域の切り取りで隠れないよう、その分だけ内側に置く
        SetResourceReference(MarginProperty, "Space1Thickness");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FocusablePeer(this, AutomationControlType.Pane);
}

/// <summary>分割比較の縦線(FR-18)。当たり判定は幅 32 DIP(hit-min)で、真ん中に accent の 2 DIP の線を描く。Tab で止まる。</summary>
public sealed class SplitLine : Border
{
    /// <summary>UIA の名前(ui.tsv の canvas.split_line)。</summary>
    public static string AutomationName => NoticeTexts.UiText("canvas.split_line");

    public SplitLine()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        KeyboardNavigationEx.MakeTabStop(this);
        ShortcutScope.SetKind(this, FocusKind.SplitLine);
        FocusRing.SetIsEnabled(this, true);
        AutomationProperties.SetName(this, AutomationName);
        SetResourceReference(WidthProperty, "HitMin");
        Cursor = System.Windows.Input.Cursors.SizeWE;
        var line = new Rectangle { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        line.SetResourceReference(WidthProperty, "StrokeEmphasis");
        line.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Child = line;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FocusablePeer(this, AutomationControlType.Thumb);
}

/// <summary>Tab で止まる設定(KeyboardNavigation.IsTabStop を明示する)。</summary>
internal static class KeyboardNavigationEx
{
    public static void MakeTabStop(FrameworkElement e) => System.Windows.Input.KeyboardNavigation.SetIsTabStop(e, true);
}

/// <summary>フォーカスを受ける自前の部品の UIA の peer(名前は AutomationProperties.Name)。</summary>
internal sealed class FocusablePeer(FrameworkElement owner, AutomationControlType type) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => type;

    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}
