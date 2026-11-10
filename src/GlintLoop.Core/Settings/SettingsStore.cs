// 設定の読み込みと保存(FR-90・§9.11 の 2・INV-16)。変更は即座にモデルへ入り、ファイルへは最後の変更から 500 ms 後に 1 回だけ書く。
// 書くときは settings.json.tmp を経由する(AtomicFile)。JSON として壊れたファイルと schema が 1 でないファイルは settings.json.bad に改名して既定で始める(E-42)。
// 保存に失敗したら SaveFailed を出す(AppShell が通知バーに、設定画面が上端の帯に settings.save_failed を出す。E-43)。タイマーは注入する(テストは偽のタイマー)。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;

namespace GlintLoop.Core.Settings;

public sealed class SettingsStore : IDisposable
{
    /// <summary>最後の変更から保存までの時間(FR-90)。</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly string _path;
    private readonly ILogSink _log;
    private readonly IOneShotTimer _timer;
    private readonly IFileSystem _fs;

    public SettingsStore(AppPaths paths, ITimerFactory timers, ILogSink log, IFileSystem? fileSystem = null)
    {
        _path = paths.SettingsFile;
        _log = log;
        _fs = fileSystem ?? RealFileSystem.Instance;
        _timer = timers.Create();
        _timer.Elapsed += OnTimer;
        Settings.Changed += OnChanged;
    }

    public AppSettings Settings { get; } = new();

    public string FilePath => _path;

    /// <summary>保存を待っているか(最後の変更から 500 ms 経っていない)。</summary>
    public bool SavePending => _timer.IsRunning;

    /// <summary>保存した回数(診断とテスト用)。</summary>
    public int SaveCount { get; private set; }

    /// <summary>保存に失敗したとき。</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>ファイルから読む(無ければ既定のまま)。変更の通知と保存の予約は起こさない。</summary>
    public SettingsLoadStatus Load()
    {
        byte[] bytes;
        try
        {
            if (!_fs.Exists(_path)) return SettingsLoadStatus.Missing;
            bytes = _fs.ReadAllBytes(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write(LogEvents.SettingsInvalid(LogSettingsKey.File));
            return SettingsLoadStatus.Broken;
        }
        var status = SettingsJson.Read(bytes, Settings, _log);
        if (status is SettingsLoadStatus.WrongSchema or SettingsLoadStatus.Broken)
        {
            try
            {
                _fs.Move(_path, _path + ".bad");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return status;
    }

    /// <summary>待っている保存があれば、今すぐ書く(終了時)。</summary>
    public void Flush()
    {
        if (!_timer.IsRunning) return;
        _timer.Stop();
        SaveNow();
    }

    /// <summary>今すぐ書く。成功したら true。</summary>
    public bool SaveNow()
    {
        try
        {
            AtomicFile.WriteAllBytes(_path, SettingsJson.Serialize(Settings), _fs);
            SaveCount++;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SaveFailed?.Invoke(e);
            return false;
        }
    }

    private void OnChanged(string key) => _timer.Start(SaveDelay);

    private void OnTimer() => SaveNow();

    public void Dispose()
    {
        Settings.Changed -= OnChanged;
        _timer.Elapsed -= OnTimer;
        _timer.Dispose();
    }
}
