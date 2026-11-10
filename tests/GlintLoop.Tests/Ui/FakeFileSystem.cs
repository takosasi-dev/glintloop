// 偽のファイルシステム(§11 Phase 5 の前書き)。本物の一時フォルダに素通しし、指定した操作とパスの条件で例外を投げる。呼び出しを操作ごとに数え、順に記録する。
// 操作の名前は FileOps の定数(IFileSystem のメンバーと同じ名前と、書き込みの Write)。Write は Create・OpenAppend で開いたストリームへの書き込みで、書き込みの途中の失敗を起こす。
// 書き出しのスレッドと UI スレッドの両方から呼ばれるので、記録は lock で守る。
using System.IO;
using GlintLoop.Core.Platform;

namespace GlintLoop.Tests.Ui;

public static class FileOps
{
    public const string OpenRead = nameof(IFileSystem.OpenRead);
    public const string Create = nameof(IFileSystem.Create);
    public const string OpenAppend = nameof(IFileSystem.OpenAppend);
    public const string Move = nameof(IFileSystem.Move);
    public const string Delete = nameof(IFileSystem.Delete);
    public const string Exists = nameof(IFileSystem.Exists);
    public const string DirectoryExists = nameof(IFileSystem.DirectoryExists);
    public const string CreateDirectory = nameof(IFileSystem.CreateDirectory);
    public const string EnumerateFiles = nameof(IFileSystem.EnumerateFiles);
    public const string GetLength = nameof(IFileSystem.GetLength);
    public const string ReadAllBytes = nameof(IFileSystem.ReadAllBytes);
    public const string GetLastWriteUtc = nameof(IFileSystem.GetLastWriteUtc);
    public const string SetLastWriteUtc = nameof(IFileSystem.SetLastWriteUtc);

    /// <summary>Create・OpenAppend で開いたストリームへの書き込み(パスは開いたファイル)。</summary>
    public const string Write = "Write";
}

public sealed class FakeFileSystem(IFileSystem? inner = null) : IFileSystem
{
    private sealed record Rule(string Operation, Func<string, bool> When, Func<Exception> Make)
    {
        public int Skip;
    }

    private readonly IFileSystem _inner = inner ?? RealFileSystem.Instance;
    private readonly object _gate = new();
    private readonly List<Rule> _rules = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<(string Operation, string Path)> _calls = new();

    /// <summary>呼び出しのたびに(操作の名前, パス)で呼ぶ(ほかの偽物と順を 1 つに記録するときに使う)。Move のパスは移動先。</summary>
    public Action<string, string>? Observer { get; set; }

    /// <summary>OpenRead の中身の差し替え(パスに合わなければ null を返して本物に回す。偽のストリームを読ませるテスト用)。</summary>
    public Func<string, Stream?>? OpenReadOverride { get; set; }

    /// <summary>
    /// operation の呼び出しで、パスが when に合えば make の例外を投げる(when が null ならいつでも)。skip 回は素通しし、そのあと投げ続ける。
    /// Move は移動元と移動先のどちらかが when に合えば当たる。投げた呼び出しも数える。
    /// </summary>
    public void FailOn(string operation, Func<string, bool>? when = null, Func<Exception>? make = null, int skip = 0)
    {
        lock (_gate)
        {
            _rules.Add(new Rule(operation, when ?? (_ => true), make ?? (() => new IOException("偽のファイルシステムの失敗"))) { Skip = skip });
        }
    }

    /// <summary>FailOn の指定をすべて消す。</summary>
    public void ClearFailures()
    {
        lock (_gate) _rules.Clear();
    }

    /// <summary>その操作が呼ばれた回数。</summary>
    public int Count(string operation)
    {
        lock (_gate) return _counts.GetValueOrDefault(operation);
    }

    /// <summary>呼び出しの一覧(古い順)。</summary>
    public IReadOnlyList<(string Operation, string Path)> Calls
    {
        get
        {
            lock (_gate) return _calls.ToArray();
        }
    }

    private void Enter(string operation, string path, string? otherPath = null)
    {
        Exception? fail = null;
        lock (_gate)
        {
            _counts[operation] = _counts.GetValueOrDefault(operation) + 1;
            _calls.Add((operation, path));
            foreach (var r in _rules)
            {
                if (r.Operation != operation || !(r.When(path) || (otherPath is not null && r.When(otherPath)))) continue;
                if (r.Skip > 0)
                {
                    r.Skip--;
                    continue;
                }
                fail = r.Make();
                break;
            }
        }
        Observer?.Invoke(operation, path);
        if (fail is not null) throw fail;
    }

    public Stream OpenRead(string path)
    {
        Enter(FileOps.OpenRead, path);
        return OpenReadOverride?.Invoke(path) ?? _inner.OpenRead(path);
    }

    public Stream Create(string path)
    {
        Enter(FileOps.Create, path);
        return new WatchedStream(_inner.Create(path), this, path);
    }

    public Stream OpenAppend(string path)
    {
        Enter(FileOps.OpenAppend, path);
        return new WatchedStream(_inner.OpenAppend(path), this, path);
    }

    public void Move(string source, string destination)
    {
        Enter(FileOps.Move, destination, source);
        _inner.Move(source, destination);
    }

    public void Delete(string path)
    {
        Enter(FileOps.Delete, path);
        _inner.Delete(path);
    }

    public bool Exists(string path)
    {
        Enter(FileOps.Exists, path);
        return _inner.Exists(path);
    }

    public bool DirectoryExists(string path)
    {
        Enter(FileOps.DirectoryExists, path);
        return _inner.DirectoryExists(path);
    }

    public void CreateDirectory(string path)
    {
        Enter(FileOps.CreateDirectory, path);
        _inner.CreateDirectory(path);
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        Enter(FileOps.EnumerateFiles, directory);
        return _inner.EnumerateFiles(directory, pattern);
    }

    public long GetLength(string path)
    {
        Enter(FileOps.GetLength, path);
        return _inner.GetLength(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        Enter(FileOps.ReadAllBytes, path);
        return _inner.ReadAllBytes(path);
    }

    public DateTime GetLastWriteUtc(string path)
    {
        Enter(FileOps.GetLastWriteUtc, path);
        return _inner.GetLastWriteUtc(path);
    }

    public void SetLastWriteUtc(string path, DateTime utc)
    {
        Enter(FileOps.SetLastWriteUtc, path);
        _inner.SetLastWriteUtc(path, utc);
    }

    // 書き込みのたびに FileOps.Write を通すストリーム。ほかはそのまま本物に回す
    private sealed class WatchedStream(Stream inner, FakeFileSystem owner, string path) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            owner.Enter(FileOps.Write, path);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            owner.Enter(FileOps.Write, path);
            inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            owner.Enter(FileOps.Write, path);
            inner.WriteByte(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
