// ファイルの書き込みを、同じフォルダの .tmp に書いてからリネームで置き換える(INV-16)。設定・ルック・プリセットの保存が使う。
// 書く途中で例外が起きたら .tmp を消して元のファイルを残し、例外は呼び出し側へ返す。起動時の .tmp の掃除もここに置く。
namespace GlintLoop.Core.Platform;

public static class AtomicFile
{
    public const string TempExtension = ".tmp";

    /// <summary>path の一時ファイルの名前(同じフォルダ、名前の末尾に .tmp)。</summary>
    public static string TempPathFor(string path) => path + TempExtension;

    /// <summary>bytes を path に書く。write には一時ファイルのストリームを渡す(テストが途中で例外を起こせるように)。</summary>
    public static void Write(string path, Action<Stream> write, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? RealFileSystem.Instance;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) fs.CreateDirectory(dir);
        var tmp = TempPathFor(path);
        try
        {
            using (var s = fs.Create(tmp))
            {
                write(s);
                // 偽のファイルシステムは FileStream を包んで返すことがある(そのときは普通の Flush でよい)
                if (s is FileStream file) file.Flush(flushToDisk: true);
                else s.Flush();
            }
            fs.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp, fs);
            throw;
        }
    }

    public static void WriteAllBytes(string path, byte[] bytes, IFileSystem? fileSystem = null) =>
        Write(path, s => s.Write(bytes, 0, bytes.Length), fileSystem);

    /// <summary>dirs の直下の *.tmp を消す(起動時。INV-16)。消せないファイルは飛ばす。消した個数を返す。</summary>
    public static int SweepTemp(IEnumerable<string> dirs, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? RealFileSystem.Instance;
        var removed = 0;
        foreach (var dir in dirs)
        {
            string[] files;
            try
            {
                if (!fs.DirectoryExists(dir)) continue;
                files = fs.EnumerateFiles(dir, "*" + TempExtension).ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files)
            {
                if (TryDelete(f, fs)) removed++;
            }
        }
        return removed;
    }

    private static bool TryDelete(string path, IFileSystem fs)
    {
        try
        {
            if (!fs.Exists(path)) return false;
            fs.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
