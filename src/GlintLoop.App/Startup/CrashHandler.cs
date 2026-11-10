// 処理されない例外の処理(FR-104・E-47・AC-50): 入口は Handle の 1 つで、順序は crash のログ → 退避の .glint → ダイアログ → app_exit のログ → 終了の依頼(コード 1)。
// どの段が失敗しても次の段へ進み、終了の依頼まで必ず行く。再入は別のスレッドからでも何もせずに戻る。ログ・ファイルシステム・ダイアログ・終了・時計はすべて口の越しに使う。
using System.Globalization;
using System.IO;
using GlintLoop.App.Text;
using GlintLoop.Codec;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Startup;

/// <summary>処理されない例外の発生元。どちらでも動きは同じ。</summary>
public enum CrashSource { UiThread, AppDomain }

/// <summary>処理されない例外の入口(FR-104)。担当 gpu の「処理されない例外として扱う」もここを呼ぶ。</summary>
public interface ICrashHandler
{
    void Handle(Exception exception, CrashSource source);
}

/// <summary>crash.title のダイアログ。閉じるまで戻らない。</summary>
public interface ICrashDialog
{
    void Show(string title, string body, IReadOnlyList<string> buttons);
}

/// <summary>プロセスの終了の依頼。</summary>
public interface IProcessExit
{
    void Exit(int code);
}

/// <summary>未処理例外の発生元。起動の処理が、UI スレッド用と AppDomain 用に 1 個ずつハンドラーを登録する。</summary>
public interface IUnhandledExceptionSource
{
    event Action<Exception>? UiThreadException;

    event Action<Exception>? AppDomainException;
}

/// <summary>退避する内容。文書と、その画像(元のバイト列を持つ)。</summary>
public sealed record CrashSnapshot(SessionDocument Document, IReadOnlyDictionary<string, ImportedImage> Images);

/// <summary>退避の .glint を path に書く処理。.tmp を経由し、失敗は例外で返す。</summary>
public delegate void CrashRecoverySaver(string path, CrashSnapshot snapshot);

public sealed class CrashHandler : ICrashHandler
{
    private const int ExitCode = 1;

    private readonly ILogSink _log;
    private readonly ICrashDialog _dialog;
    private readonly IProcessExit _exit;
    private readonly IClock _clock;
    private readonly string _recoveryDir;
    private readonly Func<CrashSnapshot?> _snapshot;
    private readonly CrashRecoverySaver _saver;
    private int _entered;

    /// <summary>snapshot は文書が無ければ null を返す。saver の既定は GlintProjectFile.Save(テストは例外を投げる偽物を渡せる)。</summary>
    public CrashHandler(ILogSink log, IFileSystem fileSystem, ICrashDialog dialog, IProcessExit processExit, IClock clock,
        string recoveryDir, string appVersion, Func<CrashSnapshot?> snapshot, CrashRecoverySaver? saver = null)
    {
        _log = log;
        _dialog = dialog;
        _exit = processExit;
        _clock = clock;
        _recoveryDir = recoveryDir;
        _snapshot = snapshot;
        _saver = saver ?? ((path, s) => GlintProjectFile.Save(path, s.Document, s.Images, clock, appVersion, fileSystem));
    }

    /// <summary>製品の組み立て: シェルのログ・文書・画像と、ShellServices の口を使う。</summary>
    public static CrashHandler ForShell(AppShell shell)
    {
        var s = shell.Services;
        return new CrashHandler(shell.Log, s.FileSystem, s.CrashDialog, s.ProcessExit, s.Clock, s.Paths.RecoveryDir, s.Version,
            () => shell.Session.Document is { } doc ? new CrashSnapshot(doc, shell.ImportTarget.Images) : null);
    }

    /// <summary>退避のファイルの名前(§9.11 の 1。時刻はローカル)。</summary>
    public static string RecoveryFileName(DateTimeOffset utcNow, TimeZoneInfo zone) =>
        "recovery-" + TimeZoneInfo.ConvertTime(utcNow, zone).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + GlintProjectFile.FileExtension;

    /// <summary>入口。動きは発生元によらない。2 回目以降の呼び出しは、どのスレッドからでも何もせずに戻る。</summary>
    public void Handle(Exception exception, CrashSource source)
    {
        if (Interlocked.Exchange(ref _entered, 1) != 0) return;
        Step(() => _log.Write(LogEvents.Crash(exception)));
        var saved = SaveRecovery();
        Step(() => _dialog.Show(NoticeTexts.DialogMessage(NoticeKeys.CrashTitle),
            saved ?? NoticeTexts.DialogMessage(NoticeKeys.CrashNoSave), [NoticeTexts.UiText("crash.title.exit")]));
        Step(() => _log.Write(LogEvents.AppExit(ExitCode)));
        Step(() => _exit.Exit(ExitCode));
    }

    // 退避の .glint を書く。書けたら絶対パス、文書が無いか書けなければ null(E-47)
    private string? SaveRecovery()
    {
        string? saved = null;
        Step(() =>
        {
            if (_snapshot() is not { } snapshot) return;
            var path = Path.Combine(_recoveryDir, RecoveryFileName(_clock.UtcNow, _clock.LocalZone));
            _saver(path, snapshot);
            saved = path;
        });
        return saved;
    }

    // どの段の失敗も次の段を止めない(クラッシュの処理の中なので、例外の種類を選ばない)
    private static void Step(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
        }
    }
}
