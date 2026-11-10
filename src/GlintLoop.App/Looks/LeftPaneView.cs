// 左ペインの枠(§9.3): ルック・エフェクト・モーションの 3 つのタブ。ルックのタブの中身はギャラリー(FR-33)、エフェクトのタブの中身は
// 担当 stack が EffectsContent に入れる(FR-26)。モーションのタブは Phase 4 で中身を入れるので、いまは空。タブの切替は動かさない(§9.1 の規則 1)。
// 文書が無い間(ウェルカム画面)はタブを出さず、「画像を取り込むと使えます」(ui.tsv の panes.disabled)を出す。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlintLoop.App.Text;

namespace GlintLoop.App.Looks;

/// <summary>左ペインのタブ(Alt+1・Alt+2・Alt+3 の順)。</summary>
public enum LeftPaneTab { Looks, Effects, Motion }

public sealed class LeftPaneView : Grid
{
    public LeftPaneView()
    {
        Tabs = new TabControl();
        AutomationProperties.SetName(Tabs, LooksText.Get("left.tabs"));
        AutomationProperties.SetAutomationId(Tabs, "LeftPaneTabs");
        LooksTab = Tab("left.tab.looks", "LeftTabLooks");
        EffectsTab = Tab("left.tab.effects", "LeftTabEffects");
        MotionTab = Tab("left.tab.motion", "LeftTabMotion");
        Tabs.Items.Add(LooksTab);
        Tabs.Items.Add(EffectsTab);
        Tabs.Items.Add(MotionTab);
        Tabs.SelectedIndex = 0;
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs)) TabChanged?.Invoke(SelectedTab);
        };

        DisabledText = new TextBlock
        {
            Text = NoticeTexts.UiText("panes.disabled"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        DisabledText.SetResourceReference(StyleProperty, "TextBody");
        DisabledText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Children.Add(Tabs);
        Children.Add(DisabledText);
        SetHasDocument(false);
    }

    private static TabItem Tab(string textKey, string automationId)
    {
        var t = new TabItem { Header = LooksText.Get(textKey) };
        AutomationProperties.SetAutomationId(t, automationId);
        return t;
    }

    public TabControl Tabs { get; }

    public TabItem LooksTab { get; }

    public TabItem EffectsTab { get; }

    public TabItem MotionTab { get; }

    /// <summary>文書が無い間に出す「画像を取り込むと使えます」。</summary>
    public TextBlock DisabledText { get; }

    /// <summary>ルックのタブの中身(ギャラリー)。</summary>
    public object? LooksContent
    {
        get => LooksTab.Content;
        set => LooksTab.Content = value;
    }

    /// <summary>エフェクトのタブの中身の差し込み口(担当 stack が FR-26 の一覧を入れる)。</summary>
    public object? EffectsContent
    {
        get => EffectsTab.Content;
        set => EffectsTab.Content = value;
    }

    /// <summary>モーションのタブの中身の差し込み口(Phase 4)。</summary>
    public object? MotionContent
    {
        get => MotionTab.Content;
        set => MotionTab.Content = value;
    }

    public LeftPaneTab SelectedTab => Tabs.SelectedIndex switch
    {
        1 => LeftPaneTab.Effects,
        2 => LeftPaneTab.Motion,
        _ => LeftPaneTab.Looks,
    };

    /// <summary>タブが切り替わったとき。</summary>
    public event Action<LeftPaneTab>? TabChanged;

    /// <summary>タブを選ぶ(Alt+1〜3、取り込みのあとのルックのタブ、Ctrl+F の検索欄のエフェクトのタブ)。</summary>
    public void SelectTab(LeftPaneTab tab) => Tabs.SelectedIndex = (int)tab;

    /// <summary>文書があるか。無い間はタブを隠して panes.disabled の文を出す。</summary>
    public bool HasDocument { get; private set; }

    public void SetHasDocument(bool has)
    {
        HasDocument = has;
        Tabs.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DisabledText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }
}
