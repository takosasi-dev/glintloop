// ファイルシステムの差し込み口(§11 Phase 5 の前書き・INV-18)。製品は RealFileSystem.Instance、テストは指定した操作で例外を投げられる偽物を渡す。
// 読み取りは必ず FileAccess.Read かつ FileShare.ReadWrite で開く(INV-2。元のファイルを書き換えず、ほかのアプリが開いていても読める)。
// 書き込みは AtomicFile が Create と Move を組み合わせて行う(INV-16)。ログの追記だけが OpenAppend を使う。
namespace GlintLoop.Core.Platform;

public interface IFileSystem
{
    /// <summary>読み取り専用で開く(FileAccess.Read・FileShare.ReadWrite。INV-2)。</summary>
    Stream OpenRead(string path);

    /// <summary>作るか空にして開く(FileAccess.Write・FileShare.None)。一時ファイルの書き込みに使う。</summary>
    Stream Create(string path);

    /// <summary>末尾に足す形で開く(FileAccess.Write・FileShare.ReadWrite と Delete)。ログの追記に使う。</summary>
    Stream OpenAppend(string path);

    /// <summary>名前を変える。移動先に同じ名前のファイルがあれば置き換える。</summary>
    void Move(string source, string destination);

    /// <summary>消す(ファイルが無くても例外にしない)。</summary>
    void Delete(string path);

    bool Exists(string path);

    bool DirectoryExists(string path);

    /// <summary>フォルダを作る(途中のフォルダも。既にあれば何もしない)。</summary>
    void CreateDirectory(string path);

    /// <summary>フォルダの直下のファイルの絶対パス(下のフォルダは見ない)。pattern は * を使う名前の形。</summary>
    IEnumerable<string> EnumerateFiles(string directory, string pattern);

    long GetLength(string path);

    /// <summary>全体を読む(OpenRead と同じ開き方。INV-2)。</summary>
    byte[] ReadAllBytes(string path);

    DateTime GetLastWriteUtc(string path);

    void SetLastWriteUtc(string path, DateTime utc);
}

/// <summary>本物のファイルシステム。</summary>
public sealed class RealFileSystem : IFileSystem
{
    public static RealFileSystem Instance { get; } = new();

    private RealFileSystem()
    {
    }

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public Stream Create(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    public Stream OpenAppend(string path) => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    public void Move(string source, string destination) => File.Move(source, destination, overwrite: true);

    public void Delete(string path) => File.Delete(path);

    public bool Exists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);

    public long GetLength(string path) => new FileInfo(path).Length;

    public byte[] ReadAllBytes(string path)
    {
        using var s = OpenRead(path);
        if (s.Length > Array.MaxLength) throw new IOException("ファイルが大きすぎて 1 つの配列に読めない");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    public DateTime GetLastWriteUtc(string path) => File.GetLastWriteTimeUtc(path);

    public void SetLastWriteUtc(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);
}
