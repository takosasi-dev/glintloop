// 取り込みの操作(貼り付け・ドロップ・開く・サンプル・起動引数。FR-1〜FR-7・FR-14)を ImageImporter につなぐ。デコードは別のスレッドで行い、UI スレッドを止めない(INV-5)。.glint は ProjectCoordinator へ渡す。
// 取り込みは同時に 1 つで、ダイアログの表示中と取り込みの処理中の新しい操作は無視する(FR-2・E-20・§9.3 の規則 (1))。200 ms を超えたら Slow を立てる(ウェルカム画面の進捗リング)。
// 結果の通知は通知バーに出し、最初の表示が済んだら(WaitForDisplay。既定は Dispatcher が一巡したあと)CompleteDisplay で import_ok を書いて門を出る。
using System.Windows;
using System.Windows.Threading;
using GlintLoop.App.Platform;
using GlintLoop.App.Startup;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Project;

namespace GlintLoop.App.Screens;

public sealed class ImportCoordinator
{
    /// <summary>進捗リングを出すまでの時間(§9.3 の画面の表のウェルカム画面の「処理中」)。</summary>
    public static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(200);

    /// <summary>開くダイアログの絞り込みのパターン(FR-8 の 6 形式。形式の判定は拡張子でなく先頭バイトで行う)。</summary>
    public const string ImagePatterns = "*.png;*.jpg;*.jpeg;*.jpe;*.webp;*.bmp;*.dib;*.ico;*.gif";

    /// <summary>開くダイアログの絞り込みのパターン(FR-6。画像と .glint)。</summary>
    public const string OpenPatterns = ImagePatterns + ";*" + GlintProjectFile.FileExtension;

    private readonly AppShell _shell;
    private bool _running;
    private bool _slow;

    public ImportCoordinator(AppShell shell)
    {
        _shell = shell;
        Importer = new ImageImporter(shell.ImportTarget, shell.Log, fileSystem: shell.Services.FileSystem) { Clock = shell.Services.Clock };
        Projects = new ProjectCoordinator(shell, this);
    }

    /// <summary>取り込みの本体(門は置き換えの確認ダイアログと共有する)。</summary>
    public ImageImporter Importer { get; }

    public ImportGate Gate => Importer.Gate;

    /// <summary>.glint の保存と開く。</summary>
    public ProjectCoordinator Projects { get; }

    /// <summary>取り込みの処理中か(読み込みを始めてから、最初の表示が済むまで)。</summary>
    public bool IsImporting => _running;

    /// <summary>取り込みが 200 ms を超えて続いているか(進捗リングを出す)。</summary>
    public bool IsSlow => _slow;

    /// <summary>IsImporting か IsSlow が変わったとき(UI スレッド)。</summary>
    public event Action? StateChanged;

    /// <summary>1 回の取り込みが終わったとき(門を出たあと。UI スレッド。テストと統合が使う)。</summary>
    public event Action<ImportOutcome>? Completed;

    /// <summary>
    /// 足せた画像の最初の表示が済むのを待つ口(キャンバスの担当が、プロキシを WriteableBitmap に載せ終えたら完了する Task を返す)。
    /// null のあいだは、Dispatcher が描画の優先度まで一巡したところで済んだとみなす(仮の決め)。
    /// </summary>
    public Func<ImportOutcome, Task>? WaitForDisplay { get; set; }

    /// <summary>新しい取り込みを始められるか(ダイアログの表示中・取り込みの処理中・確認ダイアログの門の中は不可)。</summary>
    public bool CanStart => !_running && !_shell.Dialogs.IsOpen && !Gate.IsBusy;

    /// <summary>貼り付け(Ctrl+V・Ctrl+Shift+V・「画像を貼り付け」)。クリップボードは UI スレッドで読み、デコードは別のスレッドで行う。</summary>
    public Task<ImportOutcome?> PasteAsync()
    {
        if (!CanStart) return Task.FromResult<ImportOutcome?>(null);
        // import_ok の ms は操作を受け取った時刻から測る(クリップボードを読む時間を含める。§9.11)
        var received = Now;
        IImportData data;
        try
        {
            data = _shell.Services.Clipboard.Read();
        }
        catch (ImportDataUnavailableException e)
        {
            data = new UnavailableData(e);
        }
        return RunAsync(() => Importer.Paste(data, received));
    }

    private TimeSpan Now => _shell.Services.Clock.Monotonic;

    /// <summary>ドロップ(FR-3〜FR-5)。データは UI スレッドで読み切ってから渡す。</summary>
    public Task<ImportOutcome?> DropAsync(IImportData data) => DropAsync(data, Now);

    private Task<ImportOutcome?> DropAsync(IImportData data, TimeSpan received)
    {
        if (!CanStart) return Task.FromResult<ImportOutcome?>(null);
        return RunAsync(() => Importer.Drop(data, received));
    }

    /// <summary>WPF のドロップのデータ(IDataObject)から取り込む。</summary>
    public Task<ImportOutcome?> DropAsync(IDataObject data)
    {
        if (!CanStart) return Task.FromResult<ImportOutcome?>(null);
        var received = Now;
        return DropAsync(DataObjectImportData.From(data), received);
    }

    /// <summary>
    /// 開く(Ctrl+O・「開く」。FR-6)。既定のフォルダは前回開いたフォルダ(無ければピクチャフォルダ)。選んだら、そのフォルダを lastOpenDir に入れる。
    /// </summary>
    public Task<ImportOutcome?> OpenAsync()
    {
        if (!CanStart) return Task.FromResult<ImportOutcome?>(null);
        var settings = _shell.Settings;
        var initial = !string.IsNullOrEmpty(settings.LastOpenDir) ? settings.LastOpenDir : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var filters = new[] { new FileDialogFilter(ScreenTexts.Get("open.filter.images_project"), OpenPatterns) };
        var paths = _shell.Services.FileDialogs.OpenFiles(initial, filters, multiselect: true);
        if (paths is null || paths.Count == 0) return Task.FromResult<ImportOutcome?>(null);
        if (System.IO.Path.GetDirectoryName(paths[0]) is { Length: > 0 } dir) settings.LastOpenDir = dir;
        return OpenPathsAsync(paths);
    }

    /// <summary>選んだファイルの一覧を取り込む(開くダイアログの結果。扱いは FR-4)。</summary>
    public Task<ImportOutcome?> OpenPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanStart) return Task.FromResult<ImportOutcome?>(null);
        var received = Now;
        return RunAsync(() => Importer.Open(paths, received));
    }

    /// <summary>「サンプルで試す」(FR-14)。</summary>
    public Task<ImportOutcome?> SampleAsync()
    {
        if (!CanStart) return Task.FromResult<ImportOutcome?>(null);
        return RunAsync(() => Importer.Sample());
    }

    /// <summary>起動引数(FR-7)。最初の 1 個だけを使う。取り込めないときはウェルカム画面のまま、通知バーにエラーを出す。</summary>
    public Task<ImportOutcome?> StartupArgsAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !CanStart) return Task.FromResult<ImportOutcome?>(null);
        var received = Now;
        return RunAsync(() => Importer.StartupArgs(args, received));
    }

    private async Task<ImportOutcome?> RunAsync(Func<ImportOutcome> work)
    {
        _running = true;
        StateChanged?.Invoke();
        var timer = _shell.Services.Timers.Create();
        timer.Elapsed += () =>
        {
            if (!_running) return;
            _slow = true;
            StateChanged?.Invoke();
        };
        timer.Start(SlowThreshold);
        try
        {
            var outcome = await Task.Run(work);
            var perf = _shell.Services.Perf;
            // 測定の記録の gallery は、import_ok を書く取り込み(画像を足した。サンプルを除く)だけを測る(§6.8)
            if (perf is not null && outcome is { Added: > 0, Via: not ImportVia.Sample, ReceivedAt: { } received }) perf.ImportApplied(received);
            if (outcome.Notice is { } n) _shell.NoticeBar.Show(n);
            try
            {
                // .glint は読み込みと確認を挟む。開かなかったときは最初の表示を待たない
                var display = outcome.Status == ImportStatus.ProjectRequested ? await Projects.OpenAsync(outcome.ProjectPath!) : outcome.AwaitingDisplay;
                if (display) await (WaitForDisplay?.Invoke(outcome) ?? DefaultDisplayWait());
            }
            finally
            {
                outcome.CompleteDisplay();
                if (outcome.DisplayMs is { } ms) perf?.ImportDisplayed(ms);
            }
            Completed?.Invoke(outcome);
            return outcome;
        }
        finally
        {
            timer.Stop();
            timer.Dispose();
            _running = false;
            _slow = false;
            StateChanged?.Invoke();
        }
    }

    // 文書の変更の通知(別のスレッドから Dispatcher に回したもの)と、そのあとのレイアウトと描画が一巡するまで待つ
    private static async Task DefaultDisplayWait() => await Dispatcher.Yield(DispatcherPriority.Loaded);

    /// <summary>クリップボードを開けなかったときのデータ(読むと ImportDataUnavailableException。取り込みが clip.failed にする)。</summary>
    private sealed class UnavailableData(ImportDataUnavailableException error) : IImportData
    {
        public IReadOnlyList<string> Formats => throw error;

        public byte[]? GetBytes(string format) => throw error;

        public IReadOnlyList<string>? GetFileList() => throw error;

        public bool HasUrlOrText => throw error;
    }
}
