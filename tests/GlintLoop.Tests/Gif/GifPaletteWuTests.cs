// パレットの作り方(仕様書 §9.8 の 3 の 1・2、AC-5 の (2))の中身の検査。ヒストグラムの区間の式を手で計算した値で固定し、
// Wu 法の箱の平均と並びを、累積を使わないテスト側の別実装(WuReference)と照合する。
// 同点の規則(箱の選び方・軸の順・位置・番号)は、座標を 2 進で割り切れる値にして同点が本当に起きる対称な入力で確かめる。
using GlintLoop.Core.Gif;
using GlintLoop.Core.Numerics;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Gif.GifTestKit;

namespace GlintLoop.Tests.Gif;

public sealed class GifPaletteWuTests
{
    // ---- 規則 3 の 1: 区間の式 ----

    [Test]
    public void AC5_2_ヒストグラムの区間は規則の式()
    {
        // (L, a, b) → (iL, ia, ib)。iL=min(63, floor(L×64))、ia=clamp(floor((a+0.24)÷0.52×64), 0, 63)、ib=clamp(floor((b+0.32)÷0.52×64), 0, 63)
        var cases = new (Lab P, (int L, int A, int B) Bin, string What)[]
        {
            (new Lab(1.0, 0, 0), (63, 29, 39), "白に当たる (1,0,0)"),
            (new Lab(0, 0, 0), (0, 29, 39), "黒に当たる (0,0,0)"),
            (new Lab(0.999, 0, 0), (63, 29, 39), "L=0.999"),
            (new Lab(0.15625, 0, 0), (10, 29, 39), "L=10/64 ちょうど"),
            (new Lab(0.5 - 1e-9, 0, 0), (31, 29, 39), "L=0.5 の直前"),
            (new Lab(0.5, -0.24, 0), (32, 0, 39), "a=−0.24 ちょうど"),
            (new Lab(0.5, -0.25, 0), (32, 0, 39), "a=−0.25 は 0 に切る"),
            (new Lab(0.5, -0.231875 - 1e-9, 0), (32, 0, 39), "a=−0.24+0.52/64 の直前"),
            (new Lab(0.5, -0.231875 + 1e-9, 0), (32, 1, 39), "a=−0.24+0.52/64 の直後"),
            (new Lab(0.5, 0.276, 0), (32, 63, 39), "a=0.276"),
            (new Lab(0.5, 0.3, 0), (32, 63, 39), "a=0.3 は 63 に切る"),
            (new Lab(0.5, 0, -0.32), (32, 29, 0), "b=−0.32 ちょうど"),
            (new Lab(0.5, 0, -0.33), (32, 29, 0), "b=−0.33 は 0 に切る"),
            (new Lab(0.5, 0, -0.23875 - 1e-9), (32, 29, 9), "b=−0.32+10×0.52/64 の直前"),
            (new Lab(0.5, 0, -0.23875 + 1e-9), (32, 29, 10), "b=−0.32+10×0.52/64 の直後"),
            (new Lab(0.5, 0, 0.199), (32, 29, 63), "b=0.199"),
            (new Lab(0.5, 0, 0.25), (32, 29, 63), "b=0.25 は 63 に切る"),
            (new Lab(0.25, -0.125, 0.0625), (16, 14, 47), "(0.25,−0.125,0.0625)"),
        };
        foreach (var (p, bin, what) in cases)
        {
            var cell = (bin.L * 64 + bin.A) * 64 + bin.B;
            Check.Equal(cell, GifHistogram.CellOf(p), what + " のセル");
            Check.Equal(bin, WuReference.BinOf(p), what + " の区間(テスト側の別実装)");
        }
        // 白と黒の 8 ビット色(§9.4 の変換)も同じセル
        Check.Equal((63 * 64 + 29) * 64 + 39, GifHistogram.CellOf(LabOf(255, 255, 255)), "#FFFFFF のセル");
        Check.Equal((0 * 64 + 29) * 64 + 39, GifHistogram.CellOf(LabOf(0, 0, 0)), "#000000 のセル");
    }

    // ---- 規則 3 の 2: 別実装との照合 ----

    private static Lab[] ImplMeans(IReadOnlyList<Lab> pixels, int nc)
    {
        var h = GifHistogram.CreateTotal();
        foreach (var p in pixels) h.AddPixel(p);
        return GifPalette.Build(h, nc).BoxMeans;
    }

    private static List<Lab> Pixels(byte[] rgba)
    {
        var list = new List<Lab>(rgba.Length / 4);
        for (var o = 0; o < rgba.Length; o += 4) list.Add(LabOf(rgba[o], rgba[o + 1], rgba[o + 2]));
        return list;
    }

    /// <summary>実装の BoxMeans と別実装の平均を、数と並びと値で比べる(箱の和の足し方が違うので値は 1e-9 まで)。</summary>
    private static void CheckSameAsReference(IReadOnlyList<Lab> pixels, int nc, string what)
    {
        var expect = WuReference.Build(WuReference.Histogram(pixels), nc).Means;
        var actual = ImplMeans(pixels, nc);
        Check.Equal(expect.Count, actual.Length, what + " の色の数");
        for (var i = 0; i < expect.Count; i++)
        {
            Check.Near(expect[i].L, actual[i].L, 1e-9, $"{what} 番号 {i} の L");
            Check.Near(expect[i].A, actual[i].A, 1e-9, $"{what} 番号 {i} の a");
            Check.Near(expect[i].B, actual[i].B, 1e-9, $"{what} 番号 {i} の b");
        }
    }

    [Test]
    public void AC5_2_Wu法の箱の平均と並びは累積を使わない別実装と一致する()
    {
        // 乱数の色
        var random = Pixels(RandomOpaque(64, 64, 90));
        foreach (var nc in new[] { 256, 16, 5 }) CheckSameAsReference(random, nc, $"乱数・nc={nc}");

        // 色の種類が 40 の乱数(塊のある分布)
        var clustered = Pixels(RandomOpaque(64, 64, 91, 40));
        foreach (var nc in new[] { 39, 16, 3 }) CheckSameAsReference(clustered, nc, $"40 色・nc={nc}");

        // グラデーション(写真に近い分布。AC5_2 と同じ 5 フレーム)
        var gradient = new List<Lab>();
        for (var k = 0; k < 5; k++) gradient.AddRange(Pixels(Gradient(96, 64, k, 40)));
        foreach (var nc in new[] { 256, 64, 7 }) CheckSameAsReference(gradient, nc, $"グラデーション・nc={nc}");
    }

    [Test]
    public void AC5_2_Wu法の箱の選び方は少ないセルでも別実装と一致する()
    {
        // セルが 5〜8 個で nc=3〜4。箱の選び方の違いがそのまま結果に出る大きさ。
        for (uint seed = 0; seed < 120; seed++)
        {
            var cells = 5 + (int)(Rand(0x5E11u, seed) % 4);
            var pixels = new List<Lab>();
            for (var c = 0; c < cells; c++)
            {
                var v = Rand(0x5E12u + seed, (uint)c);
                var p = new Lab(Hash.U01(v), Hash.U01(Rand(0x5E13u + seed, (uint)c)) * 0.5 - 0.23, Hash.U01(Rand(0x5E14u + seed, (uint)c)) * 0.5 - 0.31);
                var count = 1 + (int)(Rand(0x5E15u + seed, (uint)c) % 7);
                for (var i = 0; i < count; i++) pixels.Add(p);
            }
            foreach (var nc in new[] { 3, 4 }) CheckSameAsReference(pixels, nc, $"種 {seed}・セル {cells}・nc={nc}");
        }
    }

    [Test]
    public void AC5_2_Wu法は画素のあるセルが2つ以上の箱だけを割り画素の多い1セルの箱は割らない()
    {
        // 規則 3 の 2 (a) の「w>0 のセルを 2 つ以上含む箱」は、画素の数ではなくセルの数で数える。
        // L=0.157 と 0.170 を 50 個ずつ(どちらも iL=10 の同じセル)、L=0.5(iL=32)と 0.52(iL=33)を 1 個ずつ置き、nc=3 にする。
        // 最初の切り方のあと、セル 10 だけの箱は画素が 100 個で分散が最大になるが、セルが 1 つなので割らない(割れずに止まってはいけない)。
        var pixels = new List<Lab>();
        for (var i = 0; i < 50; i++) pixels.Add(new Lab(0.157, 0, 0));
        for (var i = 0; i < 50; i++) pixels.Add(new Lab(0.170, 0, 0));
        pixels.Add(new Lab(0.5, 0, 0));
        pixels.Add(new Lab(0.52, 0, 0));
        Check.Equal((10, 29, 39), WuReference.BinOf(new Lab(0.157, 0, 0)), "L=0.157 の区間");
        Check.Equal((10, 29, 39), WuReference.BinOf(new Lab(0.170, 0, 0)), "L=0.170 の区間");
        Check.Equal((32, 29, 39), WuReference.BinOf(new Lab(0.5, 0, 0)), "L=0.5 の区間");
        Check.Equal((33, 29, 39), WuReference.BinOf(new Lab(0.52, 0, 0)), "L=0.52 の区間");

        CheckSameAsReference(pixels, 3, "1 セルに 100 画素・nc=3");
        var means = ImplMeans(pixels, 3);
        Check.Equal(3, means.Length, "色の数");
        var expectL = new[] { 0.1635, 0.5, 0.52 };
        for (var i = 0; i < 3; i++)
        {
            Check.Near(expectL[i], means[i].L, 1e-12, $"番号 {i} の L");
            Check.Near(0, means[i].A, 1e-12, $"番号 {i} の a");
            Check.Near(0, means[i].B, 1e-12, $"番号 {i} の b");
        }
    }

    // ---- 規則 3 の 2: 同点の規則 ----
    // 座標は 2 進で割り切れる値、各点の画素は 4 個にし、和・二乗の和・|Σ|²÷w が誤差なく計算できるようにする(同点が本当に同点になる)。

    private static List<Lab> Points(params (double L, double A, double B)[] points)
    {
        var list = new List<Lab>();
        foreach (var (l, a, b) in points)
        {
            for (var i = 0; i < 4; i++) list.Add(new Lab(l, a, b));
        }
        return list;
    }

    private static void CheckMeans(Lab[] expected, IReadOnlyList<Lab> pixels, int nc, string what, Func<WuReference.Result, int> ties, string tieName)
    {
        var r = WuReference.Build(WuReference.Histogram(pixels), nc);
        Check.True(ties(r) >= 1, $"{what}: 入力で{tieName}の同点が起きていない(検査が規則を通っていない)");
        Check.SequenceEqual(expected, r.Means, what + "(テスト側の別実装)");
        Check.SequenceEqual(expected, ImplMeans(pixels, nc), what);
    }

    [Test]
    public void AC5_2_Wu法_分散が同点の箱は番号の小さい方を割り小さい側が元の番号()
    {
        // L 軸に 4 点。最初に真ん中で割ると、両側の箱の分散が同点になる。
        // 小さい側が番号 0 を引き継ぐので、次は小さい側(L=0.25 と 0.375)が割れる。
        var px = Points((0.25, 0, 0), (0.375, 0, 0), (0.625, 0, 0), (0.75, 0, 0));
        Check.SequenceEqual(new[] { new Lab(0.3125, 0, 0), new Lab(0.6875, 0, 0) }, ImplMeans(px, 2), "nc=2 は真ん中で割る");
        CheckMeans([new Lab(0.25, 0, 0), new Lab(0.375, 0, 0), new Lab(0.6875, 0, 0)], px, 3, "nc=3", r => r.PickTies, "箱の選び方");
    }

    [Test]
    public void AC5_2_Wu法_切る位置が同点なら小さい位置()
    {
        // L 軸に等間隔の 3 点。1 点目のあとで切っても 2 点目のあとで切っても、|Σ|²÷w の和は 3.375 で同点。
        var px = Points((0.25, 0, 0), (0.5, 0, 0), (0.75, 0, 0));
        CheckMeans([new Lab(0.25, 0, 0), new Lab(0.625, 0, 0)], px, 2, "等間隔の 3 点・nc=2", r => r.PositionTies, "位置");
    }

    [Test]
    public void AC5_2_Wu法_軸が同点なら軸の順Lab()
    {
        // (L, a) の正方形の 4 隅。L で切っても a で切っても同点なので、L で切る。
        var square = Points((0.25, -0.125, 0), (0.25, 0.125, 0), (0.5, -0.125, 0), (0.5, 0.125, 0));
        CheckMeans([new Lab(0.25, 0, 0), new Lab(0.5, 0, 0)], square, 2, "正方形・nc=2", r => r.AxisTies, "軸");

        // 立方体の 8 隅(L・a・b のどれで切っても同点)。L→a→b の順に、同点の箱は番号の小さい方から割れる。
        var cube = new List<Lab>();
        foreach (var l in new[] { 0.25, 0.5 })
            foreach (var a in new[] { -0.125, 0.125 })
                foreach (var b in new[] { -0.1875, 0.0625 })
                    cube.AddRange(Points((l, a, b)));
        CheckMeans([new Lab(0.25, 0, -0.0625), new Lab(0.5, 0, -0.0625)], cube, 2, "立方体・nc=2", r => r.AxisTies, "軸");
        CheckMeans(
            [new Lab(0.25, -0.125, -0.0625), new Lab(0.25, 0.125, -0.0625), new Lab(0.5, 0, -0.0625)],
            cube, 3, "立方体・nc=3", r => r.PickTies, "箱の選び方");
        CheckMeans(
            [
                new Lab(0.25, -0.125, -0.1875), new Lab(0.25, -0.125, 0.0625), new Lab(0.25, 0.125, -0.0625),
                new Lab(0.5, -0.125, -0.0625), new Lab(0.5, 0.125, -0.0625),
            ],
            cube, 5, "立方体・nc=5", r => r.PickTies, "箱の選び方");
    }

    [Test]
    public void AC5_2_Wu法_軸が同点なら_Lはbより先_aはbより先()
    {
        // (L, b) の正方形の 4 隅。L で切っても b で切っても同点なので、L で切る(b で切ると L=0.375 の 2 色になる)。
        var lb = Points((0.25, 0, -0.1875), (0.25, 0, 0.0625), (0.5, 0, -0.1875), (0.5, 0, 0.0625));
        CheckMeans([new Lab(0.25, 0, -0.0625), new Lab(0.5, 0, -0.0625)], lb, 2, "(L,b) の正方形・nc=2", r => r.AxisTies, "軸");

        // (a, b) の正方形の 4 隅。L は 1 つの区間なので L では切れない。a と b が同点なので、a で切る。
        var ab = Points((0.5, -0.125, -0.1875), (0.5, -0.125, 0.0625), (0.5, 0.125, -0.1875), (0.5, 0.125, 0.0625));
        CheckMeans([new Lab(0.5, -0.125, -0.0625), new Lab(0.5, 0.125, -0.0625)], ab, 2, "(a,b) の正方形・nc=2", r => r.AxisTies, "軸");
    }

    [Test]
    public void AC5_2_パレットの並びはLが同じならa_それも同じならb()
    {
        // 平均の昇順は L、同じなら a、それも同じなら b。L が同じ 3 色は、a の順と b の順が食い違うように置く。
        // (L→b→a の順に並べると、2 番目と 3 番目が入れ替わる)
        var px = Points((0.25, 0.125, 0.0625), (0.5, -0.125, 0.0625), (0.5, 0.125, -0.1875), (0.5, 0.125, 0.0625));
        Lab[] expected =
        [
            new Lab(0.25, 0.125, 0.0625), new Lab(0.5, -0.125, 0.0625), new Lab(0.5, 0.125, -0.1875), new Lab(0.5, 0.125, 0.0625),
        ];
        // nc=5 はセルが nc 未満(規則 4)、nc=4 は Wu 法で 1 セルずつの箱に割れる。どちらも同じ並べ替えを通る。
        foreach (var nc in new[] { 5, 4 })
        {
            Check.SequenceEqual(expected, WuReference.Build(WuReference.Histogram(px), nc).Means, $"nc={nc}(テスト側の別実装)");
            Check.SequenceEqual(expected, ImplMeans(px, nc), $"nc={nc}");
        }
    }

    [Test]
    public void AC5_2_パレットの並びはLとaが同じならbの昇順で箱の番号の順ではない()
    {
        // 画素は (0.25,0,−0.1875) と (0.5,0,−0.1875) が 1 個ずつ、(0.5,0,0.0625) が 2 個。nc=3 で Wu 法を通る。
        // 最初は b で割れ(和 0.859375 が L で割る 0.8489… より大きい)、次に b の小さい側を L で割るので、
        // 箱の番号の順は (0.25,0,−0.1875)・(0.5,0,0.0625)・(0.5,0,−0.1875)。並べ替えの 3 番目の鍵 b が 2 番目と 3 番目を入れ替える。
        var px = new List<Lab> { new(0.25, 0, -0.1875), new(0.5, 0, -0.1875), new(0.5, 0, 0.0625), new(0.5, 0, 0.0625) };
        Lab[] expected = [new Lab(0.25, 0, -0.1875), new Lab(0.5, 0, -0.1875), new Lab(0.5, 0, 0.0625)];
        Check.SequenceEqual(expected, WuReference.Build(WuReference.Histogram(px), 3).Means, "nc=3(テスト側の別実装)");
        Check.SequenceEqual(expected, ImplMeans(px, 3), "nc=3");
    }
}
