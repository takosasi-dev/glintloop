// probe-gif の入口。引数なしで §8.4 の測定 5 の 7 ファイルを作り、--compare で AC-8 の比較用の GIF を作る(仕様書 §8.4・AC-8)。
// 測定用プログラムは製品に含めない(AC-7)。結果は貼りやすい文字列で標準出力に出す。出力先の既定は実行ファイルの横の output。
// 使い方: probe-gif [--out <フォルダ>] / probe-gif --compare <画像1> <画像2> <画像3> [--out <フォルダ>] [--ezgif-kb a,b,c] [--bg RRGGBB] [--parallel n]
using System.Globalization;
using System.IO;
using GlintLoop.Core.Gif;

namespace GlintLoop.Probes.Gif;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string? outDir = null;
        var compare = false;
        var images = new List<string>();
        double[]? ezgif = null;
        GifColor? background = null;
        var parallel = Environment.ProcessorCount;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--compare":
                    compare = true;
                    break;
                case "--ezgif-kb" when i + 1 < args.Length:
                    ezgif = args[++i].Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
                    break;
                case "--bg" when i + 1 < args.Length:
                    background = ParseColor(args[++i]);
                    break;
                case "--parallel" when i + 1 < args.Length:
                    parallel = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"知らない引数: {args[i]}");
                        PrintUsage();
                        return 2;
                    }
                    images.Add(args[i]);
                    break;
            }
        }
        outDir ??= Path.Combine(AppContext.BaseDirectory, "output");
        if (compare) return CompareMode.Run(images, outDir, ezgif, background, parallel);
        if (images.Count > 0)
        {
            Console.Error.WriteLine("画像は --compare のときだけ渡す");
            return 2;
        }
        return DecodeTestFiles.Run(outDir);
    }

    private static GifColor? ParseColor(string s)
    {
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;
        var t = s.TrimStart('#');
        if (t.Length != 6) throw new ArgumentException("--bg は RRGGBB(16 進 6 桁)か transparent");
        var v = Convert.ToInt32(t, 16);
        return new GifColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("probe-gif                       測定 5 の 7 ファイル(loop_inf ほか)を出力フォルダに書く");
        Console.WriteLine("probe-gif --compare 画像1 画像2 画像3 [--ezgif-kb a,b,c] [--bg RRGGBB|transparent]");
        Console.WriteLine("                                AC-8 の比較用の GIF(色数 256・128・64・48・32)を書き、容量を出す");
        Console.WriteLine("共通: --out フォルダ(既定は実行ファイルの横の output)、--parallel n(符号化の並列度)");
    }
}
