// 自作の GifEncoder の検査(仕様書 AC-5 の (1)〜(8))。最近傍・パレット・透明・青色雑音・差分・色数・マット色・誤差拡散。
// 内部の関数(GifNearest・GifPalette の BoxMeans・GifDitherer)を直接呼び、全探索や規則の式とテストの側で比べる。
// (5) の合成は、GifBitmapDecoder が disposal を扱わないので、テストの側で重ねる。
using GlintLoop.Core.Dither;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Numerics;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Gif.GifTestKit;

namespace GlintLoop.Tests.Gif;

public sealed class GifEncoderAc5Tests
{
    // ---- (1) 最近傍 ----

    [Test]
    public void AC5_1_最近傍は全探索と一致する()
    {
        // パレット: 写真に近い画像から作ったもの、乱数の色(重複を含む)、少ない色
        const int w = 120, h = 90;
        var plan = GifEncoder.Analyze(new GifFrameList(w, h, [Gradient(w, h, 0, 30), RandomOpaque(w, h, 31)]),
            new GifEncoderOptions { Colors = 256, DiffOptimization = false }, default);
        var palettes = new List<GifColor[]> { plan.Palette.Colors };
        var rnd = new GifColor[200];
        for (var i = 0; i < rnd.Length; i++)
        {
            // 番号 i と i−50 が同じ色(同じ距離のときは小さい番号)
            var v = Rand(32, (uint)(i >= 150 ? i - 50 : i));
            rnd[i] = new GifColor((byte)v, (byte)(v >> 8), (byte)(v >> 16));
        }
        palettes.Add(rnd);
        palettes.Add([new GifColor(10, 10, 10), new GifColor(10, 10, 10), new GifColor(250, 0, 0)]);

        foreach (var pal in palettes)
        {
            var nearest = new GifNearest(pal);
            var labs = pal.Select(c => LabOf(c.R, c.G, c.B)).ToList();
            for (var i = 0; i < 100_000; i++)
            {
                var v = Rand(33, (uint)i);
                Lab p;
                if ((i & 1) == 0)
                {
                    p = LabOf((byte)v, (byte)(v >> 8), (byte)(v >> 16));
                }
                else
                {
                    // 誤差拡散のように、8 ビットの色に無い位置も調べる
                    p = new Lab(Hash.U01(v) * 1.1 - 0.05, Hash.U01(Rand(34, (uint)i)) * 0.6 - 0.3, Hash.U01(Rand(35, (uint)i)) * 0.6 - 0.3);
                }
                var expect = BruteNearest(p, labs);
                Check.Equal(expect, nearest.Find(p), $"パレット {pal.Length} 色・画素 {i} の最近傍");
                var ex = BruteNearest(p, labs, expect);
                Check.Equal(ex, nearest.Find(p, expect), $"パレット {pal.Length} 色・画素 {i} の 2 番目");
            }
        }
    }

    // ---- (2) パレット ----

    [Test]
    public void AC5_2_パレットは毎回同じで箱の平均の昇順()
    {
        const int w = 96, h = 64, n = 5;
        var frames = Enumerable.Range(0, n).Select(k => Gradient(w, h, k, 40)).ToList();
        foreach (var colors in new[] { 256, 64, 7 })
        {
            var o = new GifEncoderOptions { Colors = colors };
            var a = GifEncoder.Analyze(new GifFrameList(w, h, frames), o, default);
            var b = GifEncoder.Analyze(new GifFrameList(w, h, frames), o with { Parallelism = 1 }, default);
            Check.SequenceEqual(a.Palette.Colors, b.Palette.Colors, $"色数 {colors}: 2 回の色と並び");
            Check.SequenceEqual(a.Palette.BoxMeans, b.Palette.BoxMeans, $"色数 {colors}: 2 回の箱の平均");
            var means = a.Palette.BoxMeans;
            Check.Equal(a.Palette.Colors.Length, means.Length, "平均の数");
            for (var i = 1; i < means.Length; i++)
            {
                var p = means[i - 1];
                var q = means[i];
                var ordered = p.L < q.L || (p.L == q.L && (p.A < q.A || (p.A == q.A && p.B <= q.B)));
                Check.True(ordered, $"色数 {colors}: 番号 {i - 1} と {i} の平均が昇順でない");
            }
            // 色は平均を fromOk→0〜1 に切る→enc→round したもの
            for (var i = 0; i < means.Length; i++)
            {
                var rgb = Oklab.FromOk(means[i]);
                var e = new GifColor(Q8(rgb.R), Q8(rgb.G), Q8(rgb.B));
                Check.Equal(e, a.Palette.Colors[i], $"色数 {colors}: 番号 {i} の色");
            }
            // ファイルのカラーテーブルも同じ並び
            var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o));
            for (var i = 0; i < means.Length; i++)
            {
                var c = a.Palette.Colors[i];
                Check.Equal((c.R, c.G, c.B), f.TableColor(i), $"色数 {colors}: カラーテーブル {i}");
            }
        }
    }

    private static byte Q8(double v) => (byte)Math.Floor(Srgb.Enc(Math.Clamp(v, 0, 1)) * 255 + 0.5);

    [Test]
    public void AC5_2_色の種類が少ないときは各セルが1色()
    {
        // 3 色の画像は、色数 256 でも 3 色になり、各色はその色そのもの
        const int w = 30, h = 30;
        var px = new byte[w * h * 4];
        var cols = new[] { (200, 30, 30), (30, 200, 30), (30, 30, 200) };
        for (var p = 0; p < w * h; p++)
        {
            var (r, g, b) = cols[p % 3];
            px[p * 4] = (byte)r;
            px[p * 4 + 1] = (byte)g;
            px[p * 4 + 2] = (byte)b;
            px[p * 4 + 3] = 255;
        }
        var plan = GifEncoder.Analyze(new GifFrameList(w, h, [px]), new GifEncoderOptions(), default);
        Check.Equal(3, plan.Palette.Count, "色の数");
        var got = plan.Palette.Colors.Select(c => ((int)c.R, (int)c.G, (int)c.B)).OrderBy(x => x).ToList();
        Check.SequenceEqual(cols.OrderBy(x => x).ToList(), got, "色");
    }

    // ---- (3) 透明 ----

    [Test]
    public void AC5_3_透明インデックスは色の直後で透明画素はすべてT()
    {
        const int w = 40, h = 40, n = 3;
        var frames = new List<byte[]>();
        for (var k = 0; k < n; k++)
        {
            var px = Gradient(w, h, k, 50);
            for (var p = 0; p < w * h; p++)
            {
                // α を 0〜255 にばらまく(127 は透明、128 は不透明)
                px[p * 4 + 3] = (byte)Rand(51 + (uint)k, (uint)p);
            }
            px[3] = 127;
            px[7] = 128;
            frames.Add(px);
        }
        foreach (var colors in new[] { 2, 16, 256 })
        {
            foreach (var dither in new[] { GifDither.BlueNoise, GifDither.ErrorDiffusion, GifDither.None })
            {
                var o = new GifEncoderOptions { Colors = colors, Dither = dither };
                var plan = GifEncoder.Analyze(new GifFrameList(w, h, frames), o, default);
                Check.Equal(plan.Palette.Count, plan.TransparentIndex, $"n={colors}: T は色の並びの直後");
                Check.True(plan.Palette.Count <= colors - 1 || (colors == 2 && plan.Palette.Count == 1), $"n={colors}: 色の数 {plan.Palette.Count} が n−1 以下");
                var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o));
                for (var k = 0; k < n; k++)
                {
                    var fr = f.Frames[k];
                    Check.True(fr.TransparentFlag && fr.TransparentIndexField == plan.TransparentIndex, $"n={colors} フレーム {k} の透明番号");
                    for (var p = 0; p < w * h; p++)
                    {
                        var transparent = frames[k][p * 4 + 3] < 128;
                        Check.True(transparent == (fr.Indices[p] == plan.TransparentIndex),
                            $"n={colors}・{dither} フレーム {k} 画素 {p}(α={frames[k][p * 4 + 3]})");
                    }
                }
            }
        }
    }

    // ---- (4) 青色雑音 ----

    [Test]
    public void AC5_4_青色雑音の番号は座標だけで決まる()
    {
        // 左半分は全フレームで同じ絵、右半分だけが動く
        const int w = 64, h = 32, n = 6;
        var still = Gradient(w, h, 0, 60);
        var frames = new List<byte[]>();
        for (var k = 0; k < n; k++)
        {
            var px = (byte[])still.Clone();
            var moving = Gradient(w, h, 7 * k + 3, 61);
            for (var y = 0; y < h; y++)
                Buffer.BlockCopy(moving, (y * w + w / 2) * 4, px, (y * w + w / 2) * 4, w / 2 * 4);
            frames.Add(px);
        }
        foreach (var dither in new[] { GifDither.BlueNoise, GifDither.None })
        {
            var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames),
                new GifEncoderOptions { Colors = 32, Dither = dither, DiffOptimization = false }));
            for (var k = 1; k < n; k++)
            {
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w / 2; x++)
                    {
                        Check.Equal(f.Frames[0].Indices[y * w + x], f.Frames[k].Indices[y * w + x], $"{dither} フレーム {k} の ({x},{y})");
                    }
                }
            }
        }
    }

    private static readonly GifColor[] BlackWhite = [new GifColor(0, 0, 0), new GifColor(255, 255, 255)];

    [Test]
    public void AC5_4_白黒のパレットと一様な灰色で1639個が0()
    {
        const int s = 64;
        var px = Solid(s, s, 0x80, 0x80, 0x80);
        var pal = new GifNearest(BlackWhite);
        var idx = GifDitherer.MapFrame(px, s, s, pal, GifDither.BlueNoise, GifPixel.Matte.None, -1);

        // 規則 5 の式で w を求める(c1 は白、c2 は黒)
        var p = LabOf(0x80, 0x80, 0x80);
        var c1 = LabOf(255, 255, 255);
        var c2 = LabOf(0, 0, 0);
        double dx = c2.L - c1.L, dy = c2.A - c1.A, dz = c2.B - c1.B;
        var wv = Math.Clamp(((p.L - c1.L) * dx + (p.A - c1.A) * dy + (p.B - c1.B) * dz) / Math.Max(dx * dx + dy * dy + dz * dz, 1e-12), 0, 1);
        Console.WriteLine($"  w={wv:F6}");
        Check.Near(0.4001, wv, 0.0005, "w");
        var zeros = 0;
        for (var j = 0; j < s; j++)
        {
            for (var i = 0; i < s; i++)
            {
                var expectZero = (BlueNoise.At(i, j) + 0.5) / 4096 < wv;
                Check.Equal(expectZero ? (byte)0 : (byte)1, idx[j * s + i], $"({i},{j}) の番号");
                if (idx[j * s + i] == 0) zeros++;
            }
        }
        Check.Equal(1639, zeros, "番号 0 の画素の数");
    }

    /// <summary>規則 5 の c1・c2・w を、テストの側で全探索と式から求める。</summary>
    private static (int C1, int C2, double W) ExpectedBlueNoise(Lab p, IReadOnlyList<Lab> labs)
    {
        var c1 = BruteNearest(p, labs);
        var l1 = labs[c1];
        var q = new Lab(2 * p.L - l1.L, 2 * p.A - l1.A, 2 * p.B - l1.B);
        var c2 = BruteNearest(q, labs, c1);
        if (c2 < 0) c2 = c1;
        var l2 = labs[c2];
        double dx = l2.L - l1.L, dy = l2.A - l1.A, dz = l2.B - l1.B;
        var wv = Math.Clamp(((p.L - l1.L) * dx + (p.A - l1.A) * dy + (p.B - l1.B) * dz) / Math.Max(dx * dx + dy * dy + dz * dz, 1e-12), 0, 1);
        return (c1, c2, wv);
    }

    [Test]
    public void AC5_4_青色雑音のc2はq_2p引くc1に最も近いc1以外の色()
    {
        // L 軸の灰色 3 色(L が約 0.35・0.5・1.0)と、L が約 0.6 の #808080。
        // c1 は L≈0.5、q の L は約 0.7 なので c2 は L≈1.0(p に 2 番目に近い色は L≈0.35 で、q=p と取り違えると変わる)。
        GifColor[] greys = [new GifColor(58, 58, 58), new GifColor(99, 99, 99), new GifColor(255, 255, 255)];
        var greyLabs = greys.Select(c => LabOf(c.R, c.G, c.B)).ToList();
        var grey = LabOf(0x80, 0x80, 0x80);
        Check.Equal(1, BruteNearest(grey, greyLabs), "c1 は L≈0.5");
        Check.Equal(0, BruteNearest(grey, greyLabs, 1), "p に 2 番目に近いのは L≈0.35(q と取り違えないことの前提)");
        var got = GifDitherer.BlueNoiseChoice(grey, new GifNearest(greys));
        Check.Equal((1, 2), (got.C1, got.C2), "灰色 3 色の (c1, c2)");
        Check.Near(ExpectedBlueNoise(grey, greyLabs).W, got.W, 1e-12, "灰色 3 色の w");

        // 黒・灰・白・赤の 4 色と、いろいろな p
        GifColor[] four = [new GifColor(0, 0, 0), new GifColor(128, 128, 128), new GifColor(255, 255, 255), new GifColor(255, 0, 0)];
        var fourLabs = four.Select(c => LabOf(c.R, c.G, c.B)).ToList();
        var fourNearest = new GifNearest(four);
        var differsFromQEqualsP = 0;
        var samples = new List<Lab> { grey, LabOf(200, 40, 40), LabOf(90, 20, 20), LabOf(180, 180, 180), LabOf(30, 30, 30), LabOf(255, 128, 128) };
        for (var i = 0; i < 200; i++)
        {
            var v = Rand(0xB1Eu, (uint)i);
            samples.Add(LabOf((byte)v, (byte)(v >> 8), (byte)(v >> 16)));
        }
        foreach (var p in samples)
        {
            var e = ExpectedBlueNoise(p, fourLabs);
            var a = GifDitherer.BlueNoiseChoice(p, fourNearest);
            Check.Equal((e.C1, e.C2), (a.C1, a.C2), $"4 色・p={p} の (c1, c2)");
            Check.Near(e.W, a.W, 1e-12, $"4 色・p={p} の w");
            if (BruteNearest(p, fourLabs, e.C1) != e.C2) differsFromQEqualsP++;
        }
        Console.WriteLine($"  q=p としたときと c2 が変わる p: {differsFromQEqualsP} 個");
        Check.True(differsFromQEqualsP >= 1, "q=p としたときと c2 が変わる p を含む");

        // ディザの関数を通した番号の列も、bn と w から求めた値と一致する
        const int s = 64;
        var cases = new (GifColor[] Pal, List<Lab> Labs, byte R, byte G, byte B, string What)[]
        {
            (greys, greyLabs, 0x80, 0x80, 0x80, "灰色 3 色・#808080"),
            (four, fourLabs, 200, 40, 40, "4 色・#C82828"),
        };
        foreach (var (pal, labs, r, g, b, what) in cases)
        {
            var e = ExpectedBlueNoise(LabOf(r, g, b), labs);
            Check.True(e.C1 != e.C2 && e.W > 0.05 && e.W < 0.95, $"{what}: c1 と c2 の両方が出る入力");
            var idx = GifDitherer.MapFrame(Solid(s, s, r, g, b), s, s, new GifNearest(pal), GifDither.BlueNoise, GifPixel.Matte.None, -1);
            for (var j = 0; j < s; j++)
            {
                for (var i = 0; i < s; i++)
                {
                    var expect = (BlueNoise.At(i, j) + 0.5) / 4096 < e.W ? e.C2 : e.C1;
                    Check.Equal((byte)expect, idx[j * s + i], $"{what} ({i},{j}) の番号");
                }
            }
        }
    }

    [Test]
    public void AC5_4_青色雑音の閾値はbnに0_5を足して4096で割りwより小さいとき()
    {
        // (bn+0.5)÷4096 < w のときだけ c2。等しければ c1、+0.5 を外した値 bn÷4096 より少し大きいだけでも c1。
        var seen = new HashSet<int>();
        for (var j = 0; j < 64; j += 5)
        {
            for (var i = 0; i < 64; i += 3)
            {
                var v = BlueNoise.At(i, j);
                seen.Add(v);
                var at = (v + 0.5) / 4096;
                Check.True(!GifDitherer.BlueNoisePicksSecond(i, j, at), $"({i},{j}) bn={v}: w=(bn+0.5)/4096 ちょうどは c1");
                Check.True(GifDitherer.BlueNoisePicksSecond(i, j, at + 1e-9), $"({i},{j}) bn={v}: w が (bn+0.5)/4096 より少し大きければ c2");
                Check.True(!GifDitherer.BlueNoisePicksSecond(i, j, at - 1e-9), $"({i},{j}) bn={v}: w が (bn+0.5)/4096 より少し小さければ c1");
                Check.True(!GifDitherer.BlueNoisePicksSecond(i, j, v / 4096.0 + 1e-9), $"({i},{j}) bn={v}: w=bn/4096+ε は c1");
                // 座標は 64 で回る
                Check.True(GifDitherer.BlueNoisePicksSecond(i + 64, j + 128, at + 1e-9), $"({i},{j}) は 64 で回る(c2)");
                Check.True(!GifDitherer.BlueNoisePicksSecond(i + 64, j + 128, at), $"({i},{j}) は 64 で回る(c1)");
            }
        }
        Check.True(seen.Count >= 100, "いろいろな bn の値を試した");
    }

    // ---- (5) 差分 ----

    private static readonly byte[] Levels = [0, 51, 102, 153, 204, 255];

    private static byte[] ShapesFrame(int w, int h, int k)
    {
        // 背景は 6 段階の色の格子、上に動く四角と丸
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                px[o] = Levels[x / 22 % 6];
                px[o + 1] = Levels[y / 22 % 6];
                px[o + 2] = Levels[(x / 22 + y / 22) % 6];
                px[o + 3] = 255;
                var sx = x - (10 + 4 * k);
                var sy = y - (20 + 2 * k);
                if (sx is >= 0 and < 24 && sy is >= 0 and < 18)
                {
                    px[o] = 255;
                    px[o + 1] = 204;
                    px[o + 2] = 0;
                }
                var dx = x - (100 - 3 * k);
                var dy = y - (90 - k);
                if (dx * dx + dy * dy <= 100)
                {
                    px[o] = 51;
                    px[o + 1] = 0;
                    px[o + 2] = 153;
                }
            }
        }
        return px;
    }

    [Test]
    public void AC5_5_差分ありとなしで合成した画面が元と一致し差分ありが小さい()
    {
        const int w = 128, h = 128, n = 20;
        var frames = Enumerable.Range(0, n).Select(k => ShapesFrame(w, h, k)).ToList();
        var sizes = new Dictionary<bool, int>();
        foreach (var diff in new[] { true, false })
        {
            var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), new GifEncoderOptions { DiffOptimization = diff });
            sizes[diff] = gif.Length;
            var wpf = ReadWithWpf(gif);
            Check.Equal(n, wpf.Count, $"差分{(diff ? "あり" : "なし")} のフレーム数");
            foreach (var fr in wpf) Check.Equal(1, fr.Disposal, $"差分{(diff ? "あり" : "なし")} の disposal");
            var shown = Composite(wpf, w, h);
            for (var k = 0; k < n; k++) CheckSameOpaque(frames[k], shown[k], $"差分{(diff ? "あり" : "なし")} フレーム {k}");
            if (diff)
            {
                Check.True(wpf.Skip(1).All(fr => fr.Width * fr.Height < w * h), "差分ありの 2 枚目以降は全面より小さい矩形");
                CheckDiffRects(MiniGifDecoder.Parse(gif), w, h);
            }
        }
        Console.WriteLine($"  差分あり {sizes[true]} バイト、差分なし {sizes[false]} バイト");
        Check.True(sizes[true] < sizes[false], "差分ありのほうが小さい");
    }

    /// <summary>
    /// 規則 6: 自作のデコーダーで戻した番号を disposal 1(T は前の値を残す)で重ね、各フレームの番号の配列を作る。
    /// フレーム k≥1 の画像記述子が、前のフレームと異なる画素の外接矩形そのもので、その中で前と同じ画素がちょうど T。
    /// </summary>
    private static void CheckDiffRects(MiniGifFile f, int w, int h)
    {
        var first = f.Frames[0];
        Check.Equal((0, 0, w, h), (first.Left, first.Top, first.Width, first.Height), "フレーム 0 は全面");
        Check.True(first.TransparentFlag, "差分ありは透明フラグが 1");
        var t = first.TransparentIndexField;
        var canvas = (byte[])first.Indices.Clone();
        var withSame = 0;
        for (var k = 1; k < f.Frames.Count; k++)
        {
            var fr = f.Frames[k];
            var next = (byte[])canvas.Clone();
            for (var y = 0; y < fr.Height; y++)
            {
                for (var x = 0; x < fr.Width; x++)
                {
                    var v = fr.Indices[y * fr.Width + x];
                    if (v != t) next[(fr.Top + y) * w + fr.Left + x] = v;
                }
            }
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (canvas[y * w + x] == next[y * w + x]) continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
            Check.True(maxX >= 0, $"フレーム {k} は前と異なる画素がある入力");
            Check.Equal((minX, minY, maxX - minX + 1, maxY - minY + 1), (fr.Left, fr.Top, fr.Width, fr.Height),
                $"フレーム {k} の画像記述子が異なる画素の外接矩形");
            for (var y = 0; y < fr.Height; y++)
            {
                for (var x = 0; x < fr.Width; x++)
                {
                    var p = (fr.Top + y) * w + fr.Left + x;
                    var same = canvas[p] == next[p];
                    if (same) withSame++;
                    Check.Equal(same, fr.Indices[y * fr.Width + x] == t, $"フレーム {k} の矩形の ({x},{y}): 前と同じ画素だけが T");
                }
            }
            canvas = next;
        }
        Check.True(withSame > 0, "矩形の中に前と同じ画素がある入力");
    }

    [Test]
    public void AC5_5_差分の矩形は異なる画素の外接矩形で中の同じ画素はT()
    {
        // 6×5 の番号の配列。異なる画素は (2,1)・(4,1)・(3,3) で、外接矩形は (2,1) から 3×3。その中の残りは前と同じ。
        byte[] prev =
        [
            0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 2,
            3, 3, 3, 3, 3, 3,
            4, 4, 4, 4, 4, 4,
        ];
        byte[] cur =
        [
            0, 0, 0, 0, 0, 0,
            1, 1, 5, 1, 6, 1,
            2, 2, 2, 2, 2, 2,
            3, 3, 3, 7, 3, 3,
            4, 4, 4, 4, 4, 4,
        ];
        const byte t = 9;
        var r1 = GifEncoder.DiffRect(6, 5, prev, cur, t);
        Check.Equal((2, 1, 3, 3), (r1.X, r1.Y, r1.W, r1.H), "外接矩形");
        Check.SequenceEqual(new byte[] { 5, t, 6, t, t, t, t, 7, t }, r1.Data, "矩形の中の番号(前と同じ画素は T)");

        // 端の画素だけが違う(左上と右下)
        var cur2 = (byte[])prev.Clone();
        cur2[0] = 8;
        cur2[29] = 8;
        var r2 = GifEncoder.DiffRect(6, 5, prev, cur2, t);
        Check.Equal((0, 0, 6, 5), (r2.X, r2.Y, r2.W, r2.H), "左上と右下が違うときは全面");
        Check.Equal((byte)8, r2.Data[0], "左上");
        Check.Equal((byte)8, r2.Data[29], "右下");
        Check.True(r2.Data.Skip(1).Take(28).All(v => v == t), "ほかは T");

        // 1 画素だけが違う
        var cur3 = (byte[])prev.Clone();
        cur3[3 * 6 + 5] = 0;
        var r3 = GifEncoder.DiffRect(6, 5, prev, cur3, t);
        Check.Equal((5, 3, 1, 1), (r3.X, r3.Y, r3.W, r3.H), "1 画素の外接矩形");
        Check.SequenceEqual(new byte[] { 0 }, r3.Data, "1 画素の番号");

        // 同じなら (0,0) の 1×1 で T
        var r4 = GifEncoder.DiffRect(6, 5, prev, (byte[])prev.Clone(), t);
        Check.Equal((0, 0, 1, 1), (r4.X, r4.Y, r4.W, r4.H), "同じときの矩形");
        Check.SequenceEqual(new byte[] { t }, r4.Data, "同じときの番号");
    }

    [Test]
    public void AC5_5_同じフレームが続くと2枚目は1x1のT()
    {
        const int w = 128, h = 128;
        var a = ShapesFrame(w, h, 3);
        var frames = new List<byte[]> { a, (byte[])a.Clone(), ShapesFrame(w, h, 4) };
        var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), new GifEncoderOptions());
        var f = MiniGifDecoder.Parse(gif);
        var second = f.Frames[1];
        Check.Equal((0, 0, 1, 1), (second.Left, second.Top, second.Width, second.Height), "2 枚目の画像記述子");
        Check.True(second.TransparentFlag, "透明フラグ");
        Check.Equal(second.TransparentIndexField, (int)second.Indices[0], "2 枚目の画素は T");
        var shown = Composite(ReadWithWpf(gif), w, h);
        for (var k = 0; k < frames.Count; k++) CheckSameOpaque(frames[k], shown[k], $"フレーム {k}");
    }

    [Test]
    public void AC5_5_透明を含むアニメは差分を使わずdisposal2()
    {
        const int w = 128, h = 128, n = 6;
        var frames = Enumerable.Range(0, n)
            .Select(k => WithTransparentOutsideCircle(ShapesFrame(w, h, k), w, h, 40 + 8 * k, 64, 30)).ToList();
        var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), new GifEncoderOptions { DiffOptimization = true });
        var f = MiniGifDecoder.Parse(gif);
        foreach (var fr in f.Frames)
        {
            Check.Equal(2, fr.Disposal, "disposal");
            Check.Equal((0, 0, w, h), (fr.Left, fr.Top, fr.Width, fr.Height), "全面");
        }
        var shown = Composite(ReadWithWpf(gif), w, h);
        for (var k = 0; k < n; k++)
        {
            for (var p = 0; p < w * h; p++)
            {
                var a = frames[k][p * 4 + 3];
                if (a < 128) Check.Equal((byte)0, shown[k][p * 4 + 3], $"フレーム {k} 画素 {p} は透明(残像なし)");
                else Check.Equal((byte)255, shown[k][p * 4 + 3], $"フレーム {k} 画素 {p} は不透明");
            }
        }
    }

    // ---- (6) 色数 ----

    [Test]
    public void AC5_6_色数nのときパレットはn以下で単色でも通る()
    {
        const int w = 48, h = 32;
        var many = new List<byte[]> { RandomOpaque(w, h, 70), RandomOpaque(w, h, 71) };
        var manyT = many.Select(px => WithTransparentOutsideCircle(px, w, h, 24, 16, 12)).ToList();
        foreach (var colors in new[] { 2, 16, 256 })
        {
            foreach (var (frames, name) in new[] { (many, "不透明"), (manyT, "透明を含む") })
            {
                foreach (var diff in new[] { true, false })
                {
                    var o = new GifEncoderOptions { Colors = colors, DiffOptimization = diff };
                    var plan = GifEncoder.Analyze(new GifFrameList(w, h, frames), o, default);
                    var limit = plan.TransparentIndex >= 0 ? Math.Max(1, colors - 1) : colors;
                    Check.True(plan.Palette.Count <= limit, $"n={colors}・{name}・差分{diff}: 色 {plan.Palette.Count} 個が {limit} 以下");
                    var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o));
                    Check.True(f.GlobalTableEntries <= Math.Max(2, colors), $"n={colors}: M={f.GlobalTableEntries}");
                }
            }
            // 単色の画像(1 フレームと 3 フレーム、不透明と透明あり、すべて透明)
            var solid = Solid(w, h, 12, 200, 99);
            var solidT = WithTransparentOutsideCircle(solid, w, h, 24, 16, 10);
            var clear = Solid(w, h, 0, 0, 0, 0);
            foreach (var frames in new List<byte[]>[] { [solid], [solid, solid, solid], [solidT, solidT], [clear], [clear, clear] })
            {
                var o = new GifEncoderOptions { Colors = colors };
                var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o);
                var f = MiniGifDecoder.Parse(gif);
                Check.Equal(frames.Count, f.Frames.Count, $"n={colors} 単色のフレーム数");
                Check.Equal(frames.Count, ReadWithWpf(gif).Count, $"n={colors} 単色を WPF で読んだフレーム数");
            }
        }
    }

    // ---- (7) マット色 ----

    [Test]
    public void AC5_7_マット色は線形光で合成した色に最も近いパレットの色()
    {
        const int w = 32, h = 32;
        var px = Gradient(w, h, 0, 80);
        var semi = new List<int>();
        for (var p = 0; p < w * h; p += 7)
        {
            px[p * 4 + 3] = 128; // α=128/255(約 0.5)
            semi.Add(p);
        }
        px[3] = 0;   // 完全に透明でも、マット色があれば合成して不透明
        px[11] = 127;
        var matte = new GifColor(255, 255, 255);
        foreach (var frames in new List<byte[]>[] { [px], [px, Gradient(w, h, 1, 81)] })
        {
            var o = new GifEncoderOptions { Colors = 64, Dither = GifDither.None, Matte = matte, DiffOptimization = false };
            var plan = GifEncoder.Analyze(new GifFrameList(w, h, frames), o, default);
            Check.Equal(-1, plan.TransparentIndex, "マット色があれば不透明(T を置かない)");
            var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o));
            var labs = TableLabs(f, plan.Palette.Count);
            foreach (var p in semi.Concat([0, 2]))
            {
                var o4 = p * 4;
                var al = px[o4 + 3] / 255.0;
                var c = Oklab.ToOk(
                    Srgb.Lin8(px[o4]) * al + Srgb.Lin8(255) * (1 - al),
                    Srgb.Lin8(px[o4 + 1]) * al + Srgb.Lin8(255) * (1 - al),
                    Srgb.Lin8(px[o4 + 2]) * al + Srgb.Lin8(255) * (1 - al));
                Check.Equal(BruteNearest(c, labs), (int)f.Frames[0].Indices[p], $"画素 {p}(α={px[o4 + 3]})の番号");
            }
        }
    }

    // ---- (8) 誤差拡散 ----

    [Test]
    public void AC5_8_誤差拡散の白黒の8x4()
    {
        const int w = 8, h = 4;
        var px = Solid(w, h, 0x80, 0x80, 0x80);
        var expected = new[] { "10101010", "11010101", "01101010", "10110101" };
        for (var run = 0; run < 3; run++)
        {
            var idx = GifDitherer.MapFrame(px, w, h, new GifNearest(BlackWhite), GifDither.ErrorDiffusion, GifPixel.Matte.None, -1);
            for (var j = 0; j < h; j++)
            {
                var row = string.Concat(Enumerable.Range(0, w).Select(i => idx[j * w + i].ToString()));
                Check.Equal(expected[j], row, $"{run + 1} 回目の {j + 1} 行目");
            }
        }
    }

    /// <summary>
    /// 規則 5 の誤差拡散をテストの側で素直に書いたもの。行は上から、画素は左から右。p'=p+err の最近傍 c、e=p'−c を ±0.25 に切り、
    /// 右 7/16・左下 3/16・下 5/16・右下 1/16 に足す。透明画素は T にし、そこに当たった誤差は使わずに捨てる(誤差も配らない)。
    /// </summary>
    private static byte[] ReferenceErrorDiffusion(byte[] rgba, int w, int h, IReadOnlyList<Lab> labs, int t)
    {
        var err = new Lab[w * h];
        var result = new byte[w * h];
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var p = j * w + i;
                if (rgba[p * 4 + 3] < 128)
                {
                    result[p] = (byte)t;
                    continue;
                }
                var src = LabOf(rgba[p * 4], rgba[p * 4 + 1], rgba[p * 4 + 2]);
                var pp = new Lab(src.L + err[p].L, src.A + err[p].A, src.B + err[p].B);
                var c = BruteNearest(pp, labs);
                result[p] = (byte)c;
                var e = new Lab(
                    Math.Clamp(pp.L - labs[c].L, -0.25, 0.25),
                    Math.Clamp(pp.A - labs[c].A, -0.25, 0.25),
                    Math.Clamp(pp.B - labs[c].B, -0.25, 0.25));
                Add(i + 1, j, 7.0 / 16);
                Add(i - 1, j + 1, 3.0 / 16);
                Add(i, j + 1, 5.0 / 16);
                Add(i + 1, j + 1, 1.0 / 16);

                void Add(int x, int y, double f)
                {
                    if (x < 0 || x >= w || y >= h) return;
                    var q = y * w + x;
                    err[q] = new Lab(err[q].L + e.L * f, err[q].A + e.A * f, err[q].B + e.B * f);
                }
            }
        }
        return result;
    }

    private static string Rows(byte[] idx, int w, int h) =>
        string.Join("/", Enumerable.Range(0, h).Select(j => string.Concat(Enumerable.Range(0, w).Select(i => idx[j * w + i].ToString()))));

    [Test]
    public void AC5_8_誤差拡散で透明画素に当たった誤差は捨てる()
    {
        const int w = 8, h = 4;
        var labs = BlackWhite.Select(c => LabOf(c.R, c.G, c.B)).ToList();
        // テスト側の別実装が、不透明の 8×4 で AC-5 (8) の列を再現することを先に確かめる
        var opaque = Solid(w, h, 0x80, 0x80, 0x80);
        Check.Equal("10101010/11010101/01101010/10110101", Rows(ReferenceErrorDiffusion(opaque, w, h, labs, 2), w, h), "別実装の不透明の 8×4");

        foreach (var column in new[] { 3, 0, 7 })
        {
            // 1 列だけ α=0。透明画素の右隣は、上の行から来る誤差だけを受ける。
            var px = Solid(w, h, 0x80, 0x80, 0x80);
            for (var j = 0; j < h; j++) px[(j * w + column) * 4 + 3] = 0;
            var expect = ReferenceErrorDiffusion(px, w, h, labs, 2);
            var idx = GifDitherer.MapFrame(px, w, h, new GifNearest(BlackWhite), GifDither.ErrorDiffusion, GifPixel.Matte.None, 2);
            Console.WriteLine($"  x={column} が透明: {Rows(idx, w, h)}");
            Check.Equal(Rows(expect, w, h), Rows(idx, w, h), $"x={column} の列が透明な 8×4 の番号");
        }
    }

    [Test]
    public void AC5_8_誤差拡散で透明な1画素に配られた誤差は下の行へも回さない()
    {
        // AC-5 (8) の 8×4 の #808080 で、(3,1) の 1 画素だけを α=0 にする。透明画素に溜まった誤差は捨てるので、
        // 真下の (3,2) は、まわりの不透明画素から来る誤差だけを受ける(透明画素の誤差を下へ回すと 3・4 行目が変わる)。
        const int w = 8, h = 4;
        var labs = BlackWhite.Select(c => LabOf(c.R, c.G, c.B)).ToList();
        var px = Solid(w, h, 0x80, 0x80, 0x80);
        px[(1 * w + 3) * 4 + 3] = 0;
        const string expected = "10101010/11021101/01101010/10110101";
        Check.Equal(expected, Rows(ReferenceErrorDiffusion(px, w, h, labs, 2), w, h), "別実装の (3,1) が透明な 8×4");
        for (var run = 0; run < 3; run++)
        {
            var idx = GifDitherer.MapFrame(px, w, h, new GifNearest(BlackWhite), GifDither.ErrorDiffusion, GifPixel.Matte.None, 2);
            Check.Equal(expected, Rows(idx, w, h), $"{run + 1} 回目の (3,1) が透明な 8×4 の番号");
        }

        // 1 画素だけ透明な位置を変えても、別実装と一致する(端と角を含む)
        foreach (var (x, y) in new[] { (0, 0), (7, 0), (0, 1), (7, 1), (4, 2), (0, 3), (5, 3) })
        {
            var q = Solid(w, h, 0x80, 0x80, 0x80);
            q[(y * w + x) * 4 + 3] = 0;
            var expect = ReferenceErrorDiffusion(q, w, h, labs, 2);
            var idx = GifDitherer.MapFrame(q, w, h, new GifNearest(BlackWhite), GifDither.ErrorDiffusion, GifPixel.Matte.None, 2);
            Check.Equal(Rows(expect, w, h), Rows(idx, w, h), $"({x},{y}) が透明な 8×4 の番号");
        }
    }
}
