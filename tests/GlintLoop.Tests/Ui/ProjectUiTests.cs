// .glint の画面の経路(AC-46・AC-47 の (1)(2)・FR-13・E-20・E-34)を、画面の外のアプリ・偽のダイアログ・偽のクリップボード・一時フォルダ・WARP で確かめる。
// 読み込みの検査そのものは Codec/GlintProjectSafetyTests が見る。ここは操作から辿れることと、通知・文書・未渡し・履歴・ダイアログの動きを見る。
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.App.Screens;
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Preview;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Ui;

public sealed class ProjectUiTests
{
    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

    private static readonly byte[] PngA = SkiaBytes.Png(40, 30, Img.Solid(40, 30, 250, 10, 10));
    private static readonly byte[] PngB = SkiaBytes.Png(20, 10, Img.Solid(20, 10, 10, 250, 10));

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static ProjectCoordinator Projects(TestApp app) => M<ScreensModule>(app).Imports.Projects;

    private static string? Notice(TestApp app) => app.Ui(() => app.Shell.NoticeBar.Current?.Key);

    private static EditorSession Session(TestApp app) => app.Shell.Session;

    /// <summary>開くダイアログに paths を答えさせて Ctrl+O を押し、取り込みが終わるまで待つ。</summary>
    private static void CtrlO(TestApp app, params string[] paths)
    {
        app.FileDialogs.OpenAnswers.Enqueue(paths);
        Check.True(Key(app, System.Windows.Input.Key.O, ModifierKeys.Control), "Ctrl+O が処理されない");
        WaitImports(app);
    }

    /// <summary>保存ダイアログに path を答えさせて Ctrl+Shift+S を押し、書き込みが終わるまで待つ。</summary>
    private static void CtrlShiftS(TestApp app, string? path)
    {
        app.FileDialogs.SaveAnswers.Enqueue(path);
        Check.True(Key(app, System.Windows.Input.Key.S, CtrlShift), "Ctrl+Shift+S が処理されない");
        Check.True(UiHost.WaitUntil(() => !Projects(app).IsSaving, Wait), "保存が終わらない");
    }

    private static void Paste(TestApp app, MemoryImportData data)
    {
        app.Clipboard.Data = data;
        Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
        WaitImports(app);
    }

    private static byte[] Entry(string glint, string name)
    {
        using var zip = ZipFile.OpenRead(glint);
        using var s = (zip.GetEntry(name) ?? throw new InvalidOperationException("エントリーが無い: " + name)).Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string[] EntryNames(string glint)
    {
        using var zip = ZipFile.OpenRead(glint);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    // 中央ディレクトリから、エントリーごとの圧縮の方法(0 が無圧縮、8 が Deflate)を読む
    private static Dictionary<string, int> Methods(string glint)
    {
        var zip = File.ReadAllBytes(glint);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + 46 <= zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 0x01 || zip[i + 3] != 0x02) continue;
            result[Encoding.UTF8.GetString(zip, i + 46, BitConverter.ToUInt16(zip, i + 28))] = BitConverter.ToUInt16(zip, i + 10);
        }
        return result;
    }

    private static string[] Sorted(params string[] names) => names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string Logs(TestApp app)
    {
        var text = "";
        foreach (var f in Directory.GetFiles(app.Paths.LogsDir, "*.jsonl"))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            text += r.ReadToEnd();
        }
        return text;
    }

    private static byte[] ManifestV(int v) => Encoding.UTF8.GetBytes("{\"format\":\"glint\",\"version\":" + v + "}");

    private static byte[] ZipOf(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>session.json の JSON で作る 1 枚の PngA のプロジェクト。</summary>
    private static byte[] OneImageProject(string layerExtra = "", string allExtra = "") => ZipOf(
        ("manifest.json", ManifestV(1)),
        ("session.json", Encoding.UTF8.GetBytes("{\"schema\":1,\"all\":{" + allExtra + "},\"assets\":[{\"sha256\":\"" + Sha(PngA) + "\",\"ext\":\"png\",\"bytes\":" + PngA.Length + "}],"
            + "\"layers\":[{\"name\":\"p\",\"kind\":\"image\",\"asset\":\"" + Sha(PngA) + "\"" + layerExtra + "}]}")),
        (GlintProjectFile.AssetEntryName(Sha(PngA)), PngA));

    // ---------------------------------------- AC-46 の (1)

    [Test]
    public void AC46_1_CtrlShiftSでtmpを経由して決まりのZIPを書きproject_savedが出て未渡しでなくなる()
    {
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        using var dir = new TempDir();
        CtrlO(app, dir.Write("a.png", PngA));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        Check.True(app.Ui(() => Session(app).IsUndelivered), "編集で未渡しになる");

        var path = dir.PathOf("p.glint");
        CtrlShiftS(app, path);

        Check.Equal(NoticeKeys.ProjectSaved, Notice(app), "project.saved");
        Check.True(app.Ui(() => !Session(app).IsUndelivered), "未渡しでなくなる");
        var calls = app.FileSystem.Calls;
        Check.True(calls.Any(c => c.Operation == FileOps.Create && c.Path == path + ".tmp"), ".tmp に書く");
        Check.True(calls.Any(c => c.Operation == FileOps.Move && c.Path == path), ".tmp からリネームする");
        Check.True(!File.Exists(path + ".tmp"), ".tmp が残らない");

        var asset = GlintProjectFile.AssetEntryName(Sha(PngA));
        Check.SequenceEqual(Sorted("manifest.json", "session.json", asset), EntryNames(path), "土台 1 枚の文書は 3 つ");
        var methods = Methods(path);
        Check.Equal(8, methods["manifest.json"], "manifest.json は Deflate");
        Check.Equal(8, methods["session.json"], "session.json は Deflate");
        Check.Equal(0, methods[asset], "アセットは Stored");
        using var manifest = JsonDocument.Parse(Entry(path, "manifest.json"));
        var root = manifest.RootElement;
        Check.SequenceEqual(new[] { "format", "version", "app", "appVersion", "createdAt" }, root.EnumerateObject().Select(p => p.Name).ToArray(), "キーの順");
        Check.Equal("glint", root.GetProperty("format").GetString(), "format");
        Check.Equal(1, root.GetProperty("version").GetInt32(), "version");
        Check.Equal("GlintLoop", root.GetProperty("app").GetString(), "app");
        Check.Equal("2026-10-14T11:38:05.000Z", root.GetProperty("createdAt").GetString(), "createdAt は UTC の ISO 8601");
    }

    [Test]
    public void AC46_1_文書が無いときと保存ダイアログを閉じたときは何もしない()
    {
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        using var dir = new TempDir();
        Key(app, System.Windows.Input.Key.S, CtrlShift);
        UiHost.Idle();
        Check.Equal(0, app.FileDialogs.Calls, "文書が無ければダイアログを出さない");

        CtrlO(app, dir.Write("a.png", PngA));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        var calls = app.FileDialogs.Calls;
        CtrlShiftS(app, null);
        Check.Equal(calls + 1, app.FileDialogs.Calls, "ダイアログを出す");
        Check.True(app.Ui(() => Session(app).IsUndelivered), "閉じたら未渡しのまま");
        Check.True(Notice(app) != NoticeKeys.ProjectSaved, "閉じたら project.saved は出ない");
    }

    [Test]
    public void AC46_1_保存ダイアログの既定の名前とフォルダはFR84とFR85の形で絞り込みはglint()
    {
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        using var dir = new TempDir();
        CtrlO(app, dir.Write("a.png", PngA));
        Check.True(app.FileDialogs.LastOpenFilters!.Single().Patterns.Split(';').Contains("*.glint"), "開くの絞り込みに .glint がある");
        Check.True(app.FileDialogs.LastOpenFilters!.Single().Patterns.Split(';').Contains("*.png"), "開くの絞り込みに画像がある");
        var path = dir.PathOf("p.glint");
        CtrlShiftS(app, path);
        var first = app.FileDialogs.LastSave!.Value;
        Check.Equal("glint-20261014-203805.glint", first.FileName, "既定の名前");
        Check.True(!string.IsNullOrEmpty(first.InitialDir) && first.InitialDir != dir.Path, "最初は前回の保存先でない(ピクチャフォルダ)");
        Check.Equal("*.glint", first.Filters.Single().Patterns, "絞り込み");
        Check.Equal(dir.Path, app.Ui(() => app.Shell.Settings.LastSaveDir), "lastSaveDir が保存したフォルダになる");

        CtrlShiftS(app, path);
        Check.Equal(dir.Path, app.FileDialogs.LastSave!.Value.InitialDir, "次は前回の保存先");
        using var other = new TempDir();
        app.Ui(() => app.Shell.Settings.DefaultSaveDir = other.Path);
        CtrlShiftS(app, path);
        Check.Equal(other.Path, app.FileDialogs.LastSave!.Value.InitialDir, "defaultSaveDir が優先");
    }

    // ---------------------------------------- AC-46 の (2)

    // 位置情報(GPS の IFD)を持つ APP1 を JPEG の SOI の直後に入れる
    private static byte[] JpegWithGps(byte[] jpeg)
    {
        byte[] tiff =
        [
            0x49, 0x49, 0x2A, 0, 8, 0, 0, 0,
            1, 0, 0x25, 0x88, 4, 0, 1, 0, 0, 0, 26, 0, 0, 0, 0, 0, 0, 0,
            1, 0, 1, 0, 2, 0, 2, 0, 0, 0, (byte)'N', 0, 0, 0, 0, 0, 0, 0,
        ];
        var body = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = body.Length + 2;
        return jpeg[..2].Concat(new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }).Concat(body).Concat(jpeg[2..]).ToArray();
    }

    [Test]
    public void AC46_2_GPS付きのJPEGは元のバイト列のまま保存されextがjpgになる()
    {
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        using var dir = new TempDir();
        var jpeg = JpegWithGps(SkiaBytes.Jpeg(32, 24, Img.Solid(32, 24, 90, 120, 200)));
        CtrlO(app, dir.Write("gps.jpg", jpeg));
        Check.True(app.Ui(() => Session(app).HasDocument), "GPS 付きの JPEG を取り込めない");
        var path = dir.PathOf("p.glint");
        CtrlShiftS(app, path);

        Check.SequenceEqual(jpeg, Entry(path, GlintProjectFile.AssetEntryName(Sha(jpeg))), "元のファイルと完全に一致する");
        using var session = JsonDocument.Parse(Entry(path, "session.json"));
        var a = session.RootElement.GetProperty("assets")[0];
        Check.Equal("jpg", a.GetProperty("ext").GetString(), "ext");
        Check.Equal((long)jpeg.Length, a.GetProperty("bytes").GetInt64(), "bytes");
        Check.Equal(Sha(jpeg), a.GetProperty("sha256").GetString(), "sha256");
    }

    [Test]
    public void AC46_2_クリップボードのDIBとPNGとサンプルのext_DIBは開くと同じ画素()
    {
        using var dir = new TempDir();
        using (var app = TestApp.Start(clock: FakeClock.ForPhase5()))
        {
            var dib = Dib.Build(40, 2, 2, 24, 0, (_, _) => 10u << 16 | 20u << 8 | 30u);
            Paste(app, new MemoryImportData().With(ImportFormatNames.Dib, dib));
            var path = dir.PathOf("dib.glint");
            CtrlShiftS(app, path);
            var sha = app.Ui(() => Session(app).Document!.Assets.Single().Sha256);
            Check.Equal("dib", app.Ui(() => Session(app).Document!.Assets.Single().Ext), "DIB の ext");
            Check.SequenceEqual(dib, Entry(path, GlintProjectFile.AssetEntryName(sha)), "読み取った DIB のバイト列");
            var before = app.Ui(() => app.Shell.ImportTarget.Images[sha].Pixels);
            var json = app.Ui(() => Session(app).Json);

            // 別のアプリで開く
            using var other = TestApp.Start(clock: FakeClock.ForPhase5());
            CtrlO(other, path);
            Check.Equal(json, other.Ui(() => Session(other).Json), "開いた文書");
            Check.SequenceEqual(before, other.Ui(() => other.Shell.ImportTarget.Images[sha].Pixels), "取り込み直後と同じ画素");
        }
        using (var app = TestApp.Start(clock: FakeClock.ForPhase5()))
        {
            Paste(app, new MemoryImportData().With(ImportFormatNames.Png, PngB));
            var path = dir.PathOf("png.glint");
            CtrlShiftS(app, path);
            Check.Equal("png", app.Ui(() => Session(app).Document!.Assets.Single().Ext), "クリップボードの PNG の ext");
            Check.SequenceEqual(PngB, Entry(path, GlintProjectFile.AssetEntryName(Sha(PngB))), "PNG のバイト列");
        }
        using (var app = TestApp.Start(clock: FakeClock.ForPhase5()))
        {
            app.Ui(() => { _ = M<ScreensModule>(app).Imports.SampleAsync(); });
            WaitImports(app);
            var path = dir.PathOf("sample.glint");
            CtrlShiftS(app, path);
            var sample = SampleImage.CreatePng();
            Check.Equal("png", app.Ui(() => Session(app).Document!.Assets.Single().Ext), "サンプルの ext");
            Check.SequenceEqual(sample, Entry(path, GlintProjectFile.AssetEntryName(Sha(sample))), "生成した画像の PNG");
        }
    }

    // ---------------------------------------- AC-46 の (3)・AC-47 の (1)(2)

    /// <summary>AC-47 の (1) の 4 枚の文書(P は 64×48 の JPEG。E の大きさは合成と同じ 64×48)。</summary>
    private static (byte[] P, byte[] Q) FourLayers(TestApp app, TempDir dir)
    {
        var p = SkiaBytes.Jpeg(64, 48, Img.Pattern(64, 48, alpha: false));
        var q = SkiaBytes.Png(30, 20, Img.Pattern(30, 20, alpha: true));
        CtrlO(app, dir.Write("P.jpg", p), dir.Write("Q.png", q));
        app.Ui(() =>
        {
            Check.True(Session(app).AddEmptyLayer().Changed, "空のレイヤーを足す");
            Check.True(Session(app).SetLayerFill(2, "#FF000080").Changed, "塗り");
        });
        CtrlO(app, dir.Write("R.jpg", p));
        Check.Equal(4, app.Ui(() => Session(app).Document!.Layers.Count), "4 枚");
        return (p, q);
    }

    [Test]
    public void AC47_1_4枚の文書は同じ画像を1つのアセットにしキーの順と圧縮が決まりのとおり()
    {
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        using var dir = new TempDir();
        var (p, q) = FourLayers(app, dir);
        var path = dir.PathOf("four.glint");
        CtrlShiftS(app, path);
        Check.Equal(NoticeKeys.ProjectSaved, Notice(app), "project.saved");

        var pEntry = GlintProjectFile.AssetEntryName(Sha(p));
        var qEntry = GlintProjectFile.AssetEntryName(Sha(q));
        Check.SequenceEqual(Sorted("manifest.json", "session.json", pEntry, qEntry), EntryNames(path), "エントリーは 4 つ");
        var methods = Methods(path);
        Check.Equal(0, methods[pEntry], "P は Stored");
        Check.Equal(0, methods[qEntry], "Q は Stored");
        Check.Equal(8, methods["manifest.json"], "manifest.json");
        Check.Equal(8, methods["session.json"], "session.json");
        Check.SequenceEqual(p, Entry(path, pEntry), "P のバイト列");
        Check.SequenceEqual(q, Entry(path, qEntry), "Q のバイト列");

        using var session = JsonDocument.Parse(Entry(path, "session.json"));
        var assets = session.RootElement.GetProperty("assets");
        Check.Equal(2, assets.GetArrayLength(), "assets は 2 項目");
        Check.Equal(Sha(p), assets[0].GetProperty("sha256").GetString(), "最初に使われた P");
        Check.Equal("jpg", assets[0].GetProperty("ext").GetString(), "P の ext");
        Check.Equal((long)p.Length, assets[0].GetProperty("bytes").GetInt64(), "P の bytes");
        Check.Equal(Sha(q), assets[1].GetProperty("sha256").GetString(), "次に使われた Q");
        Check.Equal("png", assets[1].GetProperty("ext").GetString(), "Q の ext");
        Check.Equal((long)q.Length, assets[1].GetProperty("bytes").GetInt64(), "Q の bytes");

        var layers = session.RootElement.GetProperty("layers");
        Check.Equal(4, layers.GetArrayLength(), "layers は 4 個");
        string[] imageKeys = ["name", "kind", "asset", "visible", "opacity", "blend", "place", "lookStrength", "stack"];
        string[] emptyKeys = ["name", "kind", "size", "fill", "visible", "opacity", "blend", "place", "lookStrength", "stack"];
        Check.SequenceEqual(imageKeys, layers[0].EnumerateObject().Select(k => k.Name).ToArray(), "image のキーの順");
        Check.Equal(Sha(p), layers[0].GetProperty("asset").GetString(), "先頭は土台 P");
        Check.SequenceEqual(emptyKeys, layers[2].EnumerateObject().Select(k => k.Name).ToArray(), "empty のキーの順");
        Check.Equal(64, layers[2].GetProperty("size").GetProperty("w").GetInt32(), "size.w");
        Check.Equal(48, layers[2].GetProperty("size").GetProperty("h").GetInt32(), "size.h");
        Check.Equal("#FF000080", layers[2].GetProperty("fill").GetString(), "fill");
        Check.Equal(Sha(p), layers[3].GetProperty("asset").GetString(), "R は P と同じアセット");
    }

    private static string ExportedGifSha(TestApp app)
    {
        var band = M<ExportBandModule>(app);
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, TimeSpan.FromSeconds(60)), "本物の中身が入らない");
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, TimeSpan.FromSeconds(120)), "書き出しが終わらない");
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "状態");
        Check.Equal("gif", app.Ui(() => band.Auto.Entry!.Format), "GIF を書き出す");
        return Sha(File.ReadAllBytes(app.Ui(() => band.Auto.Entry!.Path)));
    }

    [Test]
    public void AC46_3_AC47_2_編集を保存して開くとJSONとGIFのSHA256が一致し履歴が空でlastOpenDirが更新される()
    {
        using var dir = new TempDir();
        string json, gif, path = dir.PathOf("edit.glint");
        using (var app = TestApp.Start(clock: FakeClock.ForPhase5(), gpu: GpuAdapterRequest.Warp))
        {
            FourLayers(app, dir);
            app.Ui(() =>
            {
                var s = Session(app);
                var looks = BuiltInLooks.Build(app.Shell.Catalog);
                s.SetLayerName(1, "ステッカー");
                s.SetLayerOpacity(1, 80);
                s.SetLayerBlend(1, BlendMode.Multiply);
                s.SetLayerPlacement(1, 10.25, -4.5, 60.5);
                s.SetLookStrength(Target.OfLayer(1), 70);
                s.SetLayerVisible(3, false);
                Check.True(s.ApplyLook(Target.OfLayer(0), looks.Single(l => l.Key == "vivid").Effects).Changed, "origin が look のスタック");
                Check.True(s.AddEffect(Target.OfLayer(1), "exposure").Changed, "origin が user のスタック");
                Check.True(s.AddEffect(Target.OfLayer(1), "spin").Changed, "動きのスタック");
                Check.True(s.AddEffect(Target.All, "exposure").Changed, "全体のスタック");
                s.SetEffectParam(Target.All, 0, "ev", new NumberValue(-0.1));
                s.SetLookStrength(Target.All, 60);
                s.RerollSeed();
                s.SetLength10(5);
                s.SetFps(20);
                s.SetOutputFormat("gif");
                s.SetOutputSize("long", 64);
                s.Select(Target.OfLayer(2));
            });
            json = app.Ui(() => Session(app).Json)!;
            Check.True(json.Contains("\"origin\":\"look\"", StringComparison.Ordinal) || json.Contains("\"origin\": \"look\"", StringComparison.Ordinal), "look の行がある");
            Check.True(json.Contains("\"origin\":\"user\"", StringComparison.Ordinal) || json.Contains("\"origin\": \"user\"", StringComparison.Ordinal), "user の行がある");
            gif = ExportedGifSha(app);
            CtrlShiftS(app, path);
            Check.Equal(NoticeKeys.ProjectSaved, Notice(app), "project.saved");
        }

        using var fresh = TestApp.Start(clock: FakeClock.ForPhase5(), gpu: GpuAdapterRequest.Warp);
        var other = Path.Combine(dir.Path, "別のフォルダ");
        Directory.CreateDirectory(other);
        var moved = Path.Combine(other, "edit.glint");
        File.Copy(path, moved);
        CtrlO(fresh, moved);
        Check.Equal(json, fresh.Ui(() => Session(fresh).Json), "EditorSession の JSON");
        Check.Equal(0, fresh.Ui(() => Session(fresh).UndoCount), "Undo の履歴が空");
        Check.Equal(Target.OfLayer(2), fresh.Ui(() => Session(fresh).Selected), "選んでいる対象");
        Check.True(fresh.Ui(() => !Session(fresh).IsUndelivered), "開いた直後は未渡しでない");
        Check.Equal(other, fresh.Ui(() => fresh.Shell.Settings.LastOpenDir), "lastOpenDir");
        Check.Equal(gif, ExportedGifSha(fresh), "同じ PC・同じビルドの GIF の SHA-256");
    }

    [Test]
    public void AC47_2_32枚の文書はエントリーが34個になり開くと32枚に戻る()
    {
        using var dir = new TempDir();
        var paths = Enumerable.Range(0, 32).Select(i => dir.Write("l" + i + ".png", SkiaBytes.Png(4, 4, Img.Solid(4, 4, (byte)(i * 7 + 3), 10, 10)))).ToArray();
        var save = dir.PathOf("many.glint");
        string json;
        using (var app = TestApp.Start(clock: FakeClock.ForPhase5()))
        {
            CtrlO(app, paths);
            Check.Equal(32, app.Ui(() => Session(app).Document!.Layers.Count), "32 枚");
            CtrlShiftS(app, save);
            json = app.Ui(() => Session(app).Json)!;
        }
        Check.Equal(34, EntryNames(save).Length, "エントリーは 34 個");
        using var fresh = TestApp.Start(clock: FakeClock.ForPhase5());
        CtrlO(fresh, save);
        Check.Equal(32, fresh.Ui(() => Session(fresh).Document?.Layers.Count), "32 枚のまま");
        Check.Equal(json, fresh.Ui(() => Session(fresh).Json), "JSON");
    }

    // ---------------------------------------- AC-46 の (4)

    [Test]
    public void AC46_4_CtrlOとドロップと起動引数の3つの経路でglintが開く()
    {
        using var dir = new TempDir();
        var file = dir.Write("p.glint", OneImageProject());
        using (var app = TestApp.Start())
        {
            CtrlO(app, file);
            Check.True(app.Ui(() => Session(app).HasDocument), "Ctrl+O");
            Check.Equal(dir.Path, app.Ui(() => app.Shell.Settings.LastOpenDir), "Ctrl+O の lastOpenDir");
        }
        using (var app = TestApp.Start())
        {
            var m = M<ScreensModule>(app);
            app.Ui(() => { _ = m.Imports.DropAsync(new DataObject(DataFormats.FileDrop, new[] { file })); });
            WaitImports(app);
            Check.True(app.Ui(() => Session(app).HasDocument), "ドロップ");
            Check.Equal(dir.Path, app.Ui(() => app.Shell.Settings.LastOpenDir), "ドロップの lastOpenDir");
        }
        using (var app = TestApp.Start())
        {
            var m = M<ScreensModule>(app);
            app.Ui(() => { _ = m.ImportStartupArgs([file, dir.PathOf("無い.png")]); });
            WaitImports(app);
            Check.True(app.Ui(() => Session(app).HasDocument), "起動引数");
            Check.Equal(dir.Path, app.Ui(() => app.Shell.Settings.LastOpenDir), "起動引数の lastOpenDir");
        }
    }

    [Test]
    public void AC46_4_判定は拡張子でなく先頭バイトで行う()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start();
        // 拡張子が .glint の PNG は画像
        CtrlO(app, dir.Write("image.glint", PngB));
        Check.Equal(1, app.Ui(() => Session(app).Document?.Layers.Count), "画像として取り込む");
        Check.Equal(Sha(PngB), app.Ui(() => Session(app).Document!.Assets.Single().Sha256), "PNG の中身");
        Check.Equal("image", app.Ui(() => Session(app).Document!.Layers[0].Name), "ファイル名がレイヤーの名前");
        Check.Equal(0, app.Ui(() => Session(app).UndoCount), "最初の取り込みは履歴に積まない");

        // 拡張子が .png の .glint はプロジェクト(前の文書を置き換える)
        CtrlO(app, dir.Write("project.png", OneImageProject()));
        Check.Equal(Sha(PngA), app.Ui(() => Session(app).Document!.Assets.Single().Sha256), "プロジェクトとして開く");
        Check.Equal("p", app.Ui(() => Session(app).Document!.Layers[0].Name), "session.json の名前");

        // 先頭が ZIP でない .glint は画像として読み、非対応になる
        var json = app.Ui(() => Session(app).Json);
        CtrlO(app, dir.Write("text.glint", "not a zip file at all"u8.ToArray()));
        Check.Equal(NoticeKeys.ImportUnsupported, Notice(app), "非対応");
        Check.Equal(json, app.Ui(() => Session(app).Json), "文書は変わらない");

        // 複数のファイルの中の ZIP は読み飛ばす(FR-4)
        CtrlO(app, dir.PathOf("project.png"), dir.Write("b.png", PngB));
        Check.Equal(2, app.Ui(() => Session(app).Document!.Layers.Count), "画像だけを足す");
        Check.Equal(NoticeKeys.ImportMulti, Notice(app), "import.multi");
    }

    [Test]
    public void AC46_4_32枚の文書でも1個のglintは開く()
    {
        using var dir = new TempDir();
        var fake = new FakeFileSystem();
        using var app = TestApp.Start(fileSystem: fake);
        var paths = Enumerable.Range(0, 32).Select(i => dir.Write("l" + i + ".png", SkiaBytes.Png(4, 4, Img.Solid(4, 4, (byte)(i * 7 + 3), 10, 10)))).ToArray();
        CtrlO(app, paths);
        Check.Equal(32, app.Ui(() => Session(app).Document!.Layers.Count), "32 枚");
        // 満杯の文書に画像を 1 個: layer.full で、全体を読まない
        var png = dir.Write("more.png", PngB);
        CtrlO(app, png);
        Check.Equal(NoticeKeys.LayerFull, Notice(app), "layer.full");
        Check.Equal(32, app.Ui(() => Session(app).Document!.Layers.Count), "足さない");
        // 満杯の文書に .glint を 1 個: 開く
        var project = dir.Write("p.glint", OneImageProject());
        app.Ui(() => Session(app).MarkDelivered());
        CtrlO(app, project);
        Check.Equal(1, app.Ui(() => Session(app).Document!.Layers.Count), "開く");
    }

    [Test]
    public void INV5_読み込みと書き込みはUIスレッドの外で行う()
    {
        using var dir = new TempDir();
        var fake = new FakeFileSystem();
        var threads = new List<int>();
        var file = dir.Write("p.glint", OneImageProject());
        var saved = dir.PathOf("saved.glint");
        bool Note(string path)
        {
            if (string.Equals(Path.GetFileName(path), "p.glint", StringComparison.Ordinal) || path.EndsWith("saved.glint.tmp", StringComparison.Ordinal))
                lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            return false;
        }
        fake.FailOn(FileOps.OpenRead, when: Note);
        fake.FailOn(FileOps.Create, when: Note);
        using var app = TestApp.Start(fileSystem: fake);
        var ui = app.Ui(() => Environment.CurrentManagedThreadId);
        CtrlO(app, file);
        Check.True(app.Ui(() => Session(app).HasDocument), "開く");
        CtrlShiftS(app, saved);
        Check.True(File.Exists(saved), "保存");
        lock (threads)
        {
            Check.True(threads.Count >= 2, "読み書きを記録した");
            Check.True(threads.All(t => t != ui), "UI スレッドでファイルを読み書きした");
        }
    }

    // ---------------------------------------- AC-46 の (5)・E-44・E-45

    [Test]
    public void AC46_5_開けないファイルは通知を出して編集中の内容を変えない()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start();
        CtrlO(app, dir.Write("a.png", PngB));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        var json = app.Ui(() => Session(app).Json);
        var undo = app.Ui(() => Session(app).UndoCount);
        var good = OneImageProject();
        var session = "{\"schema\":1,\"assets\":[{\"sha256\":\"" + Sha(PngA) + "\",\"ext\":\"png\",\"bytes\":" + PngA.Length + "}],\"layers\":[{\"kind\":\"image\",\"asset\":\"" + Sha(PngA) + "\"}]}";
        var bad = new (string What, byte[] File, string Key)[]
        {
            ("version が 2", ZipOf(("manifest.json", ManifestV(2)), ("session.json", Encoding.UTF8.GetBytes(session)), (GlintProjectFile.AssetEntryName(Sha(PngA)), PngA)), NoticeKeys.ProjectTooNew),
            ("session.json が 4,000,001 バイト", ZipOf(("manifest.json", ManifestV(1)), ("session.json", Encoding.UTF8.GetBytes(session + new string(' ', 4_000_001 - session.Length))), (GlintProjectFile.AssetEntryName(Sha(PngA)), PngA)), NoticeKeys.ProjectInvalid),
            ("sha256 が合わない", ZipOf(("manifest.json", ManifestV(1)), ("session.json", Encoding.UTF8.GetBytes(session)), (GlintProjectFile.AssetEntryName(Sha(PngA)), PngA.Select((b, i) => i == PngA.Length - 1 ? (byte)(b ^ 1) : b).ToArray())), NoticeKeys.ProjectInvalid),
            ("ZIP が壊れている", good[..(good.Length / 2)], NoticeKeys.ProjectInvalid),
            ("エントリーが足りない", ZipOf(("manifest.json", ManifestV(1)), ("session.json", Encoding.UTF8.GetBytes(session))), NoticeKeys.ProjectInvalid),
            ("ソースをデコードできない", DecodeFails(), NoticeKeys.ProjectInvalid),
        };
        foreach (var (what, file, key) in bad)
        {
            app.Ui(() => app.Shell.NoticeBar.Close());
            CtrlO(app, dir.Write("bad.glint", file));
            Check.Equal(key, Notice(app), what);
            Check.Equal(json, app.Ui(() => Session(app).Json), what + " で文書が変わった");
            Check.Equal(undo, app.Ui(() => Session(app).UndoCount), what + " で履歴が変わった");
            Check.True(app.Ui(() => Session(app).IsUndelivered), what + " で未渡しが変わった");
            Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), what + " で確認が出た");
        }

        // 文書が無いときも通知が出てウェルカム画面のまま
        using var welcome = TestApp.Start();
        CtrlO(welcome, dir.PathOf("bad.glint"));
        Check.Equal(NoticeKeys.ProjectInvalid, Notice(welcome), "ウェルカム画面の通知");
        Check.True(welcome.Ui(() => !Session(welcome).HasDocument), "ウェルカム画面のまま");
    }

    private static byte[] DecodeFails()
    {
        var bad = "not an image"u8.ToArray();
        var json = "{\"schema\":1,\"assets\":[{\"sha256\":\"" + Sha(bad) + "\",\"ext\":\"png\",\"bytes\":" + bad.Length + "}],\"layers\":[{\"kind\":\"image\",\"asset\":\"" + Sha(bad) + "\"}]}";
        return ZipOf(("manifest.json", ManifestV(1)), ("session.json", Encoding.UTF8.GetBytes(json)), (GlintProjectFile.AssetEntryName(Sha(bad)), bad));
    }

    // ---------------------------------------- AC-56 の (2)(3) の通知

    private static string StackRows(string id, int n) =>
        string.Join(",", Enumerable.Range(0, n).Select(i => "{\"id\":\"" + id + "\",\"enabled\":true,\"origin\":\"user\",\"salt\":" + (i + 1) + ",\"amount\":100,\"params\":{}}"));

    [Test]
    public void AC56_2_3_読み込みのstack_fullとstack_unknown_effectが通知バーに出る()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start();
        var rows = StackRows("exposure", 65);
        CtrlO(app, dir.Write("full.glint", OneImageProject(",\"stack\":[" + rows + "]", "\"stack\":[" + rows + "]")));
        Check.Equal(NoticeKeys.StackFull, Notice(app), "stack.full");
        Check.Equal(64, app.Ui(() => Session(app).Document!.Layers[0].Stack.Count), "レイヤーは 64 個");
        Check.Equal(64, app.Ui(() => Session(app).Document!.All.Stack.Count), "全体は 64 個");

        CtrlO(app, dir.Write("unknown.glint", OneImageProject(",\"stack\":[" + StackRows("no_such_effect", 1) + "," + StackRows("exposure", 1) + "]")));
        Check.Equal(NoticeKeys.StackUnknownEffect, Notice(app), "stack.unknown_effect");
        Check.SequenceEqual(new[] { "exposure" }, app.Ui(() => Session(app).Document!.Layers[0].Stack.Select(r => r.Id).ToArray()), "ほかの行は読む");
    }

    // ---------------------------------------- AC-46 の (6)・FR-13・FR-43・E-20

    private static ReplaceConfirmView WaitConfirm(TestApp app)
    {
        Check.True(UiHost.WaitUntil(() => app.Shell.Dialogs.Top is ReplaceConfirmView, Wait), "確認ダイアログが出ない");
        return app.Ui(() => (ReplaceConfirmView)app.Shell.Dialogs.Top!);
    }

    private static void Choose(TestApp app, string label)
    {
        WaitConfirm(app);
        Uia.Invoke(Uia.Find(Uia.Root(app.Hwnd), label));
    }

    [Test]
    public void AC46_6_保存した直後は新規で確認が出ず保存したあとに編集すると出る()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        CtrlO(app, dir.Write("a.png", PngA));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        CtrlShiftS(app, dir.PathOf("p.glint"));

        Check.True(Key(app, System.Windows.Input.Key.N, ModifierKeys.Control), "Ctrl+N");
        Check.True(UiHost.WaitUntil(() => !Session(app).HasDocument, Wait), "確認なしで閉じる");
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "確認が出た");

        CtrlO(app, dir.Write("a2.png", PngA));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        CtrlShiftS(app, dir.PathOf("p.glint"));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 40));
        Key(app, System.Windows.Input.Key.N, ModifierKeys.Control);
        WaitConfirm(app);
        Choose(app, "キャンセル");
        Check.True(UiHost.WaitUntil(() => !app.Shell.Dialogs.IsOpen, Wait), "閉じる");
    }

    [Test]
    public void FR13_確認は3つのボタンで既定はキャンセルで幅に収まる()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        CtrlO(app, dir.Write("a.png", PngA));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        Key(app, System.Windows.Input.Key.N, ModifierKeys.Control);
        var v = WaitConfirm(app);
        UiHost.Idle();
        app.Ui(() =>
        {
            Check.SequenceEqual(new[] { ReplaceChoice.SaveAndReplace, ReplaceChoice.ReplaceWithoutSaving, ReplaceChoice.Cancel }, v.Buttons.Keys.ToList(), "ボタンの並び");
            Check.True(ReferenceEquals(v.DefaultButton, v.Buttons[ReplaceChoice.Cancel]) && v.DefaultButton.IsDefault, "既定はキャンセル");
            foreach (var b in v.Buttons.Values)
            {
                var right = b.TranslatePoint(new Point(b.ActualWidth, 0), v).X;
                var left = b.TranslatePoint(new Point(0, 0), v).X;
                Check.True(left >= 0 && right <= v.ActualWidth + 0.5, $"ボタンがダイアログからはみ出す: {left}〜{right} / {v.ActualWidth}");
            }
        });
        Choose(app, "キャンセル");
        Check.True(UiHost.WaitUntil(() => !app.Shell.Dialogs.IsOpen, Wait), "閉じる");
    }

    [Test]
    public void FR13_新規の保存して置き換えは保存できたときだけ閉じる()
    {
        using var dir = new TempDir();
        var fake = new FakeFileSystem();
        using var app = TestApp.Start(clock: FakeClock.ForPhase5(), fileSystem: fake);
        var m = M<ScreensModule>(app);
        CtrlO(app, dir.Write("a.png", PngA));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        var json = app.Ui(() => Session(app).Json);
        var path = dir.PathOf("saved.glint");

        // ダイアログを閉じたら置き換えず、編集画面へ戻る
        app.FileDialogs.SaveAnswers.Enqueue(null);
        Key(app, System.Windows.Input.Key.N, ModifierKeys.Control);
        Choose(app, "保存して置き換え");
        Check.True(UiHost.WaitUntil(() => !Projects(app).IsConfirming && !m.Imports.Gate.IsBusy, Wait), "戻らない");
        Check.Equal(json, app.Ui(() => Session(app).Json), "閉じたら文書が変わらない");
        Check.True(app.Ui(() => Session(app).IsUndelivered), "閉じたら未渡しのまま");

        // 保存に失敗したら(E-34)置き換えず、save.failed とログ export_fail
        fake.FailOn(FileOps.Write, when: p => p.EndsWith(".glint.tmp", StringComparison.Ordinal));
        app.FileDialogs.SaveAnswers.Enqueue(path);
        Key(app, System.Windows.Input.Key.N, ModifierKeys.Control);
        Choose(app, "保存して置き換え");
        Check.True(UiHost.WaitUntil(() => !Projects(app).IsConfirming && !m.Imports.Gate.IsBusy, Wait), "戻らない");
        Check.Equal(NoticeKeys.SaveFailed, Notice(app), "save.failed");
        Check.Equal(json, app.Ui(() => Session(app).Json), "失敗で文書が変わらない");
        Check.True(!File.Exists(path), "失敗で最終的な名前のファイルが無い");
        Check.True(!File.Exists(path + ".tmp"), "失敗で .tmp が残らない");
        Check.True(Logs(app).Contains("\"reason\":\"write_error\"", StringComparison.Ordinal), "export_fail の write_error");

        // 成功したら保存してから閉じる
        fake.ClearFailures();
        app.FileDialogs.SaveAnswers.Enqueue(path);
        Key(app, System.Windows.Input.Key.N, ModifierKeys.Control);
        Choose(app, "保存して置き換え");
        Check.True(UiHost.WaitUntil(() => !Session(app).HasDocument, Wait), "保存のあとで閉じる");
        Check.True(File.Exists(path), "保存した");
        CtrlO(app, path);
        Check.Equal(json, app.Ui(() => Session(app).Json), "置き換える前の編集が保存されている");
    }

    [Test]
    public void FR13_glintを開くときも未渡しなら確認しキャンセルは何も変えず保存して置き換えは保存してから開く()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        var m = M<ScreensModule>(app);
        var project = dir.Write("open.glint", OneImageProject());

        // 未渡しでなければ確認しない
        CtrlO(app, dir.Write("b.png", PngB));
        app.Ui(() => { _ = m.Imports.OpenPathsAsync([project]); });
        WaitImports(app);
        Check.Equal(Sha(PngA), app.Ui(() => Session(app).Document!.Assets.Single().Sha256), "確認なしで開く");
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "確認が出た");

        // 未渡しのとき: キャンセル
        CtrlO(app, dir.Write("b2.png", PngB));
        Check.Equal(2, app.Ui(() => Session(app).Document!.Layers.Count), "レイヤーを足した");
        var json = app.Ui(() => Session(app).Json);
        var undo = app.Ui(() => Session(app).UndoCount);
        app.Ui(() => { _ = m.Imports.OpenPathsAsync([project]); });
        WaitConfirm(app);
        Check.True(app.Ui(() => m.Imports.IsImporting && m.Imports.Gate.IsBusy), "確認の間は門の中");
        Check.True(app.Ui(() => m.IsConfirmingReplace), "確認中");
        // 確認中の新しい操作は無視する(E-20)
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, PngA);
        app.Ui(() => { _ = m.Imports.PasteAsync(); });
        app.Ui(() => { _ = m.Imports.DropAsync(new DataObject(DataFormats.FileDrop, new[] { project })); });
        app.Ui(() => { _ = m.Imports.OpenPathsAsync([project]); });
        app.Ui(() => { _ = m.ImportStartupArgs([project]); });
        app.Ui(() => { _ = m.RequestNewAsync(); });
        Key(app, System.Windows.Input.Key.S, CtrlShift);
        UiHost.Idle();
        Check.Equal(json, app.Ui(() => Session(app).Json), "確認中の操作で文書が変わった");
        Check.True(app.Ui(() => app.Shell.Dialogs.Top is ReplaceConfirmView), "確認は 1 つのまま");
        Choose(app, "キャンセル");
        WaitImports(app);
        Check.Equal(json, app.Ui(() => Session(app).Json), "キャンセルで文書が変わった");
        Check.Equal(undo, app.Ui(() => Session(app).UndoCount), "キャンセルで履歴が変わった");

        // 保存して置き換え: ダイアログを閉じたら開かない
        app.FileDialogs.SaveAnswers.Enqueue(null);
        app.Ui(() => { _ = m.Imports.OpenPathsAsync([project]); });
        Choose(app, "保存して置き換え");
        WaitImports(app);
        Check.Equal(json, app.Ui(() => Session(app).Json), "保存ダイアログを閉じたら開かない");

        // 保存して置き換え: 保存してから開く
        var saved = dir.PathOf("before.glint");
        app.FileDialogs.SaveAnswers.Enqueue(saved);
        app.Ui(() => { _ = m.Imports.OpenPathsAsync([project]); });
        Choose(app, "保存して置き換え");
        WaitImports(app);
        Check.True(File.Exists(saved), "保存した");
        Check.Equal(Sha(PngA), app.Ui(() => Session(app).Document!.Assets.Single().Sha256), "保存のあとで開く");
        Check.Equal(0, app.Ui(() => Session(app).UndoCount), "履歴が空");

        // 保存せず置き換え
        CtrlO(app, dir.PathOf("b.png"));
        app.Ui(() => { _ = m.Imports.OpenPathsAsync([project]); });
        Choose(app, "保存せず置き換え");
        WaitImports(app);
        Check.Equal(1, app.Ui(() => Session(app).Document!.Layers.Count), "保存せず開く");
    }

    [Test]
    public void FR13_開けないファイルでは確認を出さない()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start();
        CtrlO(app, dir.Write("a.png", PngB));
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        CtrlO(app, dir.Write("bad.glint", DecodeFails()));
        Check.Equal(NoticeKeys.ProjectInvalid, Notice(app), "project.invalid");
        Check.True(app.Ui(() => !app.Shell.Dialogs.IsOpen), "確認が出た");
    }

    // ---------------------------------------- E-34・INV-2

    [Test]
    public void E34_保存先が取り込んだ画像と同じパスなら何も書かずsave_failed()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start(clock: FakeClock.ForPhase5());
        var a = dir.Write("Source.png", PngA);
        CtrlO(app, a);
        foreach (var same in new[] { a, Path.Combine(dir.Path, "SOURCE.PNG") })
        {
            app.Ui(() => app.Shell.NoticeBar.Close());
            CtrlShiftS(app, same);
            Check.Equal(NoticeKeys.SaveFailed, Notice(app), "save.failed: " + same);
            Check.SequenceEqual(PngA, File.ReadAllBytes(a), "取り込んだファイルが変わらない");
            Check.True(!File.Exists(a + ".tmp"), ".tmp が残らない");
        }
    }

    [Test]
    public void FR13_書き込みの間に編集したら未渡しのまま()
    {
        using var dir = new TempDir();
        var fake = new FakeFileSystem();
        using var hold = new ManualResetEventSlim(false);
        using var app = TestApp.Start(clock: FakeClock.ForPhase5(), fileSystem: fake);
        CtrlO(app, dir.Write("a.png", PngA));
        fake.Observer = (op, p) =>
        {
            if (op == FileOps.Create && p.EndsWith(".glint.tmp", StringComparison.Ordinal)) hold.Wait(TimeSpan.FromSeconds(20));
        };
        app.FileDialogs.SaveAnswers.Enqueue(dir.PathOf("p.glint"));
        Key(app, System.Windows.Input.Key.S, CtrlShift);
        Check.True(app.Ui(() => Projects(app).IsSaving), "書き込み中");
        app.Ui(() => Session(app).SetLookStrength(Target.OfLayer(0), 50));
        hold.Set();
        Check.True(UiHost.WaitUntil(() => !Projects(app).IsSaving, Wait), "保存が終わらない");
        Check.Equal(NoticeKeys.ProjectSaved, Notice(app), "保存はできた");
        Check.True(app.Ui(() => Session(app).IsUndelivered), "書き込みの間の編集は未渡しのまま");
    }
}
