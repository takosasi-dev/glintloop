// AC-11 の中身(貼り付けの順序・DIB の読み取り・ドロップのデータを直接渡す取り込み・起動引数・サンプル)の検査。
// クリップボードとドロップのデータは MemoryImportData(偽の DataObject)で渡し、文書は偽の IImportTarget で受ける。
// あわせて、文書の上限(FR-57)・同時に 1 つの門(E-20)・ログの中身(INV-15)・GPU の上限の縮小(E-11)を確かめる。
using System.IO;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Codec;

public sealed class IngestAc11Tests
{
    private static readonly byte[] PngA = SkiaBytes.Png(4, 3, Img.Solid(4, 3, 250, 10, 10));
    private static readonly byte[] PngB = SkiaBytes.Png(5, 2, Img.Solid(5, 2, 10, 250, 10));
    private static readonly byte[] JpegB = SkiaBytes.Jpeg(6, 4, Img.Solid(6, 4, 10, 10, 250));
    private static readonly byte[] TruncatedPng = Cut(SkiaBytes.Png(32, 32, Img.Pattern(32, 32, alpha: true)));

    // 画素のデータの途中で切る(IDAT の半ばまで)。
    private static byte[] Cut(byte[] png) => png[..(png.Length / 2)];
    private static readonly byte[] TextBytes = "これは画像ではありません\n"u8.ToArray();

    // .glint は先頭バイトで見分ける(§9.11 の 4 の (1))
    private static readonly byte[] ZipHead = [0x50, 0x4B, 0x03, 0x04, 0, 0];

    private static byte[] DibOf(int w, int h, byte r, byte g, byte b) =>
        Dib.Build(40, w, h, 24, 0, (_, _) => (uint)(r << 16 | g << 8 | b));

    private static byte[] DibV5Of(int w, int h, byte r, byte g, byte b) =>
        Dib.Build(124, w, h, 32, 3, (_, _) => 0xFF000000u | (uint)(r << 16 | g << 8 | b), alphaMask: 0xFF000000u);

    private static (ImageImporter Importer, FakeTarget Target, MemoryLogSink Log) New(FakeTarget? target = null)
    {
        var t = target ?? new FakeTarget();
        var log = new MemoryLogSink();
        return (new ImageImporter(t, log), t, log);
    }

    private static void ExpectMulti(ImportOutcome o, int n, int m, string what)
    {
        Check.Equal(NoticeKeys.ImportMulti, o.Notice?.Key, what + " の通知");
        Check.Equal(n.ToString(), o.Notice!.Args["N"], what + " の {N}");
        Check.Equal(m.ToString(), o.Notice.Args["M"], what + " の {M}");
    }

    private static int Count(MemoryLogSink log, string name) => log.Events.Count(e => e.Name == name);

    // ---------------- (1) 貼り付けの順序 ----------------

    [Test]
    public void AC11_1_貼り付けはFileDrop_PNG_DIBV5_DIBの順に試す()
    {
        using var dir = new TempDir();
        var fileA = dir.Write("fromfile.png", PngA);
        var full = new MemoryImportData()
            .With(ImportFormatNames.Dib, DibOf(2, 2, 1, 2, 3))
            .With(ImportFormatNames.DibV5, DibV5Of(2, 2, 4, 5, 6))
            .With(ImportFormatNames.Png, PngB)
            .WithFiles(fileA);

        // 全部あれば FileDrop(名前はファイル名から)
        var (imp, t, _) = New();
        var o = imp.Paste(full);
        Check.Equal(1, o.Added, "FileDrop の足せた枚数");
        Check.Equal("fromfile", o.AddedLayers[0].Name, "FileDrop の名前");
        Check.Equal(ImportVia.Paste, t.Calls[0].Via, "via");

        // FileDrop が読めない(切れた PNG)なら PNG
        var cut = dir.Write("cut.png", TruncatedPng);
        var d2 = new MemoryImportData().WithFiles(cut).With(ImportFormatNames.Png, PngB)
            .With(ImportFormatNames.DibV5, DibV5Of(2, 2, 4, 5, 6)).With(ImportFormatNames.Dib, DibOf(2, 2, 1, 2, 3));
        (imp, t, _) = New();
        o = imp.Paste(d2);
        Check.Equal(1, o.Added, "PNG の足せた枚数");
        Check.Equal(ImageFormat.Png, o.AddedLayers[0].Image.Format, "PNG を使った");
        Check.Equal(5, o.AddedLayers[0].Image.Width, "PNG の B を使った");
        Check.Equal(LayerNames.Paste, o.AddedLayers[0].Name, "クリップボードの名前は「貼り付け」");
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.Equal(null, o.Notice, "新しい文書の 1 枚は通知なし");

        // PNG が壊れていれば DIBV5
        (imp, _, _) = New();
        o = imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, TruncatedPng)
            .With(ImportFormatNames.DibV5, DibV5Of(2, 2, 4, 5, 6)).With(ImportFormatNames.Dib, DibOf(2, 2, 1, 2, 3)));
        Check.Equal(ImageFormat.Dib, o.AddedLayers[0].Image.Format, "DIBV5 の形式");
        Check.Equal(((byte)4, (byte)5, (byte)6, (byte)255), Img.At(o.AddedLayers[0].Image.Pixels, 2, 0, 0), "DIBV5 の画素");

        // DIBV5 も読めなければ DIB
        (imp, _, _) = New();
        o = imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, TruncatedPng)
            .With(ImportFormatNames.DibV5, DibV5Of(2, 2, 4, 5, 6)[..50]).With(ImportFormatNames.Dib, DibOf(2, 2, 1, 2, 3)));
        Check.Equal(((byte)1, (byte)2, (byte)3, (byte)255), Img.At(o.AddedLayers[0].Image.Pixels, 2, 0, 0), "DIB の画素");
        Check.Equal("dib", o.AddedLayers[0].Image.LogFormat, "fmt");
    }

    [Test]
    public void AC11_1_読む形式が無いとpaste_empty()
    {
        foreach (var (what, data) in new[]
                 {
                     ("空", new MemoryImportData()),
                     ("文字だけ", new MemoryImportData().WithUrlOrText("UnicodeText")),
                     ("URL だけ", new MemoryImportData().WithUrlOrText()),
                 })
        {
            var (imp, t, log) = New(FakeTarget.WithDocument());
            var o = imp.Paste(data);
            Check.Equal(NoticeKeys.PasteEmpty, o.Notice?.Key, what + " の通知");
            Check.Equal(NoticeLevel.Warn, o.Notice!.Level, what + " の種別");
            Check.Equal(0, t.Calls.Count, what + " で何も足さない");
            Check.Equal(0, log.Events.Count, what + " ではログを書かない");
        }
    }

    [Test]
    public void AC11_1_壊れたPNGだけならimport_decode_error()
    {
        var (imp, t, log) = New();
        var o = imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, TruncatedPng));
        Check.Equal(NoticeKeys.ImportDecodeError, o.Notice?.Key, "通知");
        Check.Equal(0, t.Calls.Count, "足さない");
        Check.Equal(1, Count(log, "import_fail"), "import_fail の行数");

        // PNG の形式名に画像でないバイト列があるときも、壊れている扱い
        (imp, _, _) = New();
        o = imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, TextBytes));
        Check.Equal(NoticeKeys.ImportDecodeError, o.Notice?.Key, "画像でない PNG のデータの通知");
    }

    [Test]
    public void AC11_1_FileDropが対応形式でないファイルだけなら読めない形式として次へ進む()
    {
        using var dir = new TempDir();
        var text = dir.Write("text.png", TextBytes);
        var tiff = dir.Write("scan.tif", "II*\0"u8.ToArray().Concat(new byte[40]).ToArray());
        var proj = dir.Write("work.glint", "{}"u8.ToArray());

        var (imp, t, log) = New();
        var o = imp.Paste(new MemoryImportData().WithFiles(text, tiff, proj));
        Check.Equal(NoticeKeys.PasteEmpty, o.Notice?.Key, "ほかに形式が無ければ paste.empty");
        Check.Equal(0, t.Calls.Count, "足さない");
        Check.Equal(0, log.Events.Count, "ログを書かない(import.unsupported も import.multi も無い)");

        (imp, t, _) = New();
        o = imp.Paste(new MemoryImportData().WithFiles(text, tiff, proj).With(ImportFormatNames.Png, PngB));
        Check.Equal(1, o.Added, "次の PNG を使う");
        Check.Equal(5, o.AddedLayers[0].Image.Width, "PNG の B");
        Check.Equal(null, o.Notice, "通知なし(新しい文書の 1 枚)");
    }

    [Test]
    public void AC11_1_FileDropのA_テキスト_Bは判定できる2個を順に足してlayer_added()
    {
        using var dir = new TempDir();
        var a = dir.Write("A.png", PngA);
        var text = dir.Write("note.png", TextBytes);
        var b = dir.Write("B.jpg", JpegB);
        var (imp, t, log) = New();
        var o = imp.Paste(new MemoryImportData().WithFiles(a, text, b));
        Check.Equal(2, o.Requested, "数えたのは判定できる 2 個");
        Check.Equal(2, o.Added, "足せた枚数");
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.SequenceEqual(["A", "B"], t.Calls[0].Layers.Select(l => l.Name).ToArray(), "渡された順");
        Check.Equal(NoticeKeys.LayerAdded, o.Notice?.Key, "layer.added");
        o.CompleteDisplay();
        Check.Equal(2, Count(log, "import_ok"), "import_ok の行数");
        Check.Equal(0, Count(log, "import_fail"), "テキストは数えない");
    }

    [Test]
    public void AC11_1_FileDropのAと切れたPNGはimport_multi_2_1()
    {
        using var dir = new TempDir();
        var a = dir.Write("A.png", PngA);
        var cut = dir.Write("cut.png", TruncatedPng);
        var (imp, t, _) = New();
        var o = imp.Paste(new MemoryImportData().WithFiles(a, cut));
        Check.Equal(1, o.Added, "A だけ足す");
        Check.Equal("A", t.Calls[0].Layers[0].Name, "A");
        ExpectMulti(o, 2, 1, "[A, 切れた PNG]");
    }

    [Test]
    public void AC11_1_切れたPNG2個ではほかの形式も無ければimport_decode_error()
    {
        using var dir = new TempDir();
        var c1 = dir.Write("c1.png", TruncatedPng);
        var c2 = dir.Write("c2.png", Cut(SkiaBytes.Png(40, 20, Img.Pattern(40, 20, alpha: false))));
        var (imp, t, _) = New();
        var o = imp.Paste(new MemoryImportData().WithFiles(c1, c2));
        Check.Equal(0, t.Calls.Count, "足さない");
        Check.Equal(NoticeKeys.ImportDecodeError, o.Notice?.Key, "通知");

        // ほかに読める形式があれば、そちらを使う(足せた枚数が 0 のときだけ次へ)
        (imp, _, _) = New();
        o = imp.Paste(new MemoryImportData().WithFiles(c1, c2).With(ImportFormatNames.Dib, DibOf(3, 1, 7, 8, 9)));
        Check.Equal(1, o.Added, "DIB を使う");
        Check.Equal(ImageFormat.Dib, o.AddedLayers[0].Image.Format, "DIB");
    }

    [Test]
    public void AC11_1_E2_クリップボードを開けないとclip_failed()
    {
        var (imp, t, log) = New();
        var o = imp.Paste(new ThrowingData(0x800401D0u));
        Check.Equal(NoticeKeys.ClipFailed, o.Notice?.Key, "通知");
        Check.Equal(0, t.Calls.Count, "足さない");
        var e = log.Events.Single();
        Check.Equal("clipboard_failed", e.Name, "ログ");
        Check.Equal(2147746256u, e.Get("hr"), "hr は符号なし 32 ビット");
    }

    private sealed class ThrowingData(uint hr) : IImportData
    {
        public IReadOnlyList<string> Formats => throw new ImportDataUnavailableException(hr);

        public byte[]? GetBytes(string format) => throw new ImportDataUnavailableException(hr);

        public IReadOnlyList<string>? GetFileList() => throw new ImportDataUnavailableException(hr);

        public bool HasUrlOrText => throw new ImportDataUnavailableException(hr);
    }

    // ---------------- (2) DIB の読み取り ----------------

    // 上から数えた (x, y) の色。幅が奇数でも行ごとに違う値になる。
    private static (byte R, byte G, byte B) ColorAt(int x, int y) => ((byte)(10 + x * 40), (byte)(20 + y * 50), (byte)(200 - x * 10 - y * 20));

    private static void ExpectPixels(byte[]? px, int w, int h, Func<int, int, (byte, byte, byte, byte)> expected, string what)
    {
        Check.True(px is not null, what + " を読める");
        Check.Equal(w * h * 4, px!.Length, what + " のバイト数");
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Check.Equal(expected(x, y), Img.At(px, w, x, y), $"{what} の ({x},{y})");
    }

    [Test]
    public void AC11_2_DIBの24ビットは高さの正負で行の向きが変わり行は4バイト境界()
    {
        foreach (var w in new[] { 3, 5 }) // 3×3=9 → 12 バイト、5×3=15 → 16 バイトに詰める
        {
            const int h = 3;
            foreach (var height in new[] { h, -h })
            {
                var dib = Dib.Build(40, w, height, 24, 0, (x, y) => { var c = ColorAt(x, y); return (uint)(c.R << 16 | c.G << 8 | c.B); });
                Check.Equal(40 + (w * 3 + 3) / 4 * 4 * h, dib.Length, $"幅 {w} の DIB の長さ");
                var px = DibReader.Read(dib, out var rw, out var rh);
                Check.Equal(w, rw, "幅");
                Check.Equal(h, rh, "高さ");
                ExpectPixels(px, w, h, (x, y) => { var c = ColorAt(x, y); return (c.R, c.G, c.B, (byte)255); },
                    $"24 ビット 幅 {w} 高さ {height}");
            }
        }
    }

    [Test]
    public void AC11_2_DIBの32ビットBI_RGBは不透明()
    {
        const int w = 3, h = 2;
        // 4 バイト目に 0 でない値があっても、アルファのマスクが無いので α=255
        var dib = Dib.Build(40, w, h, 32, 0, (x, y) => { var c = ColorAt(x, y); return (uint)(0x40 << 24 | c.R << 16 | c.G << 8 | c.B); });
        ExpectPixels(DibReader.Read(dib, out _, out _), w, h, (x, y) => { var c = ColorAt(x, y); return (c.R, c.G, c.B, (byte)255); },
            "32 ビット BI_RGB");
    }

    [Test]
    public void AC11_2_DIBの32ビットBI_BITFIELDSはマスクで色を取りアルファのマスクの有無で変わる()
    {
        const int w = 3, h = 2;
        // 40 バイトのヘッダー + 3 つのマスク(赤と青を入れ替えた並び)。アルファのマスクは無いので α=255。
        uint[] swapped = [0x000000FFu, 0x0000FF00u, 0x00FF0000u];
        var noAlpha = Dib.Build(40, w, h, 32, 3, (x, y) => { var c = ColorAt(x, y); return (uint)(0x80 << 24 | c.B << 16 | c.G << 8 | c.R); }, swapped);
        Check.Equal(40 + 12 + w * 4 * h, noAlpha.Length, "マスクの 12 バイトを含む長さ");
        ExpectPixels(DibReader.Read(noAlpha, out _, out _), w, h, (x, y) => { var c = ColorAt(x, y); return (c.R, c.G, c.B, (byte)255); },
            "BI_BITFIELDS アルファのマスクなし");

        // V5 のヘッダー + アルファのマスクあり: straight のアルファとして読む
        byte A(int x, int y) => (byte)(x * 100 + y * 20);
        var withAlpha = Dib.Build(124, w, -h, 32, 3, (x, y) => { var c = ColorAt(x, y); return (uint)(A(x, y) << 24 | c.R << 16 | c.G << 8 | c.B); },
            alphaMask: 0xFF000000u);
        ExpectPixels(DibReader.Read(withAlpha, out _, out _), w, h, (x, y) => { var c = ColorAt(x, y); return (c.R, c.G, c.B, A(x, y)); },
            "BI_BITFIELDS アルファのマスクあり(V5)");

        // アルファのマスクがあってもアルファがすべて 0 なら不透明
        var zeroAlpha = Dib.Build(124, w, h, 32, 3, (x, y) => { var c = ColorAt(x, y); return (uint)(c.R << 16 | c.G << 8 | c.B); },
            alphaMask: 0xFF000000u);
        ExpectPixels(DibReader.Read(zeroAlpha, out _, out _), w, h, (x, y) => { var c = ColorAt(x, y); return (c.R, c.G, c.B, (byte)255); },
            "アルファがすべて 0");
    }

    [Test]
    public void AC11_2_V5ヘッダーでアルファありのDIBVはstraightで取り込まれる()
    {
        const int w = 2, h = 2;
        var dib = Dib.Build(124, w, h, 32, 3, (x, y) => (uint)((x == 0 ? 128 : 255) << 24 | 200 << 16 | 100 << 8 | 50), alphaMask: 0xFF000000u);
        var (imp, _, _) = New();
        var o = imp.Paste(new MemoryImportData().With(ImportFormatNames.DibV5, dib));
        Check.Equal(1, o.Added, "足せた");
        var px = o.AddedLayers[0].Image.Pixels;
        Check.Equal(((byte)200, (byte)100, (byte)50, (byte)128), Img.At(px, w, 0, 0), "α=128 の画素は straight の色のまま");
        Check.Equal(((byte)200, (byte)100, (byte)50, (byte)255), Img.At(px, w, 1, 1), "α=255 の画素");
    }

    [Test]
    public void AC11_2_ヘッダーが短いか画素が足りないDIBは読めない形式として次の順位へ進む()
    {
        var good = DibOf(3, 2, 9, 8, 7);
        var v5 = DibV5Of(3, 2, 1, 2, 3);
        byte[][] bad =
        [
            good[..39],                      // 40 バイトのヘッダーに足りない
            v5[..100],                       // 124 バイトのヘッダーに足りない
            good[..(good.Length - 1)],       // 画素が 1 バイト足りない
            v5[..(v5.Length - 4)],           // 画素が 1 画素足りない
            Dib.Build(40, 3, 2, 16, 0, (_, _) => 0), // 16 ビットは読まない
            Dib.Build(40, 3, 2, 24, 1, (_, _) => 0), // BI_RLE8 は読まない
            Dib.Build(108, 3, 2, 32, 0, (_, _) => 0), // V4 のヘッダー(108 バイト)は読まない
        ];
        foreach (var (b, i) in bad.Select((b, i) => (b, i)))
        {
            Check.Equal(null, DibReader.Read(b, out _, out _), $"読めない DIB {i}");
            Check.Equal(null, DibReader.ReadHeader(b), $"読めない DIB {i} のヘッダー");
        }

        // DIBV5 が読めなければ DIB へ
        var (imp, _, _) = New();
        var o = imp.Paste(new MemoryImportData().With(ImportFormatNames.DibV5, v5[..100]).With(ImportFormatNames.Dib, good));
        Check.Equal(((byte)9, (byte)8, (byte)7, (byte)255), Img.At(o.AddedLayers[0].Image.Pixels, 3, 0, 0), "次の DIB を使った");

        // どれも読めなければ import.decode_error
        var (imp2, t2, _) = New();
        o = imp2.Paste(new MemoryImportData().With(ImportFormatNames.DibV5, v5[..100]).With(ImportFormatNames.Dib, good[..39]));
        Check.Equal(NoticeKeys.ImportDecodeError, o.Notice?.Key, "どれも読めない");
        Check.Equal(0, t2.Calls.Count, "足さない");
    }

    // ---------------- (4) ドロップ(データを直接渡す) ----------------

    [Test]
    public void AC11_4_PNG_テキスト_JPEGの3個は読めた2個を順に足してimport_multi_3_2()
    {
        using var dir = new TempDir();
        var a = dir.Write("first.png", PngA);
        var text = dir.Write("fake.png", TextBytes);
        var b = dir.Write("second.jpg", JpegB);
        var (imp, t, log) = New();
        var o = imp.Drop(new MemoryImportData().WithFiles(a, text, b));
        Check.Equal(3, o.Requested, "N");
        Check.Equal(2, o.Added, "M");
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回だけ(Undo の 1 件)");
        Check.Equal(ImportVia.Drop, t.Calls[0].Via, "via");
        Check.SequenceEqual(["first", "second"], t.Calls[0].Layers.Select(l => l.Name).ToArray(), "渡された順(最初の 1 枚が土台)");
        Check.SequenceEqual([ImageFormat.Png, ImageFormat.Jpeg], t.Calls[0].Layers.Select(l => l.Image.Format).ToArray(), "形式");
        ExpectMulti(o, 3, 2, "[PNG, テキスト, JPEG]");
        Check.Equal(2, o.LayersAfter, "足したあとの枚数");

        Check.Equal(1, Count(log, "import_fail"), "import_fail はすぐに書く");
        Check.Equal(0, Count(log, "import_ok"), "import_ok は最初の表示まで待つ");
        o.CompleteDisplay();
        Check.Equal(2, Count(log, "import_ok"), "import_ok の行数");
        Check.Equal(3, Count(log, "import_ok") + Count(log, "import_fail"), "行数の合計は N");
        Check.Equal("unsupported", log.Events.Single(e => e.Name == "import_fail").Get("reason"), "テキストの reason");
    }

    [Test]
    public void AC11_4_読めるファイル3個ではlayer_addedでimport_multiは出ない()
    {
        using var dir = new TempDir();
        var files = new[] { dir.Write("1.png", PngA), dir.Write("2.png", PngB), dir.Write("3.jpg", JpegB) };
        foreach (var target in new[] { new FakeTarget(), FakeTarget.WithDocument(2) })
        {
            var hadDoc = target.HasDocument;
            var (imp, t, _) = New(target);
            var o = imp.Drop(new MemoryImportData().WithFiles(files));
            Check.Equal(3, o.Added, "足せた枚数");
            Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
            Check.Equal(NoticeKeys.LayerAdded, o.Notice?.Key, $"layer.added(文書 {(hadDoc ? "あり" : "なし")})");
            Check.Equal(hadDoc ? 5 : 3, o.LayersAfter, "足したあとの枚数");
        }
    }

    [Test]
    public void AC11_4_glintとPNGの2個はPNGだけを足してimport_multi_2_1()
    {
        using var dir = new TempDir();
        var proj = dir.Write("work.glint", ZipHead);
        var png = dir.Write("pic.png", PngA);
        var (imp, t, log) = New();
        var o = imp.Drop(new MemoryImportData().WithFiles(proj, png));
        Check.Equal(ImportStatus.Completed, o.Status, ".glint は開かない");
        Check.Equal(null, o.ProjectPath, "開くパスは無い");
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.Equal("pic", t.Calls[0].Layers.Single().Name, "PNG だけ");
        ExpectMulti(o, 2, 1, "[.glint, PNG]");
        o.CompleteDisplay();
        Check.Equal("unsupported", log.Events.Single(e => e.Name == "import_fail").Get("reason"), ".glint は unsupported と数える");
        Check.Equal(1, Count(log, "import_ok"), "import_ok");

        // .glint が 1 個だけなら開く操作として呼び出し側へ返す(開く処理は対象外)
        var (imp2, t2, log2) = New();
        var o2 = imp2.Drop(new MemoryImportData().WithFiles(proj));
        Check.Equal(ImportStatus.ProjectRequested, o2.Status, "1 個の .glint");
        Check.Equal(proj, o2.ProjectPath, "パス");
        Check.Equal(0, t2.Calls.Count + log2.Events.Count, "足さずログも書かない");
    }

    [Test]
    public void AC11_4_URLだけのデータはimport_web_url()
    {
        var (imp, t, log) = New();
        var o = imp.Drop(new MemoryImportData().WithUrlOrText());
        Check.Equal(NoticeKeys.ImportWebUrl, o.Notice?.Key, "通知");
        Check.Equal(NoticeLevel.Warn, o.Notice!.Level, "種別");
        Check.Equal(0, t.Calls.Count, "足さない");
        Check.Equal(0, log.Events.Count, "ログなし");
    }

    [Test]
    public void AC11_4_PNGの画像データを持つドロップは取り込まれる()
    {
        var (imp, t, log) = New(FakeTarget.WithDocument());
        var o = imp.Drop(new MemoryImportData().With(ImportFormatNames.Png, PngB).WithUrlOrText());
        Check.Equal(1, o.Added, "足せた");
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.Equal(ImportVia.Drop, t.Calls[0].Via, "via");
        Check.Equal(NoticeKeys.LayerAdded, o.Notice?.Key, "layer.added");
        o.CompleteDisplay();
        Check.Equal("drop", log.Events.Single().Get("via"), "import_ok の via");
    }

    [Test]
    public void AC11_4_開くで複数を選ぶとドロップと同じ扱い()
    {
        using var dir = new TempDir();
        var (imp, t, _) = New();
        var o = imp.Open([dir.Write("x.png", PngA), dir.Write("y.txt", TextBytes), dir.Write("z.jpg", JpegB)]);
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.Equal(ImportVia.Open, t.Calls[0].Via, "via");
        Check.SequenceEqual(["x", "z"], t.Calls[0].Layers.Select(l => l.Name).ToArray(), "順");
        ExpectMulti(o, 3, 2, "開くの 3 個");

        var o2 = New().Importer.Open([dir.Write("work.glint", ZipHead)]);
        Check.Equal(ImportStatus.ProjectRequested, o2.Status, "1 個の .glint は開く操作");
    }

    // ---------------- (5) 起動引数とサンプル ----------------

    [Test]
    public void AC11_5_起動引数は最初の1個だけを取り込む()
    {
        using var dir = new TempDir();
        var a = dir.Write("startup.png", PngA);
        var b = dir.Write("second.png", PngB);
        var (imp, t, log) = New();
        var o = imp.StartupArgs([a, b]);
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.Equal(ImportVia.Arg, t.Calls[0].Via, "via");
        Check.Equal("startup", t.Calls[0].Layers.Single().Name, "最初の 1 個だけ");
        Check.Equal(1, o.Requested, "2 個目は数えない");
        Check.Equal(null, o.Notice, "新しい文書の 1 枚は通知なし");
        o.CompleteDisplay();
        Check.Equal("arg", log.Events.Single().Get("via"), "import_ok の via");

        var none = New().Importer.StartupArgs([]);
        Check.Equal(ImportStatus.NoInput, none.Status, "引数なしは何もしない");
        Check.Equal(null, none.Notice, "引数なしは通知なし");
        Check.Equal(ImportStatus.ProjectRequested, New().Importer.StartupArgs([dir.Write("a.glint", ZipHead), a]).Status, "最初が .glint なら開く操作");
    }

    [Test]
    public void AC11_5_起動引数の最初が非対応のファイルならimport_unsupported()
    {
        using var dir = new TempDir();
        var text = dir.Write("readme.png", TextBytes);
        var png = dir.Write("ok.png", PngA);
        var (imp, t, log) = New();
        var o = imp.StartupArgs([text, png]);
        Check.Equal(NoticeKeys.ImportUnsupported, o.Notice?.Key, "通知");
        Check.Equal(0, t.Calls.Count, "足さない(ウェルカム画面のまま)");
        Check.True(!t.HasDocument, "文書を作らない");
        Check.Equal("unsupported", log.Events.Single().Get("reason"), "ログ");

        var (imp2, t2, _) = New();
        Check.Equal(NoticeKeys.ImportReadError, imp2.StartupArgs([dir.PathOf("nothing.png")]).Notice?.Key, "開けないパスは read_error");
        Check.Equal(0, t2.Calls.Count, "足さない");
    }

    [Test]
    public void AC11_5_FR14_サンプルで試すは1024x768の画像を作る()
    {
        var (imp, t, log) = New();
        var o = imp.Sample();
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.Equal(ImportVia.Sample, t.Calls[0].Via, "via");
        var layer = t.Calls[0].Layers.Single();
        Check.Equal(LayerNames.Sample, layer.Name, "名前は「サンプル」");
        Check.Equal(SampleImage.Width, layer.Image.Width, "幅");
        Check.Equal(SampleImage.Height, layer.Image.Height, "高さ");
        Check.Equal(1024, layer.Image.Width, "幅 1024");
        Check.Equal(768, layer.Image.Height, "高さ 768");
        Check.Equal(ImageFormat.Png, layer.Image.Format, "PNG として持つ");
        Check.Equal(null, o.Notice, "新しい文書の 1 枚は通知なし");
        o.CompleteDisplay();
        Check.Equal(0, log.Events.Count, "サンプルは import_ok を書かない(via に値が無い)");

        // 中身: 不透明で、色が場所ごとに変わる(グラデーションと図形)
        var px = layer.Image.Pixels;
        var colors = new HashSet<int>();
        for (var i = 0; i < px.Length; i += 4)
        {
            Check.True(px[i + 3] == 255, "不透明");
            colors.Add(px[i] << 16 | px[i + 1] << 8 | px[i + 2]);
        }
        Check.True(colors.Count > 1000, $"色の種類が多い({colors.Count})");
        Check.True(Img.At(px, 1024, 0, 0) != Img.At(px, 1024, 1023, 767), "左上と右下の色が違う");

        // 決定的: もう一度作ると同じバイト列
        var again = New().Importer.Sample().AddedLayers[0].Image;
        Check.Equal(layer.Image.Sha256, again.Sha256, "同じ PNG のバイト列");
        Check.SequenceEqual(SampleImage.CreatePng(), SampleImage.CreatePng(), "CreatePng は毎回同じ");
    }

    // ---------------- 文書の上限(FR-57・E-12・E-13) ----------------

    [Test]
    public void FR57_E12_32枚を超えるファイルとその後ろは判定もデコードもせずに足さない()
    {
        using var dir = new TempDir();
        var files = new[] { dir.Write("a.png", PngA), dir.Write("b.png", PngB), dir.Write("c.txt", TextBytes) };
        var (imp, t, log) = New(FakeTarget.WithDocument(31));
        var o = imp.Drop(new MemoryImportData().WithFiles(files));
        Check.Equal(1, t.Calls.Count, "AddImages は 1 回");
        Check.Equal("a", t.Calls[0].Layers.Single().Name, "32 枚目まで");
        ExpectMulti(o, 3, 1, "31 枚の文書に 3 個");
        var fails = log.Events.Where(e => e.Name == "import_fail").Select(e => (string)e.Get("reason")!).ToArray();
        Check.SequenceEqual(["too_many_layers", "too_many_layers"], fails, "超えたファイルと後ろのファイル(テキストも判定しない)");

        var (imp2, t2, log2) = New(FakeTarget.WithDocument(32));
        var o2 = imp2.Open([files[0]]);
        Check.Equal(NoticeKeys.LayerFull, o2.Notice?.Key, "32 枚の文書に 1 個は layer.full");
        Check.Equal(0, t2.Calls.Count, "足さない");
        Check.Equal("too_many_layers", log2.Events.Single().Get("reason"), "reason");

        // すでに 32 枚の文書では、先頭が画像でないファイルも中身を判定せずに枚数の上限と数える(途中で達したときと同じ扱い)
        var (imp3, t3, log3) = New(FakeTarget.WithDocument(32));
        var o3 = imp3.Drop(new MemoryImportData().WithFiles(files[2], files[0]));
        Check.Equal(0, t3.Calls.Count, "32 枚の文書にテキストと PNG: 足さない");
        Check.Equal(NoticeKeys.LayerFull, o3.Notice?.Key, "32 枚の文書にテキストと PNG: layer.full");
        var fails3 = log3.Events.Where(e => e.Name == "import_fail").Select(e => (string)e.Get("reason")!).ToArray();
        Check.SequenceEqual(["too_many_layers", "too_many_layers"], fails3, "テキストも unsupported でなく too_many_layers");
    }

    [Test]
    public void FR57_E13_合計の上限はバイト数と画素数で判定し同じ画像は1回だけ数える()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.png", PngA);
        long bytes = PngA.Length, pixels = 4 * 3;

        // ちょうど上限なら通る、1 超えると layer.budget
        var (imp, t, _) = New(new FakeTarget { HasDocument = true, LayerCount = 1, SourceBytesTotal = 300_000_000 - bytes });
        Check.Equal(1, imp.Open([a]).Added, "バイト数の合計が 300,000,000 ちょうどは通る");
        (imp, t, var log) = New(new FakeTarget { HasDocument = true, LayerCount = 1, SourceBytesTotal = 300_000_000 - bytes + 1 });
        var o = imp.Open([a]);
        Check.Equal(NoticeKeys.LayerBudget, o.Notice?.Key, "バイト数で超える");
        Check.Equal(0, t.Calls.Count, "足さない");
        Check.Equal("over_budget", log.Events.Single().Get("reason"), "reason");

        (imp, _, _) = New(new FakeTarget { HasDocument = true, LayerCount = 1, SourcePixelsTotal = 100_000_000 - pixels });
        Check.Equal(1, imp.Open([a]).Added, "画素数の合計が 100,000,000 ちょうどは通る");
        (imp, _, _) = New(new FakeTarget { HasDocument = true, LayerCount = 1, SourcePixelsTotal = 100_000_000 - pixels + 1 });
        Check.Equal(NoticeKeys.LayerBudget, imp.Open([a]).Notice?.Key, "画素数で超える");

        // すでに文書にある画像は合計が増えないので足せる
        var known = new FakeTarget { HasDocument = true, LayerCount = 1, SourceBytesTotal = 300_000_000 };
        known.AddKnownSource(ImportedImage.ComputeSha256(PngA));
        (imp, _, _) = New(known);
        Check.Equal(1, imp.Open([a]).Added, "同じ SHA-256 の画像は数えない");

        // 同じ取り込みの中の同じ画像も 1 回だけ数え、デコードした画像を共有する
        var copy = dir.Write("a-copy.png", PngA);
        (imp, t, _) = New(new FakeTarget { HasDocument = true, LayerCount = 1, SourceBytesTotal = 300_000_000 - bytes });
        o = imp.Open([a, copy]);
        Check.Equal(2, o.Added, "同じ画像 2 個は 1 回だけ数える");
        Check.True(ReferenceEquals(t.Calls[0].Layers[0].Image, t.Calls[0].Layers[1].Image), "同じ ImportedImage を共有する");

        // 超えたものから後ろは足さない(後ろが小さくても)
        var big = dir.Write("big.png", SkiaBytes.Png(64, 64, Img.Pattern(64, 64, alpha: true)));
        (imp, t, log) = New(new FakeTarget { HasDocument = true, LayerCount = 1, SourcePixelsTotal = 100_000_000 - 1000 });
        o = imp.Open([big, a]);
        Check.Equal(0, t.Calls.Count, "足さない");
        Check.SequenceEqual(["over_budget", "over_budget"], log.Events.Select(e => (string)e.Get("reason")!).ToArray(), "超えたものと後ろ");
        Check.Equal(NoticeKeys.LayerBudget, o.Notice?.Key, "M=0 は最後の理由");
    }

    // ---------------- 門(E-20)・ログ(INV-15)・縮小(E-11) ----------------

    [Test]
    public void E20_取り込みの処理中の別の取り込みは無視される()
    {
        var (imp, t, log) = New();
        var first = imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, PngA));
        Check.True(first.AwaitingDisplay && imp.Gate.IsBusy, "最初の表示までは処理中");
        var ignored = new[]
        {
            imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, PngB)),
            imp.Drop(new MemoryImportData().With(ImportFormatNames.Png, PngB)),
            imp.Open(["x.png"]),
            imp.StartupArgs(["x.png"]),
            imp.Sample(),
        };
        foreach (var o in ignored)
        {
            Check.Equal(ImportStatus.Ignored, o.Status, o.Via + " は無視");
            Check.Equal(null, o.Notice, o.Via + " は通知なし");
        }
        Check.Equal(1, t.Calls.Count, "足したのは最初の 1 回だけ");
        first.CompleteDisplay();
        Check.True(!imp.Gate.IsBusy, "表示が終われば門を出る");
        first.CompleteDisplay(); // 2 回目は何もしない
        Check.Equal(1, Count(log, "import_ok"), "import_ok は 1 回だけ");
        Check.Equal(1, imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, PngB)).Added, "そのあとは取り込める");

        // 足せなかった取り込みはすぐに門を出る
        var (imp2, _, _) = New();
        imp2.Paste(new MemoryImportData().With(ImportFormatNames.Png, TruncatedPng));
        Check.True(!imp2.Gate.IsBusy, "足せなければすぐに出る");

        // ダイアログなどが門を持っている間も無視
        var (imp3, t3, _) = New();
        using (imp3.Gate.TryEnter())
            Check.Equal(ImportStatus.Ignored, imp3.Paste(new MemoryImportData().With(ImportFormatNames.Png, PngA)).Status, "門が使われている");
        Check.Equal(0, t3.Calls.Count, "足さない");
    }

    [Test]
    public void INV15_取り込みのログにパスとファイル名を書かず項目は表のとおり()
    {
        using var dir = new TempDir();
        var names = new[] { "秘密の写真.png", "private-name.jpg", "notes.png", "missing.png" };
        var paths = new[] { dir.Write(names[0], PngA), dir.Write(names[1], JpegB), dir.Write(names[2], TextBytes), dir.PathOf(names[3]) };
        var (imp, _, log) = New();
        var o = imp.Open(paths);
        o.CompleteDisplay();
        Check.Equal(4, log.Events.Count, "N 行");
        foreach (var e in log.Events)
        {
            foreach (var kv in e.Fields)
            {
                var s = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                Check.True(!s.Contains(dir.Path, StringComparison.OrdinalIgnoreCase), $"{e.Name}.{kv.Key} にパスが無い");
                foreach (var n in names)
                    Check.True(!s.Contains(Path.GetFileNameWithoutExtension(n), StringComparison.OrdinalIgnoreCase), $"{e.Name}.{kv.Key} にファイル名が無い");
            }
        }
        var ok = log.Events.Where(e => e.Name == "import_ok").ToArray();
        Check.Equal(2, ok.Length, "import_ok");
        foreach (var e in ok)
        {
            Check.SequenceEqual(["ms", "w", "h", "fmt", "bytes", "via", "layers"], e.Fields.Select(f => f.Key).ToArray(), "import_ok の項目");
            Check.Equal(2, e.Get("layers"), "layers は足し終えたあとの数");
            Check.Equal("open", e.Get("via"), "via");
            Check.True(e.Get("ms") is long, "ms は整数");
        }
        Check.Equal(ok[0].Get("ms"), ok[1].Get("ms"), "ms はどの行も同じ");
        Check.SequenceEqual(["png", "jpeg"], ok.Select(e => (string)e.Get("fmt")!).ToArray(), "fmt");
        Check.SequenceEqual([(object)PngA.LongLength, JpegB.LongLength], ok.Select(e => e.Get("bytes")!).ToArray(), "bytes");
        var fail = log.Events.Where(e => e.Name == "import_fail").ToArray();
        Check.SequenceEqual(["unsupported", "read_error"], fail.Select(e => (string)e.Get("reason")!).ToArray(), "import_fail の reason");
        foreach (var e in fail) Check.SequenceEqual(["reason"], e.Fields.Select(f => f.Key).ToArray(), "import_fail の項目");
    }

    [Test]
    public void E11_GPUの上限を超える辺は1_2の箱形縮小を繰り返す()
    {
        // 7×5 を最大寸法 4 に: 7×5 → 4×3(奇数の辺は端を複製)
        const int w = 7, h = 5;
        var px = Img.Pattern(w, h, alpha: true);
        var r = GpuFit.Fit(px, w, h, 4, out var ow, out var oh, out var shrunk);
        Check.True(shrunk, "縮めた");
        Check.Equal(4, ow, "幅 ceil(7÷2)");
        Check.Equal(3, oh, "高さ ceil(5÷2)");
        // 右下の画素は元の (6,4) だけを 4 回数えた平均 = 元の画素(複製した端)
        var (er, eg, eb, ea) = Img.At(px, w, 6, 4);
        var (rr, rg, rb, ra) = Img.At(r, ow, 3, 2);
        Check.Equal((er, eg, eb, ea), (rr, rg, rb, ra), "端の複製");
        // 左上の画素は 2×2 の線形・プリマルチプライの平均
        double Lin(byte v) => GlintLoop.Core.Numerics.Srgb.Lin8(v);
        for (var c = 0; c < 3; c++)
        {
            double num = 0, den = 0;
            foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                var i = (y * w + x) * 4;
                num += Lin(px[i + c]) * px[i + 3];
                den += px[i + 3];
            }
            var expected = (int)Math.Floor(255 * GlintLoop.Core.Numerics.Srgb.Enc(num / den) + 0.5);
            Check.Near(expected, r[c], 0, $"左上の成分 {c}");
        }
        Check.Equal((byte)Math.Floor((px[3] + px[7] + px[w * 4 + 3] + px[w * 4 + 7]) / 4.0 + 0.5), r[3], "左上の α");

        // 透明な 4 画素は (0,0,0,0)
        var clear = Img.Solid(2, 2, 255, 255, 255, 0);
        Check.SequenceEqual([0, 0, 0, 0], GpuFit.Halve(clear, 2, 2, out _, out _).Select(b => (int)b).ToArray(), "透明");
        // 収まっていれば同じ配列
        Check.True(ReferenceEquals(px, GpuFit.Fit(px, w, h, 7, out _, out _, out var s2)) && !s2, "収まっていれば縮めない");
        // 2 回の縮小: 9×3 を最大 3 に → 5×2 → 3×1
        GpuFit.Fit(Img.Pattern(9, 3, false), 9, 3, 3, out var w2, out var h2, out _);
        Check.Equal((3, 1), (w2, h2), "2 回の縮小");

        // 取り込みで縮めたら import.gpu_shrunk(新しい文書の 1 枚でも出す)
        var imp = new ImageImporter(new FakeTarget(), new MemoryLogSink(), maxTextureSide: 4);
        var o = imp.Paste(new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(w, h, px)));
        var img = o.AddedLayers[0].Image;
        Check.True(img.GpuShrunk, "縮めた印");
        Check.Equal((4, 3), (img.Width, img.Height), "縮めた大きさ");
        Check.Equal((long)w * h, img.SourcePixels, "合計に数える画素数は縮める前");
        Check.Equal(NoticeKeys.ImportGpuShrunk, o.Notice?.Key, "import.gpu_shrunk");
    }

    [Test]
    public void FR42_レイヤーの名前は拡張子を除いた40文字で空なら画像()
    {
        Check.Equal("photo", LayerNames.FromPath(@"C:\x\photo.jpeg"), "拡張子を除く");
        Check.Equal("archive.tar", LayerNames.FromPath(@"C:\x\archive.tar.png"), "最後の拡張子だけを除く");
        Check.Equal(LayerNames.Fallback, LayerNames.FromPath(@"C:\x\.png"), "空なら「画像」");
        var longName = new string('あ', 45);
        Check.Equal(new string('あ', 40), LayerNames.FromPath(@"C:\x\" + longName + ".png"), "先頭の 40 文字");
        Check.Equal(40, LayerNames.Truncate(new string('a', 41)).Length, "41 文字は 40 文字に");
        Check.Equal("画像", LayerNames.Fallback, "既定の名前");
        Check.Equal("貼り付け", LayerNames.Paste, "クリップボードの名前");
        Check.Equal("サンプル", LayerNames.Sample, "サンプルの名前");
    }
}
