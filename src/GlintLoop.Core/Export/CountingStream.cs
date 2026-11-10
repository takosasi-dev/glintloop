// 書いたバイトの合計を数え、上限を超える書き込みが来た時点で打ち切る書き込み専用のストリーム(§9.9 の 4 の 5、AC-38 の (3))。
// 打ち切りは ExportLimitExceededException で知らせ、超えた分は下のストリームに渡さない。エンコーダーはこれを捕まえて Overflowed を返す。
// 下のストリームは閉じない(持ち主は呼ぶ側)。
namespace GlintLoop.Core.Export;

/// <summary>書いたバイトの合計が上限を超えた(試行の符号化の打ち切り)。</summary>
public sealed class ExportLimitExceededException(long limitBytes, long attemptedBytes)
    : Exception($"書いたバイトの合計 {attemptedBytes} が上限 {limitBytes} を超えた")
{
    public long LimitBytes { get; } = limitBytes;

    /// <summary>超えた書き込みを足したときの合計。</summary>
    public long AttemptedBytes { get; } = attemptedBytes;
}

public sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly long? _limit;

    /// <summary>inner に書き、合計が limitBytes(null は上限なし)を超える書き込みで打ち切る。</summary>
    public CountingStream(Stream inner, long? limitBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _limit = limitBytes;
    }

    /// <summary>下のストリームに渡したバイトの合計。</summary>
    public long Written { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var total = Written + buffer.Length;
        if (_limit is long limit && total > limit) throw new ExportLimitExceededException(limit, total);
        _inner.Write(buffer);
        Written = total;
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
