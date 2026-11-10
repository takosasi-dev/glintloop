// 書いた GIF のブロックをたどって、フレーム数・遅延・disposal・透明・ループの有無を読み、遅延を書き換える(測定用プログラムの道具)。
// LZW は復号しない。遅延 1 と 3 のファイルは、エンコーダーの規則(遅延の最小 2・fps は整数)では作れないので、ここで書き換える。
// 製品のコードは参照しない側の道具で、GifEncoder の出力の構造(§9.8 の 8)だけを前提にする。
using System.IO;

namespace GlintLoop.Probes.Gif;

internal sealed record GifSummary(
    int Width,
    int Height,
    int TableSize,
    bool LoopForever,
    IReadOnlyList<int> Delays,
    IReadOnlyList<int> Disposals,
    IReadOnlyList<bool> TransparentFlags,
    int TransparentIndex,
    IReadOnlyList<(int X, int Y, int W, int H)> Rects);

internal static class GifInspect
{
    private static readonly byte[] Netscape = "NETSCAPE2.0"u8.ToArray();

    /// <summary>ブロックをたどり、グラフィック制御拡張の遅延の位置(バイトの番号)を返す。要約も作る。</summary>
    private static List<int> Walk(byte[] gif, out GifSummary summary)
    {
        var w = gif[6] | (gif[7] << 8);
        var h = gif[8] | (gif[9] << 8);
        var packed = gif[10];
        var pos = 13;
        var table = 0;
        if ((packed & 0x80) != 0)
        {
            table = 2 << (packed & 7);
            pos += 3 * table;
        }
        var delayOffsets = new List<int>();
        var delays = new List<int>();
        var disposals = new List<int>();
        var flags = new List<bool>();
        var rects = new List<(int, int, int, int)>();
        var loop = false;
        var transparentIndex = -1;
        while (pos < gif.Length)
        {
            var b = gif[pos++];
            if (b == 0x3B) break;
            if (b == 0x21)
            {
                var label = gif[pos++];
                if (label == 0xF9)
                {
                    var pk = gif[pos + 1];
                    delayOffsets.Add(pos + 2);
                    delays.Add(gif[pos + 2] | (gif[pos + 3] << 8));
                    disposals.Add((pk >> 2) & 7);
                    flags.Add((pk & 1) != 0);
                    if ((pk & 1) != 0) transparentIndex = gif[pos + 4];
                }
                else if (label == 0xFF && gif[pos] == 11 && gif.AsSpan(pos + 1, 11).SequenceEqual(Netscape))
                {
                    loop = true;
                }
                pos = SkipBlocks(gif, pos);
                continue;
            }
            if (b == 0x2C)
            {
                rects.Add((gif[pos] | (gif[pos + 1] << 8), gif[pos + 2] | (gif[pos + 3] << 8),
                    gif[pos + 4] | (gif[pos + 5] << 8), gif[pos + 6] | (gif[pos + 7] << 8)));
                var dp = gif[pos + 8];
                pos += 9;
                if ((dp & 0x80) != 0) pos += 3 * (2 << (dp & 7));
                pos++; // 最小コードサイズ
                pos = SkipBlocks(gif, pos);
                continue;
            }
            throw new InvalidDataException($"位置 {pos - 1} に知らないブロック 0x{b:X2}");
        }
        summary = new GifSummary(w, h, table, loop, delays, disposals, flags, transparentIndex, rects);
        return delayOffsets;
    }

    private static int SkipBlocks(byte[] gif, int pos)
    {
        while (true)
        {
            var len = gif[pos++];
            if (len == 0) return pos;
            pos += len;
        }
    }

    public static GifSummary Summarize(byte[] gif)
    {
        Walk(gif, out var s);
        return s;
    }

    /// <summary>すべてのフレームの遅延を delay(1/100 秒)に書き換える。</summary>
    public static void SetAllDelays(byte[] gif, int delay)
    {
        foreach (var o in Walk(gif, out _))
        {
            gif[o] = (byte)(delay & 0xFF);
            gif[o + 1] = (byte)(delay >> 8);
        }
    }

    /// <summary>貼りやすい 1 行の要約。</summary>
    public static string Describe(GifSummary s)
    {
        static string Runs(IEnumerable<string> xs)
        {
            var list = xs.ToList();
            return list.Distinct().Count() == 1 ? $"すべて {list[0]}" : string.Join(",", list);
        }
        var full = s.Rects.Count(r => r.X == 0 && r.Y == 0 && r.W == s.Width && r.H == s.Height);
        return $"{s.Width}×{s.Height}、{s.Delays.Count} フレーム、M={s.TableSize}、" +
               $"遅延 {Runs(s.Delays.Select(d => d.ToString()))}、disposal {Runs(s.Disposals.Select(d => d.ToString()))}、" +
               $"透明フラグ {Runs(s.TransparentFlags.Select(f => f ? "1" : "0"))}、" +
               $"NETSCAPE2.0 {(s.LoopForever ? "あり" : "なし")}、全面のフレーム {full}/{s.Rects.Count}";
    }
}
