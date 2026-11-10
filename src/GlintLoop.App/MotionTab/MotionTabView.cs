// モーションのタブの中身(FR-64・§9.3): タイルの一覧(「モーションなし」・組み込みの 28 個の順)。タイルは内側の幅に 132×2+8 DIP が入るときだけ 2 列(ルックと同じ)。
// タイルのクリックか Enter で ApplyRequested を出す。マウスが載った・キーボードのフォーカスが当たったタイル 1 つだけを「再生中」として PlayingChanged で知らせる。
// 「再生中」の判定は入力から切り離した SetPointerOver・SetKeyboardFocus に集め、テストは入力を使わずにそれを呼ぶ(§11 の Phase 3 の前書き)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Platform;
using GlintLoop.App.Text;

namespace GlintLoop.App.MotionTab;

public sealed class MotionTabView : DockPanel
{
    private readonly IModifierKeys _modifiers;
    private MotionTile? _pointer;
    private MotionTile? _focus;

    /// <summary>modifiers は修飾キーの状態(Enter の判定に使う。Keyboard.Modifiers を直接読まない)。</summary>
    public MotionTabView(IModifierKeys modifiers)
    {
        _modifiers = modifiers;
        SetResourceReference(MarginProperty, "Space2TopThickness");
        LastChildFill = true;

        // ---- タイルの一覧(仮想化しない UniformGrid。外側の ScrollViewer で縦にスクロールする)
        List = new ListBox { SelectionMode = SelectionMode.Single };
        AutomationProperties.SetName(List, NoticeTexts.UiText("motion.list"));
        AutomationProperties.SetAutomationId(List, "MotionGallery");
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
        // 輪(部品の外側 2+2 DIP)が一覧の表示の範囲で切れないよう、タイルの周りに space-1 を取る(ルックのギャラリーと同じ)
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

    public ListBox List { get; }

    public ScrollViewer Scroll { get; }

    /// <summary>タイルを並べるパネル(読み込まれたあと)。</summary>
    public UniformGrid? Grid { get; private set; }

    /// <summary>いまの列の数(1 か 2)。</summary>
    public int Columns { get; private set; } = 2;

    public IEnumerable<MotionTile> Tiles => List.Items.OfType<MotionTile>();

    /// <summary>「再生中」のタイル(マウスが載った、またはキーボードのフォーカスが当たったタイル 1 つ。無ければ null)。</summary>
    public MotionTile? PlayingTile { get; private set; }

    /// <summary>タイルのクリックか Enter(FR-64)。</summary>
    public event Action<MotionTile>? ApplyRequested;

    /// <summary>「再生中」のタイルが変わったとき(null は再生中のタイルが無い)。</summary>
    public event Action<MotionTile?>? PlayingChanged;

    /// <summary>タイルを並べ直す(並びは呼ぶ側が決める)。「再生中」の状態は消す。</summary>
    public void SetTiles(IEnumerable<MotionTile> tiles)
    {
        ClearPlaying();
        List.Items.Clear();
        foreach (var t in tiles)
        {
            t.PreviewMouseLeftButtonUp -= OnTileMouseUp;
            t.PreviewMouseLeftButtonUp += OnTileMouseUp;
            t.MouseEnter -= OnTileMouseEnter;
            t.MouseEnter += OnTileMouseEnter;
            t.MouseLeave -= OnTileMouseLeave;
            t.MouseLeave += OnTileMouseLeave;
            t.IsKeyboardFocusWithinChanged -= OnTileFocusChanged;
            t.IsKeyboardFocusWithinChanged += OnTileFocusChanged;
            List.Items.Add(t);
        }
    }

    /// <summary>マウスがタイルに載った・外れた(入力から切り離した口。マウスの出入りの受け手もここを呼ぶ)。</summary>
    public void SetPointerOver(MotionTile tile, bool over)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (over)
        {
            _pointer = tile;
            SetPlaying(tile);
        }
        else if (ReferenceEquals(_pointer, tile))
        {
            _pointer = null;
            SetPlaying(_focus);
        }
    }

    /// <summary>キーボードのフォーカスがタイルに当たった・外れた(入力から切り離した口。フォーカスの受け手もここを呼ぶ)。</summary>
    public void SetKeyboardFocus(MotionTile tile, bool focused)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (focused)
        {
            _focus = tile;
            SetPlaying(tile);
        }
        else if (ReferenceEquals(_focus, tile))
        {
            _focus = null;
            SetPlaying(_pointer);
        }
    }

    /// <summary>「再生中」の状態を消す(タブが見えなくなったとき・並べ直したとき)。</summary>
    public void ClearPlaying()
    {
        _pointer = null;
        _focus = null;
        SetPlaying(null);
    }

    /// <summary>最後に載った・当たったタイルを再生中にする(マウスとフォーカスが別のタイルにあるときは後の方。仮の決め)。</summary>
    private void SetPlaying(MotionTile? tile)
    {
        if (ReferenceEquals(PlayingTile, tile)) return;
        PlayingTile = tile;
        PlayingChanged?.Invoke(tile);
    }

    /// <summary>
    /// 列の数(§9.3): 左ペインの内側の幅(ペインの幅 − 24 − 縦スクロールバー 16)に 132×2+8 が入るときだけ 2 列。
    /// この部品はペインの内側の余白の中にあるので、自分の幅からスクロールバーの幅を引いた値で判定する(ルックのギャラリーと同じ)。
    /// </summary>
    public void UpdateColumns()
    {
        var inner = ActualWidth - (double)FindResource("ScrollBarWidth");
        var need = 2 * (double)FindResource("TileSize") + (double)FindResource("Space2");
        Columns = ActualWidth <= 0 || inner >= need ? 2 : 1;
        if (Grid is not null) Grid.Columns = Columns;
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is MotionTile t) ApplyRequested?.Invoke(t);
    }

    private void OnTileMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is MotionTile t) SetPointerOver(t, true);
    }

    private void OnTileMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is MotionTile t) SetPointerOver(t, false);
    }

    private void OnTileFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // マウスの押下で得たフォーカスは数えない(押したタイルがマウスを外しても動き続けないよう。フォーカスの輪と同じ判定)
        if (sender is MotionTile t) SetKeyboardFocus(t, (bool)e.NewValue && InputManager.Current.MostRecentInputDevice is not (MouseDevice or StylusDevice));
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _modifiers.Current != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is not DependencyObject d) return;
        var tile = d as MotionTile ?? ItemsControl.ContainerFromElement(List, d) as MotionTile;
        if (tile is null) return;
        List.SelectedItem = tile;
        ApplyRequested?.Invoke(tile);
        e.Handled = true;
    }
}
