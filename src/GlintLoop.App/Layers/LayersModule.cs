// レイヤーの画面の部品(担当 layers)を AppShell につなぐ(IShellModule、Order 300): 右ペインの見出しの行・レイヤーの一覧・レイヤーの設定・
// キャンバス上の枠とハンドル・行のサムネイル・空のキャンバスのダイアログ、レイヤーの操作のショートカット(§9.12 の 5・14〜18 行)と Esc の名前の編集。
// スタックの行の選択(FR-24)は担当 stack の状態なので、SelectedStackRow で読み、変わったら StackSelectionChanged を呼んでもらう。
using System.Windows;
using System.Windows.Threading;
using GlintLoop.App.Input;
using GlintLoop.App.Stack;
using GlintLoop.App.Startup;
using GlintLoop.App.Theme;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Layers;

public sealed class LayersModule : IShellModule, IDisposable
{
    private AppShell? _shell;
    private Dispatcher? _dispatcher;
    private bool _disposed;
    private bool _refreshQueued;

    public int Order => 300;

    public RightHeaderView Header { get; private set; } = null!;

    public LayerListView List { get; private set; } = null!;

    public LayerSettingsView Settings { get; private set; } = null!;

    public LayerThumbnails Thumbnails { get; private set; } = null!;

    public LayerFrameController Frame { get; private set; } = null!;

    public LayerFrameVisual FrameVisual { get; private set; } = null!;

    public LayerCanvasInput CanvasInput { get; private set; } = null!;

    /// <summary>
    /// 担当 stack がつなぐ: いまの対象のスタックで選んでいる行の番号(選んでいなければ null)。既定は「選んでいない」。
    /// レイヤーの設定を出すか(FR-24・FR-48)と、枠を出すか(ハンドルを持つ行を選んでいないか。FR-21・FR-50)に使う。
    /// </summary>
    public Func<int?> SelectedStackRow { get; set; } = () => null;

    /// <summary>担当 stack が呼ぶ: スタックの行の選択が変わった(枠と設定の表示を決め直す)。</summary>
    public void StackSelectionChanged() => Refresh();

    /// <summary>一覧で選んでいる行をもう一度クリックした(スタックの行の選択を外してレイヤーの設定へ戻す。FR-48)。担当 stack が受ける。</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 「すべてリセット」(FR-32)を押した。担当 stack が受ける。受け手が 1 つも無いときは Session.ResetAll を呼ぶ。
    /// </summary>
    public event Action? ResetAllRequested;

    /// <summary>
    /// レイヤーの設定を出しているか(対象がレイヤーで、スタックの行を選んでいないとき)。設定の部品をパラメータ欄に置くのは担当 stack の ParamsView
    /// (StackModule.LayerSettingsFactory。統合の ModuleWiring がつなぐ)で、ここは同じ条件で部品の表示と中身を合わせるだけ。
    /// </summary>
    public bool ShowsSettings { get; private set; }

    public void Attach(AppShell shell)
    {
        _shell = shell;
        _dispatcher = shell.Window.Dispatcher;
        var session = shell.Session;
        var w = shell.Window;

        Header = new RightHeaderView();
        Header.ResetAllClicked += OnResetAll;
        w.RightHeaderHost.Content = Header;

        Thumbnails = new LayerThumbnails(shell.Catalog, shell.Services.Timers, () => session.Document, shell.RendererReady);
        Thumbnails.Updated += () => List.Refresh(session.Document, Thumbnails.Get);
        Thumbnails.Failed += e => shell.ReportRenderFailure(GlintLoop.Core.Diagnostics.LogRenderTarget.Thumb, e);

        List = new LayerListView();
        List.RowSelected += t => session.Select(t);
        List.SelectedRowClicked += _ => SettingsRequested?.Invoke();
        List.VisibleToggled += (i, v) => session.SetLayerVisible(i, v);
        List.DuplicateClicked += i => ShowNotice(session.DuplicateLayer(i));
        List.DeleteClicked += i => session.DeleteLayer(i);
        List.RenameCommitted += (i, text) => session.SetLayerName(i, text);
        List.MoveRequested += (from, to) => session.MoveLayer(from, to);
        w.LayerListHost.Content = List;

        Settings = new LayerSettingsView(session) { Visibility = Visibility.Collapsed };

        Frame = new LayerFrameController(session, shell.CanvasTransform, HandlesShown);
        CanvasInput = new LayerCanvasInput(Frame);
        FrameVisual = new LayerFrameVisual(Frame);
        // 入力は層の一番下に置き、先につないだ担当(スタックのハンドル)の要素の下にする。枠の絵は上に重ねる(当たり判定は持たない)。
        shell.CanvasView.OverlayLayer.Children.Insert(0, CanvasInput);
        shell.CanvasView.OverlayLayer.Add(FrameVisual);
        CanvasInput.WatchClicks(shell.CanvasView);

        RegisterShortcuts(shell);
        session.Changed += OnSessionChanged;
        Refresh();
    }

    /// <summary>空のキャンバスのダイアログ(FR-44)を開く。ウェルカム画面の「空のキャンバス」ボタンから呼ぶ(担当 screens)。作ったら true。</summary>
    public async Task<bool> ShowEmptyCanvasDialog(MotionCause cause = MotionCause.Pointer)
    {
        var shell = _shell ?? throw new InvalidOperationException("Attach の前");
        if (shell.Dialogs.Top is EmptyCanvasDialog) return false;
        var dialog = new EmptyCanvasDialog(shell.Session, shell.Dialogs);
        var result = await shell.Dialogs.Show(dialog, "DialogEmptyCanvasWidth", null, cause);
        return result is true;
    }

    /// <summary>いま選んでいるスタックの行がハンドルを持つか(FR-21。HasHandles)。</summary>
    public bool HandlesShown()
    {
        var doc = _shell?.Session.Document;
        if (doc is null || SelectedStackRow() is not { } row) return false;
        var stack = doc.StackOf(doc.Selected);
        if (row < 0 || row >= stack.Count) return false;
        return _shell!.Catalog.Find(stack[row].Id) is { } def && HandleMath.HasHandles(def, stack[row]);
    }

    private void RegisterShortcuts(AppShell shell)
    {
        var r = shell.Router;
        var s = shell.Session;
        r.Register(ShortcutAction.AddEmptyLayer, _ =>
        {
            if (!s.HasDocument) return false;
            ShowNotice(s.AddEmptyLayer());
            return true;
        });
        r.Register(ShortcutAction.LayerSelectUp, _ => SelectBy(-1));
        r.Register(ShortcutAction.LayerSelectDown, _ => SelectBy(1));
        r.Register(ShortcutAction.LayerMoveFront, _ => MoveSelected(1));
        r.Register(ShortcutAction.LayerMoveBack, _ => MoveSelected(-1));
        r.Register(ShortcutAction.LayerDuplicate, _ =>
        {
            if (SelectedLayer() is { } i) ShowNotice(s.DuplicateLayer(i));
            return true;
        });
        r.Register(ShortcutAction.LayerDelete, _ =>
        {
            if (SelectedLayer() is { } i) s.DeleteLayer(i);
            return true;
        });
        r.Register(ShortcutAction.LayerRename, _ =>
        {
            if (SelectedLayer() is not { } i) return true;
            var row = List.Rows.FirstOrDefault(x => !x.IsAll && x.LayerIndex == i);
            if (row is not null) List.BeginEdit(row);
            return true;
        });
        r.Escape.Register(EscapeTarget.NameEdit, () => List.EditingRow is not null, List.CancelEdit);
    }

    private int? SelectedLayer()
    {
        var doc = _shell?.Session.Document;
        return doc is null || doc.Selected.IsAll ? null : doc.Selected.LayerIndex;
    }

    /// <summary>一覧で 1 つ上(−1)・1 つ下(+1)の行を選ぶ。端では何も起きない(FR-46)。</summary>
    private bool SelectBy(int delta)
    {
        var s = _shell!.Session;
        if (s.Document is not { } doc) return false;
        var n = doc.Layers.Count;
        var row = LayerListMath.RowOf(doc.Selected, n) + delta;
        if (row >= 0 && row <= n) s.Select(LayerListMath.TargetOfRow(row, n));
        List.FocusSelected();
        return true;
    }

    /// <summary>選んでいるレイヤーを手前(+1)・奥(−1)へ。端と「全体」では何も起きない(FR-52)。</summary>
    private bool MoveSelected(int delta)
    {
        var s = _shell!.Session;
        if (SelectedLayer() is { } i && s.Document is { } doc)
        {
            var to = i + delta;
            if (to >= 0 && to < doc.Layers.Count) s.MoveLayer(i, to);
        }
        List.FocusSelected();
        return true;
    }

    private void ShowNotice(EditOutcome o)
    {
        if (o.Notice is { } n) _shell!.NoticeBar.Show(n);
    }

    private void OnResetAll()
    {
        if (_shell?.Session.HasDocument != true) return;
        if (ResetAllRequested is { } h) h();
        else _shell.Session.ResetAll();
    }

    private void OnSessionChanged(SessionChange what)
    {
        if (_disposed || _dispatcher is null) return;
        if (_dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }
        if (_refreshQueued) return;
        _refreshQueued = true;
        _dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>一覧・設定・枠・サムネイルを文書に合わせる(UI スレッド)。</summary>
    public void Refresh()
    {
        if (_disposed || _shell is null) return;
        var doc = _shell.Session.Document;
        Thumbnails.Update(doc);
        List.Refresh(doc, Thumbnails.Get);
        ShowsSettings = doc is not null && !doc.Selected.IsAll && SelectedStackRow() is null;
        if (ShowsSettings) Settings.Bind(doc!, doc!.Selected.LayerIndex);
        Settings.Visibility = ShowsSettings ? Visibility.Visible : Visibility.Collapsed;
        Frame.Refresh();
        FrameVisual.InvalidateVisual();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_shell is not null) _shell.Session.Changed -= OnSessionChanged;
        Thumbnails?.Dispose();
    }
}
