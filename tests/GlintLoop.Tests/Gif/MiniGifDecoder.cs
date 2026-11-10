// テスト用の最小の GIF デコーダー(仕様書 AC-4)。構造の解析と LZW の復号を、エンコーダーとコードを共有せずに書いたもの。
// LZW は一般的なデコーダーの動き(符号を読むたびに 1 つ遅れて項目を足し、free=2^width になったら幅を増やす)で読む。
// 途中のクリアコード(データの先頭以外のもの)の数と、それを読んだ時点の次に足す番号、終了コードのあとの詰め物が 0 かどうかも記録する。
using System.IO;

namespace GlintLoop.Tests.Gif;

internal sealed class MiniGifFile
{
    public string Signature = "";
    public int Width;
    public int Height;
    public byte Packed;
    public byte BackgroundIndex;
    public byte AspectRatio;
    public byte[] GlobalTable = [];
    public int GlobalTableEntries;
    /// <summary>アプリケーション拡張の生のバイト列(21 FF から終端の 00 まで)。</summary>
    public List<byte[]> AppExtensions = [];
    /// <summary>最初のアプリケーション拡張の位置と、最初の画像(またはグラフィック制御拡張)の位置。</summary>
    public int FirstAppExtensionOffset = -1;
    public int FirstFrameOffset = -1;
    public List<MiniGifFrame> Frames = [];
    public bool EndsWithTrailer;

    public (byte R, byte G, byte B) TableColor(int i) => (GlobalTable[3 * i], GlobalTable[3 * i + 1], GlobalTable[3 * i + 2]);
}

internal sealed class MiniGifFrame
{
    public bool HasControl;
    public byte[] ControlRaw = [];
    public byte ControlPacked;
    public int Delay;
    public int TransparentIndexField;
    public int Disposal => (ControlPacked >> 2) & 7;
    public bool TransparentFlag => (ControlPacked & 1) != 0;

    public int Left;
    public int Top;
    public int Width;
    public int Height;
    public byte DescriptorPacked;
    public int MinCodeSize;
    public int DataBlockCount;
    public int MaxDataBlockLength;
    public byte[] Indices = [];
    public int ClearCodes;
    public int MidClearCodes;
    /// <summary>途中のクリアコードを読んだ時点ごとの、デコーダーが次に足す番号(next)。</summary>
    public List<int> NextAtMidClears = [];
    public bool PaddingIsZero;
    public int BytesAfterEnd;
}

internal static class MiniGifDecoder
{
    public static MiniGifFile Parse(byte[] gif)
    {
        var f = new MiniGifFile();
        var pos = 0;
        f.Signature = System.Text.Encoding.ASCII.GetString(gif, 0, 6);
        pos = 6;
        f.Width = gif[pos] | (gif[pos + 1] << 8);
        f.Height = gif[pos + 2] | (gif[pos + 3] << 8);
        f.Packed = gif[pos + 4];
        f.BackgroundIndex = gif[pos + 5];
        f.AspectRatio = gif[pos + 6];
        pos += 7;
        if ((f.Packed & 0x80) != 0)
        {
            f.GlobalTableEntries = 2 << (f.Packed & 7);
            f.GlobalTable = gif.AsSpan(pos, 3 * f.GlobalTableEntries).ToArray();
            pos += 3 * f.GlobalTableEntries;
        }

        MiniGifFrame? pending = null;
        while (true)
        {
            if (pos >= gif.Length) throw new InvalidDataException("トレーラーの前にデータが終わった");
            var start = pos;
            var b = gif[pos++];
            if (b == 0x3B)
            {
                f.EndsWithTrailer = pos == gif.Length;
                break;
            }
            if (b == 0x21)
            {
                var label = gif[pos++];
                if (label == 0xF9)
                {
                    if (gif[pos] != 4) throw new InvalidDataException("グラフィック制御拡張の長さが 4 ではない");
                    pending = new MiniGifFrame
                    {
                        HasControl = true,
                        ControlPacked = gif[pos + 1],
                        Delay = gif[pos + 2] | (gif[pos + 3] << 8),
                        TransparentIndexField = gif[pos + 4],
                    };
                    if (gif[pos + 5] != 0) throw new InvalidDataException("グラフィック制御拡張の終端が 00 ではない");
                    pos += 6;
                    pending.ControlRaw = gif.AsSpan(start, pos - start).ToArray();
                    if (f.FirstFrameOffset < 0) f.FirstFrameOffset = start;
                }
                else
                {
                    pos = SkipSubBlocks(gif, pos);
                    if (label == 0xFF)
                    {
                        f.AppExtensions.Add(gif.AsSpan(start, pos - start).ToArray());
                        if (f.FirstAppExtensionOffset < 0) f.FirstAppExtensionOffset = start;
                    }
                }
                continue;
            }
            if (b == 0x2C)
            {
                var fr = pending ?? new MiniGifFrame();
                pending = null;
                if (f.FirstFrameOffset < 0) f.FirstFrameOffset = start;
                fr.Left = gif[pos] | (gif[pos + 1] << 8);
                fr.Top = gif[pos + 2] | (gif[pos + 3] << 8);
                fr.Width = gif[pos + 4] | (gif[pos + 5] << 8);
                fr.Height = gif[pos + 6] | (gif[pos + 7] << 8);
                fr.DescriptorPacked = gif[pos + 8];
                pos += 9;
                if ((fr.DescriptorPacked & 0x80) != 0) pos += 3 * (2 << (fr.DescriptorPacked & 7));
                fr.MinCodeSize = gif[pos++];
                var data = new List<byte>();
                while (true)
                {
                    var len = gif[pos++];
                    if (len == 0) break;
                    fr.DataBlockCount++;
                    fr.MaxDataBlockLength = Math.Max(fr.MaxDataBlockLength, len);
                    for (var i = 0; i < len; i++) data.Add(gif[pos + i]);
                    pos += len;
                }
                var r = MiniLzw.Decode(data.ToArray(), fr.MinCodeSize, fr.Width * fr.Height);
                fr.Indices = r.Pixels;
                fr.ClearCodes = r.ClearCodes;
                fr.MidClearCodes = r.MidClearCodes;
                fr.NextAtMidClears = r.NextAtMidClears;
                fr.PaddingIsZero = r.PaddingIsZero;
                fr.BytesAfterEnd = r.BytesAfterEnd;
                f.Frames.Add(fr);
                continue;
            }
            throw new InvalidDataException($"位置 {start} に知らないブロック 0x{b:X2}");
        }
        return f;
    }

    private static int SkipSubBlocks(byte[] gif, int pos)
    {
        while (true)
        {
            var len = gif[pos++];
            if (len == 0) return pos;
            pos += len;
        }
    }
}

/// <summary>LZW の復号(テスト用の別実装)。</summary>
internal static class MiniLzw
{
    /// <summary>NextAtMidClears は、途中のクリアコードを読んだ時点ごとの next(次に足す番号。辞書を空にする前の値)。</summary>
    public readonly record struct Result(byte[] Pixels, int ClearCodes, int MidClearCodes, List<int> NextAtMidClears, bool PaddingIsZero, int BytesAfterEnd);

    public static Result Decode(byte[] data, int minCodeSize, int expectedPixels)
    {
        if (minCodeSize is < 2 or > 8) throw new InvalidDataException($"最小コードサイズ {minCodeSize}");
        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var firstOf = new byte[4096];
        for (var i = 0; i < clear; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            firstOf[i] = (byte)i;
        }
        var stack = new byte[4097];
        var output = new byte[expectedPixels];
        var outLen = 0;

        var width = minCodeSize + 1;
        var next = clear + 2;
        var old = -1;
        var clears = 0;
        var mid = 0;
        var nextAtMid = new List<int>();
        var codesRead = 0;
        long bitPos = 0;
        var totalBits = (long)data.Length * 8;

        while (true)
        {
            if (bitPos + width > totalBits) throw new InvalidDataException("終了コードの前にデータが終わった");
            var code = 0;
            for (var k = 0; k < width; k++, bitPos++)
            {
                var bit = (data[bitPos >> 3] >> (int)(bitPos & 7)) & 1;
                code |= bit << k;
            }
            codesRead++;

            if (code == clear)
            {
                clears++;
                if (codesRead > 1)
                {
                    mid++;
                    nextAtMid.Add(next);
                }
                width = minCodeSize + 1;
                next = clear + 2;
                old = -1;
                continue;
            }
            if (code == end) break;

            if (old < 0)
            {
                if (code >= clear) throw new InvalidDataException($"クリアの直後の符号 {code} が色ではない");
                Emit(code);
                old = code;
                continue;
            }

            byte head;
            if (code < next)
            {
                head = firstOf[code];
                Emit(code);
            }
            else if (code == next)
            {
                head = firstOf[old];
                Emit(old);
                Put(head);
            }
            else
            {
                throw new InvalidDataException($"符号 {code} が次の番号 {next} を超えた");
            }

            if (next < 4096)
            {
                prefix[next] = old;
                suffix[next] = head;
                firstOf[next] = firstOf[old];
                next++;
                if (next == 1 << width && width < 12) width++;
            }
            old = code;
        }

        if (outLen != expectedPixels) throw new InvalidDataException($"画素の数 期待 {expectedPixels}、実際 {outLen}");
        // 終了コードのあとの端数のビットが 0 か、余ったバイトが無いか
        var paddingZero = true;
        while ((bitPos & 7) != 0)
        {
            if (((data[bitPos >> 3] >> (int)(bitPos & 7)) & 1) != 0) paddingZero = false;
            bitPos++;
        }
        var after = (int)(data.Length - (bitPos >> 3));
        return new Result(output, clears, mid, nextAtMid, paddingZero, after);

        void Emit(int c)
        {
            var sp = 0;
            while (c >= 0)
            {
                stack[sp++] = suffix[c];
                c = prefix[c];
            }
            while (sp > 0) Put(stack[--sp]);
        }

        void Put(byte v)
        {
            if (outLen >= output.Length) throw new InvalidDataException("画素が多すぎる");
            output[outLen++] = v;
        }
    }
}
