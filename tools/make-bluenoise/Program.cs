// make-bluenoise の入口。青色雑音の順位の表を作り、uint16 リトルエンディアンの 8192 バイトで書く(仕様書 §9.7 の 8)。
// 引数: 出力先のパス(省略時は作業フォルダからの src/GlintLoop.Core/Assets/bluenoise64.bin)。
// 書いたあと、SHA-256 と 8×8 の窓の平均の最小・最大を標準出力に出す。
using System.Security.Cryptography;
using GlintLoop.Tools.MakeBlueNoise;

var output = args.Length > 0 ? args[0] : Path.Combine("src", "GlintLoop.Core", "Assets", "bluenoise64.bin");
var table = VoidAndCluster.Generate();
var bytes = VoidAndCluster.ToBytes(table);
var full = Path.GetFullPath(output);
var dir = Path.GetDirectoryName(full);
if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
File.WriteAllBytes(full, bytes);

var (min, max) = VoidAndCluster.WindowMeanRange(table);
Console.WriteLine($"bytes: {bytes.Length}");
Console.WriteLine($"sha256: {Convert.ToHexStringLower(SHA256.HashData(bytes))}");
Console.WriteLine($"window8x8 mean: min {min:F6} max {max:F6}");
