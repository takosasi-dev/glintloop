// GIF の LZW 符号化(仕様書 §9.8 の 9、規則 A)。フレームごとに独立に符号化し、最小コードサイズの 1 バイト・255 バイト以下の塊・00 を書く。
// 符号を書いた直後・項目を足す前に、(a) free=2^width かつ width<12 なら width を増やし、(b) free≥4095 ならクリアコードを書いて辞書を空にし、
// (c) それ以外なら項目を足す。辞書は (前の符号, 次の番号) の表で、世代の印を付けて空にする手間を省く(スレッドごとに 1 つ)。
namespace GlintLoop.Core.Gif;

internal static class GifLzw
{
    internal const int MaxBits = 12;

    /// <summary>辞書を空にする閾値(free がこの値以上ならクリアする。項目 4095 は作らない)。</summary>
    internal const int ClearAtFree = 4095;

    private const int GenerationShift = MaxBits;
    private const uint MaxGeneration = (1u << (32 - GenerationShift)) - 1;

    [ThreadStatic] private static uint[]? t_table;
    [ThreadStatic] private static uint t_generation;

    /// <summary>番号の列を、最小コードサイズの 1 バイト+LZW のデータブロック+終端の 00 にする。</summary>
    internal static byte[] EncodeImageData(ReadOnlySpan<byte> indices, int minCodeSize)
    {
        if (minCodeSize is < 2 or > 8) throw new ArgumentOutOfRangeException(nameof(minCodeSize));
        var cc = 1 << minCodeSize;
        var eoi = cc + 1;
        foreach (var v in indices)
        {
            if (v >= cc) throw new ArgumentException($"番号 {v} が最小コードサイズ {minCodeSize} の範囲を超える", nameof(indices));
        }

        var sink = new GifByteSink(Math.Max(64, indices.Length / 2));
        sink.Add((byte)minCodeSize);
        var bits = new BlockBitWriter(sink);

        var table = t_table ??= new uint[(1 << MaxBits) * 256];
        var gen = NextGeneration(table);

        var free = cc + 2;
        var width = minCodeSize + 1;
        bits.Write(cc, width);

        if (indices.Length > 0)
        {
            int prefix = indices[0];
            for (var i = 1; i < indices.Length; i++)
            {
                int c = indices[i];
                var slot = (prefix << 8) | c;
                var entry = table[slot];
                if (entry >> GenerationShift == gen)
                {
                    prefix = (int)(entry & ((1u << GenerationShift) - 1));
                    continue;
                }

                bits.Write(prefix, width);
                // (a) 項目を足す前の free で判定する。(b)(c) とは独立に、いつも先に行う。
                if (free == 1 << width && width < MaxBits) width++;
                if (free >= ClearAtFree)
                {
                    // (b) 項目を足さずにクリアコードを書き、辞書を空にする。
                    bits.Write(cc, width);
                    free = cc + 2;
                    width = minCodeSize + 1;
                    gen = NextGeneration(table);
                }
                else
                {
                    // (c) (文字列+次の画素) を番号 free で足す。
                    table[slot] = (gen << GenerationShift) | (uint)free;
                    free++;
                }
                prefix = c;
            }

            bits.Write(prefix, width);
            if (free == 1 << width && width < MaxBits) width++;
        }

        bits.Write(eoi, width);
        bits.Finish();
        sink.Add(0x00);
        return sink.ToArray();
    }

    private static uint NextGeneration(uint[] table)
    {
        if (t_generation >= MaxGeneration)
        {
            Array.Clear(table);
            t_generation = 0;
        }
        return ++t_generation;
    }

    /// <summary>符号を下位ビットから詰め、255 バイトごとに塊の長さを付けて書く。</summary>
    private sealed class BlockBitWriter(GifByteSink sink)
    {
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private ulong _acc;
        private int _accBits;

        internal void Write(int code, int width)
        {
            _acc |= (ulong)(uint)code << _accBits;
            _accBits += width;
            while (_accBits >= 8)
            {
                Put((byte)_acc);
                _acc >>= 8;
                _accBits -= 8;
            }
        }

        /// <summary>バイトの端数を 0 で詰め、残りの塊を書く。</summary>
        internal void Finish()
        {
            if (_accBits > 0) Put((byte)_acc);
            _acc = 0;
            _accBits = 0;
            if (_blockLength > 0) FlushBlock();
        }

        private void Put(byte b)
        {
            _block[_blockLength++] = b;
            if (_blockLength == _block.Length) FlushBlock();
        }

        private void FlushBlock()
        {
            sink.Add((byte)_blockLength);
            sink.Add(_block.AsSpan(0, _blockLength));
            _blockLength = 0;
        }
    }
}

/// <summary>伸びるバイト列(GIF のブロックを組み立てるため)。</summary>
internal sealed class GifByteSink(int capacity)
{
    private byte[] _buf = new byte[Math.Max(16, capacity)];

    internal int Length { get; private set; }

    internal void Add(byte b)
    {
        if (Length == _buf.Length) Grow(Length + 1);
        _buf[Length++] = b;
    }

    internal void Add(ReadOnlySpan<byte> s)
    {
        if (Length + s.Length > _buf.Length) Grow(Length + s.Length);
        s.CopyTo(_buf.AsSpan(Length));
        Length += s.Length;
    }

    internal void AddUInt16(int v)
    {
        Add((byte)(v & 0xFF));
        Add((byte)((v >> 8) & 0xFF));
    }

    internal byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();

    private void Grow(int min)
    {
        var n = Math.Max(min, _buf.Length * 2);
        Array.Resize(ref _buf, n);
    }
}
