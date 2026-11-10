// AC-10(取り込みの形式・向き・ICC・上限・アニメーション・失敗)の検査。画像はすべてコードで作り、一時フォルダのファイルから取り込む。
// 取り込みは ImageImporter に偽の文書(FakeTarget)を渡して行い、通知・ログ・足し込みの呼び出しを確かめる。
// 20000×20000 の PNG のメモリの検査は時間がかかるので Slow にする(--all で走る)。
using System.Diagnostics;
using System.IO;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Tests.Framework;
using SkiaSharp;

namespace GlintLoop.Tests.Codec;

public sealed class IngestAc10Tests
{
    // 1 個のファイルを「開く」で取り込む。
    private static (ImportOutcome Outcome, FakeTarget Target, MemoryLogSink Log) OpenOne(string path, FakeTarget? target = null)
    {
        var t = target ?? new FakeTarget();
        var log = new MemoryLogSink();
        var o = new ImageImporter(t, log).Open([path]);
        o.CompleteDisplay();
        return (o, t, log);
    }

    private static ImportedImage OpenImage(TempDir dir, string name, byte[] bytes)
    {
        var (o, t, _) = OpenOne(dir.Write(name, bytes));
        Check.Equal(1, o.Added, name + " の足せた枚数");
        Check.Equal(1, t.Calls.Count, name + " の AddImages の回数");
        return o.AddedLayers[0].Image;
    }

    private static void ExpectFailure(ImportOutcome o, FakeTarget t, MemoryLogSink log, string noticeKey, string reason, string what)
    {
        Check.Equal(ImportStatus.Completed, o.Status, what + " の状態");
        Check.Equal(0, o.Added, what + " の足せた枚数");
        Check.Equal(0, t.Calls.Count, what + " で AddImages が呼ばれない(編集中の内容を変えない)");
        Check.Equal(noticeKey, o.Notice?.Key, what + " の通知");
        var fails = log.Events.Where(e => e.Name == "import_fail").ToArray();
        Check.Equal(1, fails.Length, what + " の import_fail の行数");
        Check.Equal(reason, fails[0].Get("reason"), what + " の reason");
        Check.Equal(0, log.Events.Count(e => e.Name == "import_ok"), what + " の import_ok の行数");
    }

    // ---------------- (1) 形式 ----------------

    [Test]
    public void AC10_1_6形式を先頭バイトで判定して読める()
    {
        using var dir = new TempDir();
        const int w = 6, h = 4;
        var alpha = Img.Pattern(w, h, alpha: true);
        var opaque = Img.Pattern(w, h, alpha: false);
        var png = SkiaBytes.Png(w, h, alpha);

        var cases = new (string Name, byte[] Bytes, ImageFormat Format, byte[] Expected, int Tolerance)[]
        {
            ("a.png", png, ImageFormat.Png, alpha, 0),
            ("b.jpg", SkiaBytes.Jpeg(w, h, Img.Solid(w, h, 180, 120, 60)), ImageFormat.Jpeg, Img.Solid(w, h, 180, 120, 60), 3), // 非可逆なので単色で比べる
            ("c.webp", SkiaBytes.WebpLossless(w, h, alpha), ImageFormat.Webp, alpha, 0),
            ("d.bmp", OtherBytes.Bmp24(w, h, opaque), ImageFormat.Bmp, opaque, 0),
            ("e.ico", OtherBytes.IcoWithPng(w, h, png), ImageFormat.Ico, alpha, 0),
            ("f.gif", OtherBytes.Gif(w, h, [Img.Solid(w, h, 200, 40, 90)]), ImageFormat.Gif, Img.Solid(w, h, 200, 40, 90), 2),
        };
        foreach (var c in cases)
        {
            Check.Equal(c.Format, ImageFormats.Sniff(c.Bytes), c.Name + " の判定");
            var img = OpenImage(dir, c.Name, c.Bytes);
            Check.Equal(c.Format, img.Format, c.Name + " の形式");
            Check.Equal(w, img.Width, c.Name + " の幅");
            Check.Equal(h, img.Height, c.Name + " の高さ");
            Check.True(Img.MaxDiff(c.Expected, img.Pixels) <= c.Tolerance,
                $"{c.Name} の画素の差 {Img.MaxDiff(c.Expected, img.Pixels)} が {c.Tolerance} 以内");
            Check.Equal(c.Bytes.LongLength, img.ByteCount, c.Name + " のバイト数");
            Check.Equal(ImportedImage.ComputeSha256(c.Bytes), img.Sha256, c.Name + " の SHA-256");
            Check.True(img.Sha256.Length == 64 && img.Sha256.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'),
                c.Name + " の SHA-256 は小文字の 16 進 64 桁");
            Check.True(!img.Animated && !img.GpuShrunk, c.Name + " に印が付かない");
        }
    }

    [Test]
    public void AC10_1_拡張子がjpgのPNGも先頭バイトで読める()
    {
        using var dir = new TempDir();
        var px = Img.Pattern(5, 3, alpha: true);
        var img = OpenImage(dir, "photo.jpg", SkiaBytes.Png(5, 3, px));
        Check.Equal(ImageFormat.Png, img.Format, "形式");
        Check.Equal("png", img.Ext, "ext");
        Check.Equal("png", img.LogFormat, "fmt");
        Check.Equal(0, Img.MaxDiff(px, img.Pixels), "画素");
    }

    [Test]
    public void AC10_1_TIFFの先頭バイトはimport_unsupported()
    {
        using var dir = new TempDir();
        foreach (var (name, head) in new[] { ("le.png", "II*\0"u8.ToArray()), ("be.tif", "MM\0*"u8.ToArray()) })
        {
            var bytes = head.Concat(new byte[60]).ToArray();
            Check.Equal(null, ImageFormats.Sniff(bytes), name + " の判定");
            var (o, t, log) = OpenOne(dir.Write(name, bytes));
            ExpectFailure(o, t, log, NoticeKeys.ImportUnsupported, "unsupported", name);
            Check.Equal(NoticeLevel.Error, o.Notice!.Level, name + " の通知の種別");
        }
    }

    [Test]
    public void AC10_1_16ビットPNGはv16割る257の丸めで8ビットになる()
    {
        // 灰の 256×256 で 16 ビットの全 65536 値を 1 回ずつ並べる。行のフィルターは 0〜4 を順に使う。
        const int n = 256;
        var gray = PngBytes.Encode(n, n, 16, 0, (x, y, _) => y * n + x, filterOf: r => r % 5);
        Check.True(Png16.IsSixteenBit(gray), "16 ビットの PNG を作れた");
        var d = ImageDecoder.Decode(gray);
        Check.Equal(n, d.Width, "幅");
        Check.Equal(n, d.Height, "高さ");
        for (var v = 0; v < 65536; v++)
        {
            var expected = (int)Math.Floor(v / 257.0 + 0.5);
            var i = v * 4;
            if (d.Rgba[i] != expected || d.Rgba[i + 1] != expected || d.Rgba[i + 2] != expected || d.Rgba[i + 3] != 255)
                throw new CheckFailedException($"v16={v}: 期待 {expected}、実際 ({d.Rgba[i]},{d.Rgba[i + 1]},{d.Rgba[i + 2]},{d.Rgba[i + 3]})");
        }

        // RGBA の 16 ビット・Adam7(インターレース)・フィルターあり。α も round(v÷257)(straight のまま)。
        const int w = 13, h = 11;
        int S(int x, int y, int c) => (int)((uint)(x * 7919 + y * 104729 + c * 15485863) * 2654435761u >> 16);
        var rgba = PngBytes.Encode(w, h, 16, 6, S, adam7: true, filterOf: r => (r * 3) % 5);
        var img = new ImageImporter(new FakeTarget(), new MemoryLogSink()).Paste(new MemoryImportData().With(ImportFormatNames.Png, rgba));
        Check.Equal(1, img.Added, "RGBA16 の Adam7 を取り込めた");
        var px = img.AddedLayers[0].Image.Pixels;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var expected = (int)Math.Floor(S(x, y, c) / 257.0 + 0.5);
                    if (c < 3 && px[(y * w + x) * 4 + 3] == 0) continue; // 完全に透明な画素の色は比べない
                    Check.Equal(expected, (int)px[(y * w + x) * 4 + c], $"RGBA16 ({x},{y}) の成分 {c}");
                }
            }
        }
    }

    // ---------------- (2) 向き ----------------

    [Test]
    public void AC10_2_EXIFの向き1から8を適用した姿勢になる()
    {
        // 8×8 の白地に、左上 (0,0) に赤・右上 (6,0) に緑・左下 (0,6) に青の 2×2 の塊。品質 100 の JPEG。
        const int n = 8;
        var px = Img.Solid(n, n, 255, 255, 255);
        void Block(int bx, int by, byte r, byte g, byte b)
        {
            for (var y = by; y < by + 2; y++)
                for (var x = bx; x < bx + 2; x++)
                {
                    var i = (y * n + x) * 4;
                    (px[i], px[i + 1], px[i + 2]) = (r, g, b);
                }
        }
        Block(0, 0, 255, 0, 0);
        Block(6, 0, 0, 255, 0);
        Block(0, 6, 0, 0, 255);
        var jpeg = SkiaBytes.Jpeg(n, n, px, 100);

        var expected = new Dictionary<int, ((int, int) R, (int, int) G, (int, int) B)>
        {
            [1] = ((0, 0), (6, 0), (0, 6)),
            [2] = ((6, 0), (0, 0), (6, 6)),
            [3] = ((6, 6), (0, 6), (6, 0)),
            [4] = ((0, 6), (6, 6), (0, 0)),
            [5] = ((0, 0), (0, 6), (6, 0)),
            [6] = ((6, 0), (6, 6), (0, 0)),
            [7] = ((6, 6), (6, 0), (0, 6)),
            [8] = ((0, 6), (0, 0), (6, 6)),
        };
        using var dir = new TempDir();
        for (var o = 1; o <= 8; o++)
        {
            var img = OpenImage(dir, $"o{o}.jpg", OtherBytes.JpegWithOrientation(jpeg, o));
            Check.Equal(n, img.Width, $"向き {o} の幅");
            Check.Equal(n, img.Height, $"向き {o} の高さ");
            var (er, eg, eb) = expected[o];
            Check.Equal(er, BlockOf(img.Pixels, n, 255, 0, 0), $"向き {o} の赤の塊");
            Check.Equal(eg, BlockOf(img.Pixels, n, 0, 255, 0), $"向き {o} の緑の塊");
            Check.Equal(eb, BlockOf(img.Pixels, n, 0, 0, 255), $"向き {o} の青の塊");
        }
    }

    [Test]
    public void AC10_2_向き5から8は幅と高さが入れ替わる()
    {
        // 縦横の違う 8×4 で、向き 6 は 4×8 になる(Orientation の並べ替えの向きを幅と高さでも確かめる)。
        var px = Img.Pattern(8, 4, alpha: false);
        for (var o = 1; o <= 8; o++)
        {
            var r = Orientation.Apply(px, 8, 4, o, out var ow, out var oh);
            Check.Equal(o >= 5 ? 4 : 8, ow, $"向き {o} の幅");
            Check.Equal(o >= 5 ? 8 : 4, oh, $"向き {o} の高さ");
            Check.Equal(px.Length, r.Length, $"向き {o} のバイト数");
        }
        // 向き 6(時計回りに 90 度): 出力の左上は元の左下、右上は元の左上
        var r6 = Orientation.Apply(px, 8, 4, 6, out var w6, out _);
        Check.Equal(Img.At(px, 8, 0, 3), Img.At(r6, w6, 0, 0), "向き 6 の左上");
        Check.Equal(Img.At(px, 8, 0, 0), Img.At(r6, w6, 3, 0), "向き 6 の右上");
    }

    // 色が (r,g,b) に最も近い画素が属する、座標が偶数から始まる 2×2 の塊の左上。
    private static (int, int) BlockOf(byte[] px, int w, int r, int g, int b)
    {
        var best = long.MaxValue;
        (int, int) at = (-1, -1);
        for (var i = 0; i < px.Length / 4; i++)
        {
            long dr = px[i * 4] - r, dg = px[i * 4 + 1] - g, db = px[i * 4 + 2] - b;
            var d = dr * dr + dg * dg + db * db;
            if (d < best)
            {
                best = d;
                at = (i % w & ~1, i / w & ~1);
            }
        }
        return at;
    }

    // ---------------- (3) ICC ----------------

    [Test]
    public void AC10_3_DisplayP3のPNGの代表5色がsRGBへ変換される()
    {
        // §8.4 の測定 2 の 5: P3 の値 → sRGB へ変換した値
        (byte R, byte G, byte B, int ER, int EG, int EB)[] colors =
        [
            (255, 0, 0, 255, 0, 0),
            (0, 255, 0, 0, 255, 0),
            (200, 120, 60, 213, 115, 42),
            (128, 128, 128, 128, 128, 128),
            (255, 255, 255, 255, 255, 255),
        ];
        var w = colors.Length;
        var rgba = new byte[w * 4];
        for (var i = 0; i < w; i++) (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (colors[i].R, colors[i].G, colors[i].B, 255);
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        var png = SkiaBytes.Png(w, 1, rgba, p3);
        Check.True(PngBytes.HasChunk(png, "iCCP"), "PNG に P3 の iCCP がある");

        using var dir = new TempDir();
        var img = OpenImage(dir, "p3.png", png);
        for (var i = 0; i < w; i++)
        {
            var (r, g, b, a) = Img.At(img.Pixels, w, i, 0);
            Check.Near(colors[i].ER, r, 2, $"色 {i} の R");
            Check.Near(colors[i].EG, g, 2, $"色 {i} の G");
            Check.Near(colors[i].EB, b, 2, $"色 {i} の B");
            Check.Equal((byte)255, a, $"色 {i} の α");
        }
        // 肌色は変換しないと (200,120,60) のままなので、変換されたことを確かめる
        Check.True(Img.At(img.Pixels, w, 2, 0).R >= 211, "肌色の R が変換されている");
    }

    [Test]
    public void AC10_3_壊れたプロファイルの画像はsRGBとして読める()
    {
        var px = Img.Pattern(7, 5, alpha: true);
        var png = SkiaBytes.Png(7, 5, px);
        var garbage = new byte[200];
        for (var i = 0; i < garbage.Length; i++) garbage[i] = (byte)GlintLoop.Core.Numerics.Hash.H(77u, (uint)i);
        using var dir = new TempDir();
        foreach (var (name, body) in new[]
                 {
                     ("garbage.png", PngBytes.IccpBody(garbage, validZlib: true)),
                     ("badzlib.png", PngBytes.IccpBody(garbage, validZlib: false)),
                 })
        {
            var broken = PngBytes.InsertAfterIhdr(png, "iCCP", body);
            Check.True(PngBytes.HasChunk(broken, "iCCP"), name + " に iCCP がある");
            var img = OpenImage(dir, name, broken);
            Check.Equal(0, Img.MaxDiff(px, img.Pixels), name + " の画素が sRGB のまま");
        }
    }

    // ---------------- (4) 上限 ----------------

    [Test]
    public void AC10_4_上限の境界で通ると拒否が切り替わる()
    {
        Check.Equal(null, ImportLimits.CheckSingle(100_000_000, 1, 1), "100,000,000 バイトは通る");
        var f = ImportLimits.CheckSingle(100_000_001, 1, 1);
        Check.Equal(ImportFailReason.TooLargeFile, f?.Reason, "100,000,001 バイトは拒否");
        Check.Equal(NoticeKeys.ImportTooLargeFile, f!.Notice.Key, "too_large_file の通知");
        Check.Equal("100.0", f.Notice.Args["size"], "{size}");
        Check.Equal("too_large_file", f.LogReason, "ログの reason");

        Check.Equal(null, ImportLimits.CheckSingle(1, 16384, 1), "幅 16384 は通る");
        Check.Equal(null, ImportLimits.CheckSingle(1, 1, 16384), "高さ 16384 は通る");
        foreach (var (w, h) in new[] { (16385L, 1L), (1L, 16385L) })
        {
            var s = ImportLimits.CheckSingle(1, w, h);
            Check.Equal(ImportFailReason.TooLargeSide, s?.Reason, $"{w}×{h} は辺で拒否");
            Check.Equal(NoticeKeys.ImportTooLargeSide, s!.Notice.Key, "too_large_side の通知");
            Check.Equal(w.ToString(), s.Notice.Args["w"], "{w}");
            Check.Equal(h.ToString(), s.Notice.Args["h"], "{h}");
            Check.Equal("too_large_side", s.LogReason, "ログの reason");
        }

        Check.Equal(null, ImportLimits.CheckSingle(1, 16384, 4096), "67,108,864 画素は通る");
        var p = ImportLimits.CheckSingle(1, 8065, 8321); // 8065×8321 = 67,108,865
        Check.Equal(67_108_865L, 8065L * 8321L, "画素数の計算");
        Check.Equal(ImportFailReason.TooManyPixels, p?.Reason, "67,108,865 画素は拒否");
        Check.Equal(NoticeKeys.ImportTooManyPixels, p!.Notice.Key, "too_many_pixels の通知");
        Check.Equal("67.1", p.Notice.Args["mp"], "{mp}");
        Check.Equal("too_many_pixels", p.LogReason, "ログの reason");

        // E-7 の順(大きさ → 辺 → 画素数)で最初に当てはまるものだけ
        Check.Equal(ImportFailReason.TooLargeFile, ImportLimits.CheckSingle(100_000_001, 20000, 20000)?.Reason, "大きさと辺と画素数 → 大きさ");
        Check.Equal(ImportFailReason.TooLargeSide, ImportLimits.CheckSingle(100_000_000, 16385, 16384)?.Reason, "辺と画素数 → 辺");
        foreach (var r in new[] { ImportLimits.CheckSingle(100_000_001, 1, 1)!, ImportLimits.CheckSingle(1, 16385, 1)!, p })
            Check.Equal(NoticeLevel.Error, r.Notice.Level, r.Notice.Key + " の種別");
    }

    [Test]
    public void AC10_4_大きすぎるファイルはデコードせずにtoo_large_file()
    {
        // 先頭が PNG で 100,000,001 バイトのファイル(中身は空き)。読まずに大きさで拒否する。
        using var dir = new TempDir();
        var path = dir.PathOf("big.png");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(SkiaBytes.Png(1, 1, Img.Solid(1, 1, 1, 2, 3)));
            fs.SetLength(100_000_001);
        }
        var (o, t, log) = OpenOne(path);
        ExpectFailure(o, t, log, NoticeKeys.ImportTooLargeFile, "too_large_file", "100,000,001 バイト");
        Check.Equal("100.0", o.Notice!.Args["size"], "{size}");
    }

    [Test(Slow = true)]
    public void AC10_4_INV11_20000四方のPNGは画素のメモリを確保せずに拒否される()
    {
        using var dir = new TempDir();
        var png = PngBytes.OneBitGray(20000, 20000, white: true);
        Check.True(png.Length < 5_000_000, $"PNG のファイルは数 MB 以下({png.Length} バイト)");
        var path = dir.Write("huge.png", png);
        png = null!;

        var target = new FakeTarget();
        var log = new MemoryLogSink();
        var importer = new ImageImporter(target, log);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var privateBefore = proc.PrivateMemorySize64;
        var workingBefore = proc.WorkingSet64;
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);

        var o = importer.Open([path]);

        proc.Refresh();
        var privateGrowth = proc.PrivateMemorySize64 - privateBefore;
        var workingGrowth = proc.WorkingSet64 - workingBefore;
        var allocGrowth = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        o.CompleteDisplay();

        ExpectFailure(o, target, log, NoticeKeys.ImportTooLargeSide, "too_large_side", "20000×20000");
        Check.Equal("20000", o.Notice!.Args["w"], "{w}");
        Check.Equal("20000", o.Notice.Args["h"], "{h}");
        const long limit = 200_000_000;
        Check.True(privateGrowth <= limit, $"プライベートのメモリの増加 {privateGrowth} バイトが 200 MB 以下");
        Check.True(workingGrowth <= limit, $"ワーキングセットの増加 {workingGrowth} バイトが 200 MB 以下");
        Check.True(allocGrowth <= limit, $"マネージドの確保 {allocGrowth} バイトが 200 MB 以下");
    }

    // ---------------- (5) アニメーション ----------------

    private static readonly (byte R, byte G, byte B)[] FrameColors = [(220, 40, 40), (40, 200, 60), (40, 60, 220)];

    private static List<byte[]> ThreeFrames(int w, int h) =>
        FrameColors.Select(c => Img.Solid(w, h, c.R, c.G, c.B)).ToList();

    [Test]
    public void AC10_5_3フレームのGIFは先頭フレームだけでimport_animated()
    {
        const int w = 8, h = 6;
        var gif = OtherBytes.Gif(w, h, ThreeFrames(w, h));
        using (var codec = SKCodec.Create(SKData.CreateCopy(gif)))
            Check.Equal(3, codec.FrameCount, "組み立てた GIF のフレーム数");
        using var dir = new TempDir();
        var (o, _, _) = OpenOne(dir.Write("anim.gif", gif));
        Check.Equal(1, o.Added, "足せた枚数");
        var img = o.AddedLayers[0].Image;
        Check.True(img.Animated, "アニメーションの印");
        Check.Equal(NoticeKeys.ImportAnimated, o.Notice?.Key, "通知(新しい文書の 1 枚でも出す)");
        Check.Equal(NoticeLevel.Info, o.Notice!.Level, "種別");
        Check.True(Img.MaxDiff(Img.Solid(w, h, 220, 40, 40), img.Pixels) <= 2, "先頭フレームの色(赤)");
    }

    [Test]
    public void AC10_5_アニメーションWebPは先頭フレームだけでimport_animated()
    {
        const int w = 8, h = 6;
        var webp = OtherBytes.AnimatedWebp(w, h, ThreeFrames(w, h));
        using (var codec = SKCodec.Create(SKData.CreateCopy(webp)))
        {
            Check.True(codec is not null, "組み立てた WebP を SkiaSharp が読める");
            Check.Equal(3, codec!.FrameCount, "組み立てた WebP のフレーム数");
        }
        using var dir = new TempDir();
        var (o, _, _) = OpenOne(dir.Write("anim.webp", webp), FakeTarget.WithDocument());
        Check.Equal(1, o.Added, "足せた枚数");
        var img = o.AddedLayers[0].Image;
        Check.Equal(ImageFormat.Webp, img.Format, "形式");
        Check.True(img.Animated, "アニメーションの印");
        Check.Equal(NoticeKeys.ImportAnimated, o.Notice?.Key, "通知(layer.added より優先)");
        Check.Equal(0, Img.MaxDiff(Img.Solid(w, h, 220, 40, 40), img.Pixels), "先頭フレームの色(赤)");
    }

    [Test]
    public void AC10_5_1フレームのGIFではimport_animatedが出ない()
    {
        const int w = 8, h = 6;
        var gif = OtherBytes.Gif(w, h, [Img.Solid(w, h, 40, 200, 60)]);
        using var dir = new TempDir();
        var path = dir.Write("one.gif", gif);

        var (o, _, _) = OpenOne(path);
        Check.True(!o.AddedLayers[0].Image.Animated, "印が無い");
        Check.Equal(null, o.Notice, "新しい文書の 1 枚では通知なし");

        var (o2, _, _) = OpenOne(path, FakeTarget.WithDocument());
        Check.True(!o2.AddedLayers[0].Image.Animated, "印が無い(文書あり)");
        Check.Equal(NoticeKeys.LayerAdded, o2.Notice?.Key, "文書があれば layer.added");
    }

    [Test]
    public void AC10_5_E10_APNGは通常のPNGとして先頭の画像だけを読みimport_animatedが出ない()
    {
        const int w = 6, h = 4;
        var apng = PngBytes.Apng(w, h, Img.Solid(w, h, 220, 40, 40), Img.Solid(w, h, 40, 60, 220));
        Check.True(PngBytes.HasChunk(apng, "acTL") && PngBytes.HasChunk(apng, "fdAT"), "APNG の塊がある");
        using var dir = new TempDir();
        var path = dir.Write("anim.png", apng);

        var (o, _, _) = OpenOne(path);
        Check.Equal(1, o.Added, "足せた枚数");
        var img = o.AddedLayers[0].Image;
        Check.Equal(ImageFormat.Png, img.Format, "形式");
        Check.Equal(0, Img.MaxDiff(Img.Solid(w, h, 220, 40, 40), img.Pixels), "先頭の画像(赤)");
        Check.True(!img.Animated, "APNG には印を付けない");
        Check.Equal(null, o.Notice, "新しい文書の 1 枚は通知なし");

        var (o2, _, _) = OpenOne(path, FakeTarget.WithDocument());
        Check.Equal(NoticeKeys.LayerAdded, o2.Notice?.Key, "文書があれば layer.added(import.animated ではない)");
    }

    // ---------------- (6) 失敗 ----------------

    [Test]
    public void AC10_6_途中で切れたPNGとヘッダーだけのJPEGはimport_decode_error()
    {
        var png = SkiaBytes.Png(32, 32, Img.Pattern(32, 32, alpha: true));
        var jpeg = SkiaBytes.Jpeg(32, 32, Img.Pattern(32, 32, alpha: false));
        // ヘッダーだけの JPEG: SOI から、最初の SOS(FF DA)の手前まで
        var sos = jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        Check.True(sos > 0, "JPEG に SOS がある");
        using var dir = new TempDir();
        foreach (var (name, bytes) in new[] { ("cut.png", png[..(png.Length / 2)]), ("head.jpg", jpeg[..sos]) })
        {
            var (o, t, log) = OpenOne(dir.Write(name, bytes), FakeTarget.WithDocument(3));
            ExpectFailure(o, t, log, NoticeKeys.ImportDecodeError, "decode_error", name);
            Check.Equal(3, t.LayerCount, name + " で文書のレイヤーが変わらない");
        }
    }

    [Test]
    public void AC10_6_存在しないパスと排他で開かれたファイルはimport_read_error()
    {
        using var dir = new TempDir();
        var (o1, t1, log1) = OpenOne(dir.PathOf("missing.png"), FakeTarget.WithDocument(2));
        ExpectFailure(o1, t1, log1, NoticeKeys.ImportReadError, "read_error", "存在しないパス");
        Check.Equal(2, t1.LayerCount, "存在しないパスで文書が変わらない");

        var path = dir.Write("locked.png", SkiaBytes.Png(4, 4, Img.Pattern(4, 4, alpha: true)));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (o2, t2, log2) = OpenOne(path, FakeTarget.WithDocument(2));
            ExpectFailure(o2, t2, log2, NoticeKeys.ImportReadError, "read_error", "排他で開かれたファイル");
            Check.Equal(2, t2.LayerCount, "排他のファイルで文書が変わらない");
        }
        // 閉じたあとは読める
        Check.Equal(1, OpenOne(path).Outcome.Added, "排他が解けたあとは取り込める");
    }

    [Test]
    public void INV2_読み取り専用のファイルを取り込めてバイト列が変わらない()
    {
        using var dir = new TempDir();
        var bytes = SkiaBytes.Png(4, 3, Img.Pattern(4, 3, alpha: true));
        var path = dir.Write("ro.png", bytes);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var (o, _, _) = OpenOne(path);
        Check.Equal(1, o.Added, "読み取り専用のファイルを取り込める");
        Check.SequenceEqual(bytes, File.ReadAllBytes(path), "ファイルのバイト列");
    }
}
