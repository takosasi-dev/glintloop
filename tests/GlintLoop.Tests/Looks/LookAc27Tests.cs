// AC-27: 「ルックなし」と組み込みの 24 個の 25 通りの 192 px のサムネイルを、画像 3 枚(土台 1 枚だけの文書)で作り、300 組の差の平均を調べる(WARP)。
// 差は sRGB 8 ビットの RGB の絶対差の平均で、透明な画素は #808080 の上に線形光で合成してから比べる(合成の仕方は仮の決め)。seed は 12345。
// 基準(3.0、暫定)未満の組は一覧を出力する。AC-27 は一覧を作者が判断する手順なので、基準未満の組があってもテストは失敗にしない(仮の決め)。
using GlintLoop.Core.Looks;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Looks;

public sealed class LookAc27Tests
{
    /// <summary>基準(暫定)。</summary>
    public const double MeanLimit = 3.0;

    /// <summary>#808080 の線形光の値。</summary>
    private static readonly double GrayLin = Srgb.Lin8(128);

    /// <summary>straight の RGBA を #808080 の上に線形光で合成した RGB(画素ごとに 3 バイト)。</summary>
    internal static byte[] OverGray(DocumentImage im)
    {
        var n = im.Width * im.Height;
        var rgb = new byte[n * 3];
        for (var i = 0; i < n; i++)
        {
            var a = im.Pixels[i * 4 + 3] / 255.0;
            for (var c = 0; c < 3; c++)
            {
                var lin = Srgb.Lin8(im.Pixels[i * 4 + c]) * a + GrayLin * (1 - a);
                rgb[i * 3 + c] = (byte)Math.Clamp(MathX.RoundHalfUpToInt(255 * Srgb.Enc(MathX.Sat(lin))), 0, 255);
            }
        }
        return rgb;
    }

    /// <summary>RGB の絶対差の平均(チャンネルごとの差の平均)。</summary>
    internal static double MeanDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new CheckFailedException("サムネイルの大きさが違う");
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    [Test(Slow = true)]
    public void AC27_ルックなしと24個のサムネイルの300組の差の平均()
    {
        var images = LookTestImages.Three();
        var below = new List<string>();
        var pairs = 0;
        for (var k = 0; k < images.Count; k++)
        {
            using var kit = new CompositeKit();
            kit.Import(images[k]);
            Check.Equal(12345, kit.Doc.Seed, "seed");
            var looks = BuiltInLooks.Build(kit.Rs.Effects);
            Check.Equal(24, looks.Count, "組み込みのルックの数");
            var names = new List<string> { "ルックなし" };
            var thumbs = new List<byte[]>();
            var target = Target.OfLayer(0);
            var none = kit.Session.LookPreview(target, []);
            var lout = kit.Renderer.ThumbnailLongSide(none);
            Check.Equal(192, lout, $"画像 {k + 1} のサムネイルの長辺");
            thumbs.Add(OverGray(kit.Render(none, lout)));
            foreach (var look in looks)
            {
                names.Add(look.Name);
                var doc = kit.Session.LookPreview(target, look.Effects);
                thumbs.Add(OverGray(kit.Render(doc, kit.Renderer.ThumbnailLongSide(doc))));
            }
            var min = double.MaxValue;
            var minPair = "";
            for (var i = 0; i < thumbs.Count; i++)
            for (var j = i + 1; j < thumbs.Count; j++)
            {
                pairs++;
                var m = MeanDiff(thumbs[i], thumbs[j]);
                if (m < min)
                {
                    min = m;
                    minPair = $"{names[i]} と {names[j]}";
                }
                if (m < MeanLimit) below.Add($"画像 {k + 1}: {names[i]} と {names[j]} の平均 {m:F3}");
            }
            Console.WriteLine($"    AC-27 画像 {k + 1}: 最小の平均 {min:F3}({minPair})");
        }
        Check.Equal(300 * images.Count, pairs, "組の数");
        Console.WriteLine($"    AC-27 画像は{(LookTestImages.UsesReference ? "基準画像のフォルダの 3 枚" : "コードで作った 3 枚")}。基準 {MeanLimit:F1} 未満の組: {below.Count} 個");
        foreach (var line in below) Console.WriteLine("    AC-27 基準未満 " + line);
    }
}
