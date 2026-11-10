// .glint を読むときの差し込み口(ZIP の読み出しとデコード。AC-47 の (6) が呼び出しの回数を数える)と、製品の実装と、開けないときの例外。
// エントリーは固定の名前で 1 つずつ開くだけで、名前をパスとして使わない(§9.11 の 4)。ZIP のヘッダーの大きさは信用せず、製品の実装は読み終えたときに CRC を照合する。
// ZIP の末尾の記録の総エントリー数は、ZipArchive を作る前にここで読む。
using System.IO.Compression;
using GlintLoop.Codec.Import;

namespace GlintLoop.Codec.Project;

/// <summary>.glint を開けなかった。NoticeKey は project.invalid か project.too_new。</summary>
public sealed class GlintProjectOpenException(string noticeKey, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string NoticeKey { get; } = noticeKey;
}

/// <summary>読み取り用に開いた ZIP。</summary>
public interface IProjectZip : IDisposable
{
    /// <summary>すべてのエントリーの名前(同じ名前が 2 回あれば 2 回)。</summary>
    IReadOnlyList<string> EntryNames { get; }

    /// <summary>固定の名前のエントリーを読むストリーム。無いときは InvalidDataException。</summary>
    Stream OpenEntry(string name);
}

/// <summary>読み込みの差し込み口。製品は Default、テストは呼び出しを数える偽物を渡す。</summary>
public sealed record ProjectOpenHooks
{
    public static ProjectOpenHooks Default { get; } = new();

    /// <summary>ZIP を開く。製品は ZipArchive を作る。</summary>
    public Func<Stream, IProjectZip> OpenZip { get; init; } = s => new SystemProjectZip(s);

    /// <summary>ソースのバイト列・形式・SHA-256・ヘッダーの画素数・GPU の上限から画素を作る。</summary>
    public Func<byte[], ImageFormat, string, long, int, ImportedImage> Decode { get; init; } = ImageImporter.DecodeSource;
}

internal static class ProjectZipTail
{
    private const int EocdLength = 22;
    private const int MaxCommentLength = 65535;

    /// <summary>
    /// ZIP の末尾の記録(End of central directory)の総エントリー数を読む。末尾に近い方から署名を探す(ZipArchive と同じ向き)。
    /// このディスクの数と全体の数の大きい方を返す。記録が無ければ null。
    /// </summary>
    public static int? ReadTotalEntries(Stream stream)
    {
        var length = stream.Length;
        if (length < EocdLength) return null;
        var tailLength = (int)Math.Min(length, EocdLength + MaxCommentLength);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);
        for (var i = tailLength - EocdLength; i >= 0; i--)
        {
            if (tail[i] != 0x50 || tail[i + 1] != 0x4B || tail[i + 2] != 0x05 || tail[i + 3] != 0x06) continue;
            var onDisk = tail[i + 8] | (tail[i + 9] << 8);
            var total = tail[i + 10] | (tail[i + 11] << 8);
            return Math.Max(onDisk, total);
        }
        return null;
    }
}

/// <summary>ZipArchive で読む製品の実装。</summary>
internal sealed class SystemProjectZip(Stream stream) : IProjectZip
{
    private readonly ZipArchive _zip = new(stream, ZipArchiveMode.Read, leaveOpen: true);

    public IReadOnlyList<string> EntryNames => _zip.Entries.Select(e => e.FullName).ToArray();

    public Stream OpenEntry(string name)
    {
        var entry = _zip.GetEntry(name) ?? throw new InvalidDataException("エントリーが無い");
        return new CrcCheckedStream(entry.Open(), entry.Crc32);
    }

    public void Dispose() => _zip.Dispose();
}

/// <summary>終わりまで読んだときに CRC-32 を照合するストリーム(ZipArchive は照合しない)。合わなければ InvalidDataException。</summary>
internal sealed class CrcCheckedStream(Stream inner, uint expected) : Stream
{
    private static readonly uint[] Table = MakeTable();
    private uint _crc = uint.MaxValue;

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        if (n == 0 && count > 0)
        {
            if (~_crc != expected) throw new InvalidDataException("CRC が合わない");
            return 0;
        }
        for (var i = 0; i < n; i++) _crc = Table[(_crc ^ buffer[offset + i]) & 0xFF] ^ (_crc >> 8);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
