// 右ペインの見出しの行(§9.3。高さ 32): 左に「レイヤー」の見出し(subtitle)、右端に「すべてリセット」(FR-32)のボタン。
// ボタンの動作(FR-32)は担当 stack の範囲なので、押されたことを ResetAllClicked で外へ渡すだけにする。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Layers;

public sealed class RightHeaderView : DockPanel
{
    public RightHeaderView()
    {
        LastChildFill = false;
        SetResourceReference(MarginProperty, "Space3HorizontalThickness");
        ResetAllButton = new Button { Content = LayerTexts.ResetAll, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(ResetAllButton, "stack.resetAll");
        ResetAllButton.Click += (_, _) => ResetAllClicked?.Invoke();
        SetDock(ResetAllButton, Dock.Right);
        Title = new TextBlock { Text = LayerTexts.Header, VerticalAlignment = VerticalAlignment.Center };
        Title.SetResourceReference(StyleProperty, "TextSubtitle");
        Fx.SetText(Title, "TextPrimaryBrush");
        SetDock(Title, Dock.Left);
        Children.Add(ResetAllButton);
        Children.Add(Title);
    }

    public TextBlock Title { get; }

    public Button ResetAllButton { get; }

    /// <summary>「すべてリセット」を押した。</summary>
    public event Action? ResetAllClicked;
}
