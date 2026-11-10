// .glint の読み込みの安全の検査(AC-46 の (5)・AC-47 の (3)〜(6)・AC-56 の (2)(3)・E-23〜E-25・E-44・E-45・§9.11 の 4)を、コードで作った ZIP で確かめる。
// 差し込み口(ProjectOpenHooks)に偽の ZIP 読み出し器と偽のデコードを渡して、呼び出しの回数を数える。画面の経路は Ui/ProjectUiTests が見る。
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Codec;

public sealed class GlintProjectSafetyTests
{
    private static readonly byte[] PngA = SkiaBytes.Png(8, 6, Img.Solid(8, 6, 250, 10, 10));
    private static readonly byte[] PngB = SkiaBytes.Png(4, 4, Img.Solid(4, 4, 10, 250, 10));

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string ManifestJson(string version = "1") =>
        "{\"format\":\"glint\",\"version\":" + version + ",\"app\":\"GlintLoop\",\"appVersion\":\"9.9.9\",\"createdAt\":\"2026-10-14T11:38:05.000Z\"}";

    private static string AssetJson(string sha, string ext, string bytes) =>
        "{\"sha256\":\"" + sha + "\",\"ext\":\"" + ext + "\",\"bytes\":" + bytes + "}";

    private static string AssetJson(byte[] b, string ext = "png") => AssetJson(Sha(b), ext, b.Length.ToString());

    private static string ImageLayer(string sha) => "{\"name\":\"p\",\"kind\":\"image\",\"asset\":\"" + sha + "\"}";

    private static string SessionJson(IEnumerable<string> assets, IEnumerable<string> layers, string extra = "\"schema\":1") =>
        "{" + extra + ",\"assets\":[" + string.Join(",", assets) + "],\"layers\":[" + string.Join(",", layers) + "]}";

    /// <summary>1 枚の PngA の文書の session.json。</summary>
    private static string OneLayer() => SessionJson([AssetJson(PngA)], [ImageLayer(Sha(PngA))]);

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var level = name.StartsWith("assets/", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    private static (string, byte[]) M(string version = "1") => ("manifest.json", Utf8(ManifestJson(version)));

    private static (string, byte[]) S(string json) => ("session.json", Utf8(json));

    private static (string, byte[]) A(byte[] b) => (GlintProjectFile.AssetEntryName(Sha(b)), b);

    private static byte[] OneLayerZip() => Zip(M(), S(OneLayer()), A(PngA));

    private static GlintProject Open(TempDir dir, byte[] file, ILogSink? log = null, ProjectOpenHooks? hooks = null) =>
        GlintProjectFile.Open(dir.Write("t.glint", file), SessionTestKit.Catalog, log, hooks: hooks);

    private static void ExpectInvalid(TempDir dir, byte[] file, string what, ProjectOpenHooks? hooks = null, string key = NoticeKeys.ProjectInvalid)
    {
        GlintProjectOpenException? thrown = null;
        try
        {
            Open(dir, file, hooks: hooks);
        }
        catch (GlintProjectOpenException e)
        {
            thrown = e;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"{what}: GlintProjectOpenException でなく {e.GetType().Name}: {e.Message}", e);
        }
        Check.True(thrown is not null, $"{what}: 開けてしまった");
        Check.Equal(key, thrown!.NoticeKey, what);
    }

    // ---------------------------------------- 開ける基準

    [Test]
    public void AC46_5_正しいファイルは開ける()
    {
        using var dir = new TempDir();
        var p = Open(dir, OneLayerZip());
        Check.Equal(1, p.Document.Layers.Count, "1 枚");
        Check.SequenceEqual(PngA, p.Images.Single().Encoded, "元のバイト列");
        Check.Equal(0, p.Notices.Count, "通知なし");
    }

    // ---------------------------------------- AC-46 の (5)・AC-47 の (5)

    [Test]
    public void AC46_5_versionが2はproject_too_newで開かない()
    {
        using var dir = new TempDir();
        ExpectInvalid(dir, Zip(M("2"), S(OneLayer()), A(PngA)), "version 2", key: NoticeKeys.ProjectTooNew);
        // session.json を読む前に判定する(壊れた session.json でも too_new)
        ExpectInvalid(dir, Zip(M("2"), S("{{{"), A(PngA)), "version 2 で session.json が壊れている", key: NoticeKeys.ProjectTooNew);
    }

    [Test]
    public void AC46_5_manifestが不正ならproject_invalid()
    {
        using var dir = new TempDir();
        ExpectInvalid(dir, Zip(("manifest.json", Utf8("{{{")), S(OneLayer()), A(PngA)), "manifest が JSON でない");
        ExpectInvalid(dir, Zip(("manifest.json", Utf8("{\"format\":\"zip\",\"version\":1}")), S(OneLayer()), A(PngA)), "format が違う");
        ExpectInvalid(dir, Zip(("manifest.json", Utf8("{\"format\":\"glint\"}")), S(OneLayer()), A(PngA)), "version が無い");
        ExpectInvalid(dir, Zip(("manifest.json", Utf8("{\"format\":\"glint\",\"version\":\"1\"}")), S(OneLayer()), A(PngA)), "version が文字列");
    }

    [Test]
    public void AC46_5_壊れたZIPとエントリーの不足はproject_invalid()
    {
        using var dir = new TempDir();
        var good = OneLayerZip();
        ExpectInvalid(dir, good[..(good.Length / 2)], "途中で切れた ZIP");
        ExpectInvalid(dir, [0x50, 0x4B, 0x03, 0x04, 0, 0], "署名だけ");
        ExpectInvalid(dir, Zip(S(OneLayer()), A(PngA)), "manifest.json が無い");
        ExpectInvalid(dir, Zip(M(), A(PngA)), "session.json が無い");
        ExpectInvalid(dir, Zip(M(), S(OneLayer())), "アセットが無い");
        // 中央ディレクトリを壊す
        var broken = (byte[])good.Clone();
        var central = broken.AsSpan().IndexOf([(byte)0x50, (byte)0x4B, (byte)0x01, (byte)0x02]);
        broken[central] = 0;
        ExpectInvalid(dir, broken, "中央ディレクトリの署名が壊れた ZIP");
        // Deflate の中身を壊す(session.json は manifest.json の後ろの Deflate のエントリー)
        var flipped = (byte[])good.Clone();
        var second = flipped.AsSpan(4).IndexOf([(byte)0x50, (byte)0x4B, (byte)0x03, (byte)0x04]) + 4;
        flipped[second + 60] ^= 0xFF;
        ExpectInvalid(dir, flipped, "session.json の圧縮データが壊れた ZIP");
    }

    [Test]
    public void AC46_5_読めないファイルはproject_invalid()
    {
        using var dir = new TempDir();
        var path = dir.Write("t.glint", OneLayerZip());
        var fake = new FakeFileSystem();
        fake.FailOn(FileOps.OpenRead);
        var e = Check.Throws<GlintProjectOpenException>(() => GlintProjectFile.Open(path, SessionTestKit.Catalog, fileSystem: fake), "開けない");
        Check.Equal(NoticeKeys.ProjectInvalid, e.NoticeKey, "通知");
    }

    [Test]
    public void AC47_5_文書の規則に反するものはproject_invalid()
    {
        using var dir = new TempDir();
        var sha = Sha(PngA);
        var asset = AssetJson(PngA);
        var layer = ImageLayer(sha);
        var cases = new (string What, string Json)[]
        {
            ("schema 2", SessionJson([asset], [layer], "\"schema\":2")),
            ("layers が配列でない", "{\"schema\":1,\"assets\":[" + asset + "],\"layers\":{}}"),
            ("layers が空", SessionJson([asset], [])),
            ("layers が 33 個", SessionJson([asset], Enumerable.Repeat(layer, 33))),
            ("kind が video", SessionJson([asset], ["{\"kind\":\"video\",\"asset\":\"" + sha + "\"}"])),
            ("asset が無い", SessionJson([asset], ["{\"kind\":\"image\"}"])),
            ("asset が assets に無い", SessionJson([asset], [ImageLayer(new string('0', 64))])),
            ("sha256 が大文字", SessionJson([AssetJson(sha.ToUpperInvariant(), "png", PngA.Length.ToString())], [ImageLayer(sha.ToUpperInvariant())])),
            ("sha256 が 63 桁", SessionJson([AssetJson(sha[..63], "png", PngA.Length.ToString())], [ImageLayer(sha[..63])])),
            ("同じ sha256 が 2 回", SessionJson([asset, asset], [layer])),
            ("ext が tiff", SessionJson([AssetJson(sha, "tiff", PngA.Length.ToString())], [layer])),
            ("bytes が 0", SessionJson([AssetJson(sha, "png", "0")], [layer])),
            ("bytes が 100,000,001", SessionJson([AssetJson(sha, "png", "100000001")], [layer])),
            ("bytes が小数", SessionJson([AssetJson(sha, "png", "1.5")], [layer])),
            ("JSON として読めない", "{\"schema\":1,"),
        };
        foreach (var (what, json) in cases) ExpectInvalid(dir, Zip(M(), S(json), A(PngA)), what);
    }

    [Test]
    public void AC47_5_アセットの長さとSHA256の照合()
    {
        using var dir = new TempDir();
        var sha = Sha(PngA);
        var name = GlintProjectFile.AssetEntryName(sha);
        ExpectInvalid(dir, Zip(M(), S(OneLayer()), (name, PngA.Concat(new byte[] { 0 }).ToArray())), "1 バイト多い");
        ExpectInvalid(dir, Zip(M(), S(OneLayer()), (name, PngA[..^1])), "1 バイト少ない");
        var flipped = (byte[])PngA.Clone();
        flipped[^1] ^= 1;
        ExpectInvalid(dir, Zip(M(), S(OneLayer()), (name, flipped)), "SHA-256 が違う");
    }

    [Test]
    public void AC47_5_デコードできない画像が1つあると開かない()
    {
        using var dir = new TempDir();
        var bad = "not an image at all"u8.ToArray();
        var badHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        foreach (var b in new[] { bad, badHeader })
        {
            var json = SessionJson([AssetJson(PngA), AssetJson(b)], [ImageLayer(Sha(PngA)), ImageLayer(Sha(b))]);
            ExpectInvalid(dir, Zip(M(), S(json), A(PngA), A(b)), "読めない画像");
        }
        // ヘッダーは読めるが画素が切れている PNG
        var truncated = PngBytes.OneBitGray(600, 400, white: true);
        var cut = truncated[..(truncated.Length - 24)];
        var j = SessionJson([AssetJson(cut)], [ImageLayer(Sha(cut))]);
        ExpectInvalid(dir, Zip(M(), S(j), A(cut)), "画素が切れた PNG");
    }

    [Test]
    public void AC47_5_単独のソースの上限を超える画像は開かずデコードもしない()
    {
        using var dir = new TempDir();
        var calls = 0;
        var hooks = new ProjectOpenHooks { Decode = (b, f, s, p, m) => { calls++; return ProjectOpenHooks.Default.Decode(b, f, s, p, m); } };
        // FR-10: 16,385 px の辺・64 MP を超える画素
        foreach (var png in new[] { PngBytes.OneBitGray(16385, 1, true), PngBytes.OneBitGray(8193, 8192, true) })
        {
            var json = SessionJson([AssetJson(png)], [ImageLayer(Sha(png))]);
            ExpectInvalid(dir, Zip(M(), S(json), A(png)), "単独の上限", hooks);
        }
        Check.Equal(0, calls, "デコードの呼び出し");
    }

    [Test]
    public void AC47_5_layersが使っていないassetsの項目は読まずに捨てて開ける()
    {
        using var dir = new TempDir();
        var json = SessionJson([AssetJson(PngA), AssetJson(PngB)], [ImageLayer(Sha(PngA))]);
        var zip = new CountingHooks();
        var p = Open(dir, Zip(M(), S(json), A(PngA), A(PngB)), hooks: zip.Hooks);
        Check.Equal(1, p.Document.Assets.Count, "使っている 1 項目だけ");
        Check.Equal(Sha(PngA), p.Document.Assets[0].Sha256, "使っている項目");
        Check.Equal(1, p.Images.Count, "画像は 1 つ");
        Check.Equal(0, zip.Opened.Count(n => n.Contains(Sha(PngB), StringComparison.Ordinal)), "使っていないアセットを開かない");
        // 使っていない項目のエントリーが無いと、エントリーの数が合わない(§9.11 の 4)
        ExpectInvalid(dir, Zip(M(), S(json), A(PngA)), "2 + assets の項目数に合わない");
    }

    // ---------------------------------------- AC-47 の (6)

    [Test]
    public void AC47_6_総エントリー数が35はZipArchiveを作らずに開かない()
    {
        using var dir = new TempDir();
        var entries = new List<(string, byte[])> { M(), S(OneLayer()), A(PngA) };
        for (var i = 0; i < 32; i++) entries.Add(("x" + i, [1]));
        Check.Equal(35, entries.Count, "35 個");
        var counting = new CountingHooks();
        ExpectInvalid(dir, Zip(entries.ToArray()), "35 個", counting.Hooks);
        Check.Equal(0, counting.ZipCreated, "ZIP の読み出し器を作らない");

        // 34 個までは ZIP を作り、session.json を読んでからエントリーの数で落とす
        entries.RemoveAt(entries.Count - 1);
        counting = new CountingHooks();
        ExpectInvalid(dir, Zip(entries.ToArray()), "34 個で assets が 1 項目", counting.Hooks);
        Check.Equal(1, counting.ZipCreated, "34 個は ZIP を作る");
        Check.True(counting.Opened.Contains("session.json"), "session.json を読む");
        Check.True(!counting.Opened.Any(n => n.StartsWith("assets/", StringComparison.Ordinal)), "アセットを開かない");

        // 末尾の記録だけを書き換えた偽物(実際は 3 個)も同じ
        var forged = OneLayerZip();
        BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(forged.Length - 12), 35);
        counting = new CountingHooks();
        ExpectInvalid(dir, forged, "末尾の記録だけが 35", counting.Hooks);
        Check.Equal(0, counting.ZipCreated, "末尾の記録の偽物でも作らない");
        // 末尾の記録が無いものも作らない
        counting = new CountingHooks();
        ExpectInvalid(dir, OneLayerZip()[..^22], "末尾の記録が無い", counting.Hooks);
        Check.Equal(0, counting.ZipCreated, "末尾の記録が無ければ作らない");
    }

    [Test]
    public void AC46_5_AC47_6_危ない名前のエントリーはどこにも作られず開かない()
    {
        using var dir = new TempDir();
        var sub = Directory.CreateDirectory(Path.Combine(dir.Path, "a", "b")).FullName;
        var cwd = Environment.CurrentDirectory;
        const string Evil = "evil-p5-project.txt";
        var file = Zip(M(), S(OneLayer()), A(PngA), (@"..\..\" + Evil, [1, 2, 3]));
        var path = Path.Combine(sub, "t.glint");
        File.WriteAllBytes(path, file);
        var e = Check.Throws<GlintProjectOpenException>(() => GlintProjectFile.Open(path, SessionTestKit.Catalog), "多いエントリー");
        Check.Equal(NoticeKeys.ProjectInvalid, e.NoticeKey, "通知");
        Check.True(!File.Exists(Path.Combine(dir.Path, Evil)), "2 つ上のフォルダに作られた");
        Check.True(!File.Exists(Path.Combine(cwd, Evil)), "作業フォルダに作られた");
        Check.True(!File.Exists(Path.Combine(cwd, "..", "..", Evil)), "作業フォルダの 2 つ上に作られた");
        Check.Equal(1, Directory.GetFiles(sub).Length, "同じフォルダに作られた");

        // 固定の名前のどれかと入れ替えて数を合わせても開かない
        ExpectInvalid(dir, Zip(M(), S(SessionJson([AssetJson(PngA), AssetJson(PngB)], [ImageLayer(Sha(PngA))])), A(PngA), (@"..\..\" + Evil, [1])), "使わない項目の代わりの危ない名前");
    }

    [Test]
    public void AC47_6_manifestとsession_jsonの展開後の上限()
    {
        using var dir = new TempDir();
        var manifest = ManifestJson();
        // 上限ちょうどは開ける(空白で埋める)
        var okManifest = manifest + new string(' ', 65_536 - manifest.Length);
        Open(dir, Zip(("manifest.json", Utf8(okManifest)), S(OneLayer()), A(PngA)));
        var session = OneLayer();
        var okSession = session + new string(' ', 4_000_000 - session.Length);
        Open(dir, Zip(M(), S(okSession), A(PngA)));
        // 1 バイト多いと開かない
        ExpectInvalid(dir, Zip(("manifest.json", Utf8(okManifest + " ")), S(OneLayer()), A(PngA)), "manifest.json が 65,537 バイト");
        ExpectInvalid(dir, Zip(M(), S(okSession + " "), A(PngA)), "session.json が 4,000,001 バイト");
    }

    // ZIP の 2 つのヘッダーの「展開後の大きさ」を書き換える
    private static byte[] LieAboutSize(byte[] zip, string entry, uint size)
    {
        var bytes = (byte[])zip.Clone();
        var name = Encoding.ASCII.GetBytes(entry);
        var patched = 0;
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B) continue;
            if (bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04 && bytes.AsSpan(i + 30, name.Length).SequenceEqual(name))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), size);
                patched++;
            }
            else if (bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02 && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), size);
                patched++;
            }
        }
        Check.Equal(2, patched, "2 つのヘッダーを書き換える");
        return bytes;
    }

    [Test]
    public void AC47_6_ヘッダーに小さな大きさを書いた偽のファイルでも開かない()
    {
        using var dir = new TempDir();
        var manifest = ManifestJson();
        var session = OneLayer();
        var bigManifest = Zip(("manifest.json", Utf8(manifest + new string(' ', 65_537 - manifest.Length))), S(session), A(PngA));
        var bigSession = Zip(M(), S(session + new string(' ', 4_000_001 - session.Length)), A(PngA));
        // 中身の JSON の長さをそのまま書いた偽物(本当の長さの先頭だけを読む実装なら開けてしまう)
        ExpectInvalid(dir, LieAboutSize(bigManifest, "manifest.json", (uint)manifest.Length), "manifest.json の偽のヘッダー");
        ExpectInvalid(dir, LieAboutSize(bigSession, "session.json", (uint)session.Length), "session.json の偽のヘッダー");
        ExpectInvalid(dir, LieAboutSize(bigManifest, "manifest.json", 16), "manifest.json の小さな偽のヘッダー");
        ExpectInvalid(dir, LieAboutSize(bigSession, "session.json", 16), "session.json の小さな偽のヘッダー");
    }

    [Test]
    public void AC47_6_読み出しは上限の1バイト先で打ち切る()
    {
        using var dir = new TempDir();
        foreach (var (entry, limit) in new[] { ("manifest.json", 65_536L), ("session.json", 4_000_000L), (GlintProjectFile.AssetEntryName(Sha(PngA)), PngA.Length) })
        {
            var counting = new CountingHooks { Endless = entry };
            ExpectInvalid(dir, OneLayerZip(), entry, counting.Hooks);
            Check.Equal(limit + 1, counting.EndlessRead, entry + " を読んだバイト数");
        }
    }

    [Test]
    public void AC47_6_bytesの合計が300000001ならアセットを1つも開かない()
    {
        using var dir = new TempDir();
        var shas = Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 64)).ToArray();
        var sizes = new[] { "100000000", "100000000", "100000000", "1" };
        var json = SessionJson(shas.Select((s, i) => AssetJson(s, "png", sizes[i])), shas.Select(ImageLayer));
        var entries = new List<(string, byte[])> { M(), S(json) };
        foreach (var s in shas) entries.Add((GlintProjectFile.AssetEntryName(s), [1]));
        var counting = new CountingHooks();
        ExpectInvalid(dir, Zip(entries.ToArray()), "300,000,001", counting.Hooks);
        Check.Equal(1, counting.ZipCreated, "ZIP は作る");
        Check.True(counting.Opened.Contains("session.json"), "session.json は読む");
        Check.Equal(0, counting.Opened.Count(n => n.StartsWith("assets/", StringComparison.Ordinal)), "アセットのエントリーを開く回数");

        // 300,000,000 ちょうどは合計の検査を通り、アセットを開く(偽の 1 バイトなので長さで落ちる)
        json = SessionJson(shas[..3].Select(s => AssetJson(s, "png", "100000000")), shas[..3].Select(ImageLayer));
        entries = [M(), S(json)];
        foreach (var s in shas[..3]) entries.Add((GlintProjectFile.AssetEntryName(s), [1]));
        counting = new CountingHooks();
        ExpectInvalid(dir, Zip(entries.ToArray()), "300,000,000", counting.Hooks);
        Check.True(counting.Opened.Any(n => n.StartsWith("assets/", StringComparison.Ordinal)), "300,000,000 はアセットを開く");
    }

    [Test]
    public void AC47_6_画素の合計が100000001ならデコードを始めない()
    {
        using var dir = new TempDir();
        var big1 = PngBytes.OneBitGray(10000, 5000, white: true);
        var big2 = PngBytes.OneBitGray(10000, 5000, white: false);
        var one = PngBytes.OneBitGray(1, 1, white: true);
        var calls = 0;
        var hooks = new ProjectOpenHooks
        {
            Decode = (b, f, s, p, m) =>
            {
                calls++;
                return new ImportedImage(1, 1, new byte[4], f, b, s, p, false, false);
            },
        };
        var over = new[] { big1, big2, one };
        var json = SessionJson(over.Select(b => AssetJson(b)), over.Select(b => ImageLayer(Sha(b))));
        ExpectInvalid(dir, Zip(M(), S(json), A(big1), A(big2), A(one)), "100,000,001", hooks);
        Check.Equal(0, calls, "デコードの呼び出し");

        // 100,000,000 ちょうどはデコードへ進む
        var exact = new[] { big1, big2 };
        json = SessionJson(exact.Select(b => AssetJson(b)), exact.Select(b => ImageLayer(Sha(b))));
        var p = Open(dir, Zip(M(), S(json), A(big1), A(big2)), hooks: hooks);
        Check.Equal(2, calls, "100,000,000 はデコードする");
        Check.Equal(100_000_000L, p.Images.Sum(i => i.SourcePixels), "ヘッダーの画素数");
    }

    // ---------------------------------------- AC-47 の (3)(4)・AC-56 の (2)(3)

    [Test]
    public void AC47_3_土台のブレンドと配置は書かれた値にかかわらず強制されログを書かない()
    {
        using var dir = new TempDir();
        var layer = "{\"name\":\"p\",\"kind\":\"image\",\"asset\":\"" + Sha(PngA) + "\",\"blend\":\"multiply\",\"place\":{\"x\":5,\"y\":6,\"scale\":50}}";
        var log = new MemoryLogSink();
        var p = Open(dir, Zip(M(), S(SessionJson([AssetJson(PngA)], [layer])), A(PngA)), log);
        var b = p.Document.Layers[0];
        Check.Equal(BlendMode.Normal, b.Blend, "normal");
        Check.Equal(Placement.Identity, b.Place, "(0, 0, 100)");
        Check.Equal(0, log.Events.Count(e => e.Name == "param_clamped"), "param_clamped を書かない");
        var written = DocumentJson.Write(p.Document);
        Check.True(written.Contains("\"blend\":\"normal\"", StringComparison.Ordinal) || written.Contains("\"blend\": \"normal\"", StringComparison.Ordinal), "normal で書かれる");
    }

    [Test]
    public void AC47_4_範囲外の項目は直してlayerかallのparam_clampedを1項目1行書く()
    {
        using var dir = new TempDir();
        var over = "{\"name\":\"" + new string('n', 41) + "\",\"kind\":\"image\",\"asset\":\"" + Sha(PngB) + "\",\"opacity\":150,\"blend\":\"dissolve\","
            + "\"place\":{\"x\":100001,\"y\":0,\"scale\":4},\"lookStrength\":120,\"junk\":1}";
        var json = "{\"schema\":1,\"saltCounter\":3,\"selected\":9,\"all\":{\"lookStrength\":120,\"stack\":[{\"id\":\"exposure\",\"salt\":9,\"params\":{}}]},"
            + "\"assets\":[" + AssetJson(PngA) + "," + AssetJson(PngB) + "],\"layers\":[" + ImageLayer(Sha(PngA)) + "," + over + "]}";
        var log = new MemoryLogSink();
        var p = Open(dir, Zip(M(), S(json), A(PngA), A(PngB)), log);
        var l = p.Document.Layers[1];
        Check.Equal(new string('n', 40), l.Name, "名前は先頭の 40 文字");
        Check.Equal(100, l.Opacity, "opacity");
        Check.Equal(BlendMode.Normal, l.Blend, "blend");
        Check.Equal(100000.0, l.Place.X, "place.x");
        Check.Equal(5.0, l.Place.Scale, "place.scale");
        Check.Equal(100, l.LookStrength, "lookStrength");
        Check.Equal(100, p.Document.All.LookStrength, "全体の lookStrength");
        Check.Equal(Target.OfLayer(1), p.Document.Selected, "selected は最前面");
        Check.Equal(9L, p.Document.SaltCounter, "saltCounter は最大の salt");
        var clamps = log.Events.Where(e => e.Name == "param_clamped").Select(e => (string)e.Get("effect")! + "." + (string)e.Get("key")!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check.SequenceEqual(new[] { "all.lookStrength", "layer.blend", "layer.lookStrength", "layer.name", "layer.opacity", "layer.place.scale", "layer.place.x", "layer.selected" }, clamps, "param_clamped の行");
    }

    private static string Rows(string id, int n) =>
        string.Join(",", Enumerable.Range(0, n).Select(i => "{\"id\":\"" + id + "\",\"enabled\":true,\"origin\":\"user\",\"salt\":" + (i + 1) + ",\"amount\":100,\"params\":{}}"));

    private static string WithStack(string sha, string rows) =>
        "{\"name\":\"p\",\"kind\":\"image\",\"asset\":\"" + sha + "\",\"stack\":[" + rows + "]}";

    [Test]
    public void AC56_2_65個のstackは先頭の64個だけ読みstack_fullをGlintProjectで返す()
    {
        using var dir = new TempDir();
        var rows = Rows("exposure", 65);
        var json = "{\"schema\":1,\"all\":{\"stack\":[" + rows + "]},\"assets\":[" + AssetJson(PngA) + "," + AssetJson(PngB) + "],\"layers\":["
            + WithStack(Sha(PngA), rows) + "," + WithStack(Sha(PngB), rows) + "]}";
        var p = Open(dir, Zip(M(), S(json), A(PngA), A(PngB)));
        Check.Equal(64, p.Document.Layers[0].Stack.Count, "土台");
        Check.Equal(64, p.Document.Layers[1].Stack.Count, "2 枚目");
        Check.Equal(64, p.Document.All.Stack.Count, "全体");
        Check.Equal(NoticeKeys.StackFull, p.Notices.Single().Key, "stack.full");
        Check.Equal(NoticeLevel.Warn, p.Notices.Single().Level, "warn");
    }

    [Test]
    public void AC56_3_未知のidの行だけ読まずstack_unknown_effectをGlintProjectで返す()
    {
        using var dir = new TempDir();
        var rows = Rows("exposure", 1) + "," + Rows("no_such_effect", 1) + "," + Rows("invert", 1);
        var json = "{\"schema\":1,\"assets\":[" + AssetJson(PngA) + "],\"layers\":[" + WithStack(Sha(PngA), rows) + "]}";
        var p = Open(dir, Zip(M(), S(json), A(PngA)));
        Check.SequenceEqual(new[] { "exposure", "invert" }, p.Document.Layers[0].Stack.Select(r => r.Id).ToArray(), "ほかの行は元の順");
        Check.Equal(NoticeKeys.StackUnknownEffect, p.Notices.Single().Key, "stack.unknown_effect");
    }

    // ---------------------------------------- 偽の読み出し器

    /// <summary>本物の ZIP を包み、作った回数と開いたエントリーを数える。Endless に入れた名前のエントリーは、読むたびに終わらないストリームを返す。</summary>
    private sealed class CountingHooks
    {
        public CountingHooks()
        {
            Hooks = new ProjectOpenHooks
            {
                OpenZip = s =>
                {
                    ZipCreated++;
                    return new Wrapper(this, ProjectOpenHooks.Default.OpenZip(s));
                },
            };
        }

        public ProjectOpenHooks Hooks { get; }

        public int ZipCreated { get; private set; }

        public List<string> Opened { get; } = new();

        public string? Endless { get; init; }

        public long EndlessRead { get; private set; }

        private sealed class Wrapper(CountingHooks owner, IProjectZip inner) : IProjectZip
        {
            public IReadOnlyList<string> EntryNames => inner.EntryNames;

            public Stream OpenEntry(string name)
            {
                owner.Opened.Add(name);
                return name == owner.Endless ? new EndlessStream(owner) : inner.OpenEntry(name);
            }

            public void Dispose() => inner.Dispose();
        }

        private sealed class EndlessStream(CountingHooks owner) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                Array.Fill(buffer, (byte)' ', offset, count);
                owner.EndlessRead += count;
                return count;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
