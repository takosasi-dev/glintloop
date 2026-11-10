// メインウィンドウの組み立て: 4 か所のペインの境界、ペインの幅と縦の配分(PaneLayout)、通知バー・ダイアログの層・コマンドバー・キャンバス(CanvasView)の差し込み、ウェルカム画面のときの無効表示。
// 中身の領域(*Host)は後の担当が Content に UserControl を入れる。ウィンドウの背景(Mica か不透明)は ThemeManager が決める。
using System.Windows;
using System.Windows.Controls;
using GlintLoop.App.CanvasLayers;
using GlintLoop.App.Input;


namespace GlintLoop.App.Shell;

public partial class MainWindow : Window
{
    private readonly PaneLayout.Metrics _m;
    private RightPaneHeights? _heights;

    public MainWindow(NoticeBar noticeBar, DialogHost dialogs, CommandBarView commandBar, CanvasView canvas)
    {
        InitializeComponent();
        _m = PaneLayout.Metrics.From(Theme.AppResources.LayoutTokens);
        NoticeBar = noticeBar;
        Dialogs = dialogs;
        CommandBar = commandBar;
        NoticeBarHost.Content = noticeBar;
        DialogLayerHost.Content = dialogs;
        CommandBarHost.Content = commandBar;
        CanvasView = canvas;
        CanvasHost.Content = canvas;

        LeftSplitter = new PaneSplitter(Orientation.Vertical, "左ペインの境界");
        RightSplitter = new PaneSplitter(Orientation.Vertical, "右ペインの境界");
        LayersStackSplitter = new PaneSplitter(Orientation.Horizontal, "レイヤーの一覧とスタックの境界");
        StackParamsSplitter = new PaneSplitter(Orientation.Horizontal, "スタックとパラメータ欄の境界");
        Grid.SetColumn(LeftSplitter, 1);
        Grid.SetColumn(RightSplitter, 3);
        MiddleGrid.Children.Add(LeftSplitter);
        MiddleGrid.Children.Add(RightSplitter);
        Grid.SetRow(LayersStackSplitter, 2);
        Grid.SetRow(StackParamsSplitter, 4);
        RightGrid.Children.Add(LayersStackSplitter);
        RightGrid.Children.Add(StackParamsSplitter);
        LeftSplitter.Moved += (_, d) => SetPaneWidths(LeftPaneWidth + d, RightPaneWidth, PaneSide.Left);
        RightSplitter.Moved += (_, d) => SetPaneWidths(LeftPaneWidth, RightPaneWidth - d, PaneSide.Right);
        LayersStackSplitter.Moved += (_, d) => MoveVertical(0, d);
        StackParamsSplitter.Moved += (_, d) => MoveVertical(1, d);

        // 右ペインの高さは親のセル(MiddleGrid)から取る。RightGrid は行の高さを固定の値で持つので、セルより小さく並べられても
        // 自分の大きさが縮まず(WPF は望む大きさより小さく並べた要素を望む大きさのまま切り抜く)、SizeChanged も ActualHeight も追わない
        MiddleGrid.SizeChanged += (_, _) => UpdateVertical();
        SizeChanged += (_, _) => SetPaneWidths(LeftPaneWidth, RightPaneWidth, PaneSide.Right);
        Loaded += OnFirstLoaded;
    }

    private Size? _clientRequest;

    /// <summary>ウィンドウの枠の分(外寸 − クライアント領域。DIP)。最初のレイアウトのあとに測る。</summary>
    private Vector? _frame;

    /// <summary>
    /// クライアント領域(枠とタイトルバーを除く)の大きさで、ウィンドウの大きさを決める(§9.3 の最小と既定はクライアント領域で測る)。
    /// 枠の大きさは最初のレイアウトのあとに測って足す(最初のレイアウトのあとに呼んだときは、測った枠の分をその場で足す)。最小の大きさも同じく枠の分を足す。
    /// </summary>
    public void RequestClientSize(Size client)
    {
        _clientRequest = client;
        var f = _frame ?? default;
        Width = client.Width + f.X;
        Height = client.Height + f.Y;
    }

    /// <summary>いまのクライアント領域の大きさ(DIP)。</summary>
    public Size ClientSize => new(RootLayer.ActualWidth, RootLayer.ActualHeight);

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        var dx = Math.Max(0, ActualWidth - RootLayer.ActualWidth);
        var dy = Math.Max(0, ActualHeight - RootLayer.ActualHeight);
        _frame = new Vector(dx, dy);
        var r = Theme.AppResources.LayoutTokens;
        MinWidth = (double)r["WindowMinWidth"] + dx;
        MinHeight = (double)r["WindowMinHeight"] + dy;
        if (_clientRequest is { } c)
        {
            Width = c.Width + dx;
            Height = c.Height + dy;
        }
        // 枠の分を足す前の高さで作った配分を捨て、直した大きさの右ペインで初期の配分を作り直す(§9.3。AC-29 の (2) の (176, 220, 220))
        UpdateLayout();
        _heights = null;
        UpdateVertical();
    }

    public NoticeBar NoticeBar { get; }

    public DialogHost Dialogs { get; }

    public CommandBarView CommandBar { get; }

    /// <summary>キャンバスの領域の中身(画像の層と重ね描きの層)。</summary>
    public CanvasView CanvasView { get; }

    public PaneSplitter LeftSplitter { get; }

    public PaneSplitter RightSplitter { get; }

    public PaneSplitter LayersStackSplitter { get; }

    public PaneSplitter StackParamsSplitter { get; }

    public double LeftPaneWidth => LeftColumn.Width.Value;

    public double RightPaneWidth => RightColumn.Width.Value;

    /// <summary>右ペインの縦の配分(レイアウトが一度済むまでは null)。</summary>
    public RightPaneHeights? RightHeights => _heights;

    /// <summary>左右のペインの幅を、§9.3 の制限に収めて入れる(moving は縮める側の優先)。</summary>
    public void SetPaneWidths(double left, double right, PaneSide moving)
    {
        var width = EditorGrid.ActualWidth > 0 ? EditorGrid.ActualWidth : Width;
        var (l, r) = PaneLayout.ClampWidths(_m, left, right, width, moving);
        LeftColumn.Width = new GridLength(l);
        RightColumn.Width = new GridLength(r);
    }

    /// <summary>矢印キーでペインの境界を動かす(ショートカットの PaneBoundary* から呼ぶ)。動かしたら true。</summary>
    public bool MoveSplitterByKey(PaneSplitter splitter, ShortcutAction action, bool large)
    {
        var dir = action switch
        {
            ShortcutAction.PaneBoundaryLeft or ShortcutAction.PaneBoundaryUp => -1,
            ShortcutAction.PaneBoundaryRight or ShortcutAction.PaneBoundaryDown => 1,
            _ => 0,
        };
        if (dir == 0) return false;
        splitter.MoveBy(dir, large);
        return true;
    }

    private void MoveVertical(int boundary, double delta)
    {
        if (_heights is not { } h) return;
        ApplyHeights(PaneLayout.MoveBoundary(_m, h, boundary, delta));
    }

    private void UpdateVertical()
    {
        var p = RightPane.Margin.Top + RightPane.Margin.Bottom + RightPane.BorderThickness.Top + RightPane.BorderThickness.Bottom
            + RightPane.Padding.Top + RightPane.Padding.Bottom;
        var height = MiddleGrid.ActualHeight - p;
        if (height <= 0) return;
        ApplyHeights(_heights is { } h ? PaneLayout.Resize(_m, h, height) : PaneLayout.Initial(_m, height));
    }

    private void ApplyHeights(RightPaneHeights h)
    {
        _heights = h;
        LayersRow.Height = new GridLength(h.Layers);
        StackRow.Height = new GridLength(h.Stack);
        ParamsRow.Height = new GridLength(h.Params);
    }

    /// <summary>タイムラインを 2 行に折り返しているか(行の高さが TimelineWrappedHeight)。</summary>
    public bool IsTimelineWrapped { get; private set; }

    /// <summary>
    /// タイムラインの行の高さを、1 行(トークン TimelineHeight。56)か 2 行の折り返し(TimelineWrappedHeight。96)にする(§9.3)。
    /// 折り返すかの判定(キャンバスの幅が 616 DIP 未満)と部品の並べ替えは、担当 timeline が行い、ここは行の高さだけを変える。
    /// </summary>
    public void SetTimelineWrapped(bool wrapped)
    {
        IsTimelineWrapped = wrapped;
        var r = Theme.AppResources.LayoutTokens;
        TimelineRow.Height = new GridLength((double)r[wrapped ? "TimelineWrappedHeight" : "TimelineHeight"]);
    }

    /// <summary>ウェルカム画面(文書が無い)のとき、左右ペインと書き出し帯を無効表示にし、コマンドバーは 4 つだけを有効にする(§9.3)。「画像を取り込むと使えます」(ui.tsv の panes.disabled)はペインの担当が出す。</summary>
    public void SetWelcome(bool welcome)
    {
        LeftPane.IsEnabled = !welcome;
        RightPane.IsEnabled = !welcome;
        ExportBandFrame.IsEnabled = !welcome;
        TimelineFrame.IsEnabled = !welcome;
        WelcomeHost.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        CommandBar.SetWelcome(welcome);
    }
}
