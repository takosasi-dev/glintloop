// AC-8 の比較モード(probe-gif --compare)。基準画像 3 枚のそれぞれから、CPU で 1 回転の 40 フレーム(L=2.0・F=20・長辺 480・ルックなし)を作り、
// 色数 256・128・64・48・32 で自作の GifEncoder に通して容量を出す。--ezgif-kb を渡すと、差が 15% 以内の色数に印を付ける。
// 画像のファイル名とパスは出力に出さない(画像 1・2・3 と書く)。回転の外側は製品の spin と同じく透明(--bg で単色にもできる)。
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.Core.Gif;

namespace GlintLoop.Probes.Gif;

internal static class CompareMode
{
    private const int LongSide = 480;
    private const int Fps = 20;
    private const int FrameCount = 40; // L=2.0 秒 × F=20
    private static readonly int[] ColorCounts = [256, 128, 64, 48, 32];

    public static int Run(IReadOnlyList<string> images, string outDir, double[]? ezgifKb, GifColor? background, int parallelism)
    {
        if (images.Count != 3)
        {
            Console.Error.WriteLine("--compare には画像を 3 つ渡す");
            return 2;
        }
        if (ezgifKb is not null && ezgifKb.Length != 3)
        {
            Console.Error.WriteLine("--ezgif-kb には 3 つの値をカンマ区切りで渡す(例: 512,300,800)");
            return 2;
        }
        Directory.CreateDirectory(outDir);

        var sizes = new long[3, ColorCounts.Length];
        var usedColors = new int[3, ColorCounts.Length];
        var notes = new List<string>();
        var times = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var label = $"画像 {i + 1}";
            var src = LoadImage(images[i]);
            var (w, h) = TargetSize(src.Width, src.Height);
            var swFrames = Stopwatch.StartNew();
            var baseImage = Resample.ToLinearPremultiplied(src.Width, src.Height, src.Rgba);
            var scaled = Resample.Resize(baseImage, src.Width, src.Height, w, h);
            var frames = new List<byte[]>(FrameCount);
            for (var k = 0; k < FrameCount; k++)
            {
                // spin: θ=360°×k/N(時計回り)、中心のまわり
                var theta = 2 * Math.PI * k / FrameCount;
                frames.Add(Resample.RotateToRgba8(scaled, w, h, theta, background));
            }
            swFrames.Stop();
            notes.Add($"{label}: 元 {src.Width}×{src.Height}(EXIF の向き {src.Orientation}、{src.Decoder})→ {w}×{h}、{FrameCount} フレーム、" +
                      $"背景 {(background is { } b ? $"単色 {b.R},{b.G},{b.B}" : "透明")}、フレーム作成 {swFrames.Elapsed.TotalSeconds:F1} 秒");

            for (var c = 0; c < ColorCounts.Length; c++)
            {
                var o = new GifEncoderOptions { Colors = ColorCounts[c], Fps = Fps, Parallelism = parallelism };
                var sw = Stopwatch.StartNew();
                var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o);
                sw.Stop();
                sizes[i, c] = gif.Length;
                var info = GifInspect.Summarize(gif);
                usedColors[i, c] = info.TransparentIndex >= 0 ? info.TransparentIndex : info.TableSize;
                File.WriteAllBytes(Path.Combine(outDir, $"compare_img{i + 1}_c{ColorCounts[c]}.gif"), gif);
                times.Add($"{label} 色数 {ColorCounts[c]}: {sw.Elapsed.TotalSeconds:F2} 秒");
            }
        }

        Console.WriteLine("```");
        Console.WriteLine("probe-gif --compare: 1 回転・2 秒・20 fps・長辺 480・ルックなし(青色雑音のディザ、差分の最適化オン)");
        Console.WriteLine($"出力フォルダ: {Path.GetFullPath(outDir)}(compare_img<番号>_c<色数>.gif)");
        foreach (var n in notes) Console.WriteLine(n);
        Console.WriteLine("KB は 1024 バイト。");
        Console.WriteLine();
        var header = "| 基準画像 | " + string.Join(" | ", ColorCounts.Select(c => $"{c} 色(KB)")) + (ezgifKb is null ? " |" : " | ezgif(KB) | 選んだ色数 |");
        Console.WriteLine(header);
        Console.WriteLine("|" + string.Concat(Enumerable.Repeat("---|", ColorCounts.Length + 1 + (ezgifKb is null ? 0 : 2))));
        for (var i = 0; i < 3; i++)
        {
            var cells = new List<string>();
            var best = -1;
            var bestDiff = double.MaxValue;
            for (var c = 0; c < ColorCounts.Length; c++)
            {
                var kb = sizes[i, c] / 1024.0;
                var cell = kb.ToString("F1", CultureInfo.InvariantCulture);
                if (ezgifKb is not null)
                {
                    var diff = Math.Abs(kb - ezgifKb[i]) / ezgifKb[i];
                    if (diff <= 0.15)
                    {
                        cell += "*";
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            best = c;
                        }
                    }
                }
                cells.Add(cell);
            }
            var line = $"| {i + 1} | " + string.Join(" | ", cells);
            if (ezgifKb is not null)
            {
                var pick = best >= 0 ? $"{ColorCounts[best]}(差 {bestDiff * 100:F1}%)" : "15% 以内なし";
                line += $" | {ezgifKb[i].ToString("F1", CultureInfo.InvariantCulture)} | {pick}";
            }
            Console.WriteLine(line + " |");
        }
        if (ezgifKb is not null)
        {
            Console.WriteLine();
            Console.WriteLine("* は ezgif の容量との差が 15% 以内。選んだ色数は、その中で差が最も小さいもの。");
        }
        Console.WriteLine();
        for (var i = 0; i < 3; i++)
        {
            var used = Enumerable.Range(0, ColorCounts.Length).Select(c => $"{ColorCounts[c]}→{usedColors[i, c]}");
            Console.WriteLine($"画像 {i + 1} のパレットの色の数(指定→実際。透明インデックスを除く): " + string.Join("、", used));
        }
        Console.WriteLine("符号化の時間: " + string.Join("、", times));
        Console.WriteLine("```");
        return 0;
    }

    /// <summary>長辺を 480 にした大きさ(縦横比を保ち、短辺は round。1 以上)。</summary>
    private static (int W, int H) TargetSize(int w, int h)
    {
        if (w >= h) return (LongSide, Math.Max(1, (int)Math.Floor((double)h * LongSide / w + 0.5)));
        return (Math.Max(1, (int)Math.Floor((double)w * LongSide / h + 0.5)), LongSide);
    }

    private sealed record Loaded(int Width, int Height, byte[] Rgba, int Orientation, string Decoder);

    /// <summary>WPF の BitmapDecoder で読み、straight RGBA にして EXIF の向きを直す。埋め込みの色プロファイルは既定のとおり適用する。</summary>
    private static Loaded LoadImage(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = dec.Frames[0];
        var orientation = ReadOrientation(frame);
        var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var bgra = new byte[w * h * 4];
        conv.CopyPixels(bgra, w * 4, 0);
        var rgba = new byte[bgra.Length];
        for (var p = 0; p < w * h; p++)
        {
            rgba[p * 4] = bgra[p * 4 + 2];
            rgba[p * 4 + 1] = bgra[p * 4 + 1];
            rgba[p * 4 + 2] = bgra[p * 4];
            rgba[p * 4 + 3] = bgra[p * 4 + 3];
        }
        var (ow, oh, oriented) = ApplyOrientation(w, h, rgba, orientation);
        return new Loaded(ow, oh, oriented, orientation, dec.GetType().Name);
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation")
                && md.GetQuery("System.Photo.Orientation") is { } v)
            {
                var o = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                return o is >= 1 and <= 8 ? o : 1;
            }
        }
        catch (NotSupportedException)
        {
            // 形式によってはメタデータの問い合わせができない
        }
        catch (InvalidOperationException)
        {
        }
        return 1;
    }

    /// <summary>EXIF の向き 1〜8 を画素に反映する(表示したときの向きにする)。</summary>
    private static (int W, int H, byte[] Rgba) ApplyOrientation(int w, int h, byte[] s, int o)
    {
        if (o == 1) return (w, h, s);
        var swap = o >= 5;
        int dw = swap ? h : w, dh = swap ? w : h;
        var d = new byte[s.Length];
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                var (sx, sy) = o switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    _ => (w - 1 - y, x), // 8
                };
                Buffer.BlockCopy(s, (sy * w + sx) * 4, d, (y * dw + x) * 4, 4);
            }
        }
        return (dw, dh, d);
    }
}
