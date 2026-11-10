// 担当 looks の画面の部品をつなぐ(IShellModule、Order 400): 左ペインの枠(3 つのタブ)とルックのギャラリーを LeftPaneHost に入れ、
// 適用(FR-34)・強さ(FR-35)・ユーザーのルックの保存と名前の変更と削除(FR-36)・サムネイルの作り直し(FR-33。最後の変更から 300 ms 後、見えているタイルから)を受け持つ。
// Alt+1〜3(左ペインのタブ)と、ルックのタイルの Shift+F10・アプリケーションキー(コンテキストメニュー)を Router に登録する。
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlintLoop.App.Input;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Looks;

public sealed class LooksModule : IShellModule, IDisposable
{
    /// <summary>サムネイルの作り直しを始めるまでの時間(FR-33。最後の変更から)。UI の動きではない。</summary>
    public static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(300);

    private AppShell? _shell;
    private IOneShotTimer? _timer;
    private IReadOnlyList<LookRecipe> _builtIns = [];
    private string? _signature;
    private SessionDocument? _lastDoc;
    private int _generation;
    private SessionDocument? _genDoc;
    private Target _genTarget;
    private readonly List<LookTile> _queue = new();
    private bool _running;
    private bool _disposed;

    public int Order => 400;

    public LeftPaneView LeftPane { get; private set; } = null!;

    public LookGalleryView Gallery { get; private set; } = null!;

    public UserLookStore Store { get; private set; } = null!;

    /// <summary>組み込みのルック(§9.6 の表の順。カタログの宣言で全キーの値にしたもの)。</summary>
    public IReadOnlyList<LookRecipe> BuiltIns => _builtIns;

    /// <summary>サムネイルの作り直しを始めた回数(テストが 300 ms の間引きを確かめる)。</summary>
    public int RebuildCount { get; private set; }

    /// <summary>いちばん新しい作り直しで、作り始めた順のタイルのキー(見えているタイルが先)。</summary>
    public IReadOnlyList<string> LastBuildOrder => _buildOrder;

    private readonly List<string> _buildOrder = new();

    /// <summary>いちばん新しい作り直しを始めたときに決めた順(見えているタイルを先に、残りを一覧の順)。</summary>
    public IReadOnlyList<string> LastPlan { get; private set; } = [];

    /// <summary>作り直しを待っているか(300 ms のタイマーが動いている)。</summary>
    public bool IsRebuildPending => _timer?.IsRunning == true;

    /// <summary>作り直しの途中か(作っていないタイルが残っている)。</summary>
    public bool IsBuilding => _running || _queue.Count > 0;

    /// <summary>サムネイルを 1 枚作り終えたとき(UI スレッド)。</summary>
    public event Action<LookTile>? ThumbnailCompleted;

    public void Attach(AppShell shell)
    {
        _shell = shell;
        _builtIns = BuiltInLooks.Build(shell.Catalog);
        Store = new UserLookStore(shell.Services.Paths.LooksDir, shell.Catalog, shell.Log, fileSystem: shell.Services.FileSystem);
        Store.Load();
        Store.Changed += OnStoreChanged;

        LeftPane = new LeftPaneView();
        Gallery = new LookGalleryView(shell.Services.Modifiers);
        LeftPane.LooksContent = Gallery;
        shell.Window.LeftPaneHost.Content = LeftPane;
        RebuildTiles();

        Gallery.ApplyRequested += Apply;
        Gallery.SaveRequested += () => _ = SaveAsync();
        Gallery.RenameRequested += t => _ = RenameAsync(t);
        Gallery.DeleteRequested += Delete;
        Gallery.StrengthChanged += v =>
        {
            if (shell.Session.HasDocument) shell.Session.SetLookStrength(shell.Session.Selected, v);
        };
        Gallery.StrengthDragStarted += () => shell.Session.BeginContinuous();
        Gallery.StrengthDragCompleted += () => shell.Session.EndContinuous();

        _timer = shell.Services.Timers.Create();
        _timer.Elapsed += RebuildNow;

        shell.Router.Register(ShortcutAction.LeftTabLooks, _ => SelectTab(LeftPaneTab.Looks));
        shell.Router.Register(ShortcutAction.LeftTabEffects, _ => SelectTab(LeftPaneTab.Effects));
        shell.Router.Register(ShortcutAction.LeftTabMotion, _ => SelectTab(LeftPaneTab.Motion));
        shell.Router.Register(ShortcutAction.ContextMenu, inv => TileOf(inv.Source) is { User: not null } t && Gallery.OpenMenu(t));

        shell.Session.Changed += OnSessionChanged;
        Refresh(SessionChange.Document);
    }

    /// <summary>左ペインのタブを選ぶ(ほかの担当も使う。例: Ctrl+F の検索欄はエフェクトのタブを選んでから)。</summary>
    public bool SelectTab(LeftPaneTab tab)
    {
        if (!LeftPane.HasDocument) return false;
        LeftPane.SelectTab(tab);
        return true;
    }

    private LookTile? TileOf(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is LookTile t && Gallery.List.Items.Contains(t)) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ================================================================ タイルの並び

    /// <summary>タイルを「ルックなし」・組み込みの 24 個・ユーザーのルック(名前の順)の順に並べ直す。同じキーのタイルは画像ごと持ち越す。</summary>
    private void RebuildTiles()
    {
        var old = Gallery.Tiles.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var selected = (Gallery.List.SelectedItem as LookTile)?.Key;
        var tiles = new List<LookTile>();
        LookTile Keep(LookTile fresh)
        {
            if (old.TryGetValue(fresh.Key, out var t) && t.DisplayName == fresh.DisplayName && ReferenceEquals(t.Recipe?.Effects, fresh.Recipe?.Effects)) return t;
            return fresh;
        }
        tiles.Add(Keep(new LookTile(null, LooksText.Get("looks.none"), null)));
        foreach (var r in _builtIns) tiles.Add(Keep(new LookTile(r, r.Name, null)));
        foreach (var u in Store.Looks) tiles.Add(Keep(new LookTile(u.ToRecipe(), u.Name, u)));
        Gallery.SetTiles(tiles);
        if (selected is not null) Gallery.List.SelectedItem = tiles.FirstOrDefault(t => t.Key == selected);
    }

    private void OnStoreChanged()
    {
        if (_shell is null || Gallery is null) return;
        RebuildTiles();
        // ユーザーのルックが増えた・変わったときは、その画像だけを今の作り直しの文書で作る(作り直しの契機ではない)
        if (_genDoc is not null)
        {
            foreach (var t in Gallery.Tiles)
            {
                if (t.Thumbnail is null && !_queue.Contains(t)) _queue.Add(t);
            }
            Pump();
        }
    }

    // ================================================================ セッションの変更

    private void OnSessionChanged(SessionChange what)
    {
        var d = _shell?.Window.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) Refresh(what);
        else d.BeginInvoke(() => Refresh(what));
    }

    private void Refresh(SessionChange what)
    {
        if (_shell is null || _disposed) return;
        var session = _shell.Session;
        var doc = session.Document;
        LeftPane.SetHasDocument(doc is not null);
        if (doc is null)
        {
            _timer?.Stop();
            _signature = null;
            _lastDoc = null;
            _generation++;
            _genDoc = null;
            _queue.Clear();
            foreach (var t in Gallery.Tiles) t.ClearThumbnail();
            return;
        }
        // 画像を取り込むたびに(2 枚目以降も)ルックのタブを選ぶ(FR-33)
        if (what.HasFlag(SessionChange.Document) || IsImport(_lastDoc, doc)) LeftPane.SelectTab(LeftPaneTab.Looks);
        _lastDoc = doc;
        Gallery.ShowStrength(session.LookStrengthOf(doc.Selected));
        var sig = Signature(doc, _shell.Catalog);
        if (sig != _signature)
        {
            _signature = sig;
            _timer?.Start(RebuildDelay);
        }
    }

    /// <summary>取り込みか(前の文書に無かった画像が増えた。複製は同じ画像、空のレイヤーは画像を持たないので数えない。仮の決め)。</summary>
    private static bool IsImport(SessionDocument? before, SessionDocument after)
    {
        if (before is null) return true;
        if (after.Layers.Count <= before.Layers.Count) return false;
        var had = before.Assets.Select(a => a.Sha256).ToHashSet(StringComparer.Ordinal);
        return after.Assets.Any(a => !had.Contains(a.Sha256));
    }

    /// <summary>
    /// サムネイルの作り直しの契機になる状態(FR-33): 対象、レイヤーの並び・画像・表示・配置・不透明度・ブレンド、対象の形ステージ
    /// (全体のときは各レイヤーのスタックも)。これが変わったときだけ作り直す(ルックの適用・強さ・対象のルックと仕上げの行では作り直さない)。
    /// </summary>
    internal static string Signature(SessionDocument doc, IEffectCatalog catalog)
    {
        var sb = new StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("t=").Append(doc.Selected.ToString()).Append(';');
        foreach (var l in doc.Layers)
        {
            sb.Append(string.Format(inv, "L{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9};", l.Kind, l.Asset, l.Size, l.Fill, l.Visible, l.Opacity, l.Blend,
                l.Place.X, l.Place.Y, l.Place.Scale));
        }
        void Stack(IReadOnlyList<StackEntry> stack, bool shapeOnly)
        {
            foreach (var e in stack)
            {
                if (shapeOnly && catalog.Find(e.Id) is not { Stage: EffectStage.Shape }) continue;
                sb.Append(string.Format(inv, "{0}|{1}|{2}|{3}|{4}|{5};", e.Id, e.Enabled, e.Origin, e.Salt, e.Amount,
                    string.Join(",", e.Params.Select(p => p.Key + "=" + p.Value))));
            }
        }
        var t = doc.Selected;
        if (t.IsAll)
        {
            sb.Append("all:");
            Stack(doc.All.Stack, shapeOnly: true);
            foreach (var l in doc.Layers)
            {
                sb.Append("layer:");
                Stack(l.Stack, shapeOnly: false);
            }
            sb.Append("seed=").Append(doc.Seed.ToString(inv));
        }
        else
        {
            sb.Append("shape:");
            Stack(doc.Layers[t.LayerIndex].Stack, shapeOnly: true);
        }
        return sb.ToString();
    }

    // ================================================================ サムネイルの作り直し

    /// <summary>作り直しを今すぐ始める(タイマーが鳴ったとき)。全部のタイルを作成中に戻し、見えているタイルから順に作る。</summary>
    public void RebuildNow()
    {
        if (_shell?.Session.Document is not { } doc) return;
        _timer?.Stop();
        _generation++;
        RebuildCount++;
        // 測定の記録の gallery(§6.8): 直前の取り込みを、この作り直しで測る。途中で次の作り直しが始まったら書かない
        _shell.Services.Perf?.GalleryRebuildStarted();
        _genDoc = doc;
        _genTarget = doc.Selected;
        _buildOrder.Clear();
        _queue.Clear();
        foreach (var t in Gallery.Tiles) t.ClearThumbnail();
        _queue.AddRange(VisibleFirst(Gallery.Tiles.ToList()));
        LastPlan = _queue.Select(t => t.Key).ToList();
        Pump();
    }

    /// <summary>見えているタイル(一覧のスクロールの表示の範囲に掛かるもの)を先に、残りを一覧の順に並べる。</summary>
    private List<LookTile> VisibleFirst(List<LookTile> tiles)
    {
        var scroll = Gallery.Scroll;
        if (!scroll.IsVisible || scroll.ViewportHeight <= 0) return tiles;
        var view = new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight);
        var visible = new List<LookTile>();
        var rest = new List<LookTile>();
        foreach (var t in tiles)
        {
            var shown = false;
            if (t.IsVisible && scroll.IsAncestorOf(t))
            {
                var r = t.TransformToAncestor(scroll).TransformBounds(new Rect(t.RenderSize));
                shown = r.IntersectsWith(view) && r.Width > 0 && r.Height > 0;
            }
            (shown ? visible : rest).Add(t);
        }
        visible.AddRange(rest);
        return visible;
    }

    /// <summary>待ちのタイルを 1 つずつ描く(描画は専用のスレッド。結果は UI スレッドで受ける)。同時に動くのは 1 本だけ。</summary>
    private async void Pump()
    {
        if (_running || _shell is null) return;
        if (_shell.RendererReady is not { } ready) return;
        _running = true;
        try
        {
            var renderer = await ready;
            while (_queue.Count > 0 && !_disposed)
            {
                var tile = _queue[0];
                _queue.RemoveAt(0);
                var gen = _generation;
                if (_genDoc is not { } doc || tile.Thumbnail is not null) continue;
                _buildOrder.Add(tile.Key);
                DocumentImage image;
                try
                {
                    var preview = LookStacks.Preview(doc, _genTarget, tile.Effects, _shell.Catalog);
                    image = await renderer.RenderAsync(preview, renderer.ThumbnailLongSide(preview), DocumentPixelFormat.Pbgra32, priority: RenderPriority.Thumbnail);
                }
                catch (Exception e)
                {
                    _shell.ReportRenderFailure(LogRenderTarget.Thumb, e);
                    continue;
                }
                if (gen != _generation || _disposed) continue;
                tile.SetThumbnail(image);
                if (_shell.Services.Perf is { } perf && BuiltInsDone()) perf.GalleryBuiltInsDone();
                ThumbnailCompleted?.Invoke(tile);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // 描画の準備に失敗したとき(RendererReady は取り消しになる)・閉じたあと: タイルは作成中(surface-2)のまま
        }
        finally
        {
            _running = false;
        }
    }

    // 組み込みの 24 個のタイル(「ルックなし」とユーザーのルックを除く)がすべて作り終わったか
    private bool BuiltInsDone() => Gallery.Tiles.Count(t => t.Recipe is not null && t.User is null && t.Thumbnail is not null) == _builtIns.Count;

    // ================================================================ 適用・保存・名前の変更・削除

    private void Apply(LookTile tile)
    {
        if (_shell is not { } shell || !shell.Session.HasDocument) return;
        var o = shell.Session.ApplyLook(shell.Session.Selected, tile.Effects);
        Gallery.List.SelectedItem = tile;
        if (o.Notice is { } n) shell.NoticeBar.Show(n);
        if (tile.User is { HasUnknownEffects: true }) shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.StackUnknownEffect));
    }

    private async Task SaveAsync()
    {
        if (_shell is not { } shell || !shell.Session.HasDocument) return;
        var effects = shell.Session.CaptureLook(shell.Session.Selected);
        var name = await NameInputDialog.AskAsync(shell.Dialogs, NameDialogMode.Save, "", MotionCause.Pointer);
        if (name is null) return;
        if (Store.Find(name) is not null && !await OverwriteConfirmDialog.AskAsync(shell.Dialogs, MotionCause.Programmatic)) return;
        Write(() => Store.Save(name, effects));
    }

    private async Task RenameAsync(LookTile tile)
    {
        if (_shell is not { } shell || tile.User is not { } user) return;
        var name = await NameInputDialog.AskAsync(shell.Dialogs, NameDialogMode.Rename, user.Name, MotionCause.Pointer);
        if (name is null) return;
        var other = Store.Find(name);
        if (other is not null && other.Path != user.Path && !await OverwriteConfirmDialog.AskAsync(shell.Dialogs, MotionCause.Programmatic)) return;
        Write(() => Store.Rename(user, name));
    }

    private void Delete(LookTile tile)
    {
        if (tile.User is { } user) Write(() => Store.Delete(user));
    }

    /// <summary>ファイルの書き込み。失敗したら通知バーに save.failed(仮の決め)。</summary>
    private void Write(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _shell?.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.SaveFailed));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Clear();
        if (_shell is not null)
        {
            _shell.Session.Changed -= OnSessionChanged;
            Store.Changed -= OnStoreChanged;
        }
        _timer?.Dispose();
    }
}
