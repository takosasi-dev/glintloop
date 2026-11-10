// .glint の素直な経路(§9.11 の 4)の往復。一時フォルダと、コードで作った画像だけを使う。
// 検査(AC-46・AC-47)と画面の経路は担当 project が足す。ここでは 2 枚のレイヤーの文書の JSON が一致することと、ZIP の形を見る。
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Codec;

public sealed class GlintProjectFileTests
{
    private static readonly byte[] PngA = SkiaBytes.Png(40, 30, Img.Solid(40, 30, 250, 10, 10));
    private static readonly byte[] PngB = SkiaBytes.Png(20, 10, Img.Solid(20, 10, 10, 250, 10));

    private static (SessionImportTarget Target, EditorSession Session) TwoLayers(TempDir dir)
    {
        var session = SessionTestKit.NewSession();
        var target = new SessionImportTarget(session);
        var importer = new ImageImporter(target, new MemoryLogSink());
        importer.Open([dir.Write("a.png", PngA), dir.Write("b.png", PngB)]).CompleteDisplay();
        session.SetLayerOpacity(1, 60);
        session.SetLayerPlacement(1, 10, 20, 50);
        return (target, session);
    }

    // ZIP の中央ディレクトリから、エントリーごとの圧縮の方法(0 が無圧縮、8 が Deflate)を読む
    private static Dictionary<string, int> Methods(byte[] zip)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + 46 <= zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 0x01 || zip[i + 3] != 0x02) continue;
            var method = BitConverter.ToUInt16(zip, i + 10);
            var nameLength = BitConverter.ToUInt16(zip, i + 28);
            result[Encoding.UTF8.GetString(zip, i + 46, nameLength)] = method;
        }
        return result;
    }

    [Test]
    public void 保存して開くと2枚の文書のJSONが一致し履歴が空で画素を持つ()
    {
        using var dir = new TempDir();
        var (target, session) = TwoLayers(dir);
        var path = dir.PathOf("p.glint");
        GlintProjectFile.Save(path, session.Document!, target.Images, FakeClock.ForPhase5(), "9.9.9");

        var opened = GlintProjectFile.Open(path, SessionTestKit.Catalog);
        var fresh = SessionTestKit.NewSession(seed: 1);
        var freshTarget = new SessionImportTarget(fresh);
        fresh.NewEmptyCanvas(64, 48);
        Check.Equal(ReplaceResult.Replaced, freshTarget.OpenDocument(opened.Document, opened.Images), "置き換える");

        Check.Equal(session.Json, fresh.Json, "JSON が一致する");
        Check.Equal(0, fresh.UndoCount, "Undo の履歴が空");
        Check.True(!fresh.IsUndelivered, "開いた直後は未渡しでない");
        foreach (var im in target.Images.Values)
        {
            var back = freshTarget.Images[im.Sha256];
            Check.SequenceEqual(im.Encoded, back.Encoded, "元のバイト列");
            Check.SequenceEqual(im.Pixels, back.Pixels, "画素");
            Check.Equal(im.SourcePixels, back.SourcePixels, "ヘッダーの画素数");
        }
    }

    [Test]
    public void ZIPのエントリーと圧縮とmanifestが決まりのとおりで_tmpが残らない()
    {
        using var dir = new TempDir();
        var (target, session) = TwoLayers(dir);
        var path = dir.PathOf("p.glint");
        GlintProjectFile.Save(path, session.Document!, target.Images, FakeClock.ForPhase5(), "9.9.9");
        Check.True(!File.Exists(path + ".tmp"), ".tmp が残らない");

        var bytes = File.ReadAllBytes(path);
        Check.SequenceEqual<byte>([0x50, 0x4B, 0x03, 0x04], bytes[..4], "ZIP の署名");
        var shas = session.Document!.Assets.Select(a => a.Sha256).ToArray();
        var methods = Methods(bytes);
        Check.SequenceEqual(
            new[] { GlintProjectFile.ManifestEntry, GlintProjectFile.SessionEntry, GlintProjectFile.AssetEntryName(shas[0]), GlintProjectFile.AssetEntryName(shas[1]) },
            methods.Keys.ToArray(), "エントリー");
        Check.Equal(8, methods[GlintProjectFile.ManifestEntry], "manifest.json は Deflate");
        Check.Equal(8, methods[GlintProjectFile.SessionEntry], "session.json は Deflate");
        Check.Equal(0, methods[GlintProjectFile.AssetEntryName(shas[0])], "アセットは無圧縮");
        Check.Equal(0, methods[GlintProjectFile.AssetEntryName(shas[1])], "アセットは無圧縮");

        using var zip = ZipFile.OpenRead(path);
        using var manifest = zip.GetEntry(GlintProjectFile.ManifestEntry)!.Open();
        using var doc = JsonDocument.Parse(manifest);
        Check.SequenceEqual(
            new[] { "format", "version", "app", "appVersion", "createdAt" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray(), "manifest のキーの順");
        Check.Equal("glint", doc.RootElement.GetProperty("format").GetString(), "format");
        Check.Equal(1, doc.RootElement.GetProperty("version").GetInt32(), "version");
        Check.Equal("GlintLoop", doc.RootElement.GetProperty("app").GetString(), "app");
        Check.Equal("9.9.9", doc.RootElement.GetProperty("appVersion").GetString(), "appVersion");
        Check.Equal("2026-10-14T11:38:05.000Z", doc.RootElement.GetProperty("createdAt").GetString(), "createdAt は UTC");
        using var asset = zip.GetEntry(GlintProjectFile.AssetEntryName(shas[0]))!.Open();
        using var ms = new MemoryStream();
        asset.CopyTo(ms);
        Check.SequenceEqual(PngA, ms.ToArray(), "アセットは元のバイト列");
    }

    [Test]
    public void 保存の失敗では最終的な名前のファイルが残らず_tmpも消える()
    {
        using var dir = new TempDir();
        var (target, session) = TwoLayers(dir);
        var path = dir.PathOf("p.glint");
        var fake = new FakeFileSystem();
        fake.FailOn(FileOps.Write, skip: 1);
        Check.Throws<IOException>(() => GlintProjectFile.Save(path, session.Document!, target.Images, FakeClock.ForPhase5(), "9.9.9", fake), "書き込みの途中の失敗");
        Check.True(!File.Exists(path), "最終的な名前のファイルが無い");
        Check.True(!File.Exists(path + ".tmp"), ".tmp が残らない");
    }

    [Test]
    public void DIBのアセットはDIBの読み方で読み元の画素になる()
    {
        using var dir = new TempDir();
        var dib = Dib.Build(40, 2, 2, 24, 0, (_, _) => 10u << 16 | 20u << 8 | 30u);
        var session = SessionTestKit.NewSession();
        var target = new SessionImportTarget(session);
        var importer = new ImageImporter(target, new MemoryLogSink());
        importer.Paste(new MemoryImportData().With(ImportFormatNames.Dib, dib)).CompleteDisplay();
        var sha = session.Document!.Assets.Single().Sha256;
        Check.Equal("dib", session.Document.Assets.Single().Ext, "ext");
        var path = dir.PathOf("d.glint");
        GlintProjectFile.Save(path, session.Document, target.Images, FakeClock.ForPhase5(), "9.9.9");

        var opened = GlintProjectFile.Open(path, SessionTestKit.Catalog);
        var back = opened.Images.Single();
        Check.Equal(ImageFormat.Dib, back.Format, "形式");
        Check.SequenceEqual(dib, back.Encoded, "読み取った DIB のバイト列");
        Check.SequenceEqual(target.Images[sha].Pixels, back.Pixels, "取り込み直後と同じ画素");
    }

    [Test]
    public void 確認でキャンセルすると文書も画素も変わらない()
    {
        using var dir = new TempDir();
        var (target, session) = TwoLayers(dir);
        var path = dir.PathOf("p.glint");
        GlintProjectFile.Save(path, session.Document!, target.Images, FakeClock.ForPhase5(), "9.9.9");
        var opened = GlintProjectFile.Open(path, SessionTestKit.Catalog);

        var other = SessionTestKit.NewSession(seed: 1);
        var otherTarget = new SessionImportTarget(other);
        new ImageImporter(otherTarget, new MemoryLogSink()).Open([dir.Write("c.png", SkiaBytes.Png(8, 8, Img.Solid(8, 8, 1, 1, 200)))]).CompleteDisplay();
        other.SetLayerOpacity(0, 50);
        var before = other.Json;
        Check.Equal(ReplaceResult.Cancelled, otherTarget.OpenDocument(opened.Document, opened.Images, new CancelDialog()), "キャンセル");
        Check.Equal(before, other.Json, "文書が変わらない");
        Check.Equal(1, otherTarget.Images.Count, "開こうとした画像の画素を持たない");
    }

    private sealed class CancelDialog : IReplaceConfirmation
    {
        public ReplaceChoice Ask(IReadOnlyList<ReplaceChoice> buttons, ReplaceChoice defaultButton) => ReplaceChoice.Cancel;
    }
}
