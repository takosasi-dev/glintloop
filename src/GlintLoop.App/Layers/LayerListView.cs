// 右ペインのレイヤーの一覧(FR-46・FR-52・FR-53・FR-54・§9.3): 先頭に固定の行「全体」、続けて最前面から順にレイヤーの行(40 DIP)。
// レイヤーの行は左から 表示のトグル(目)・サムネイル(32 四方に縦横比を保って収め、透明な部分に市松模様)・名前(1 行で「…」に省略し、全文を ToolTip と UIA の Name に)・複製・削除。
// 見た目は部品の表の「スタックの行」と「選択中のタイル・行」(RowItem)。表示がオフの行はサムネイルと名前を text-secondary で描く。行のドラッグで並べ替え(ドロップ先は accent の 2 DIP の線)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GlintLoop.App.Input;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Layers;

/// <summary>一覧の行と対象の対応、行のドラッグのドロップ先(座標を引数に取る純粋な関数)。</summary>
public static class LayerListMath
{
    /// <summary>対象の行の番号(0 が「全体」、1 が最前面、layerCount が土台)。</summary>
    public static int RowOf(Target t, int layerCount) => t.IsAll ? 0 : layerCount - t.LayerIndex;

    /// <summary>行の番号の対象。</summary>
    public static Target TargetOfRow(int row, int layerCount) => row <= 0 ? Target.All : Target.OfLayer(layerCount - row);

    /// <summary>
    /// ポインターの縦の位置 y と各行の中央の y(行の番号の順。0 は「全体」)から、ドロップ先の隙間を返す。隙間 g は行 g の直前
    /// (行の数のときは最後の行の後)。「全体」の行の上(隙間 0)へは置けないので 1 以上にする。
    /// </summary>
    public static int DropGap(IReadOnlyList<double> rowMidY, double y)
    {
        for (var r = 1; r < rowMidY.Count; r++)
        {
            if (y < rowMidY[r]) return r;
        }
        return Math.Max(1, rowMidY.Count);
    }

    /// <summary>
    /// 番号 fromLayer のレイヤーを隙間 gap へ落としたときの、新しいレイヤーの番号。動かない(元の位置の上か下の隙間)ときは null。
    /// </summary>
    public static int? DropTarget(int layerCount, int fromLayer, int gap)
    {
        var fromRow = layerCount - fromLayer;
        gap = Math.Clamp(gap, 1, layerCount + 1);
        if (gap == fromRow || gap == fromRow + 1) return null;
        var newRow = gap > fromRow ? gap - 1 : gap;
        return layerCount - newRow;
    }
}

/// <summary>サムネイルの枠(UIA に「サムネイル」の画像として見せる)。</summary>
public sealed class LayerThumbnailBox : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(LayerThumbnailBox owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(LayerThumbnailBox);

        protected override bool IsControlElementCore() => true;
    }
}

/// <summary>一覧の 1 行(ListBoxItem)。「全体」の行は名前だけを持つ。</summary>
public sealed class LayerRowItem : ListBoxItem
{
    private readonly Grid _grid = new();
    private readonly Grid _nameCell = new();
    private LayerThumbnail? _shown;
    private bool _syncing;
    private Grid? _thumbArea;
    private Rectangle? _checker;
    private Image? _image;
    private Border? _placeholder;

    public LayerRowItem(bool isAll)
    {
        IsAll = isAll;
        SetResourceReference(StyleProperty, "RowItem");
        SetResourceReference(HeightProperty, "LayerRowHeight");
        var gap = (double)AppResources.LayoutTokens["Space1"];
        Margin = new Thickness(0, 0, 0, gap);
        NameText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        NameText.SetResourceReference(StyleProperty, "TextBody");
        _nameCell.Children.Add(NameText);
        Content = _grid;
        if (isAll)
        {
            NameText.Text = LayerTexts.All;
            AutomationProperties.SetName(this, LayerTexts.All);
            Fx.SetText(NameText, "TextPrimaryBrush");
            _grid.Children.Add(_nameCell);
            return;
        }

        double T(string key) => (double)AppResources.LayoutTokens[key];
        foreach (var w in new[] { T("LayerRowToggle"), gap, T("LayerRowThumb"), gap })
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = T("LayerRowNameMin") });
        foreach (var w in new[] { gap, T("LayerRowDuplicate"), gap, T("LayerRowDelete") })
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        VisibleToggle = new ToggleButton();
        VisibleToggle.SetResourceReference(StyleProperty, "IconToggleButton");
        AutomationProperties.SetName(VisibleToggle, LayerTexts.Visible);
        VisibleToggle.ToolTip = LayerTexts.Visible;
        Grid.SetColumn(VisibleToggle, 0);
        VisibleToggle.Checked += OnToggle;
        VisibleToggle.Unchecked += OnToggle;

        Thumb = new LayerThumbnailBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        Thumb.SetResourceReference(HeightProperty, "LayerRowThumb");
        Thumb.SetResourceReference(Border.BorderThicknessProperty, "StrokeThickness");
        AutomationProperties.SetName(Thumb, LayerTexts.Thumbnail);
        _placeholder = new Border();
        Fx.SetFill(_placeholder, "Surface3Brush");
        _thumbArea = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _checker = new Rectangle { Fill = CheckerBrush() };
        _image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _thumbArea.Children.Add(_checker);
        _thumbArea.Children.Add(_image);
        var thumbInner = new Grid();
        thumbInner.Children.Add(_placeholder);
        thumbInner.Children.Add(_thumbArea);
        Thumb.Child = thumbInner;
        Grid.SetColumn(Thumb, 2);

        Grid.SetColumn(_nameCell, 4);

        DuplicateButton = MakeIconButton(Icons.Duplicate);
        Grid.SetColumn(DuplicateButton, 6);
        DeleteButton = MakeIconButton(Icons.Delete);
        Grid.SetColumn(DeleteButton, 8);

        _grid.Children.Add(VisibleToggle);
        _grid.Children.Add(Thumb);
        _grid.Children.Add(_nameCell);
        _grid.Children.Add(DuplicateButton);
        _grid.Children.Add(DeleteButton);
    }

    private static Button MakeIconButton(IconDef icon)
    {
        var b = new Button { Content = Icons.Create(icon), ToolTip = icon.Name };
        b.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(b, icon.Name);
        return b;
    }

    /// <summary>市松模様(キャンバスの背景の 2 色のトークン CanvasChecker1・2 の、CanvasCheckerCell 四方の升目)。</summary>
    private static DrawingBrush CheckerBrush()
    {
        var r = AppResources.LayoutTokens;
        var cell = (double)r["CanvasCheckerCell"];
        var dark = AppResources.DarkTokens;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing((Brush)dark["CanvasChecker1Brush"], null, new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2))));
        var g = new GeometryGroup();
        g.Children.Add(new RectangleGeometry(new Rect(cell, 0, cell, cell)));
        g.Children.Add(new RectangleGeometry(new Rect(0, cell, cell, cell)));
        group.Children.Add(new GeometryDrawing((Brush)dark["CanvasChecker2Brush"], null, g));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell * 2, cell * 2),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    public bool IsAll { get; }

    /// <summary>この行のレイヤーの番号(「全体」の行は −1)。</summary>
    public int LayerIndex { get; private set; } = -1;

    public TextBlock NameText { get; }

    public ToggleButton? VisibleToggle { get; }

    public LayerThumbnailBox? Thumb { get; }

    public Button? DuplicateButton { get; }

    public Button? DeleteButton { get; }

    /// <summary>表示のトグルを操作した(マウス・キー・UIA の Toggle。値の反映のときは出さない)。</summary>
    public event Action<LayerRowItem, bool>? VisibleChanged;

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (!_syncing) VisibleChanged?.Invoke(this, VisibleToggle!.IsChecked == true);
    }

    /// <summary>名前の編集欄(編集中だけ)。</summary>
    public TextBox? NameEditor { get; private set; }

    /// <summary>いま出しているサムネイル(無ければ単色の枠)。</summary>
    public LayerThumbnail? ShownThumbnail => _shown;

    /// <summary>サムネイルの画像を置く領域(縦横比を保って枠に収めた大きさ)。</summary>
    public FrameworkElement ThumbnailArea => _thumbArea!;

    /// <summary>画像の後ろに市松模様を敷いているか。</summary>
    public bool ShowsChecker => _thumbArea is { Visibility: Visibility.Visible } && _checker?.Fill is DrawingBrush;

    /// <summary>行の中身をレイヤーに合わせる。</summary>
    public void Update(Layer layer, int index, bool canDelete, LayerThumbnail? thumb)
    {
        LayerIndex = index;
        if (NameText.Text != layer.Name) NameText.Text = layer.Name;
        NameText.ToolTip = layer.Name;
        AutomationProperties.SetName(this, layer.Name);
        var textKey = layer.Visible ? "TextPrimaryBrush" : "TextSecondaryBrush";
        Fx.SetText(NameText, textKey);
        Fx.SetStroke(Thumb!, textKey);
        _syncing = true;
        VisibleToggle!.IsChecked = layer.Visible;
        _syncing = false;
        var glyph = layer.Visible ? Icons.Show : Icons.Hide;
        if (VisibleToggle.Tag as IconDef != glyph)
        {
            VisibleToggle.Content = Icons.Create(glyph);
            VisibleToggle.Tag = glyph;
        }
        DeleteButton!.IsEnabled = canDelete;
        _thumbArea!.Opacity = layer.Visible ? 1 : (double)AppResources.LayoutTokens["DisabledOpacity"];
        ShowThumbnail(thumb);
    }

    private void ShowThumbnail(LayerThumbnail? thumb)
    {
        if (ReferenceEquals(thumb, _shown)) return;
        _shown = thumb;
        if (thumb is null)
        {
            _thumbArea!.Visibility = Visibility.Collapsed;
            _image!.Source = null;
            _placeholder!.Visibility = Visibility.Visible;
            return;
        }
        // 32 DIP 四方に縦横比を保って収める
        var box = (double)AppResources.LayoutTokens["LayerRowThumb"] - 2 * (double)AppResources.LayoutTokens["Stroke"];
        var k = Math.Min(box / thumb.Width, box / thumb.Height);
        _thumbArea!.Width = thumb.Width * k;
        _thumbArea.Height = thumb.Height * k;
        _image!.Source = thumb.Bitmap;
        _thumbArea.Visibility = Visibility.Visible;
        _placeholder!.Visibility = Visibility.Collapsed;
        var d = Motion.Duration(this, MotionToken.ThumbIn);
        if (Motion.DoubleTo(1, d, Motion.Easing(this, MotionToken.ThumbIn), from: 0) is { } fade) _image.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>名前の編集を始める(F2・ダブルクリック。FR-54)。</summary>
    public TextBox BeginEdit()
    {
        if (NameEditor is { } existing) return existing;
        var box = new TextBox { Text = NameText.Text };
        AutomationProperties.SetName(box, LayerTexts.Name);
        NameEditor = box;
        NameText.Visibility = Visibility.Hidden;
        _nameCell.Children.Add(box);
        box.Loaded += (_, _) =>
        {
            Keyboard.Focus(box);
            box.SelectAll();
        };
        return box;
    }

    /// <summary>名前の編集を終える(欄を外す)。編集中だったときは欄の文字を返す。</summary>
    public string? EndEdit()
    {
        if (NameEditor is not { } box) return null;
        NameEditor = null;
        _nameCell.Children.Remove(box);
        NameText.Visibility = Visibility.Visible;
        return box.Text;
    }
}

/// <summary>右ペインのレイヤーの一覧。行の操作はイベントで外(LayersModule)へ渡す。</summary>
public sealed class LayerListView : Grid
{
    private readonly List<LayerRowItem> _rows = [];
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _dropLine = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _disabled = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _syncing;
    private LayerRowItem? _pressRow;
    private Point _pressAt;
    private bool _dragging;
    private int _layerCount;

    public LayerListView()
    {
        SetResourceReference(MarginProperty, "Space3HorizontalThickness");
        List = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            Focusable = false,
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        List.Template = ListTemplate();
        AutomationProperties.SetName(List, LayerTexts.List);
        ShortcutScope.SetKind(List, FocusKind.LayerList);
        List.IsTextSearchEnabled = false;
        VirtualizingPanel.SetIsVirtualizing(List, false);
        KeyboardNavigation.SetTabNavigation(List, KeyboardNavigationMode.Continue);
        List.SelectionChanged += OnSelectionChanged;
        List.PreviewMouseLeftButtonDown += OnPreviewDown;
        List.PreviewMouseMove += OnPreviewMove;
        List.PreviewMouseLeftButtonUp += OnPreviewUp;
        List.LostMouseCapture += (_, _) => EndDrag();
        Children.Add(List);

        _dropLine.SetResourceReference(HeightProperty, "StrokeEmphasis");
        _dropLine.SetResourceReference(Shape.FillProperty, "AccentBrush");
        _overlay.Children.Add(_dropLine);
        Children.Add(_overlay);

        _disabled.Text = NoticeTexts.UiText("panes.disabled");
        _disabled.SetResourceReference(StyleProperty, "TextBody");
        Fx.SetText(_disabled, "TextSecondaryBrush");
        Children.Add(_disabled);
    }

    private static ControlTemplate ListTemplate()
    {
        var t = new ControlTemplate(typeof(ListBox));
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.FocusableProperty, false);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, false);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        t.VisualTree = scroll;
        return t;
    }

    public ListBox List { get; }

    /// <summary>行(先頭が「全体」、続けて最前面から)。</summary>
    public IReadOnlyList<LayerRowItem> Rows => _rows;

    /// <summary>行のクリックか矢印キー以外の一覧の操作で対象を選んだ。</summary>
    public event Action<Target>? RowSelected;

    /// <summary>選んでいる行をもう一度クリックした(スタックの行の選択からレイヤーの設定へ戻す。FR-48)。</summary>
    public event Action<Target>? SelectedRowClicked;

    public event Action<int, bool>? VisibleToggled;

    public event Action<int>? DuplicateClicked;

    public event Action<int>? DeleteClicked;

    /// <summary>名前の編集を確定した(番号と欄の文字)。</summary>
    public event Action<int, string>? RenameCommitted;

    /// <summary>行のドラッグで並べ替えを求めた(元の番号と新しい番号)。</summary>
    public event Action<int, int>? MoveRequested;

    /// <summary>名前を編集している行(無ければ null)。</summary>
    public LayerRowItem? EditingRow => _rows.FirstOrDefault(r => r.NameEditor is not null);

    /// <summary>文書に合わせて行を並べ直す(文書が無いときは「画像を取り込むと使えます」)。</summary>
    public void Refresh(SessionDocument? doc, Func<int, LayerThumbnail?> thumbnail)
    {
        _syncing = true;
        try
        {
            if (doc is null)
            {
                CancelEdit();
                List.Items.Clear();
                _rows.Clear();
                _layerCount = 0;
                List.Visibility = Visibility.Collapsed;
                _disabled.Visibility = Visibility.Visible;
                return;
            }
            List.Visibility = Visibility.Visible;
            _disabled.Visibility = Visibility.Collapsed;
            var n = doc.Layers.Count;
            var focusWithin = List.IsKeyboardFocusWithin;
            var rebuilt = false;
            if (n != _layerCount || _rows.Count != n + 1)
            {
                rebuilt = true;
                CancelEdit();
                List.Items.Clear();
                _rows.Clear();
                var all = new LayerRowItem(true);
                _rows.Add(all);
                List.Items.Add(all);
                for (var r = 1; r <= n; r++)
                {
                    var row = new LayerRowItem(false);
                    Hook(row);
                    _rows.Add(row);
                    List.Items.Add(row);
                }
                _layerCount = n;
            }
            for (var r = 1; r <= n; r++)
            {
                var i = n - r;
                _rows[r].Update(doc.Layers[i], i, n > 1, thumbnail(i));
            }
            var sel = _rows[LayerListMath.RowOf(doc.Selected, n)];
            if (!ReferenceEquals(List.SelectedItem, sel)) List.SelectedItem = sel;
            sel.BringIntoView();
            // 行を作り直したときは、外した行のフォーカスがまだ残って見える(フォーカスの移り先は後で決まる)ので、必ず選んだ行へ当て直す
            if (focusWithin && (rebuilt || !List.IsKeyboardFocusWithin)) FocusSelected();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>選んでいる行にキーボードフォーカスを移す。</summary>
    public void FocusSelected()
    {
        if (List.SelectedItem is not LayerRowItem row) return;
        // 作り直した直後の行は、まだ見える木に入っていない(フォーカスを受けられない)ので、先にレイアウトを済ませる
        if (!row.IsVisible) List.UpdateLayout();
        row.BringIntoView();
        Keyboard.Focus(row);
    }

    private void Hook(LayerRowItem row)
    {
        row.VisibleChanged += (r, on) => VisibleToggled?.Invoke(r.LayerIndex, on);
        row.DuplicateButton!.Click += (_, _) => DuplicateClicked?.Invoke(row.LayerIndex);
        row.DeleteButton!.Click += (_, _) => DeleteClicked?.Invoke(row.LayerIndex);
        row.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && IsWithin(e.OriginalSource as DependencyObject, row.NameText))
            {
                BeginEdit(row);
                e.Handled = true;
            }
        };
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || List.SelectedItem is not LayerRowItem row) return;
        RowSelected?.Invoke(LayerListMath.TargetOfRow(_rows.IndexOf(row), _layerCount));
    }

    // ------------------------------------------------ 名前の編集(FR-54)

    /// <summary>行の名前の編集を始める。</summary>
    public void BeginEdit(LayerRowItem row)
    {
        if (row.IsAll) return;
        if (EditingRow is { } other && other != row) CommitEdit();
        var box = row.BeginEdit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitEdit();
            e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (row.NameEditor == box) CommitEdit();
        };
    }

    /// <summary>編集を確定する(Enter・フォーカスを失ったとき)。</summary>
    public void CommitEdit()
    {
        if (EditingRow is not { } row) return;
        var hadFocus = row.NameEditor!.IsKeyboardFocusWithin;
        var text = row.EndEdit()!;
        if (hadFocus) Keyboard.Focus(row);
        RenameCommitted?.Invoke(row.LayerIndex, text);
    }

    /// <summary>編集を取り消す(Esc)。</summary>
    public void CancelEdit()
    {
        if (EditingRow is not { } row) return;
        var hadFocus = row.NameEditor!.IsKeyboardFocusWithin;
        row.EndEdit();
        if (hadFocus) Keyboard.Focus(row);
    }

    // ------------------------------------------------ クリックと行のドラッグ(FR-52)

    private LayerRowItem? RowAt(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is LayerRowItem r) return r;
            if (d == List) return null;
        }
        return null;
    }

    private static bool IsWithin(DependencyObject? d, DependencyObject ancestor)
    {
        for (; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d == ancestor) return true;
        }
        return false;
    }

    private static bool IsInControl(DependencyObject? d, LayerRowItem row)
    {
        for (; d is not null && d != row; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or TextBoxBase) return true;
        }
        return false;
    }

    private void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowAt(e.OriginalSource as DependencyObject);
        if (row is null || IsInControl(e.OriginalSource as DependencyObject, row)) return;
        if (e.ClickCount <= 1 && row.IsSelected) SelectedRowClicked?.Invoke(LayerListMath.TargetOfRow(_rows.IndexOf(row), _layerCount));
        if (!row.IsAll)
        {
            _pressRow = row;
            _pressAt = e.GetPosition(this);
        }
    }

    private void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (_pressRow is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            List.CaptureMouse();
        }
        ShowDropLine(DropGapAt(p.Y));
        e.Handled = true;
    }

    private void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressRow is { } row && _dragging)
        {
            var gap = DropGapAt(e.GetPosition(this).Y);
            var from = row.LayerIndex;
            EndDrag();
            if (LayerListMath.DropTarget(_layerCount, from, gap) is { } to) MoveRequested?.Invoke(from, to);
            e.Handled = true;
            return;
        }
        EndDrag();
    }

    private void EndDrag()
    {
        var wasDragging = _dragging;
        _pressRow = null;
        _dragging = false;
        _dropLine.Visibility = Visibility.Collapsed;
        if (wasDragging && List.IsMouseCaptured) List.ReleaseMouseCapture();
    }

    /// <summary>この部品の座標の y から、ドロップ先の隙間。</summary>
    public int DropGapAt(double y) =>
        LayerListMath.DropGap(_rows.Select(r => r.TranslatePoint(new Point(0, r.ActualHeight / 2), this).Y).ToList(), y);

    private void ShowDropLine(int gap)
    {
        if (_rows.Count == 0) return;
        double y;
        if (gap < _rows.Count) y = _rows[gap].TranslatePoint(new Point(), this).Y;
        else
        {
            var last = _rows[^1];
            y = last.TranslatePoint(new Point(0, last.ActualHeight), this).Y;
        }
        var line = (double)AppResources.LayoutTokens["StrokeEmphasis"];
        var gapH = (double)AppResources.LayoutTokens["Space1"];
        Canvas.SetTop(_dropLine, y - gapH / 2 - line / 2);
        _dropLine.Width = ActualWidth;
        _dropLine.Visibility = Visibility.Visible;
    }
}
