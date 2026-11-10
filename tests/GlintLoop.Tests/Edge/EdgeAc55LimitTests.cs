// AC-55 の (15)(16): 文書の上限(E-12・E-13・FR-57)。枚数の上限は 32 枚の文書を EditorSession へ直接読み込ませて調べ、合計の上限は判定の値を差し替えて調べる(本物の値での境界は AC-28 の (9) と FR57_E13 が調べる)。
// デコードの呼び出しは ImageImporter.BeforeDecode で数える。
using System.Windows.Input;
using GlintLoop.App.Layers;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc55LimitTests
{
    /// <summary>4×4 の PNG(色がすべて違う)で n 枚の文書を作り、セッションへ直接読み込ませる(履歴は空)。</summary>
    private static void LoadLayers(EdgeRig r, int n)
    {
        var images = new ImportedImage[n];
        var layers = new Layer[n];
        for (var i = 0; i < n; i++)
        {
            var png = SkiaBytes.Png(4, 4, Img.Solid(4, 4, (byte)(i * 7 + 1), (byte)(250 - i * 5), (byte)(i * 3 + 100)));
            var sha = ImportedImage.ComputeSha256(png);
            images[i] = ImageImporter.DecodeSource(png, ImageFormat.Png, sha, 16, ImageImporter.DefaultMaxTextureSide);
            layers[i] = new Layer("L" + i, LayerKind.Image, sha, null, null, true, 100, BlendMode.Normal, Placement.Identity, 100, Array.Empty<StackEntry>());
        }
        var doc = new SessionDocument(7, 0, TimeSettings.Default, Target.OfLayer(n - 1), Array.Empty<AssetEntry>(), layers, GlobalStack.Empty, OutputSettings.Default);
        r.App.Ui(() => r.App.Shell.ImportTarget.OpenDocument(doc, images));
        UiHost.Idle();
        Check.Equal(n, r.LayerNames.Length, "読み込んだ枚数");
    }

    private static byte[] Png(byte v) => EdgeBytes.Solid(v, (byte)(255 - v), 90, 4, 4);

    /// <summary>そのパスを開いた回数(「判定もされない」は、ファイルを開かないことで確かめる)。</summary>
    private static int Opens(EdgeRig r, string path) => r.App.FileSystem.Calls.Count(c => c.Operation == FileOps.OpenRead && c.Path == path);

    // ================================================================ (15) 枚数の上限(E-12)

    [Test]
    public void AC55_15_E12_32枚の文書には3つの経路で足せずデコードもしない()
    {
        using var r = EdgeRig.Start();
        LoadLayers(r, 32);
        var extra = Png(200);
        var path = r.Dir.Write("extra.png", extra);
        string[] log = ["import_fail:too_many_layers"];
        r.Reject("貼り付け", () => r.PastePng(extra), NoticeKeys.LayerFull, NoticeLevel.Warn, log);
        r.Reject("ドロップ", () => r.DropFiles(path), NoticeKeys.LayerFull, NoticeLevel.Warn, log);
        r.Reject("開く", () => r.Open(path), NoticeKeys.LayerFull, NoticeLevel.Warn, log);

        // 空のレイヤーと複製(ログは書かない)
        r.Reject("Ctrl+Shift+N", () => r.Key(Key.N, ModifierKeys.Control | ModifierKeys.Shift), NoticeKeys.LayerFull, NoticeLevel.Warn, []);
        var list = EditingScreenKit.M<LayersModule>(r.App).List;
        r.App.Ui(() => list.FocusSelected());
        r.Reject("Ctrl+D", () => r.Key(Key.D, ModifierKeys.Control), NoticeKeys.LayerFull, NoticeLevel.Warn, []);

        // 1 枚を削除して 31 枚にすると、貼り付けで 32 枚目が足される
        Check.True(r.App.Ui(() => r.Session.DeleteLayer(0).Changed), "削除できない");
        Check.Equal(31, r.LayerNames.Length, "削除のあと");
        Check.Equal(1, r.PastePng(extra)?.Added, "31 枚の文書の貼り付け");
        Check.Equal(32, r.LayerNames.Length, "32 枚目が足される");
    }

    [Test]
    public void AC55_15_E12_複数のファイルは上限のところで止まる()
    {
        using var r = EdgeRig.Start();
        var a = r.Dir.Write("a.png", Png(10));
        var b = r.Dir.Write("b.png", Png(20));
        var c = r.Dir.Write("c.png", Png(30));
        var x = r.Dir.Write("x.png", EdgeBytes.CutPng());

        // 30 枚に [A, B, C]: A と B で 32 枚。C はデコードされない。layer.full は出ない
        LoadLayers(r, 30);
        r.ClearNotice();
        var log = r.ImportLog().Length;
        var decodes = r.Decodes;
        var opensC = Opens(r, c);
        Check.Equal(2, r.DropFiles(a, b, c)?.Added, "30 枚に 3 個");
        Check.Equal(opensC, Opens(r, c), "30 枚に 3 個: C を開かない");
        Check.Equal(32, r.LayerNames.Length, "32 枚になる");
        Check.Equal(2, r.Decodes - decodes, "デコードは A と B の 2 回");
        r.ExpectMulti(3, 2, "30 枚に 3 個");
        r.ExpectLog(log, ["import_ok", "import_ok", "import_fail:too_many_layers"], "30 枚に 3 個");

        // 32 枚に [A, B]: 何も足されず layer.full
        LoadLayers(r, 32);
        var (opensA, opensB) = (Opens(r, a), Opens(r, b));
        r.Reject("32 枚に 2 個", () => r.DropFiles(a, b), NoticeKeys.LayerFull, NoticeLevel.Warn, ["import_fail:too_many_layers", "import_fail:too_many_layers"]);
        Check.Equal((opensA, opensB), (Opens(r, a), Opens(r, b)), "32 枚に 2 個: どちらも開かない");

        // 31 枚に [X, A, B]: A だけで 32 枚。B は判定もデコードもされない
        LoadLayers(r, 31);
        r.ClearNotice();
        log = r.ImportLog().Length;
        decodes = r.Decodes;
        opensB = Opens(r, b);
        Check.Equal(1, r.DropFiles(x, a, b)?.Added, "31 枚に 3 個");
        Check.Equal(opensB, Opens(r, b), "31 枚に 3 個: B を開かない");
        Check.Equal(32, r.LayerNames.Length, "32 枚になる");
        Check.Equal("a", r.LayerNames[^1], "足したのは A");
        Check.Equal(2, r.Decodes - decodes, "デコードは X と A の 2 回");
        r.ExpectMulti(3, 1, "31 枚に 3 個");
        r.ExpectLog(log, ["import_ok", "import_fail:decode_error", "import_fail:too_many_layers"], "31 枚に 3 個");
    }

    // ================================================================ (16) 合計の上限(E-13)

    private sealed record Budget(string A, string B, string C, string A2, long ABytes, long BBytes);

    // A・B・C は 40×25 の PNG(内容がすべて違う)、A2 は A と同じ内容で別の名前
    private static Budget MakeBudget(EdgeRig r)
    {
        byte[] Make(int seed)
        {
            var px = new byte[40 * 25 * 4];
            for (var i = 0; i < px.Length; i++) px[i] = (byte)(i % 4 == 3 ? 255 : GlintLoop.Core.Numerics.Hash.H((uint)seed, (uint)i));
            return SkiaBytes.Png(40, 25, px);
        }
        var (a, b, c) = (Make(1), Make(2), Make(3));
        Check.True(a.Length != 0 && !a.SequenceEqual(b) && !b.SequenceEqual(c), "内容が違う");
        return new Budget(r.Dir.Write("a.png", a), r.Dir.Write("b.png", b), r.Dir.Write("c.png", c), r.Dir.Write("a2.png", a), a.Length, b.Length);
    }

    [Test]
    public void AC55_16_E13_バイト数の上限はちょうどなら通り超えると足さず同じ内容は数えない()
    {
        using var r = EdgeRig.Start();
        var f = MakeBudget(r);
        r.App.Ui(() => r.Imports.Importer.TotalBytesLimit = f.ABytes + f.BBytes);
        Check.Equal(1, r.DropFiles(f.A)?.Added, "A");
        Check.Equal(1, r.DropFiles(f.B)?.Added, "B(合計が a+b ちょうど)");
        r.Reject("C", () => r.DropFiles(f.C), NoticeKeys.LayerBudget, NoticeLevel.Warn, ["import_fail:over_budget"]);
        Check.Equal(1, r.DropFiles(f.A2)?.Added, "A2(内容が同じ画像は 1 回だけ数える)");
        Check.Equal(3, r.LayerNames.Length, "A2 のあとの枚数");
        Check.Equal(f.ABytes + f.BBytes, r.App.Ui(() => r.Session.BeginImportBudget().Bytes), "合計は a+b のまま");
        // 空のレイヤーは 0 として数える
        Check.True(r.Key(Key.N, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+N が処理されない");
        Check.Equal(4, r.LayerNames.Length, "空のレイヤーを足しても通る");
    }

    [Test]
    public void AC55_16_E13_画素数の上限と複数のファイル()
    {
        using var r = EdgeRig.Start();
        var f = MakeBudget(r);
        r.App.Ui(() => r.Imports.Importer.TotalPixelsLimit = 2000);
        Check.Equal(1, r.DropFiles(f.A)?.Added, "A");
        Check.Equal(1, r.DropFiles(f.B)?.Added, "B(合計が 2,000 ちょうど)");
        r.Reject("C", () => r.DropFiles(f.C), NoticeKeys.LayerBudget, NoticeLevel.Warn, ["import_fail:over_budget"]);

        // 文書が A と B のとき [C, A2]: C が断られ、A2 は判定もされない
        var opensA2 = Opens(r, f.A2);
        r.Reject("[C, A2]", () => r.DropFiles(f.C, f.A2), NoticeKeys.LayerBudget, NoticeLevel.Warn, ["import_fail:over_budget", "import_fail:over_budget"]);
        Check.Equal(opensA2, Opens(r, f.A2), "[C, A2]: A2 を開かない");

        // 文書が A だけのとき [B, C, A2]: B だけが足される
        r.CloseDocument();
        Check.Equal(1, r.DropFiles(f.A)?.Added, "A だけの文書");
        r.ClearNotice();
        var log = r.ImportLog().Length;
        var decodes = r.Decodes;
        Check.Equal(1, r.DropFiles(f.B, f.C, f.A2)?.Added, "[B, C, A2]");
        Check.Equal(opensA2, Opens(r, f.A2), "[B, C, A2]: A2 を開かない");
        Check.SequenceEqual(["a", "b"], r.LayerNames, "B だけ");
        Check.Equal(1, r.Decodes - decodes, "デコードは B の 1 回");
        r.ExpectMulti(3, 1, "[B, C, A2]");
        r.ExpectLog(log, ["import_ok", "import_fail:over_budget", "import_fail:over_budget"], "[B, C, A2]");
    }
}
