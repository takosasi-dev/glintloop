// AC-55 の (1)(2)(4)(5)(6)(7): 取り込みの失敗(E-1・E-2・E-4〜E-9)。どの失敗も、ウェルカム画面と 2 枚の文書で行い、通知・ログ・文書・履歴・選択が変わらないことを確かめる。
// 各項目の場面を CasesN に分け、WARP のアプリでも同じ場面を流してキャンバスの画素が変わらないことを確かめる(AC55_1_7)。(3) は EdgeAc55UrlDropTests。
// AC-10・AC-11 が同じ通知の一部を偽の文書で調べている。ここは画面の配線(ImportCoordinator・通知バー)と本物の文書で調べる。
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GlintLoop.App.Platform;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Notices;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc55RejectTests
{
    private const int CantOpen = unchecked((int)0x800401D0);

    /// <summary>ウェルカム画面で cases を行い、続けて 2 枚の文書で同じ cases を行う。</summary>
    private static void OnWelcomeAndDocument(Action<EdgeRig, string> cases)
    {
        using var r = EdgeRig.Start();
        cases(r, "ウェルカム画面");
        r.LoadTwoLayers();
        cases(r, "2 枚の文書");
    }

    [Test(Slow = true)]
    public void AC55_1_7_失敗の全部の場面でキャンバスの画素が変わらない()
    {
        using var r = EdgeRig.Start(gpu: true);
        r.LoadTwoLayers();
        Check.True(r.State().Pixels is { Length: > 0 }, "キャンバスの画素を読めない");
        Cases1(r, "WARP");
        Cases2(r, "WARP");
        Cases4(r, "WARP");
        Cases5(r, "WARP");
        Cases6(r, "WARP");
        Cases7(r, "WARP");
    }

    // ================================================================ (1) 貼り付けで読めない(E-1)

    private static void Cases1(EdgeRig r, string where)
    {
        var txt = r.Dir.Write("note.txt", "hello"u8.ToArray());
        var glint = r.Dir.Write("work.glint", EdgeBytes.ZipHead);
        foreach (var (what, data) in new (string, DataObject)[]
                 {
                     ("空", new DataObject()),
                     ("文字列だけ", new DataObject(DataFormats.UnicodeText, "hello")),
                     ("URL の文字列だけ", new DataObject(DataFormats.UnicodeText, "https://example.com/a.png")),
                     (".txt の FileDrop", new DataObject(DataFormats.FileDrop, new[] { txt })),
                     (".glint の FileDrop", new DataObject(DataFormats.FileDrop, new[] { glint })),
                 })
        {
            r.Reject($"{where}: {what}", () => r.PasteObject(data), NoticeKeys.PasteEmpty, NoticeLevel.Warn, []);
        }
    }

    [Test]
    public void AC55_1_E1_E3_貼り付けで読めないものはpaste_emptyでログを書かない() => OnWelcomeAndDocument(Cases1);

    // ================================================================ (2) クリップボードを開けない(E-2)

    private static void Cases2(EdgeRig r, string where)
    {
        var calls = 0;
        // 偽のクリップボードが COMException を投げ続ける。製品のラッパーの変換(ImportDataUnavailableException)を通す
        r.App.Clipboard.ReadThrough = new WpfClipboard(() =>
        {
            calls++;
            throw new COMException("偽のクリップボードを開けない", CantOpen);
        });
        try
        {
            var hrBefore = r.LogCount("clipboard_failed");
            r.Reject(where + ": 開けない", () => r.Paste(new MemoryImportData().With(ImportFormatNames.Png, EdgeBytes.Solid(1, 2, 3))),
                NoticeKeys.ClipFailed, NoticeLevel.Error, ["clipboard_failed"]);
            Check.Equal(1, calls, where + ": 製品のラッパーは 1 回だけ読む(独自の再試行を持たない)");
            Check.Equal(hrBefore + 1, r.LogCount("clipboard_failed"), where + ": clipboard_failed");
            var line = r.LogLines().Last(l => l.GetProperty("event").GetString() == "clipboard_failed");
            Check.Equal(2147746256u, line.GetProperty("hr").GetUInt32(), where + ": hr");
        }
        finally
        {
            r.App.Clipboard.ReadThrough = null;
        }
    }

    [Test]
    public void AC55_2_E2_開けないクリップボードはclip_failedでhrが2147746256() => OnWelcomeAndDocument(Cases2);

    // ================================================================ (4) 非対応の形式(E-4)

    private static byte[] Ftyp(string brand)
    {
        var b = new byte[32];
        b[3] = 0x18;
        System.Text.Encoding.ASCII.GetBytes("ftyp" + brand).CopyTo(b, 4);
        return b;
    }

    private static readonly (string Name, byte[] Bytes)[] Unsupported =
    [
        ("x.heic", Ftyp("heic")),
        ("x.avif", Ftyp("avif")),
        ("le.tif", [0x49, 0x49, 0x2A, 0x00, .. new byte[28]]),
        ("be.tif", [0x4D, 0x4D, 0x00, 0x2A, .. new byte[28]]),
        ("x.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()),
        ("x.psd", [0x38, 0x42, 0x50, 0x53, .. new byte[28]]),
        ("x.mp4", Ftyp("mp42")),
        ("hello.png", "hello"u8.ToArray()),
        ("empty.png", []),
    ];

    private static void Cases4(EdgeRig r, string where)
    {
        foreach (var (name, bytes) in Unsupported)
        {
            var path = r.Dir.Write(name, bytes);
            string[] log = ["import_fail:unsupported"];
            r.Reject($"{where}: ドロップの {name}", () => r.DropFiles(path), NoticeKeys.ImportUnsupported, NoticeLevel.Error, log);
            r.Reject($"{where}: 開くの {name}", () => r.Open(path), NoticeKeys.ImportUnsupported, NoticeLevel.Error, log);
            r.Reject($"{where}: 起動引数の {name}", () => r.Arg(path), NoticeKeys.ImportUnsupported, NoticeLevel.Error, log);
            // 貼り付けは対象外で E-1 になる
            r.Reject($"{where}: 貼り付けの {name}", () => r.PasteFiles(path), NoticeKeys.PasteEmpty, NoticeLevel.Warn, []);
        }
    }

    [Test]
    public void AC55_4_E4_非対応の形式は3つの経路でimport_unsupported() => OnWelcomeAndDocument(Cases4);

    [Test]
    public void AC55_4_PNGの中身で拡張子がtxtのファイルは3つの経路で取り込める()
    {
        using var r = EdgeRig.Start();
        var path = r.Dir.Write("really-png.txt", EdgeBytes.Solid(9, 8, 7));
        Check.Equal(1, r.DropFiles(path)?.Added, "ドロップ");
        Check.Equal(1, r.Open(path)?.Added, "開く");
        Check.Equal(1, r.Arg(path)?.Added, "起動引数");
        Check.SequenceEqual(["really-png", "really-png", "really-png"], r.LayerNames, "レイヤーの名前");
    }

    // ================================================================ (5) 読めない(E-6)

    private static void Cases5(EdgeRig r, string where)
    {
        var fs = r.App.FileSystem;
        var path = r.Dir.Write("locked.png", EdgeBytes.Solid(4, 5, 6));
        string[] log = ["import_fail:read_error"];
        foreach (var (what, make) in new (string, Func<Exception>)[]
                 {
                     ("共有違反", () => new IOException("共有違反", unchecked((int)0x80070020))),
                     ("権限が無い", () => new UnauthorizedAccessException("権限が無い")),
                     ("消えた", () => new FileNotFoundException("消えた", path)),
                 })
        {
            fs.FailOn(FileOps.OpenRead, p => p == path, make);
            try
            {
                r.Reject($"{where}: {what}(ドロップ)", () => r.DropFiles(path), NoticeKeys.ImportReadError, NoticeLevel.Error, log);
                r.Reject($"{where}: {what}(開く)", () => r.Open(path), NoticeKeys.ImportReadError, NoticeLevel.Error, log);
                r.Reject($"{where}: {what}(起動引数)", () => r.Arg(path), NoticeKeys.ImportReadError, NoticeLevel.Error, log);
            }
            finally
            {
                fs.ClearFailures();
            }
        }

        // 画素の確保のメモリ不足(デコードの呼び出しの中で起こす)
        r.OnDecode = () => throw new OutOfMemoryException("偽のメモリ不足");
        try
        {
            r.Reject(where + ": メモリ不足(開く)", () => r.Open(path), NoticeKeys.ImportReadError, NoticeLevel.Error, log, decodes: 1);
            r.Reject(where + ": メモリ不足(ドロップ)", () => r.DropFiles(path), NoticeKeys.ImportReadError, NoticeLevel.Error, log, decodes: 1);
        }
        finally
        {
            r.OnDecode = null;
        }
    }

    [Test]
    public void AC55_5_E6_開けないファイルとメモリ不足はimport_read_error() => OnWelcomeAndDocument(Cases5);

    // ================================================================ (6) 壊れている(E-5)

    private static byte[] IdatFilled()
    {
        var png = SkiaBytes.Png(64, 64, Img.Pattern(64, 64, alpha: true));
        var o = png.ToArray();
        var p = 8;
        while (true)
        {
            var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(o.AsSpan(p));
            if (System.Text.Encoding.ASCII.GetString(o, p + 4, 4) == "IDAT")
            {
                Check.True(len >= 16, "IDAT が短すぎる");
                o.AsSpan(p + 8 + len / 4, len / 2).Fill(0xFF);
                return o;
            }
            p += 12 + len;
        }
    }

    private static void Cases6(EdgeRig r, string where)
    {
        var jpeg = SkiaBytes.Jpeg(64, 64, Img.Pattern(64, 64, alpha: false));
        var webp = SkiaBytes.WebpLossless(64, 64, Img.Pattern(64, 64, alpha: true));
        string[] log = ["import_fail:decode_error"];
        foreach (var (name, bytes, decodes) in new (string, byte[], int)[]
                 {
                     ("cut.png", EdgeBytes.CutPng(), 1),
                     ("filled.png", IdatFilled(), 1),
                     ("cut.jpg", jpeg[..(jpeg.Length / 2)], 1),
                     ("head.webp", webp[..20], 0), // 20 バイトではヘッダーも読めず、デコードまで進まない
                 })
        {
            var path = r.Dir.Write(name, bytes);
            r.Reject($"{where}: {name}", () => r.Open(path), NoticeKeys.ImportDecodeError, NoticeLevel.Error, log, decodes);
        }
        // 貼り付けで切れた PNG の形式だけ(paste.empty にならない)
        r.Reject(where + ": 貼り付けの切れた PNG", () => r.PastePng(EdgeBytes.CutPng()), NoticeKeys.ImportDecodeError, NoticeLevel.Error, log, decodes: 1);
    }

    [Test]
    public void AC55_6_E5_壊れたファイルと切れたPNGの貼り付けはimport_decode_error() => OnWelcomeAndDocument(Cases6);

    // ================================================================ (7) 大きさの上限(E-7・E-8・E-9)。境界の判定そのものは AC10_4 が調べる

    private static void Cases7(EdgeRig r, string where)
    {
        var fs = r.App.FileSystem;
        var big = r.Dir.PathOf("fake-big.png");
        var bigWide = r.Dir.PathOf("fake-big-wide.png");
        fs.OpenReadOverride = p =>
            p == big ? new HeadOnlyStream(EdgeBytes.PngHeader(16, 16), 100_000_001) :
            p == bigWide ? new HeadOnlyStream(EdgeBytes.PngHeader(16385, 16385), 100_000_001) : null;
        try
        {
            (string, string)[] size = [("size", "100.0")];
            string[] tooLarge = ["import_fail:too_large_file"];
            r.Reject(where + ": Length が 100,000,001", () => r.Open(big), NoticeKeys.ImportTooLargeFile, NoticeLevel.Error, tooLarge, 0, size);
            r.Reject(where + ": Length が 100,000,001(ドロップ)", () => r.DropFiles(big), NoticeKeys.ImportTooLargeFile, NoticeLevel.Error, tooLarge, 0, size);
            r.Reject(where + ": 大きさと辺の両方", () => r.Open(bigWide), NoticeKeys.ImportTooLargeFile, NoticeLevel.Error, tooLarge, 0, size);
        }
        finally
        {
            fs.OpenReadOverride = null;
        }

        // クリップボードの DIB(IImportData はバイト列を渡すので、偽のストリームでなく 100,000,001 バイトの配列で調べる)
        var dib = Dib.Build(40, 2, 2, 24, 0, (_, _) => 0x102030u);
        var hugeDib = new byte[100_000_001];
        dib.CopyTo(hugeDib, 0);
        r.Reject(where + ": 100,000,001 バイトの DIB", () => r.Paste(new MemoryImportData().With(ImportFormatNames.Dib, hugeDib)),
            NoticeKeys.ImportTooLargeFile, NoticeLevel.Error, ["import_fail:too_large_file"], 0, ("size", "100.0"));
    }

    [Test]
    public void AC55_7_E7_100000001バイトはデコードせずtoo_large_fileだけを出す() => OnWelcomeAndDocument(Cases7);

    // 署名と IHDR だけの 33 バイトの PNG(AC-55 の (7) の文のとおり、ヘッダーから辺と画素数を判定する)
    private static void Cases7Header(EdgeRig r, string where)
    {
        var wide = r.Dir.Write("wide.png", EdgeBytes.PngHeader(16385, 1));
        r.Reject(where + ": 16385×1", () => r.Open(wide), NoticeKeys.ImportTooLargeSide, NoticeLevel.Error, ["import_fail:too_large_side"], 0, ("w", "16385"), ("h", "1"));
        var many = r.Dir.Write("many.png", EdgeBytes.PngHeader(8193, 8193));
        r.Reject(where + ": 8193×8193", () => r.Open(many), NoticeKeys.ImportTooManyPixels, NoticeLevel.Error, ["import_fail:too_many_pixels"], 0, ("mp", "67.1"));
        var both = r.Dir.Write("both.png", EdgeBytes.PngHeader(16385, 16385));
        r.Reject(where + ": 小さいファイルで 16385×16385", () => r.Open(both), NoticeKeys.ImportTooLargeSide, NoticeLevel.Error, ["import_fail:too_large_side"], 0, ("w", "16385"), ("h", "16385"));
    }

    [Test]
    public void AC55_7_E8_E9_33バイトのPNGはヘッダーで判定しデコードせずに最初の1つだけを出す() => OnWelcomeAndDocument(Cases7Header);

    // 上と同じ大きさで IDAT が 1 個ある PNG(切り分け: 33 バイトの場面が落ちるときに、原因が IDAT の有無だけかどうかを見る)
    private static byte[] HeaderWithIdat(int w, int h)
    {
        using var o = new MemoryStream();
        o.Write(EdgeBytes.PngHeader(w, h));
        PngBytes.WriteChunk(o, "IDAT", new byte[16]);
        PngBytes.WriteChunk(o, "IEND", []);
        return o.ToArray();
    }

    [Test]
    public void AC55_7_E8_E9_IDATのあるPNGもデコードせずに辺と画素数で拒否する()
    {
        using var r = EdgeRig.Start();
        r.Reject("16385×1", () => r.Open(r.Dir.Write("wide.png", HeaderWithIdat(16385, 1))), NoticeKeys.ImportTooLargeSide, NoticeLevel.Error, ["import_fail:too_large_side"], 0, ("w", "16385"), ("h", "1"));
        r.Reject("8193×8193", () => r.Open(r.Dir.Write("many.png", HeaderWithIdat(8193, 8193))), NoticeKeys.ImportTooManyPixels, NoticeLevel.Error, ["import_fail:too_many_pixels"], 0, ("mp", "67.1"));
        r.Reject("16385×16385", () => r.Open(r.Dir.Write("both.png", HeaderWithIdat(16385, 16385))), NoticeKeys.ImportTooLargeSide, NoticeLevel.Error, ["import_fail:too_large_side"], 0, ("w", "16385"), ("h", "16385"));
    }

    [Test]
    public void AC55_7_16384x1のPNGは取り込める()
    {
        using var r = EdgeRig.Start();
        var path = r.Dir.Write("long.png", SkiaBytes.Png(16384, 1, Img.Solid(16384, 1, 10, 20, 30)));
        var o = r.Open(path)!;
        Check.Equal(1, o.Added, "足せた枚数");
        Check.Equal(16384, o.AddedLayers[0].Image.Width, "幅");
        Check.Equal(1, r.Decodes, "デコードの呼び出し");
        Check.Equal(0, r.ImportLog().Count(t => t.StartsWith("import_fail", StringComparison.Ordinal)), "import_fail が無い");
    }

    // 偽のストリームの確かめ(読みすぎたら例外になる。これが通らないと (7) の「読まない」を調べられない)
    [Test]
    public void AC55_7_偽のストリームは先頭より後ろを読むと例外を投げる()
    {
        using var s = new HeadOnlyStream(EdgeBytes.PngHeader(1, 1), 100_000_001);
        var buf = new byte[33];
        s.ReadExactly(buf);
        Check.Equal(100_000_001L, s.Length, "Length");
        Check.Throws<InvalidOperationException>(() => s.ReadExactly(new byte[1]), "34 バイト目");
    }
}
