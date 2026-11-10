// 取り込み(ImageImporter)と文書(EditorSession)を SessionImportTarget でつないだときの検査。
// 1 回の取り込みが Undo の 1 件になること(AC-11 の 4)、上限の合計が文書から読まれること(FR-57)、画素の保持と解放を見る。
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Codec;

public sealed class SessionImportTargetTests
{
    private static readonly byte[] PngA = SkiaBytes.Png(40, 30, Img.Solid(40, 30, 250, 10, 10));
    private static readonly byte[] PngB = SkiaBytes.Png(20, 10, Img.Solid(20, 10, 10, 250, 10));
    private static readonly byte[] PngC = SkiaBytes.Png(8, 8, Img.Solid(8, 8, 10, 10, 250));

    private static (ImageImporter Importer, SessionImportTarget Target, EditorSession Session) New()
    {
        var session = SessionTestKit.NewSession();
        var target = new SessionImportTarget(session);
        return (new ImageImporter(target, new MemoryLogSink()), target, session);
    }

    [Test]
    public void AC11_4_つないだ文書に3個をドロップすると新しい文書の3枚になり画素を持つ()
    {
        using var dir = new TempDir();
        var (imp, target, session) = New();
        var o = imp.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", PngA), dir.Write("b.png", PngB), dir.Write("c.png", PngC)));
        Check.Equal(3, o.Added, "足せた枚数");
        Check.Equal(3, o.LayersAfter, "足したあとの枚数(セッションの枚数)");
        Check.Equal(3, session.Document!.Layers.Count, "セッションのレイヤー");
        Check.Equal("a", session.Document.Layers[0].Name, "土台はファイル名から");
        Check.Equal(0, session.UndoCount, "新しい文書は履歴が空");
        Check.Equal(3, target.Images.Count, "画素を 3 枚持つ");
        foreach (var l in session.Document.Layers)
        {
            Check.True(target.Images.ContainsKey(l.Asset!), "レイヤーの asset の画素がある");
        }
        Check.Equal(40, target.Images[session.Document.Layers[0].Asset!].Width, "画素の幅");
    }

    [Test]
    public void AC11_4_文書がある所へ複数を取り込むとUndo1回で全部消える()
    {
        using var dir = new TempDir();
        var (imp, _, session) = New();
        imp.Drop(new MemoryImportData().WithFiles(dir.Write("base.png", PngA))).CompleteDisplay();
        var before = session.Json;
        var o = imp.Drop(new MemoryImportData().WithFiles(dir.Write("b.png", PngB), dir.Write("c.png", PngC)));
        Check.Equal(NoticeKeys.LayerAdded, o.Notice?.Key, "通知");
        Check.Equal(3, session.Document!.Layers.Count, "3 枚");
        Check.Equal(1, session.UndoCount, "履歴は 1 件");
        Check.True(session.Undo(), "Undo できる");
        Check.Equal(1, session.Document.Layers.Count, "Undo 1 回で 2 枚とも消える");
        Check.Equal(before, session.Json, "取り込みの前の文書に戻る");
    }

    [Test]
    public void FR57_上限の合計はつないだ文書の異なる画像から読む()
    {
        using var dir = new TempDir();
        var (imp, target, _) = New();
        Check.Equal(0L, target.SourceBytesTotal, "文書が無いときのバイト数");
        Check.Equal(0, target.LayerCount, "文書が無いときの枚数");
        var a = dir.Write("a.png", PngA);
        var o = imp.Drop(new MemoryImportData().WithFiles(a, a, dir.Write("b.png", PngB)));
        o.CompleteDisplay();
        Check.Equal(3, target.LayerCount, "同じ画像も 1 枚ずつ足す");
        Check.Equal((long)(PngA.Length + PngB.Length), target.SourceBytesTotal, "同じ SHA-256 は 1 回だけ数える");
        Check.Equal(40L * 30 + 20 * 10, target.SourcePixelsTotal, "画素数の合計");
        Check.True(target.ContainsSource(ImportedImage.ComputeSha256(PngA)), "a は文書にある");
        Check.True(!target.ContainsSource(ImportedImage.ComputeSha256(PngC)), "c は文書に無い");
        Check.Equal(2, target.Images.Count, "画素は異なる画像の 2 枚");
    }

    [Test]
    public void FR30_セッションがソースを手放すと画素も捨てる()
    {
        using var dir = new TempDir();
        var (imp, target, session) = New();
        imp.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", PngA))).CompleteDisplay();
        Check.Equal(1, target.Images.Count, "取り込んだ画素");
        session.NewEmptyCanvas(64, 64);
        Check.Equal(0, target.Images.Count, "新しい文書で履歴が空になり、画素も捨てる");
        Check.Equal(0L, target.SourceBytesTotal, "空のキャンバスはソースを持たない");
    }

    [Test]
    public void 変更の通知の中で新しいレイヤーの画素がもう読める()
    {
        using var dir = new TempDir();
        var (imp, target, session) = New();
        var seen = new List<bool>();
        session.Changed += _ =>
        {
            var doc = session.Document;
            if (doc is null) return;
            seen.Add(doc.Layers.All(l => l.Asset is null || target.Images.ContainsKey(l.Asset)));
        };
        imp.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", PngA))).CompleteDisplay();
        imp.Drop(new MemoryImportData().WithFiles(dir.Write("b.png", PngB), dir.Write("c.png", PngC)));
        Check.Equal(2, seen.Count, "取り込み 2 回で通知が 2 回");
        Check.True(seen.All(x => x), "通知の時点で、文書のすべての画像の画素が登録されている");
        Check.Equal(3, target.Images.Count, "画素を 3 枚持つ");
    }
}
