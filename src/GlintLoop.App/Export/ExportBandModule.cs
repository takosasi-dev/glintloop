// 書き出し帯の担当(IShellModule、Order 750): 帯を ExportBandHost に入れ、自動の書き出し(AutoExport)とキャッシュと書き出しプリセットをつなぐ。
// 実出力(FR-19): F5 とコマンドバーの「実出力」で、いまの文書の完成したキャッシュのファイルをデコードして(UI スレッドの外)キャンバスに出す。完成前は切り替えず「書き出し中」の進みを出す。完成ファイルが消えていたらやり直す(E-36)。
// Esc と編集で閉じる処理はキャンバスが持つ。BelowNormal の専用スレッド(ExportScheduler)はここが持ち、書き出しの中身(RenderExportPipeline)は描画ができたら入れる。
using System.IO;
using System.Windows.Controls.Primitives;
using GlintLoop.App.Input;
using GlintLoop.App.Looks;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Preview;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;

namespace GlintLoop.App.Export;

public sealed class ExportBandModule : IShellModule, IDisposable
{
    private AppShell _shell = null!;
    private CanvasModule? _canvas;
    private ToggleButton? _button;
    private ExportScheduler? _scheduler;
    private bool _syncing;
    private int _showGeneration;
    private bool _disposed;

    public int Order => 750;

    public ExportBandView View { get; private set; } = null!;

    public AutoExport Auto { get; private set; } = null!;

    public ExportCache Cache { get; private set; } = null!;

    /// <summary>キャッシュの上限と削除(FR-105)。確認は起動時と書き出しの完了のたびにここが呼ぶ。Touch と ProtectFromTrim は渡す処理が呼ぶ。</summary>
    public CacheManager CacheManager { get; private set; } = null!;

    /// <summary>並んでいるキャッシュの確認と削除の全部が終わるまでの Task(テストの待ち合わせ用)。</summary>
    public Task CacheWork { get; private set; } = Task.CompletedTask;
    /// <summary>コピー・保存・ドラッグ(FR-80〜FR-85)。</summary>
    public ExportHandoff Handoff { get; private set; } = null!;

    public UserExportPresetStore Presets { get; private set; } = null!;

    /// <summary>F5・「実出力」を完成前に押した(状態欄に「書き出し中」の進みを出す。完成・取り消しで戻す)。</summary>
    public bool WritingRequested { get; private set; }

    /// <summary>キャンバスに出している実出力(出していなければ null)。</summary>
    public ActualOutputView? Showing { get; private set; }

    /// <summary>実出力のデコードの途中の Task(テストの待ち合わせ用)。</summary>
    public Task? PendingShow { get; private set; }

    /// <summary>書き出しの中身(描画ができたら RenderExportPipeline が入る。テストは偽を入れる)。</summary>
    public IExportPipeline? Pipeline
    {
        get => Auto.Pipeline;
        set => Auto.Pipeline = value;
    }

    /// <summary>書き出しを動かす TaskScheduler(既定はこのモジュールの ExportScheduler)。</summary>
    public TaskScheduler Scheduler
    {
        get => Auto.Scheduler;
        set => Auto.Scheduler = value;
    }

    public void Attach(AppShell shell)
    {
        _shell = shell;
        _canvas = shell.Modules.OfType<CanvasModule>().FirstOrDefault();
        var paths = shell.Services.Paths;
        var fileSystem = shell.Services.FileSystem;
        Cache = new ExportCache(paths.CacheExportsDir, shell.Services.Clock, fileSystem);
        CacheManager = new CacheManager(paths.CacheExportsDir, paths.CacheHandoffDir, shell.Services.Clock, shell.Log,
            () => CacheManager.LimitBytesFromMB(shell.Settings.CacheLimitMB), fileSystem);
        Presets = new UserExportPresetStore(paths.PresetsDir, fileSystem: fileSystem);
        View = new ExportBandView(shell.Session);
        shell.Window.ExportBandHost.Content = View;
        Auto = new AutoExport(shell.Session, shell.Catalog, Cache, shell.Services.Timers, Post, () => shell.Frames.K);
        _scheduler = new ExportScheduler(ExportScheduler.DefaultThreadCount);
        Auto.Scheduler = _scheduler;
        Auto.Parallelism = _scheduler.DefaultParallelism;
        Auto.Log = shell.Log;
        Auto.Changed += OnAutoChanged;
        // Post: 同じ完了を受けるほかの手(完成を待っていたコピーなど)が動いてから確認する
        Auto.Completed += _ => Post(() => QueueCacheWork(CacheManager.Trim));
        Auto.Completed += ShowShrunkLayers;
        Auto.StillCompleted += ShowShrunkLayers;
        Handoff = new ExportHandoff(shell, Auto, View, CacheManager);

        Presets.Changed += () => View.SetUserPresets(Presets.Presets);
        Presets.Load();
        View.SavePresetRequested += () => _ = SavePresetAsync();
        View.RenamePresetRequested += p => _ = RenamePresetAsync(p);
        View.DeletePresetRequested += p => WriteFile(() => Presets.Delete(p));

        shell.Session.Changed += OnSessionChanged;
        shell.Frames.Changed += OnFramesChanged;
        shell.Router.Register(ShortcutAction.ActualOutput, OnActualOutput);
        _button = shell.CommandBar.Button(Icons.ActualOutput) as ToggleButton;
        if (_button is not null)
        {
            // クリックでも UIA の Toggle でも入・切が変わるので、Checked と Unchecked で受ける(クリックの Router の呼び出しは捨てる)
            _button.Checked += OnButtonToggled;
            _button.Unchecked += OnButtonToggled;
        }
        if (_canvas is not null) _canvas.ActualOutputChanged += OnActualOutputChanged;

        if (shell.RenderReady is { } ready)
        {
            ready.ContinueWith(t => Post(() =>
                {
                    Auto.Environment = EnvironmentOf(t.Result, shell.Services.Version);
                    UpdateStatus();
                }),
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        if (shell.Recovery is { } recovery)
        {
            recovery.RegisterBeforeRecreate(Auto.SuspendForDeviceLoss);
            recovery.Recovered += OnDeviceRecovered;
        }
        if (shell.RendererReady is { } renderer)
        {
            renderer.ContinueWith(t => Post(() => Auto.Pipeline ??= new RenderExportPipeline(t.Result, shell.Sources, shell.Catalog, shell.Log, perf: shell.Services.Perf)),
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        View.Refresh();
        UpdateStatus();
        // FR-92: キャッシュの整理は多重起動の判定に勝った起動だけが行う
        if (shell.Services.RunStartupMaintenance) QueueCacheWork(CacheManager.Trim);
    }

    // 書き出しで縮めたレイヤーの通知(E-19)。表示の経路と同じ記録を通すので、文書ごとに 1 回になる
    private void ShowShrunkLayers(ExportCompletion completion)
    {
        foreach (var n in _shell.ShrinkNotices.Observe(completion.Outcome.ShrunkLayers)) _shell.NoticeBar.Show(n);
    }

    /// <summary>設定の「キャッシュの削除」(FR-105)。</summary>
    public void ClearCacheFromSettings() => QueueCacheWork(CacheManager.ClearFromSettings);

    /// <summary>キャッシュのファイルの列挙と削除を UI スレッドの外で 1 つずつ動かす(INV-5)。UI スレッドで呼ぶ。</summary>
    private void QueueCacheWork(Action work) =>
        CacheWork = CacheWork.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>キャッシュのキーの (4)〜(6): 版と 4 つの本体アセンブリ(App・Core・Codec・Render。Q-H)、アダプター、青色雑音のタイル。</summary>
    public static ExportEnvironment EnvironmentOf(RenderService render, string version) =>
        new(BuildIdentity.FromAssemblies(version,
                typeof(AppShell).Assembly, typeof(EditorSession).Assembly, typeof(OutputFrameDecoder).Assembly, typeof(RenderService).Assembly),
            render.IsWarp, render.AdapterName, ExportEnvironment.BlueNoiseTileSha256);

    private void Post(Action action)
    {
        if (_disposed) return;
        _shell.Window.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) action();
        });
    }

    // ================================================================ 変更の受け手

    private void OnSessionChanged(SessionChange what)
    {
        var edit = (what & CanvasModule.ActualOutputExitTriggers) != 0;
        // 待機の取り消し(FR-83)は編集より後の押下を消さないように、UI スレッドの編集ならその場で行う
        var onUi = _shell.Window.Dispatcher.CheckAccess();
        if (edit && onUi) Handoff.CancelWaits();
        Post(() =>
        {
            // 編集(キャンバスが実出力を閉じる変更と同じ)なら書き出しをやり直す。選択・履歴の数・未渡しだけの変化ではやり直さない
            if (edit)
            {
                if (!onUi) Handoff.CancelWaits();
                Auto.NotifyEdit();
            }
            View.Refresh();
            UpdateStatus();
            Handoff.OnSessionChanged();
        });
    }

    // 静止形式はスクラブバーの位置の 1 枚を書くので、再生していない間に k が変わったらやり直す(仮の決め)
    private void OnFramesChanged()
    {
        if (_shell.Session.Document is not { } doc || ExportFormat.IsAnimated(doc.Output.Format) || _shell.Frames.IsPlaying) return;
        var n = MotionInfo.FrameCount(doc, _shell.Catalog);
        if (n <= 1) return;
        var k = Math.Clamp(_shell.Frames.K, 0, n - 1);
        if (Auto.Job is { } job && job.FrameK == k && ReferenceEquals(job.Document, doc)) return;
        if (Auto.Job is null && Auto.State == ExportState.Idle) return; // 待っている間は、始めるときの k を使う
        Auto.NotifyEdit();
    }

    // デバイスを作り直した(E-39)。WARP に切り替わったらキャッシュのキーのアダプターも変わる
    private void OnDeviceRecovered()
    {
        if (_disposed) return;
        if (Auto.Environment is { } env && _shell.RenderReady is { IsCompletedSuccessfully: true } ready && env.IsWarp != ready.Result.IsWarp)
            Auto.Environment = EnvironmentOf(ready.Result, _shell.Services.Version);
        Auto.ResumeAfterDeviceLoss();
        UpdateStatus();
    }

    private void OnAutoChanged()
    {
        if (Auto.State is ExportState.Done or ExportState.Failed) WritingRequested = false;
        if (_canvas is not null) _canvas.IsActualOutputAvailable = Auto.IsReady;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (View is null || Auto is null) return;
        var limit = _shell.Session.Document is { } doc ? OutputPlan.Limit(doc.Output.Limit).Bytes : null;
        View.SetStatus(
            // ウェルカム画面のあいだ(§9.3)は「画像を取り込むと使えます」を状態欄に出す
            _shell.Session.HasDocument ? BandTexts.Line1(Auto.State, Auto.Progress, Auto.Outcome, WritingRequested) : BandTexts.Ui("panes.disabled"),
            BandTexts.Line2(Auto.State, Auto.Outcome, limit, Auto.Error),
            Auto.Environment?.IsWarp == true);
    }

    // ================================================================ 実出力(FR-19)

    private bool OnActualOutput(ShortcutInvocation inv)
    {
        if (!_shell.Session.HasDocument || _canvas is null) return false;
        // コマンドバーのボタンのクリックから来た呼び出しは、ボタンの Checked・Unchecked で扱う
        if (_button is not null && ReferenceEquals(inv.Source, _button) && inv.Chord == default) return true;
        SetActualOutput(!(_canvas.IsActualOutput || PendingShow is not null));
        return true;
    }

    private void OnButtonToggled(object? sender, System.Windows.RoutedEventArgs e)
    {
        if (_syncing || _button is null) return;
        SetActualOutput(_button.IsChecked == true);
    }

    /// <summary>実出力の入・切。入れるときに完成したファイルが無ければ、切り替えずに「書き出し中」の進みを出す。</summary>
    public void SetActualOutput(bool on)
    {
        if (_canvas is null) return;
        if (!on)
        {
            _showGeneration++;
            PendingShow = null;
            _canvas.HideActualOutput();
            SyncButton();
            return;
        }
        if (!_shell.Session.HasDocument) return;
        if (!Auto.IsReady || Auto.Entry is not { } entry)
        {
            WritingRequested = Auto.State == ExportState.Running || (Auto.State == ExportState.Idle && Auto.Pipeline is not null && Auto.Environment is not null);
            UpdateStatus();
            SyncButton();
            return;
        }
        PendingShow = ShowAsync(entry);
        SyncButton();
    }

    private async Task ShowAsync(ExportCacheEntry entry)
    {
        var gen = ++_showGeneration;
        // デコードは UI スレッドの外で(NFR-7)
        var fileSystem = _shell.Services.FileSystem;
        var (frames, gone) = await Task.Run<(ActualOutputFrames?, bool)>(() =>
        {
            if (!Handoff.IsIntact(entry)) return (null, true);
            try
            {
                CacheManager.Touch(entry);
                return (ActualOutputFrames.Load(entry.Path, entry.Format, fileSystem), false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutputDecodeException or NotSupportedException)
            {
                // 確認のあとで消されたのなら E-36
                return (null, !Handoff.IsIntact(entry));
            }
        });
        if (gen != _showGeneration || _disposed) return;
        PendingShow = null;
        if (gone)
        {
            // 完成の扱いを取り消して書き出しをやり直す(E-36)。表示は完成前の F5 と同じ。編集が入っていれば、やり直しはその編集の 600 ms に任せる
            if (Auto.IsCurrent && ReferenceEquals(Auto.Entry, entry))
            {
                Auto.Retry();
                SetActualOutput(true);
                return;
            }
        }
        else if (frames is null)
        {
            _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.ExportFailed));
        }
        else if (_canvas is not null && Auto.IsReady && ReferenceEquals(Auto.Entry, entry))
        {
            var view = new ActualOutputView(frames, _shell.Services.Timers);
            if (_canvas.ShowActualOutput(view))
            {
                Showing?.Dispose();
                Showing = view;
            }
            else
            {
                view.Dispose();
            }
        }
        SyncButton();
    }

    private void OnActualOutputChanged()
    {
        if (_canvas is not null && !_canvas.IsActualOutput && Showing is not null)
        {
            Showing.Dispose();
            Showing = null;
        }
        SyncButton();
    }

    private void SyncButton()
    {
        if (_button is null || _canvas is null) return;
        _syncing = true;
        try
        {
            _button.IsChecked = _canvas.IsActualOutput || PendingShow is not null;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ================================================================ 書き出しプリセット(FR-77)

    private async Task SavePresetAsync()
    {
        if (_shell.Session.Document is not { } doc) return;
        var output = doc.Output;
        var name = await NameInputDialog.AskAsync(_shell.Dialogs, NameDialogMode.Save, "", MotionCause.Pointer);
        if (name is null) return;
        if (Presets.Find(name) is not null && !await OverwriteConfirmDialog.AskAsync(_shell.Dialogs, MotionCause.Programmatic)) return;
        WriteFile(() => Presets.Save(name, output));
    }

    private async Task RenamePresetAsync(UserExportPreset preset)
    {
        var name = await NameInputDialog.AskAsync(_shell.Dialogs, NameDialogMode.Rename, preset.Name, MotionCause.Pointer);
        if (name is null) return;
        var other = Presets.Find(name);
        if (other is not null && other.Path != preset.Path && !await OverwriteConfirmDialog.AskAsync(_shell.Dialogs, MotionCause.Programmatic)) return;
        WriteFile(() => Presets.Rename(preset, name));
    }

    /// <summary>ファイルの書き込み。失敗したら通知バーに save.failed(ルックと同じ扱い。仮の決め)。</summary>
    private void WriteFile(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.SaveFailed));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shell.Session.Changed -= OnSessionChanged;
        _shell.Frames.Changed -= OnFramesChanged;
        if (_canvas is not null) _canvas.ActualOutputChanged -= OnActualOutputChanged;
        if (_shell.Recovery is { } recovery) recovery.Recovered -= OnDeviceRecovered;
        Showing?.Dispose();
        Handoff.Dispose();
        Auto.Dispose();
        // 専用スレッドは、取り消した仕事の後始末が済んでから UI スレッドの外で止める
        if (_scheduler is { } scheduler) Auto.Idle.ContinueWith(_ => scheduler.Dispose(), TaskScheduler.Default);
    }
}
