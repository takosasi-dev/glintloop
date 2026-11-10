// 左ペインの「エフェクト」のタブの中身(FR-26): 検索欄と、全エフェクトをステージごと(その中は group ごと)に並べた一覧。項目のクリックか Enter で、
// 選んでいる対象のスタックのそのステージの末尾に既定値・origin=user で足し、足した行を選ぶ。対象が全体のときは回転・切り抜き・余白を押せない表示にして理由をツールチップに出す。
// 検索は名前・id・キーワードへの部分一致(大文字と小文字を区別しない)。絞り込みは EffectSearch(純粋な関数)。モーションのステージは Phase 4 なので項目が無い。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

public static class EffectSearch
{
    /// <summary>名前・id・キーワードのどれかに query を含むもの(大文字と小文字を区別しない)。空や空白だけなら全部。</summary>
    public static IReadOnlyList<EffectDef> Filter(IEnumerable<EffectDef> defs, string? query)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return defs.ToList();
        return defs.Where(d => Has(d.Name, q) || Has(d.Id, q) || d.Keywords.Any(k => Has(k, q))).ToList();
    }

    private static bool Has(string s, string q) => s.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>タブに並べる順: ステージの順、同じステージの中は group の最初に出た順、その中は名前の順。</summary>
    public static IReadOnlyList<EffectDef> Ordered(IEnumerable<EffectDef> defs)
    {
        var list = defs.ToList();
        var groupOrder = new Dictionary<(EffectStage, string), int>();
        foreach (var d in list.OrderBy(d => d.Id, StringComparer.Ordinal))
            groupOrder.TryAdd((d.Stage, d.Group), groupOrder.Count);
        return list.OrderBy(d => EffectSchema.StageOrder(d.Stage))
            .ThenBy(d => EffectSchema.IsFixedShape(d.Id) ? EffectSchema.OrderKey(d) : 10)
            .ThenBy(d => groupOrder[(d.Stage, d.Group)])
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed class EffectsTabView : UserControl
{
    private readonly StackModule _module;
    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly List<(EffectDef Def, ListBoxItem Item)> _items = new();
    private readonly Dictionary<EffectStage, TextBlock> _stageHeaders = new();
    private readonly Dictionary<(EffectStage, string), ListBoxItem> _groupHeaders = new();
    private readonly Dictionary<EffectStage, ListBox> _stageLists = new();

    public EffectsTabView(StackModule module)
    {
        _module = module;
        AutomationProperties.SetName(this, StackTexts.EffectsTab);
        var dock = new DockPanel();
        _search = new TextBox();
        AutomationProperties.SetName(_search, StackTexts.Search);
        AutomationProperties.SetAutomationId(_search, "effects.search");
        _search.SetResourceReference(MarginProperty, "Space2BottomThickness");
        _search.TextChanged += (_, _) => ApplyFilter();
        DockPanel.SetDock(_search, Dock.Top);
        dock.Children.Add(_search);
        _list = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _list,
        };
        dock.Children.Add(scroll);
        Content = dock;
        Build();
    }

    /// <summary>検索欄。</summary>
    public TextBox SearchBox => _search;

    /// <summary>並べた全項目(タブの順)。</summary>
    public IReadOnlyList<(EffectDef Def, ListBoxItem Item)> Items => _items;

    /// <summary>いま見えている項目の id(検索の結果)。</summary>
    public IReadOnlyList<string> VisibleIds => _items.Where(i => i.Item.Visibility == Visibility.Visible).Select(i => i.Def.Id).ToList();

    private void Build()
    {
        var defs = EffectSearch.Ordered(_module.Shell.Catalog.Effects);
        var itemStyle = new Style(typeof(ListBoxItem), (Style)FindResource("RowItem"));
        itemStyle.Setters.Add(new Setter(MarginProperty, FindResource("Space1Thickness")));
        var groupStyle = new Style(typeof(ListBoxItem));
        groupStyle.Setters.Add(new Setter(FocusableProperty, false));
        groupStyle.Setters.Add(new Setter(IsHitTestVisibleProperty, false));
        groupStyle.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        foreach (var stage in StackView.Stages)
        {
            var header = new TextBlock { Text = StackTexts.StageName(stage) };
            header.SetResourceReference(StyleProperty, "TextBodyStrong");
            header.SetResourceReference(MarginProperty, "Space2TopThickness");
            AutomationProperties.SetAutomationId(header, "effects.stage." + stage.ToString().ToLowerInvariant());
            _stageHeaders[stage] = header;
            _list.Children.Add(header);
            // ステージごとに 1 つの一覧(Tab で 1 回止まり、上下の矢印で項目を移る)
            var list = new ListBox { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent, BorderThickness = default, Focusable = false };
            list.Template = StackView.PlainListTemplate();
            list.ItemsPanel = StackView.PlainPanel();
            AutomationProperties.SetName(list, StackTexts.EffectsTab + " " + StackTexts.StageName(stage));
            _stageLists[stage] = list;
            _list.Children.Add(list);
            var stageDefs = defs.Where(d => d.Stage == stage).ToList();
            foreach (var group in stageDefs.Select(d => d.Group).Distinct())
            {
                var text = new TextBlock { Text = group };
                text.SetResourceReference(StyleProperty, "TextCaption");
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                text.SetResourceReference(MarginProperty, "Space1Thickness");
                var g = new ListBoxItem { Content = text, Style = groupStyle };
                _groupHeaders[(stage, group)] = g;
                list.Items.Add(g);
                foreach (var d in stageDefs.Where(d => d.Group == group)) AddItem(list, d, itemStyle);
            }
        }
        ApplyFilter();
    }

    private void AddItem(ListBox list, EffectDef d, Style itemStyle)
    {
        var item = new ListBoxItem { Content = d.Name, Style = itemStyle, Tag = d.Id };
        AutomationProperties.SetName(item, d.Name);
        AutomationProperties.SetAutomationId(item, "effects.item." + d.Id);
        ToolTipService.SetShowOnDisabled(item, true);
        var id = d.Id;
        item.MouseLeftButtonUp += (_, e) =>
        {
            if (item.IsEnabled) _module.AddEffect(id);
            e.Handled = true;
        };
        item.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !item.IsEnabled) return;
            _module.AddEffect(id);
            e.Handled = true;
        };
        list.Items.Add(item);
        _items.Add((d, item));
    }

    private void ApplyFilter()
    {
        var shown = EffectSearch.Filter(_items.Select(i => i.Def), _search.Text).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (d, item) in _items) item.Visibility = shown.Contains(d.Id) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var ((stage, group), header) in _groupHeaders)
            header.Visibility = _items.Any(i => i.Def.Stage == stage && i.Def.Group == group && shown.Contains(i.Def.Id)) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (stage, header) in _stageHeaders)
        {
            header.Visibility = _items.Any(i => i.Def.Stage == stage && shown.Contains(i.Def.Id)) ? Visibility.Visible : Visibility.Collapsed;
            _stageLists[stage].Visibility = header.Visibility;
        }
    }

    /// <summary>対象が全体のとき、回転・切り抜き・余白を押せない表示にする(FR-26)。</summary>
    public void Refresh(SessionChange what)
    {
        var all = _module.Shell.Session.Selected.IsAll;
        foreach (var (d, item) in _items)
        {
            var off = all && EffectSchema.IsFixedShape(d.Id);
            item.IsEnabled = !off;
            item.ToolTip = off ? StackTexts.FixedShapeNotForAll : null;
            AutomationProperties.SetHelpText(item, off ? StackTexts.FixedShapeNotForAll : "");
        }
    }

    /// <summary>Ctrl+F: 検索欄にフォーカスを移す(タブは先に StackModule.SelectEffectsTab で選んでおく)。タブを選んだ直後でも、レイアウトを済ませてから移す。</summary>
    public bool FocusSearch()
    {
        UpdateLayout();
        _search.Focus();
        Keyboard.Focus(_search);
        return true;
    }
}
