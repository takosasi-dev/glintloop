// §8.4 の測定 5 の 7 つのテストファイルを、自作の GifEncoder で作る(AC-6 の材料)。画像はすべてコードで描く。
// loop_inf・loop_none は色の違う 4 フレーム(遅延 50)、delay_1〜3 は動く棒の 8 フレーム、transp_d2 と diff_d1 は動く玉。
// 遅延 1 と 3 は、規則 7 で作れない(最小 2、fps は整数)ので、遅延 2 で符号化してから GifInspect で書き換える。
using System.IO;
using GlintLoop.Core.Gif;

namespace GlintLoop.Probes.Gif;

internal static class DecodeTestFiles
{
    private sealed record Spec(string Name, string Content, string Expect, Func<byte[]> Make);

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var specs = new List<Spec>
        {
            new("loop_inf.gif", "色の違う 4 フレーム、遅延 50(0.5 秒)、NETSCAPE2.0 のループ回数 0", "止まらずに繰り返す",
                () => Encode(160, 120, ColorFrames(160, 120), new GifEncoderOptions { Fps = 2, Loop = GifLoop.Infinite, Dither = GifDither.None })),
            new("loop_none.gif", "上と同じで、NETSCAPE2.0 を書かない", "1 回だけ再生して止まる(未確認)",
                () => Encode(160, 120, ColorFrames(160, 120), new GifEncoderOptions { Fps = 2, Loop = GifLoop.Once, Dither = GifDither.None })),
            new("delay_1.gif", "動く棒の 8 フレーム、遅延 1", "遅延 2 より遅い、または同じ速さ(ブラウザの下限)", () => Bar(1)),
            new("delay_2.gif", "同上、遅延 2", "遅延 1 より速い(未確認)", () => Bar(2)),
            new("delay_3.gif", "同上、遅延 3", "遅延 2 よりやや遅い", () => Bar(3)),
            new("transp_d2.gif", "透明な背景を動く不透明な玉、透明インデックスあり、disposal 2", "白とダークの背景のどちらでも残像が出ない",
                () => Encode(160, 160, BallFrames(160, 160, transparentBackground: true), new GifEncoderOptions { Fps = 20 })),
            new("diff_d1.gif", "不透明な動く玉、差分(透明インデックスで前フレームと同じ画素を抜く)、disposal 1", "残像も欠けも出ない",
                () => Encode(160, 160, BallFrames(160, 160, transparentBackground: false), new GifEncoderOptions { Fps = 20, DiffOptimization = true })),
        };

        Console.WriteLine("```");
        Console.WriteLine("probe-gif: 測定 5(GIF のデコード)のテストファイル");
        Console.WriteLine($"出力フォルダ: {Path.GetFullPath(outDir)}");
        Console.WriteLine();
        Console.WriteLine("| ファイル | 内容 | 期待 | 書いた中身(probe-gif が読み直した値) | バイト数 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var s in specs)
        {
            var bytes = s.Make();
            File.WriteAllBytes(Path.Combine(outDir, s.Name), bytes);
            var summary = GifInspect.Summarize(bytes);
            Console.WriteLine($"| `{s.Name}` | {s.Content} | {s.Expect} | {GifInspect.Describe(summary)} | {bytes.Length} |");
        }
        Console.WriteLine();
        Console.WriteLine("注: delay_1 と delay_3 は、遅延 2 で符号化したあと、全フレームのグラフィック制御拡張の遅延を 1 と 3 に書き換えた");
        Console.WriteLine("    (エンコーダーの規則 7 は遅延を 2 以上にし、fps は整数なので、遅延 1 と 3 を直接は作れない)。");
        Console.WriteLine("```");
        return 0;
    }

    private static byte[] Encode(int w, int h, List<byte[]> frames, GifEncoderOptions o) =>
        GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o);

    private static byte[] Bar(int delay)
    {
        // fps 50 は規則 7 で遅延がちょうど 2 になる
        var bytes = Encode(160, 40, BarFrames(160, 40), new GifEncoderOptions { Fps = 50, Dither = GifDither.None });
        if (delay != 2) GifInspect.SetAllDelays(bytes, delay);
        return bytes;
    }

    private static void Put(byte[] px, int w, int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        var o = (y * w + x) * 4;
        px[o] = r;
        px[o + 1] = g;
        px[o + 2] = b;
        px[o + 3] = a;
    }

    private static List<byte[]> ColorFrames(int w, int h)
    {
        var colors = new (byte R, byte G, byte B)[] { (220, 40, 40), (40, 170, 60), (40, 80, 220), (240, 200, 40) };
        var list = new List<byte[]>();
        for (var k = 0; k < 4; k++)
        {
            var px = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b) = colors[k];
                    // 左上に、フレームの番号の数だけ白い四角を置く(何枚目かが見て分かるように)
                    var mark = y is >= 8 and < 24 && x >= 8 && (x - 8) % 24 < 16 && (x - 8) / 24 <= k;
                    if (mark) Put(px, w, x, y, 255, 255, 255);
                    else Put(px, w, x, y, r, g, b);
                }
            }
            list.Add(px);
        }
        return list;
    }

    private static List<byte[]> BarFrames(int w, int h)
    {
        var list = new List<byte[]>();
        for (var k = 0; k < 8; k++)
        {
            var px = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var bar = x >= k * 20 && x < k * 20 + 20;
                    if (bar) Put(px, w, x, y, 20, 20, 20);
                    else Put(px, w, x, y, 245, 245, 245);
                }
            }
            list.Add(px);
        }
        return list;
    }

    private static List<byte[]> BallFrames(int w, int h, bool transparentBackground)
    {
        const int n = 16;
        const double radius = 22;
        var list = new List<byte[]>();
        for (var k = 0; k < n; k++)
        {
            var t = 2 * Math.PI * k / n;
            var cx = w / 2.0 + 48 * Math.Cos(t);
            var cy = h / 2.0 + 48 * Math.Sin(t);
            var px = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var dx = x + 0.5 - cx;
                    var dy = y + 0.5 - cy;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= radius)
                    {
                        // 玉は左上が明るい 2 段の塗り(残像と欠けが見て分かるように)
                        var light = dx + dy < -radius * 0.6;
                        if (light) Put(px, w, x, y, 255, 190, 120);
                        else Put(px, w, x, y, 230, 110, 30);
                    }
                    else if (transparentBackground)
                    {
                        Put(px, w, x, y, 0, 0, 0, 0);
                    }
                    else
                    {
                        // 不透明な背景は市松模様(欠けが見て分かるように)
                        var c = ((x / 16) + (y / 16)) % 2 == 0 ? (byte)200 : (byte)170;
                        Put(px, w, x, y, c, c, (byte)(c + 20));
                    }
                }
            }
            list.Add(px);
        }
        return list;
    }
}
