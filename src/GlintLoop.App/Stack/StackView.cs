// 右ペインのスタック(FR-22〜FR-25): 選んでいる対象のスタックを、形・ルック・仕上げ・モーションの 4 つのステージに分けて上から並べる。
// 行は目のトグル・ドラッグのつまみ・名前・複製・削除を持つ。選べる行は 1 つ(StackModule が salt で覚える)。空いている場所のクリックで選択を外す。
// 形ステージの回転・切り抜き・余白は先頭に固定で、つまみと複製を持たない。ドラッグの落とし先の計算は StackDrag(座標を引数に取る関数)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GlintLoop.App.Input;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

/// <summary>ドラッグでの並べ替えの落とし先(FR-25)。</summary>
public static class StackDrag
{
    /// <summary>
    /// ステージの中の行の縦の中心 mids(上から)、動かす行 from、ポインターの y から、動かしたあとの番号(ステージの中)を返す。
    /// 動かす行を除いて、中心がポインターより上にある行の数。固定の行(先頭の min 個)より上には置かない。
    /// </summary>
    public static int DropIndex(IReadOnlyList<double> mids, int from, double y, int min)
    {
        var n = 0;
        for (var i = 0; i < mids.Count; i++)
        {
            if (i != from && mids[i] < y) n++;
        }
        return Math.Clamp(n, Math.Min(min, mids.Count - 1), mids.Count - 1);
    }
}

public sealed class StackView : UserControl
{
    private readonly StackModule _module;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _panel;
    private readonly TextBlock _disabled;
    private readonly Dictionary<EffectStage, (TextBlock Header, ListBox List)> _stages = new();
    private readonly List<(ListBoxItem Item, int Row, long Salt)> _rows = new();
    private bool _updating;
    private (ListBox List, int From, int StageStart, int Fixed)? _drag;

    public static readonly EffectStage[] Stages = [EffectStage.Shape, EffectStage.Look, EffectStage.Finish, EffectStage.Motion];

    public StackView(StackModule module)
    {
        _module = module;
        ShortcutScope.SetKind(this, FocusKind.Stack);
        AutomationProperties.SetName(this, StackTexts.StackName);
        _panel = new StackPanel { Background = Brushes.Transparent };
        _panel.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            _module.ClearRowSelection();
        };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
            FocusVisualStyle = null,
            Content = _panel,
        };
        _scroll.SetResourceReference(PaddingProperty, "Space3HorizontalThickness");
        // スクロールの枠はフォーカスを受ける(空いている場所のクリック・Esc の「スタックにフォーカス」)ので、操作できる部品として名前を付ける(AC-21 の (2))
        AutomationProperties.SetName(_scroll, StackTexts.StackName);
        _disabled = new TextBlock { Text = NoticeTexts.UiText("panes.disabled"), TextWrapping = TextWrapping.Wrap };
        _disabled.SetResourceReference(StyleProperty, "TextBody");
        _disabled.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _disabled.SetResourceReference(MarginProperty, "Space3Thickness");
        foreach (var stage in Stages)
        {
            var header = new TextBlock { Text = StackTexts.StageName(stage) };
            header.SetResourceReference(StyleProperty, "TextBodyStrong");
            header.SetResourceReference(MarginProperty, "Space2TopThickness");
            AutomationProperties.SetAutomationId(header, "stack.stage." + stage.ToString().ToLowerInvariant());
            var list = new ListBox { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent, BorderThickness = default };
            list.Template = PlainListTemplate();
            list.ItemsPanel = PlainPanel();
            var itemStyle = new Style(typeof(ListBoxItem), (Style)FindResource("RowItem"));
            itemStyle.Setters.Add(new Setter(MarginProperty, FindResource("Space1Thickness")));
            list.ItemContainerStyle = itemStyle;
            list.Focusable = false;
            AutomationProperties.SetName(list, StackTexts.StageName(stage));
            list.SelectionChanged += (_, _) => OnSelectionChanged(list);
            _stages[stage] = (header, list);
            _panel.Children.Add(header);
            _panel.Children.Add(list);
        }
        Content = _scroll;
    }

    /// <summary>枠もスクロールも持たない一覧の見た目(外側の ScrollViewer がスクロールする)。</summary>
    internal static ControlTemplate PlainListTemplate() =>
        new(typeof(ListBox)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) };

    /// <summary>仮想化しない並べ方(見えていない行にもフォーカスを移せるように、すべての行を作っておく)。</summary>
    internal static ItemsPanelTemplate PlainPanel() => new(new FrameworkElementFactory(typeof(StackPanel)));

    /// <summary>ステージの見出し(テストが順を確かめる)。</summary>
    public TextBlock StageHeader(EffectStage stage) => _stages[stage].Header;

    /// <summary>いま並んでいる行(スタックの番号の順)。</summary>
    public IReadOnlyList<ListBoxItem> RowItems => _rows.Select(r => r.Item).ToList();

    /// <summary>セッションの変更(UI スレッド)。スタックの形が変わったときだけ作り直す。</summary>
    public void Refresh(SessionChange what)
    {
        if ((what & (SessionChange.Document | SessionChange.Selection | SessionChange.Stack | SessionChange.Layers)) != 0) Rebuild();
        UpdateSelection();
    }

    /// <summary>いま並べている行の形(対象と、各行の id・目・salt)。同じなら作り直さない。</summary>
    private string? _shape;

    private static string? ShapeOf(SessionDocument? doc)
    {
        if (doc is null) return null;
        var sb = new System.Text.StringBuilder(doc.Selected.ToString());
        foreach (var e in doc.StackOf(doc.Selected)) sb.Append('|').Append(e.Id).Append(e.Enabled ? '+' : '-').Append(e.Salt);
        return sb.ToString();
    }

    private void Rebuild()
    {
        var shape = ShapeOf(_module.Shell.Session.Document);
        if (shape is not null && shape == _shape) return;
        _shape = shape;
        var hadFocus = IsKeyboardFocusWithin;
        var doc = _module.Shell.Session.Document;
        Content = doc is null ? _disabled : _scroll;
        _updating = true;
        try
        {
            _rows.Clear();
            foreach (var (_, list) in _stages.Values) list.Items.Clear();
            if (doc is null) return;
            var target = doc.Selected;
            var stack = doc.StackOf(target);
            for (var i = 0; i < stack.Count; i++)
            {
                var e = stack[i];
                var def = _module.DefOf(e.Id);
                var stage = def?.Stage ?? EffectStage.Finish;
                var item = MakeRow(target, i, e, def);
                _stages[stage].List.Items.Add(item);
                _rows.Add((item, i, e.Salt));
            }
        }
        finally
        {
            _updating = false;
        }
        if (hadFocus) FocusSelected();
    }

    private ListBoxItem MakeRow(Target target, int row, StackEntry e, EffectDef? def)
    {
        var name = def?.Name ?? e.Id;
        var isFixed = EffectSchema.IsFixedShape(e.Id);
        var item = new ListBoxItem { Tag = row };
        AutomationProperties.SetName(item, name);
        AutomationProperties.SetAutomationId(item, "stack.row." + row);
        var grid = new Grid();
        var hit = (double)FindResource("HitMin");
        foreach (var w in new[] { new GridLength(hit), GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(hit), new GridLength(hit) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        var eye = new ToggleButton { IsChecked = e.Enabled, Content = Icons.Create(e.Enabled ? Icons.Show : Icons.Hide) };
        eye.SetResourceReference(StyleProperty, "IconToggleButton");
        eye.ToolTip = StackTexts.Visible;
        AutomationProperties.SetName(eye, StackTexts.Visible);
        AutomationProperties.SetAutomationId(eye, "stack.row." + row + ".visible");
        // UIA の Toggle は Click を出さないので、Checked と Unchecked で受ける(作るときの値の設定は、つなぐ前なので送らない)
        eye.Checked += (_, _) => _module.Shell.Session.SetEffectEnabled(target, row, true);
        eye.Unchecked += (_, _) => _module.Shell.Session.SetEffectEnabled(target, row, false);
        Grid.SetColumn(eye, 0);
        grid.Children.Add(eye);

        if (!isFixed)
        {
            var grip = new Path { Data = GripGeometry, Stretch = Stretch.Uniform, Cursor = Cursors.SizeNS, VerticalAlignment = VerticalAlignment.Center };
            grip.SetResourceReference(WidthProperty, "IconSize");
            grip.SetResourceReference(HeightProperty, "IconSize");
            grip.SetResourceReference(Shape.FillProperty, "TextSecondaryBrush");
            var gripHost = new Border { Background = Brushes.Transparent, Child = grip, Cursor = Cursors.SizeNS };
            gripHost.SetResourceReference(PaddingProperty, "Space1HorizontalThickness");
            AutomationProperties.SetName(gripHost, StackTexts.DragGrip);
            gripHost.MouseLeftButtonDown += (_, ev) => BeginDrag(gripHost, item, ev);
            gripHost.MouseLeftButtonUp += (_, ev) => EndDrag(gripHost, ev);
            gripHost.LostMouseCapture += (_, _) => _drag = null;
            Grid.SetColumn(gripHost, 1);
            grid.Children.Add(gripHost);
        }

        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "TextBody");
        label.SetResourceReference(MarginProperty, "Space1HorizontalThickness");
        if (!e.Enabled) label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);

        var dup = IconButton(Icons.Duplicate, StackTexts.Duplicate, "stack.row." + row + ".duplicate");
        dup.IsEnabled = !isFixed;
        dup.Click += (_, _) => _module.Notify(_module.Shell.Session.DuplicateEffect(target, row), select: true);
        Grid.SetColumn(dup, 3);
        grid.Children.Add(dup);
        var del = IconButton(Icons.Delete, StackTexts.Delete, "stack.row." + row + ".delete");
        del.Click += (_, _) => _module.Notify(_module.Shell.Session.RemoveEffect(target, row), select: false);
        Grid.SetColumn(del, 4);
        grid.Children.Add(del);

        item.Content = grid;
        return item;
    }

    private Button IconButton(IconDef icon, string name, string id)
    {
        var b = new Button { Content = Icons.Create(icon), ToolTip = name };
        b.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(b, name);
        AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    /// <summary>つまみの形(縦に 2 列×3 段の点)。§9.1 のアイコンの一覧に無いので自作の Geometry で描く。</summary>
    private static readonly Geometry GripGeometry = MakeGrip();

    private static Geometry MakeGrip()
    {
        var g = Geometry.Parse("M5,2 h2 v2 h-2 Z M9,2 h2 v2 h-2 Z M5,7 h2 v2 h-2 Z M9,7 h2 v2 h-2 Z M5,12 h2 v2 h-2 Z M9,12 h2 v2 h-2 Z");
        g.Freeze();
        return g;
    }

    /// <summary>行の選択の見た目を StackModule の選択に合わせる。</summary>
    public void UpdateSelection()
    {
        var sel = _module.SelectedRow;
        _updating = true;
        try
        {
            foreach (var (_, list) in _stages.Values)
            {
                ListBoxItem? want = null;
                foreach (var (item, row, _) in _rows)
                {
                    if (sel is not null && row == sel.Row && list.Items.Contains(item)) want = item;
                }
                list.SelectedItem = want;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnSelectionChanged(ListBox list)
    {
        if (_updating) return;
        if (list.SelectedItem is ListBoxItem item && item.Tag is int row) _module.SelectRow(row);
    }

    /// <summary>選んでいる行にキーボードのフォーカスを置く(無ければスタックの欄)。</summary>
    public void FocusSelected()
    {
        var sel = _module.SelectedRow;
        var item = sel is null ? null : _rows.FirstOrDefault(r => r.Row == sel.Row).Item;
        if (!IsLoaded) return;
        UpdateLayout();
        if (item is not null && item.Focus()) return;
        _scroll.Focus();
    }

    private void BeginDrag(FrameworkElement grip, ListBoxItem item, MouseButtonEventArgs e)
    {
        var doc = _module.Shell.Session.Document;
        if (doc is null || item.Tag is not int row) return;
        var list = (ListBox)ItemsControl.ItemsControlFromItemContainer(item);
        var stack = doc.StackOf(doc.Selected);
        var start = _rows.First(r => list.Items.Contains(r.Item)).Row;
        var fixedCount = 0;
        for (var i = start; i < stack.Count && EffectSchema.IsFixedShape(stack[i].Id); i++) fixedCount++;
        _drag = (list, row - start, start, fixedCount);
        grip.CaptureMouse();
        e.Handled = true;
    }

    private void EndDrag(FrameworkElement grip, MouseButtonEventArgs e)
    {
        if (_drag is not { } d) return;
        _drag = null;
        grip.ReleaseMouseCapture();
        e.Handled = true;
        var mids = new List<double>();
        foreach (ListBoxItem it in d.List.Items)
        {
            var top = it.TranslatePoint(new Point(0, 0), d.List).Y;
            mids.Add(top + it.ActualHeight / 2);
        }
        var to = StackDrag.DropIndex(mids, d.From, e.GetPosition(d.List).Y, d.Fixed);
        if (to == d.From) return;
        var target = _module.Shell.Session.Selected;
        _module.Shell.Session.MoveEffect(target, d.StageStart + d.From, d.StageStart + to);
    }
}
