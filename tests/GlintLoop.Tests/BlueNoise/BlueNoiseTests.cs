// 青色雑音のタイルの検査(仕様書 AC-3 の (1)〜(5))と、BlueNoise.At の座標の折り返し。
// 生成は tools/make-bluenoise の VoidAndCluster を直接呼び、製品側は埋め込みリソースだけを見る。
// ファイルは書かない。(5) だけ、リポジトリの src/ と tools/ の .cs を読む。
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using GlintLoop.Core.Dither;
using GlintLoop.Tests.Framework;
using GlintLoop.Tools.MakeBlueNoise;

namespace GlintLoop.Tests.Dither;

public sealed class BlueNoiseTests
{
    // (5) で、製品のコードに現れてはいけない生成の目印。tools/make-bluenoise には現れることも確かめる。
    private static readonly string[] GeneratorMarkers =
    [
        "VoidAndCluster",
        "TightestCluster",
        "LargestVoid",
        "474C494E",
        "void-and-cluster",
        "Ulichney",
    ];

    [Test]
    public void AC3_1_埋め込みのタイルは8192バイトで0から4095の置換()
    {
        var bytes = BlueNoise.ReadResourceBytes();
        CheckBytesArePermutation(bytes, "埋め込み");
    }

    [Test]
    public void AC3_2_8x8の窓の平均が042から058()
    {
        var (min, max) = VoidAndCluster.WindowMeanRange(BlueNoise.Tile);
        Console.WriteLine($"  埋め込みのタイル: 窓の平均 最小 {min:F6} 最大 {max:F6}");
        Check.InRange(0.42, 0.58, min, "窓の平均の最小");
        Check.InRange(0.42, 0.58, max, "窓の平均の最大");
    }

    [Test]
    public void AC3_3_生成を2回呼ぶと同じバイト列()
    {
        var a = VoidAndCluster.Generate();
        var b = VoidAndCluster.Generate();
        Check.True(!ReferenceEquals(a, b), "2 回の生成が別の配列であること");
        var ba = VoidAndCluster.ToBytes(a);
        var bb = VoidAndCluster.ToBytes(b);
        Check.SequenceEqual(ba, bb, "2 回の生成のバイト列");

        // 生成したものも (1) と (2) を満たす。
        CheckBytesArePermutation(ba, "生成");
        var (min, max) = VoidAndCluster.WindowMeanRange(a);
        Check.InRange(0.42, 0.58, min, "生成の窓の平均の最小");
        Check.InRange(0.42, 0.58, max, "生成の窓の平均の最大");

        // コミット済みのファイルとの一致は求めない(Math.Exp の実装の差がありうる)。結果だけ出す。
        var committed = BlueNoise.ReadResourceBytes();
        var same = committed.AsSpan().SequenceEqual(ba);
        Console.WriteLine($"  生成とコミット済みの一致: {(same ? "一致" : "不一致")}");
    }

    [Test]
    public void AC3_4_コミット済みのタイルが1と2を通る()
    {
        var bytes = BlueNoise.ReadResourceBytes();
        CheckBytesArePermutation(bytes, "コミット済み");
        var tile = BlueNoise.Tile;
        Check.Equal(4096, tile.Length, "Tile の個数");
        for (var k = 0; k < tile.Length; k++)
        {
            var expected = (ushort)(bytes[2 * k] | (bytes[2 * k + 1] << 8));
            if (tile[k] != expected) Check.Equal(expected, tile[k], $"Tile[{k}] とリトルエンディアンの値");
        }
        var (min, max) = VoidAndCluster.WindowMeanRange(tile);
        Check.InRange(0.42, 0.58, min, "窓の平均の最小");
        Check.InRange(0.42, 0.58, max, "窓の平均の最大");
        Console.WriteLine($"  SHA-256: {Convert.ToHexStringLower(SHA256.HashData(bytes))}");
    }

    [Test]
    public void AC3_5_製品のコードにタイルの生成が無い()
    {
        var root = FindRepoRoot();
        var srcFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .ToList();
        Check.True(srcFiles.Count > 0, "src に .cs が見つかること");
        foreach (var file in srcFiles)
        {
            var text = File.ReadAllText(file);
            foreach (var marker in GeneratorMarkers)
            {
                Check.True(!text.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetRelativePath(root, file)} に生成の目印「{marker}」がある");
            }
        }

        // 目印で本当に見分けられること: tools/make-bluenoise にはすべて現れる。
        var toolText = string.Concat(Directory.GetFiles(Path.Combine(root, "tools", "make-bluenoise"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .Select(File.ReadAllText));
        foreach (var marker in GeneratorMarkers)
        {
            Check.True(toolText.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"tools/make-bluenoise に目印「{marker}」が無い(検査が効いていない)");
        }

        // 製品のアセンブリにも、生成の型やメソッドが無い。パレットの量子化などの正当な名前を巻き込まないよう、
        // 生成の識別子そのものだけを見る(「Cluster」を含むだけでは落とさない)。
        string[] generatorNames = ["VoidAndCluster", "TightestCluster", "LargestVoid"];
        var core = typeof(BlueNoise).Assembly;
        foreach (var type in core.GetTypes())
        {
            foreach (var name in generatorNames)
            {
                Check.True(!type.Name.Contains(name, StringComparison.OrdinalIgnoreCase), $"Core に型 {type.FullName} がある");
            }
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var name in generatorNames)
                {
                    Check.True(!m.Name.Contains(name, StringComparison.OrdinalIgnoreCase),
                        $"Core に生成のメソッド {type.FullName}.{m.Name} がある");
                }
            }
        }
    }

    [Test]
    public void AC3_At_座標をmod64で折り返す()
    {
        var tile = BlueNoise.Tile;
        for (var j = 0; j < 64; j++)
        {
            for (var i = 0; i < 64; i++)
            {
                var v = tile[j * 64 + i];
                if (BlueNoise.At(i, j) != v) Check.Equal((int)v, BlueNoise.At(i, j), $"At({i},{j})");
                if (BlueNoise.At(i + 64, j - 64) != v) Check.Equal((int)v, BlueNoise.At(i + 64, j - 64), $"At({i}+64,{j}-64)");
                if (BlueNoise.At(i - 128, j + 640) != v) Check.Equal((int)v, BlueNoise.At(i - 128, j + 640), $"At({i}-128,{j}+640)");
            }
        }
        Check.Equal((int)tile[63 * 64 + 63], BlueNoise.At(-1, -1), "At(-1,-1) は bn(63,63)");
        Check.Equal((int)tile[0 * 64 + 63], BlueNoise.At(-1, 0), "At(-1,0) は bn(63,0)(i が列)");
        Check.Equal((int)tile[63 * 64 + 0], BlueNoise.At(0, -1), "At(0,-1) は bn(0,63)(j が行)");
        Check.Equal((int)tile[5 * 64 + 1], BlueNoise.At(int.MinValue + 1, int.MinValue + 5), "int.MinValue 付近");
    }

    private static void CheckBytesArePermutation(byte[] bytes, string what)
    {
        Check.Equal(8192, bytes.Length, $"{what}のバイト数");
        var seen = new bool[4096];
        for (var k = 0; k < 4096; k++)
        {
            var v = bytes[2 * k] | (bytes[2 * k + 1] << 8);
            Check.True(v < 4096, $"{what}の [{k}] が 4096 未満(実際 {v})");
            Check.True(!seen[v], $"{what}の値 {v} が 2 回以上ある");
            seen[v] = true;
        }
        Check.True(seen.All(s => s), $"{what}に 0〜4095 がすべて 1 回ずつある");
    }

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}bin{sep}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlintLoop.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new CheckFailedException("GlintLoop.slnx のあるフォルダが見つからない");
    }
}
