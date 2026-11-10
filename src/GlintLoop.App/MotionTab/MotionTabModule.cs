// 担当 motiontab の画面の部品(IShellModule、Order 450): モーションのタブの中身(タイルの一覧)を作り、プリセットの適用(FR-64。Undo の 1 件)・
// タイルの静止画(タイルの t の 1 フレーム。絵に効く状態が変わってから 300 ms 後、タブが見えている間だけ)・再生中のタイル 1 つの再生(L=2.0・F=12・長辺 192)を受け持つ。
// 中身を左ペインのモーションのタブ(LeftPaneView.MotionContent)へ差し込むのは統合の ModuleWiring。再生中のタイルが無い間は描画を呼ばない(NFR-9)。
using System.Windows;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Motion;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.MotionTab;

public sealed class MotionTabModule : IShellModule, IDisposable
{
    /// <summary>静止画の作り直しを始めるまでの時間(最後の変更から。ルックのギャラリーの FR-33 と同じ。仮の決め)。UI の動きではない。</summary>
    public static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>再生中のタイルの時間(§9.5: L=2.0、F=12 の 24 フレーム、無限、停止時間 0)。</summary>
    public static TimeSettings PlayTime { get; } = new(20, 12, LoopMode.Infinite, 0);

    /// <summary>再生中のタイルのフレーム数(24)。</summary>
    public static int PlayFrames => TimeModel.FrameCount(PlayTime);

    private AppShell? _shell;
    private IOneShotTimer? _rebuildTimer;
    private IOneShotTimer? _playTimer;
    private string? _signature;
    private bool _stale = true;
    private int _generation;
    private SessionDocument? _genDoc;
    private Target _genTarget;
    private readonly List<MotionTile> _queue = new();
    private bool _running;
    private bool _disposed;

    // 再生(再生中のタイル 1 つ)
    private MotionTile? _playTile;
    private SessionDocument? _playDoc;
    private DateTimeOffset _playStart;
    private int _playGeneration;
    private readonly Dictionary<int, DocumentImage> _frames = new();
    private bool _frameRendering;

    public int Order => 450;

    /// <summary>モーションのタブの中身(統合が LeftPaneView.MotionContent に入れる)。</summary>
    public MotionTabView View { get; private set; } = null!;

    /// <summary>組み込みのプリセット(§9.5 の表の順。カタログの宣言で全キーの値にしたもの)。</summary>
    public IReadOnlyList<MotionPreset> Presets { get; private set; } = [];

    /// <summary>描画を依頼した回数(静止画と再生のフレームの合計。テストが NFR-9 の「描画 0 回」を確かめる)。</summary>
    public int RenderCount { get; private set; }

    /// <summary>静止画の作り直しを始めた回数。</summary>
    public int RebuildCount { get; private set; }

    /// <summary>作り直しを待っているか(300 ms のタイマーが動いている)。</summary>
    public bool IsRebuildPending => _rebuildTimer?.IsRunning == true;

    /// <summary>静止画の作り直しの途中か(作っていないタイルが残っている)。</summary>
    public bool IsBuilding => _running || _queue.Count > 0;

    /// <summary>再生しているタイル(再生できないタイルが「再生中」でも null)。</summary>
    public MotionTile? PlayTile => _playTile;

    /// <summary>再生のフレームの描画を待っているか。</summary>
    public bool IsFrameRendering => _frameRendering;

    /// <summary>再生のタイマーが動いているか。</summary>
    public bool IsPlaybackTimerRunning => _playTimer?.IsRunning == true;

    /// <summary>静止画を 1 枚作り終えたとき(UI スレッド)。</summary>
    public event Action<MotionTile>? ThumbnailCompleted;

    /// <summary>再生のフレームを 1 枚出したとき(UI スレッド)。</summary>
    public event Action<MotionTile, int>? FrameShown;

    public void Attach(AppShell shell)
    {
        _shell = shell;
        Presets = BuiltInMotionPresets.Build(shell.Catalog);
        View = new MotionTabView(shell.Services.Modifiers);
        var tiles = new List<MotionTile> { new(null, NoticeTexts.UiText("motion.none")) };
        foreach (var p in Presets) tiles.Add(new MotionTile(p, p.Name));
        View.SetTiles(tiles);
        View.ApplyRequested += Apply;
        View.PlayingChanged += OnPlayingChanged;
        View.IsVisibleChanged += OnViewVisibleChanged;

        _rebuildTimer = shell.Services.Timers.Create();
        _rebuildTimer.Elapsed += RebuildNow;
        _playTimer = shell.Services.Timers.Create();
        _playTimer.Elapsed += Tick;

        shell.Session.Changed += OnSessionChanged;
        Refresh();
    }

    // ================================================================ セッションの変更

    private void OnSessionChanged(SessionChange what)
    {
        var d = _shell?.Window.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) Refresh();
        else d.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        if (_shell is null || _disposed) return;
        var doc = _shell.Session.Document;
        if (doc is null)
        {
            _rebuildTimer?.Stop();
            _signature = null;
            _stale = true;
            _generation++;
            _genDoc = null;
            _queue.Clear();
            StopPlayback();
            _playDoc = null;
            foreach (var t in View.Tiles) t.ClearThumbnail();
            return;
        }
        var sig = Signature(doc, _shell.Catalog);
        if (sig == _signature) return;
        _signature = sig;
        _stale = true;
        // 再生中のタイルは、経過時間を保ったまま新しい文書ですぐ描き直す(静止画は 300 ms 後にまとめて作り直す)
        _playDoc = doc;
        if (_playTile is not null)
        {
            _playGeneration++;
            _frames.Clear();
            Tick();
        }
        if (View.IsVisible) _rebuildTimer?.Start(RebuildDelay);
    }

    /// <summary>
    /// 絵の作り直しの契機になる状態: 「モーションなし」のタイルの絵の文書(MotionStacks.Preview)そのもの。対象・対象のレイヤーの配置と画像と
    /// モーション以外の行・作業面の大きさ(土台)・全体のときは各レイヤーのモーション以外・seed を含み、対象のモーションの行(プリセットの適用)・
    /// salt の数え・時間の設定・出力設定・レイヤーの名前は含まない(適用のたびに全部のタイルを作り直さないため)。
    /// </summary>
    internal static string Signature(SessionDocument doc, IEffectCatalog catalog)
    {
        var view = MotionStacks.Preview(doc, doc.Selected, Array.Empty<MotionPresetEffect>(), catalog);
        view = view with
        {
            SaltCounter = 0,
            Time = TimeSettings.Default,
            Output = OutputSettings.Default,
            Layers = view.Layers.Select(l => l with { Name = "" }).ToArray(),
        };
        return doc.Selected + "|" + DocumentJson.Write(view);
    }

    private void OnViewVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is null || _disposed) return;
        if ((bool)e.NewValue)
        {
            // 見えていない間に変わった絵は、見えたときにすぐ作る
            if (_stale && _shell.Session.HasDocument && !IsRebuildPending) RebuildNow();
        }
        else
        {
            _rebuildTimer?.Stop();
            View.ClearPlaying();
        }
    }

    // ================================================================ 静止画の作り直し

    /// <summary>作り直しを今すぐ始める(タイマーが鳴ったとき・タブが見えたとき)。全部のタイルを作成中に戻し、一覧の順に作る。</summary>
    public void RebuildNow()
    {
        if (_shell?.Session.Document is not { } doc) return;
        _rebuildTimer?.Stop();
        _stale = false;
        _generation++;
        RebuildCount++;
        _genDoc = doc;
        _genTarget = doc.Selected;
        _queue.Clear();
        foreach (var t in View.Tiles)
        {
            // 再生中のタイルは出しているフレームを残し、静止画だけを作り直す
            if (!ReferenceEquals(t, _playTile)) t.ClearThumbnail();
            else t.ForgetThumbnail();
        }
        _queue.AddRange(View.Tiles);
        Pump();
    }

    /// <summary>待ちのタイルを 1 つずつ描く(描画は専用のスレッド・優先度はサムネイル。結果は UI スレッドで受ける)。同時に動くのは 1 本だけ。</summary>
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
                if (_genDoc is not { } doc) continue;
                DocumentImage image;
                try
                {
                    var preview = MotionStacks.Preview(doc, _genTarget, tile.Effects, _shell.Catalog);
                    RenderCount++;
                    image = await renderer.RenderThumbnailAsync(preview, DocumentPixelFormat.Pbgra32, tile.StillK, tile.StillN);
                }
                catch (Exception e)
                {
                    _shell.ReportRenderFailure(LogRenderTarget.Thumb, e);
                    continue;
                }
                if (gen != _generation || _disposed) continue;
                tile.SetThumbnail(image, show: !ReferenceEquals(tile, _playTile) || tile.Shown is null);
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

    // ================================================================ 再生中のタイル(§9.5 の 1527 行)

    private void OnPlayingChanged(MotionTile? tile)
    {
        if (_disposed) return;
        if (tile is not null && tile.CanPlay && _shell?.Session.Document is not null) StartPlayback(tile);
        else StopPlayback();
    }

    /// <summary>tile の再生を最初(経過 0 ms)から始める。前のタイルは静止画に戻す。</summary>
    private void StartPlayback(MotionTile tile)
    {
        if (_shell is null) return;
        if (!ReferenceEquals(_playTile, tile)) _playTile?.ShowStill();
        _playTile = tile;
        _playDoc ??= _shell.Session.Document;
        _playGeneration++;
        _frames.Clear();
        _playStart = _shell.Services.Clock.UtcNow;
        Tick();
    }

    /// <summary>再生をやめて静止画に戻す。タイマーを止めるので、このあと描画は呼ばない。</summary>
    private void StopPlayback()
    {
        _playTimer?.Stop();
        _playGeneration++;
        _frames.Clear();
        _playTile?.ShowStill();
        _playTile = null;
    }

    /// <summary>いまの経過時間のフレームを出し(無ければ描きに行き)、次のフレームの切れ目にタイマーを掛ける。</summary>
    private void Tick()
    {
        if (_shell is null || _disposed || _playTile is null) return;
        var elapsed = ElapsedMs();
        var k = TimeModel.FrameAt(elapsed, PlayTime);
        ShowOrRender(k);
        // 次のフレームの開始時刻 S_(k+1) まで(1 周期 P の中の位置で測る)
        var period = TimeModel.PeriodMs(PlayTime);
        var r = period > 0 ? elapsed % period : 0;
        var due = Math.Max(1, TimeModel.StartMs(k + 1, PlayTime.Fps) - r);
        _playTimer?.Start(TimeSpan.FromMilliseconds(due));
    }

    private long ElapsedMs() => _shell is null ? 0 : Math.Max(0, (long)Math.Floor((_shell.Services.Clock.UtcNow - _playStart).TotalMilliseconds));

    /// <summary>フレーム k が描けていれば出す。無ければ描く(同時に描くのは 1 枚。描けたら、そのときの経過時間のフレームを出し直す)。</summary>
    private void ShowOrRender(int k)
    {
        if (_playTile is not { } tile) return;
        if (_frames.TryGetValue(k, out var img))
        {
            if (tile.ShownFrame != k)
            {
                tile.ShowFrame(img, k);
                FrameShown?.Invoke(tile, k);
            }
            return;
        }
        if (!_frameRendering) RenderFrame(tile, k);
    }

    private async void RenderFrame(MotionTile tile, int k)
    {
        if (_shell?.RendererReady is not { } ready || _playDoc is not { } doc) return;
        _frameRendering = true;
        var gen = _playGeneration;
        try
        {
            var renderer = await ready;
            var preview = MotionStacks.Preview(doc, doc.Selected, tile.Effects, _shell.Catalog);
            RenderCount++;
            var img = await renderer.RenderThumbnailAsync(preview, DocumentPixelFormat.Pbgra32, k, PlayFrames);
            if (gen != _playGeneration || _disposed || !ReferenceEquals(_playTile, tile)) return;
            _frames[k] = img;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            _shell?.ReportRenderFailure(LogRenderTarget.Thumb, e);
            return;
        }
        finally
        {
            _frameRendering = false;
        }
        // 描くのに時間がかかったときは、飛ばしていまのフレームを出す(再生は遅らせない)
        if (gen == _playGeneration && ReferenceEquals(_playTile, tile)) ShowOrRender(TimeModel.FrameAt(ElapsedMs(), PlayTime));
    }

    // ================================================================ 適用

    private void Apply(MotionTile tile)
    {
        if (_shell is not { } shell || !shell.Session.HasDocument) return;
        var o = shell.Session.ApplyMotionPreset(shell.Session.Selected, tile.Effects);
        View.List.SelectedItem = tile;
        if (o.Notice is { } n) shell.NoticeBar.Show(n);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Clear();
        if (_shell is not null) _shell.Session.Changed -= OnSessionChanged;
        _rebuildTimer?.Dispose();
        _playTimer?.Dispose();
    }
}
