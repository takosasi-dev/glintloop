// 担当 batch をつなぐ(IShellModule、Order 760): Ctrl+B とコマンドバーの「バッチ」で画面を開き、「開く」・ドロップ・出力フォルダの選択・実行・取り消しを受ける(FR-87)。
// 開いている間のドロップはダイアログの層で受け、一覧に足す(ウィンドウ全体のドロップ FR-3 には渡さない)。実行は 1 回ごとに ExportScheduler(BelowNormal)を立て、UI スレッドでは重い処理をしない(INV-5)。
// 描画は取り込みのセッションと別の画素を読むので、実行の間だけ専用の DocumentRenderer を同じ RenderService の上に作る。デバイスの消失(E-39)では、作り直しの前に処理中の 1 個を止めて .tmp を消す。テストは Pipeline に偽を入れる。
using System.Windows;
using GlintLoop.App.Export;
using GlintLoop.App.Input;
using GlintLoop.App.Platform;
using GlintLoop.App.Screens;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Batch;

public sealed class BatchModule : IShellModule, IDisposable
{
    private AppShell _shell = null!;
    private ImageImporter _importer = null!;
    private CancellationTokenSource? _cts;
    private BatchRunner? _runner;
    private string? _lastFolder;
    private bool _disposed;

    public int Order => 760;

    /// <summary>開いているバッチ画面(無ければ null)。</summary>
    public BatchView? View { get; private set; }

    /// <summary>実行中の Task(実行中でなければ null。テストの待ち合わせ用)。</summary>
    public Task? Running { get; private set; }

    /// <summary>書き出しの中身の差し替え(テスト用)。null なら本物(RenderExportPipeline)を実行のたびに作る。</summary>
    public IExportPipeline? Pipeline { get; set; }

    public void Attach(AppShell shell)
    {
        _shell = shell;
        _lastFolder = shell.Settings.DefaultSaveDir.Length > 0 ? shell.Settings.DefaultSaveDir : null;
        _importer = new ImageImporter(shell.ImportTarget, shell.Log, fileSystem: shell.Services.FileSystem);
        shell.Router.Register(ShortcutAction.OpenBatch, _ =>
        {
            if (shell.Dialogs.IsOpen) return false;
            Show();
            return true;
        });
        shell.Dialogs.AllowDrop = true;
        shell.Dialogs.DragEnter += OnDragOver;
        shell.Dialogs.DragOver += OnDragOver;
        shell.Dialogs.Drop += OnDrop;
        shell.Session.Changed += OnSessionChanged;
        shell.Recovery?.RegisterBeforeRecreate(() => _runner?.SuspendForDeviceLoss() ?? Task.CompletedTask);
    }

    /// <summary>バッチ画面を開く。</summary>
    public void Show()
    {
        var shell = _shell;
        if (shell.Dialogs.IsOpen) return;
        var view = new BatchView();
        view.SetFolder(_lastFolder);
        view.SetLayersIgnored(HasOtherLayers);
        view.Close.Click += (_, _) => shell.Dialogs.Close(null, MotionCause.Pointer);
        view.OpenFiles.Click += (_, _) => OpenFiles(view);
        view.PickFolder.Click += (_, _) => PickFolder(view);
        view.RunButton.Click += (_, _) => Start(view);
        view.CancelButton.Click += (_, _) => Cancel();
        view.CancelRequested += Cancel;
        View = view;
        _ = Track(shell.Dialogs.Show(view, "DialogBatchWidth", "DialogBatchHeight", MotionCause.Keyboard), view);
    }

    private async Task Track(Task<object?> shown, BatchView view)
    {
        await shown;
        if (ReferenceEquals(View, view)) View = null;
    }

    private bool HasOtherLayers => _shell.Session.Document is { Layers.Count: >= 2 };

    private void Post(Action action)
    {
        if (_disposed) return;
        _shell.Window.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) action();
        });
    }

    private void OnSessionChanged(SessionChange what) => Post(() => View?.SetLayersIgnored(HasOtherLayers));

    // ================================================================ 追加

    /// <summary>一覧に足す(「開く」とドロップの共通)。200 個を超えて足せなかったら、通知バーに batch.full を 1 回出す。</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        if (View is not { IsRunning: false } view) return;
        if (view.AddPaths(paths)) _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.BatchFull));
    }

    private void OpenFiles(BatchView view)
    {
        var settings = _shell.Settings;
        var initial = !string.IsNullOrEmpty(settings.LastOpenDir) ? settings.LastOpenDir : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var filters = new[] { new FileDialogFilter(ScreenTexts.Get("open.filter.images"), ImportCoordinator.ImagePatterns) };
        var paths = _shell.Services.FileDialogs.OpenFiles(initial, filters, multiselect: true);
        if (paths is null || paths.Count == 0) return;
        if (System.IO.Path.GetDirectoryName(paths[0]) is { Length: > 0 } dir) settings.LastOpenDir = dir;
        AddPaths(paths);
    }

    private void PickFolder(BatchView view)
    {
        var picked = _shell.Services.FileDialogs.PickFolder(view.Folder);
        if (picked is null) return;
        _lastFolder = picked;
        view.SetFolder(picked);
    }

    private static string[]? FilesOf(IDataObject data) =>
        data.GetDataPresent(ImportFormatNames.FileDrop, false) && data.GetData(ImportFormatNames.FileDrop) is string[] { Length: > 0 } files ? files : null;

    // バッチ画面が一番上のときだけ受ける。Handled にするので、ウィンドウ全体の受け入れ表示と取り込み(FR-3)には届かない。実行中のドロップは足さない(仮の決め)
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_shell.Dialogs.Top is not BatchView view) return;
        e.Effects = !view.IsRunning && FilesOf(e.Data) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_shell.Dialogs.Top is not BatchView) return;
        e.Handled = true;
        if (FilesOf(e.Data) is { } files) AddPaths(files);
    }

    // ================================================================ 実行

    private void Start(BatchView view)
    {
        if (Running is not null || view.Folder is not { } folder || view.Paths.Paths.Count == 0) return;
        var shell = _shell;
        // 文書は押した時点のスナップショット(不変の値)を使うので、実行中の編集は結果に効かない(仮の決め)
        var job = new BatchJob(view.Paths.Paths.ToArray(), folder, shell.Session.Document, shell.Frames.K);
        var cts = new CancellationTokenSource();
        _cts = cts;
        view.BeginRun();
        // 通常の書き出しが動いていても詰まらないよう、取りまとめと受け取りがふさぐスレッドを共有しない
        var scheduler = new ExportScheduler(ExportScheduler.DefaultThreadCount);
        DocumentRenderer? renderer = null;
        var fake = Pipeline;
        var runner = new BatchRunner(_importer, shell.Catalog, shell.Services.FileSystem, shell.Log, sources =>
        {
            if (fake is not null) return fake;
            var service = shell.RenderReady?.GetAwaiter().GetResult() ?? throw new InvalidOperationException("描画のデバイスが無い");
            renderer = new DocumentRenderer(service, sources, catalog: shell.Catalog) { TextRasterizer = shell.TextRasterizer };
            return new RenderExportPipeline(renderer, sources, shell.Catalog, shell.Log, perf: shell.Services.Perf);
        }, () => shell.RenderReady is { IsCompletedSuccessfully: true } ready ? ready.Result : null);
        _runner = runner;
        var work = Task.Factory.StartNew(
            () => runner.Run(job, scheduler, scheduler.DefaultParallelism, step => Post(() => view.Apply(step)), cts.Token),
            CancellationToken.None, TaskCreationOptions.None, scheduler);
        Running = work.ContinueWith(_ =>
        {
            renderer?.Dispose();
            scheduler.Dispose();
            cts.Dispose();
            Post(() =>
            {
                _cts = null;
                _runner = null;
                Running = null;
                view.EndRun();
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>取り消し(「取り消し」のボタン・実行中の Esc)。処理中の 1 個はフレームの切れ目で止まる。</summary>
    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shell.Session.Changed -= OnSessionChanged;
        _cts?.Cancel();
    }
}
