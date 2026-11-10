// ルックのタブの中身(FR-33・FR-35・FR-36・§9.3): 上に「ルックの強さ」のスライダー(0〜100%)とその右の「ルックを保存」(副ボタン)、
// その下にタイルの一覧(「ルックなし」・組み込みの 24 個・ユーザーのルックの順)。タイルは内側の幅に 132×2+8 DIP が入るときだけ 2 列。
// タイルのクリックか Enter で ApplyRequested を出す。ユーザーのルックのタイルは右クリック(か Shift+F10・アプリケーションキー)で「名前を変更」「削除」。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Platform;

namespace GlintLoop.App.Looks;

public sealed class LookGalleryView : DockPanel
{
    private readonly IModifierKeys _modifiers;
    private bool _settingStrength;

    /// <summary>modifiers は修飾キーの状態(Enter の判定に使う。Keyboard.Modifiers を直接読まない)。</summary>
    public LookGalleryView(IModifierKeys modifiers)
    {
        _modifiers = modifiers;
        SetResourceReference(MarginProperty, "Space2TopThickness");
        LastChildFill = true;

        // ---- 強さの見出しの行(ラベルと値)
        StrengthLabel = new TextBlock { Text = LooksText.Get("looks.strength") };
        StrengthLabel.SetResourceReference(StyleProperty, "TextBody");
        StrengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        StrengthValue = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        StrengthValue.SetResourceReference(StyleProperty, "TextBody");
        StrengthValue.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var head = new DockPanel();
        DockPanel.SetDock(StrengthValue, Dock.Right);
        head.Children.Add(StrengthValue);
        head.Children.Add(StrengthLabel);
        DockPanel.SetDock(head, Dock.Top);
        Children.Add(head);

        // ---- スライダーと「ルックを保存」
        Strength = new Slider { Minimum = 0, Maximum = 100, Value = 100, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(Strength, LooksText.Get("looks.strength"));
        AutomationProperties.SetAutomationId(Strength, "LookStrength");
        Strength.ValueChanged += (_, e) =>
        {
            UpdateStrengthText();
            if (!_settingStrength) StrengthChanged?.Invoke(e.NewValue);
        };
        Strength.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => StrengthDragStarted?.Invoke()));
        Strength.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => StrengthDragCompleted?.Invoke()));
        SaveButton = new Button { Content = LooksText.Get("looks.save") };
        AutomationProperties.SetName(SaveButton, LooksText.Get("looks.save"));
        SaveButton.SetResourceReference(MarginProperty, "Space2LeftThickness");
        SaveButton.Click += (_, _) => SaveRequested?.Invoke();
        var row = new DockPanel { LastChildFill = true };
        row.SetResourceReference(MarginProperty, "Space2BottomThickness");
        DockPanel.SetDock(SaveButton, Dock.Right);
        row.Children.Add(SaveButton);
        row.Children.Add(Strength);
        DockPanel.SetDock(row, Dock.Top);
        Children.Add(row);

        // ---- タイルの一覧(仮想化しない UniformGrid。外側の ScrollViewer で縦にスクロールする)
        List = new ListBox { SelectionMode = SelectionMode.Single };
        AutomationProperties.SetName(List, LooksText.Get("looks.list"));
        AutomationProperties.SetAutomationId(List, "LookGallery");
        KeyboardNavigation.SetTabNavigation(List, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(List, KeyboardNavigationMode.Contained);
        List.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) };
        var panel = new FrameworkElementFactory(typeof(UniformGrid));
        panel.SetValue(UniformGrid.ColumnsProperty, 2);
        panel.AddHandler(LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            Grid = (UniformGrid)s;
            UpdateColumns();
        }));
        List.ItemsPanel = new ItemsPanelTemplate(panel);
        var itemStyle = new Style(typeof(ListBoxItem), (Style)FindResource("TileItem"));
        // 輪(部品の外側 2+2 DIP)が一覧の表示の範囲で切れないよう、タイルの周りに space-1 を取る(ComponentGallery と同じ)
        itemStyle.Setters.Add(new Setter(MarginProperty, FindResource("Space1Thickness")));
        List.ItemContainerStyle = itemStyle;
        List.PreviewKeyDown += OnListKeyDown;

        Scroll = new ScrollViewer
        {
            Content = List,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            IsTabStop = false,
        };
        // 2 列のタイル(132×2+8)と輪の余白(space-1 を左右)を、ペインの内側の余白(space-3)の中へ space-1 だけ広げて入れる
        var space1 = (double)FindResource("Space1");
        Scroll.Margin = new Thickness(-space1, 0, -space1, 0);
        Children.Add(Scroll);
        SizeChanged += (_, _) => UpdateColumns();
    }

    public TextBlock StrengthLabel { get; }

    public TextBlock StrengthValue { get; }

    public Slider Strength { get; }

    public Button SaveButton { get; }

    public ListBox List { get; }

    public ScrollViewer Scroll { get; }

    /// <summary>タイルを並べるパネル(読み込まれたあと)。</summary>
    public UniformGrid? Grid { get; private set; }

    /// <summary>いまの列の数(1 か 2)。</summary>
    public int Columns { get; private set; } = 2;

    public IEnumerable<LookTile> Tiles => List.Items.OfType<LookTile>();

    /// <summary>タイルのクリックか Enter(FR-34)。</summary>
    public event Action<LookTile>? ApplyRequested;

    public event Action? SaveRequested;

    public event Action<LookTile>? RenameRequested;

    public event Action<LookTile>? DeleteRequested;

    /// <summary>強さのスライダーを動かした(0〜100)。</summary>
    public event Action<double>? StrengthChanged;

    public event Action? StrengthDragStarted;

    public event Action? StrengthDragCompleted;

    /// <summary>タイルを並べ直す(並びは呼ぶ側が決める)。選択と、同じキーのタイルの画像は呼ぶ側が持ち越す。</summary>
    public void SetTiles(IEnumerable<LookTile> tiles)
    {
        List.Items.Clear();
        foreach (var t in tiles)
        {
            t.PreviewMouseLeftButtonUp -= OnTileMouseUp;
            t.PreviewMouseLeftButtonUp += OnTileMouseUp;
            if (t.User is not null) t.ContextMenu = MenuFor(t);
            List.Items.Add(t);
        }
    }

    /// <summary>対象の強さをスライダーに出す(StrengthChanged は出さない)。</summary>
    public void ShowStrength(int value)
    {
        _settingStrength = true;
        try
        {
            Strength.Value = value;
        }
        finally
        {
            _settingStrength = false;
        }
        UpdateStrengthText();
    }

    private void UpdateStrengthText() => StrengthValue.Text = ((int)Math.Round(Strength.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// 列の数(§9.3): 左ペインの内側の幅(ペインの幅 − 24 − 縦スクロールバー 16)に 132×2+8 が入るときだけ 2 列。
    /// この部品はペインの内側の余白の中にあるので、自分の幅からスクロールバーの幅を引いた値で判定する。
    /// </summary>
    public void UpdateColumns()
    {
        var inner = ActualWidth - (double)FindResource("ScrollBarWidth");
        var need = 2 * (double)FindResource("TileSize") + (double)FindResource("Space2");
        Columns = ActualWidth <= 0 || inner >= need ? 2 : 1;
        if (Grid is not null) Grid.Columns = Columns;
    }

    /// <summary>タイルのコンテキストメニューを開く(Shift+F10・アプリケーションキー。開く位置はタイルの左下)。開いたら true。</summary>
    public bool OpenMenu(LookTile tile)
    {
        if (tile.ContextMenu is not { } menu) return false;
        menu.PlacementTarget = tile;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        return true;
    }

    private ContextMenu MenuFor(LookTile tile)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = LooksText.Get("looks.menu.rename") };
        rename.Click += (_, _) => RenameRequested?.Invoke(tile);
        var delete = new MenuItem { Header = LooksText.Get("looks.menu.delete") };
        delete.Click += (_, _) => DeleteRequested?.Invoke(tile);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        // 右クリックで開くときの位置はポインター。キーボードで開くときは OpenMenu がタイルの左下にする(§9.1 の規則 5)
        tile.ContextMenuOpening += (_, _) =>
        {
            menu.PlacementTarget = null;
            menu.Placement = PlacementMode.MousePoint;
        };
        return menu;
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is LookTile t) ApplyRequested?.Invoke(t);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _modifiers.Current != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is not DependencyObject d) return;
        var tile = d as LookTile ?? ItemsControl.ContainerFromElement(List, d) as LookTile;
        if (tile is null) return;
        List.SelectedItem = tile;
        ApplyRequested?.Invoke(tile);
        e.Handled = true;
    }
}
