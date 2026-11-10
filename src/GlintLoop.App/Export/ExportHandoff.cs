// 渡す操作(FR-80〜FR-85・FR-88・§9.10): 書き出し帯のコピー・保存・ドラッグ欄と Ctrl+C・Ctrl+S・Esc を受け、完成したキャッシュのファイルだけを渡す(INV-9)。
// 完成前の押下は待機にする(FR-83: 新しい編集・取り込み・Esc・同じボタン・30 秒で取り消す)。渡す前に上限超過の確認(E-30)と完成ファイルの確認(E-36)をし、失敗のあとの押下は 1 回だけやり直す(E-32)。
// ファイルの操作とデコードは UI スレッドの外で行い(INV-5)、クリップボード・ダイアログ・通知は UI スレッドで行う。渡せたら未渡しを解除し(FR-13)、export_handoff を書く。
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Input;
using GlintLoop.App.Platform;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Preview;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Settings;

namespace GlintLoop.App.Export;

public sealed class ExportHandoff : IDisposable
{
    /// <summary>待機を取り消すまでの時間(FR-83)。</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private enum Op { Copy, Save }

    private sealed record Wait(Op Op, IOneShotTimer Timer, DateTimeOffset Deadline);

    /// <summary>渡すもの。Still は載せる静止画(載せ方が「ファイル」なら null)。</summary>
    private sealed record Payload(ExportCompletion Main, ExportCompletion? Still, CopyModeSetting Mode);

    private enum Prepared { Ready, MainGone, StillGone, Failed }

    private readonly AppShell _shell;
    private readonly AutoExport _auto;
    private readonly ExportBandView _view;
    private readonly CacheManager _cache;
    private readonly HandoffFiles _files;
    private readonly IFileSystem _fs;
    private readonly List<Wait> _waits = new();
    private Op? _running;
    private DateTimeOffset _runningDeadline;
    private bool _dragging;
    private bool _disposed;
    private int _copyK;
    private string? _dragConfirmedKey;
    private int _thumbnailGeneration;

    public ExportHandoff(AppShell shell, AutoExport auto, ExportBandView view, CacheManager cache)
    {
        _shell = shell;
        _auto = auto;
        _view = view;
        _cache = cache;
        _fs = shell.Services.FileSystem;
        _files = new HandoffFiles(shell.Services.Paths.CacheHandoffDir, shell.Services.Clock, _fs);

        _view.Copy.Click += (_, _) => PressCopy();
        _view.Save.Click += (_, _) => PressSave();
        _view.DragStarted += StartDrag;
        shell.Router.Register(ShortcutAction.Copy, _ => OnShortcut(PressCopy));
        shell.Router.Register(ShortcutAction.SaveExport, _ => OnShortcut(PressSave));
        shell.Router.Escape.Register(EscapeTarget.HandoffWait, () => _waits.Count > 0, CancelWaits);
        _auto.Changed += OnAutoChanged;
        _auto.Completed += OnCompleted;
        _auto.StillCompleted += _ => Pump();
        Refresh();
    }

    /// <summary>ピクチャフォルダ(FR-84 の既定のフォルダの最後の候補)。テストは一時フォルダに差し替える(INV-18)。</summary>
    public Func<string> PicturesFolder { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>コピーを待機しているか。</summary>
    public bool IsCopyWaiting => Waiting(Op.Copy) is not null;

    /// <summary>保存を待機しているか。</summary>
    public bool IsSaveWaiting => Waiting(Op.Save) is not null;

    /// <summary>進行中の渡す操作(無ければ null。テストの待ち合わせ用)。</summary>
    public Task? Busy { get; private set; }

    /// <summary>サムネイルのデコードの途中の Task(テストの待ち合わせ用)。</summary>
    public Task? PendingThumbnail { get; private set; }

    /// <summary>完成のファイルがまだ完成のままか(E-36。UI スレッドの外で呼ぶ)。</summary>
    public bool IsIntact(ExportCacheEntry entry) => _files.IsIntact(entry);

    private Wait? Waiting(Op op) => _waits.Find(w => w.Op == op);

    private bool OnShortcut(Action press)
    {
        if (!_shell.Session.HasDocument || _shell.Dialogs.IsOpen) return false;
        press();
        return true;
    }

    // ================================================================ 押下と待機(FR-83)

    /// <summary>「コピー」・Ctrl+C。</summary>
    public void PressCopy() => Press(Op.Copy);

    /// <summary>「保存」・Ctrl+S。</summary>
    public void PressSave() => Press(Op.Save);

    private void Press(Op op)
    {
        if (_disposed || !_shell.Session.HasDocument || _running == op) return;
        if (Waiting(op) is { } already)
        {
            Remove(already);
            Refresh();
            return;
        }
        // 失敗のあとは、押下で 1 回だけやり直す(E-32)
        if (_auto.State == ExportState.Failed) _auto.Retry();
        if (op == Op.Copy) _copyK = _shell.Frames.K;
        AddWait(op);
        Pump();
    }

    // deadline は待機をやめる時刻(既定は 30 秒後)。E-36 で待機に戻すときは最初の押下の時刻を引き継ぎ、やり直しが続いても際限なく繰り返さない
    private void AddWait(Op op, DateTimeOffset? deadline = null)
    {
        var now = _shell.Services.Clock.UtcNow;
        var end = deadline ?? now + WaitLimit;
        if (end <= now) return;
        var timer = _shell.Services.Timers.Create();
        var wait = new Wait(op, timer, end);
        timer.Elapsed += () =>
        {
            Remove(wait);
            Refresh();
        };
        timer.Start(end - now);
        _waits.Add(wait);
    }

    private void Remove(Wait wait)
    {
        _waits.Remove(wait);
        wait.Timer.Dispose();
    }

    /// <summary>文書の有無が変わったかもしれないとき(ボタンの有効無効を入れ直す)。</summary>
    public void OnSessionChanged() => Refresh();

    /// <summary>待機をすべて取り消す(新しい編集・取り込み・Esc)。クリップボードと保存先は変えない。</summary>
    public void CancelWaits()
    {
        foreach (var w in _waits.ToArray()) Remove(w);
        Refresh();
    }

    private void OnAutoChanged()
    {
        // やり直しが失敗したら待機をやめる(次の押下でまた 1 回だけやり直す。E-32)
        if (_auto.State == ExportState.Failed) CancelWaits();
        if (!_auto.IsReady) ClearThumbnail();
        Refresh();
    }

    private void OnCompleted(ExportCompletion completion)
    {
        LoadThumbnail(completion.Entry);
        Pump();
    }

    /// <summary>待機している操作のうち、渡せるものを 1 つ始める。</summary>
    private void Pump()
    {
        if (_disposed || _running is not null || _dragging)
        {
            Refresh();
            return;
        }
        foreach (var wait in _waits.ToArray())
        {
            if (Ready(wait.Op) is not { } payload) continue;
            Remove(wait);
            _runningDeadline = wait.Deadline;
            Start(wait.Op, payload);
            break;
        }
        Refresh();
    }

    // いま渡せるなら、渡すものを返す。画像の載せ方の静止画が無ければ作り始めて null を返す(§9.10 の 2 の 1)
    private Payload? Ready(Op op)
    {
        if (!_auto.IsCurrent || _auto.Entry is not { } entry || _auto.Outcome is not { } outcome) return null;
        var main = new ExportCompletion(entry, outcome, false);
        var mode = _shell.Settings.CopyMode;
        if (op == Op.Save || mode == CopyModeSetting.File) return new Payload(main, null, mode);
        var k = _shell.Frames.IsPlaying ? _copyK : _shell.Frames.K;
        if (_auto.StillFor(k) is { } still) return new Payload(main, still, mode);
        _auto.RequestStill(k);
        return null;
    }

    private void Start(Op op, Payload payload)
    {
        _running = op;
        var task = Guard(op == Op.Copy ? () => RunCopy(payload) : () => RunSave(payload));
        if (!task.IsCompleted) Busy = task;
    }

    private async Task Guard(Func<Task> body)
    {
        try
        {
            await body();
        }
        finally
        {
            _running = null;
            Busy = null;
            Pump();
        }
    }

    private static async Task<T> Background<T>(Func<T> work) => await Task.Run(work);

    // ================================================================ 確認(E-30)

    private async Task<bool> ConfirmOver(ExportOutcome outcome) =>
        !outcome.SizeFail || await ExportConfirmOverView.AskAsync(_shell.Dialogs);

    // 渡す前の完成の確認で、完成の扱いを取り消して、押された操作を待機にする(E-36)
    private void Rewait(Op op, Prepared why)
    {
        if (why == Prepared.StillGone) _auto.InvalidateStill();
        else _auto.Retry();
        AddWait(op, _runningDeadline);
    }

    private bool StillValid(Payload p) => !_disposed && _auto.IsCurrent && ReferenceEquals(_auto.Entry, p.Main.Entry);

    private void Fail(LogExportFailReason reason)
    {
        _shell.Log.Write(LogEvents.ExportFail(reason));
        _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.ExportFailed));
    }

    private void Delivered(LogHandoffKind kind, ExportCacheMeta meta, string format, LogCopyMode? mode)
    {
        _shell.Log.Write(LogEvents.ExportHandoff(kind, LogFormat(format), meta.Bytes, meta.Frames, mode));
        _shell.Session.MarkDelivered();
    }

    private static LogExportFormat LogFormat(string format) => format switch
    {
        ExportFormat.Png => LogExportFormat.Png,
        ExportFormat.Jpeg => LogExportFormat.Jpeg,
        ExportFormat.Gif => LogExportFormat.Gif,
        ExportFormat.Webp => LogExportFormat.Webp,
        _ => LogExportFormat.Apng,
    };

    // ================================================================ コピー(FR-80・FR-81・§9.10 の 2)

    private async Task RunCopy(Payload p)
    {
        var withFile = p.Mode != CopyModeSetting.Image;
        // 「画像」では載せる静止画が渡すもの。「両方」は、渡すファイルの結果で確認する
        if (!await ConfirmOver((withFile ? p.Main : p.Still!).Outcome)) return;
        string? file = null;
        HandoffImage? image = null;
        LogExportFailReason? failure = null;
        var prepared = await Background(() =>
        {
            if (withFile && !_files.IsIntact(p.Main.Entry)) return Prepared.MainGone;
            if (p.Still is { } still && !_files.IsIntact(still.Entry)) return Prepared.StillGone;
            try
            {
                if (p.Still is { } s) image = HandoffImage.FromPng(_fs.ReadAllBytes(s.Entry.Path));
                if (withFile)
                {
                    file = _files.Materialize(p.Main.Entry);
                    _cache.Touch(p.Main.Entry);
                }
                if (p.Still is { } t) _cache.Touch(t.Entry);
                return Prepared.Ready;
            }
            catch (OutputDecodeException)
            {
                failure = LogExportFailReason.EncodeError;
                return Prepared.Failed;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failure = LogExportFailReason.WriteError;
                return Prepared.Failed;
            }
        });
        if (_disposed) return;
        switch (prepared)
        {
            case Prepared.MainGone or Prepared.StillGone:
                Rewait(Op.Copy, prepared);
                return;
            case Prepared.Failed:
                Fail(failure!.Value);
                return;
        }
        if (!StillValid(p)) return;
        try
        {
            _shell.Services.Clipboard.Place(HandoffData.Build(file, image), true);
        }
        catch (COMException e)
        {
            // 開けないときの再試行(100 ms 間隔で 10 回)は WPF の SetDataObject が行う(§9.10 の 2 の 2)
            _shell.Log.Write(LogEvents.ClipboardFailed(unchecked((uint)e.HResult)));
            _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.ClipFailed));
            return;
        }
        // 載せた量は、ファイルがあれば実体のもの、「画像」では載せた PNG のもの(§9.10 の 2 の 6)
        var shown = withFile ? p.Main.Entry : p.Still!.Entry;
        Delivered(LogHandoffKind.Copy, shown.Meta, shown.Format, ModeOf(p.Mode));
        _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.CopyDone, ("size", NoticeFormat.KilobytesOneDecimal(shown.Meta.Bytes))));
    }

    private static LogCopyMode ModeOf(CopyModeSetting mode) => mode switch
    {
        CopyModeSetting.File => LogCopyMode.File,
        CopyModeSetting.Image => LogCopyMode.Image,
        _ => LogCopyMode.Both,
    };

    // ================================================================ 保存(FR-84・§9.10 の 4)

    /// <summary>保存ダイアログの既定のフォルダ(FR-84)。</summary>
    private string InitialSaveFolder()
    {
        var s = _shell.Settings;
        foreach (var dir in new[] { s.DefaultSaveDir, s.LastSaveDir })
        {
            if (dir.Length == 0) continue;
            try
            {
                if (_fs.DirectoryExists(dir)) return dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return PicturesFolder();
    }

    private async Task RunSave(Payload p)
    {
        var entry = p.Main.Entry;
        if (!await ConfirmOver(p.Main.Outcome)) return;
        if (!await Background(() => _files.IsIntact(entry)))
        {
            if (!_disposed) Rewait(Op.Save, Prepared.MainGone);
            return;
        }
        var folder = await Background(InitialSaveFolder);
        if (_disposed) return;
        var filter = new FileDialogFilter(BandTexts.FormatName(entry.Format), "*." + ExportFormat.Extension(entry.Format));
        var chosen = _shell.Services.FileDialogs.SaveFile(folder, _files.FileName(entry.Format), [filter]);
        if (chosen is null) return; // E-35
        var saved = await Background<string?>(() =>
        {
            try
            {
                // 取り込んだファイルには書かない(INV-2)
                if (_shell.ImportTarget.IsImportedPath(chosen)) return null;
                _files.CopyTo(entry, chosen);
                _cache.Touch(entry);
                return Path.GetDirectoryName(Path.GetFullPath(chosen)) ?? "";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
        });
        if (_disposed) return;
        if (saved is null)
        {
            _shell.Log.Write(LogEvents.ExportFail(LogExportFailReason.WriteError));
            _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.SaveFailed));
            return;
        }
        _shell.Settings.LastSaveDir = saved;
        Delivered(LogHandoffKind.Save, entry.Meta, entry.Format, null);
        _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.SaveDone));
    }

    // ================================================================ ドラッグ(FR-82・§9.10 の 3)

    /// <summary>ドラッグ欄から動かし始めたとき。完成前(INV-9)と、ほかの操作の途中は何もしない。</summary>
    public void StartDrag()
    {
        if (_disposed || _running is not null || _dragging || !_auto.IsCurrent || _auto.Entry is not { } entry || _auto.Outcome is not { } outcome) return;
        _dragging = true;
        var task = RunDrag(new ExportCompletion(entry, outcome, false));
        if (!task.IsCompleted) Busy = task;
    }

    private async Task RunDrag(ExportCompletion main)
    {
        try
        {
            var entry = main.Entry;
            if (main.Outcome.SizeFail && _dragConfirmedKey != entry.Key)
            {
                if (!await ConfirmOver(main.Outcome)) return;
                // 確認のあいだにマウスのボタンは離れるので、案内を出して、同じファイルのもう一度のドラッグを確認なしで渡す(AC-57 の (1))
                _dragConfirmedKey = entry.Key;
                if (!_disposed) _shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.ExportDragAgain));
                return;
            }
            string? file = null;
            var prepared = await Background(() =>
            {
                if (!_files.IsIntact(entry)) return Prepared.MainGone;
                try
                {
                    file = _files.Materialize(entry);
                    return Prepared.Ready;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return Prepared.Failed;
                }
            });
            if (_disposed) return;
            if (prepared == Prepared.MainGone)
            {
                // ドラッグは待機にできないので、完成の扱いを取り消すだけにする(ドラッグ欄が無効になる。E-36)
                _auto.Retry();
                return;
            }
            if (prepared == Prepared.Failed)
            {
                Fail(LogExportFailReason.WriteError);
                return;
            }
            if (!_auto.IsCurrent || !ReferenceEquals(_auto.Entry, entry)) return;
            DragDropEffects effect;
            using (_cache.ProtectFromTrim(file!))
            {
                effect = _shell.Services.DragRunner.Run(_view.Drag, HandoffData.Build(file, null), DragDropEffects.Copy);
            }
            if (effect == DragDropEffects.None) return; // E-38
            _dragConfirmedKey = null;
            Delivered(LogHandoffKind.Drag, entry.Meta, entry.Format, null);
        }
        finally
        {
            _dragging = false;
            Busy = null;
            Pump();
        }
    }

    // ================================================================ 帯の表示

    private void Refresh()
    {
        if (_disposed) return;
        _view.SetHandoff(_shell.Session.HasDocument, IsCopyWaiting, IsSaveWaiting, _auto.IsReady);
    }

    // ドラッグ欄のサムネイル(§9.3)。読めなくても渡す操作には関わらないので、出さないだけにする
    private void LoadThumbnail(ExportCacheEntry entry)
    {
        var gen = ++_thumbnailGeneration;
        var r = AppResources.LayoutTokens;
        // 高い DPI でもぼけないよう、欄の 2 倍の画素で持つ
        var maxW = (int)(double)r["DragAreaWidth"] * 2;
        var maxH = (int)(double)r["DragAreaHeight"] * 2;
        PendingThumbnail = LoadThumbnailAsync(entry, gen, maxW, maxH);
    }

    private async Task LoadThumbnailAsync(ExportCacheEntry entry, int gen, int maxW, int maxH)
    {
        // 書き出しと同じ順番に並べる(読んでいる間に E-36 のやり直しが同じファイルを置き換えると、そのリネームが失敗する)
        var bitmap = await _auto.Enqueue<BitmapSource?>(() =>
        {
            try
            {
                var d = OutputFrameDecoder.Thumbnail(_fs.ReadAllBytes(entry.Path), maxW, maxH);
                var b = BitmapSource.Create(d.Width, d.Height, 96, 96, PixelFormats.Bgra32, null, d.Frames[0].Bgra, d.Width * 4);
                b.Freeze();
                return b;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutputDecodeException)
            {
                return null;
            }
        });
        if (_disposed || gen != _thumbnailGeneration) return;
        PendingThumbnail = null;
        _view.DragThumbnail.Source = bitmap;
    }

    private void ClearThumbnail()
    {
        _thumbnailGeneration++;
        PendingThumbnail = null;
        _view.DragThumbnail.Source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _auto.Changed -= OnAutoChanged;
        _auto.Completed -= OnCompleted;
        foreach (var w in _waits.ToArray()) Remove(w);
    }
}
