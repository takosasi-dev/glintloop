// 起動時の片付け(仕様書 §9.11 の 1・INV-16)。各フォルダの .tmp を消し、退避(recovery)をファイル名の降順で 5 個だけ残す。
// 多重起動の判定に勝った起動だけが呼ぶ(FR-92)。消せないファイルは飛ばし、例外にしない。
namespace GlintLoop.Core.Platform;

public static class StartupMaintenance
{
    /// <summary>退避を残す個数(§9.11 の 1)。</summary>
    public const int RecoveryKeep = 5;

    public static void Run(AppPaths paths, IFileSystem? fileSystem = null)
    {
        AtomicFile.SweepTemp(paths.TempSweepDirs, fileSystem);
        TrimRecovery(paths.RecoveryDir, fileSystem);
    }

    /// <summary>recovery-*.glint をファイル名の降順に並べ、先頭の 5 個を残してほかを消す。消した個数を返す。</summary>
    public static int TrimRecovery(string dir, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? RealFileSystem.Instance;
        string[] files;
        try
        {
            if (!fs.DirectoryExists(dir)) return 0;
            files = fs.EnumerateFiles(dir, "recovery-*.glint").ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        var removed = 0;
        foreach (var f in files.OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(RecoveryKeep))
        {
            try
            {
                fs.Delete(f);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }
}
