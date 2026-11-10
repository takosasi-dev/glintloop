// §9.11 の 2 の lastExport と defaultFormat: 新しい文書が引き継ぐこと、出力設定が変わるたびに書かれ 500 ms 後に 1 回保存されること、設定画面と初期化の扱い、範囲の直し、.glint を開いたときの扱いを調べる。
// AC-52 の通しは SuccessAc52Tests、「文書に入る」「lastExport に書かれる」の最初の確かめは AC52_0_ が持つ。ここはその周りの決めを、GPU を使わない画面の外のアプリで調べる。
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using GlintLoop.Codec.Import;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Edge;

public sealed class LastExportTests
{
    private static readonly byte[] Png = SkiaBytes.Png(32, 32, Img.Pattern(32, 32, alpha: false));

    private static TestApp StartWith(string settingsJson) => TestApp.Start(prepare: p =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
        File.WriteAllText(p.SettingsFile, settingsJson, new UTF8Encoding(false));
    });

    /// <summary>文書を閉じてから貼り付けて、新しい文書の出力設定を返す。</summary>
    private static OutputSettings NewDocumentByPaste(TestApp app)
    {
        app.Ui(() => app.Shell.Session.CloseDocument());
        app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, Png);
        Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
        WaitImports(app);
        return app.Ui(() => app.Shell.Session.Document!.Output);
    }

    private static JsonElement SavedLastExport(TestApp app)
    {
        using var saved = JsonDocument.Parse(File.ReadAllBytes(app.Paths.SettingsFile));
        return saved.RootElement.GetProperty("lastExport").Clone();
    }

    private const string Gif320 = "\"lastExport\":{\"format\":\"gif\",\"size\":{\"mode\":\"long\",\"value\":320},\"limit\":{\"kind\":\"preset\",\"key\":\"chat_small\"}}";

    [Test]
    public void S9_11_2_lastExportが空なら形式はdefaultFormatでサイズはautoで上限はnoneで既定のoptions()
    {
        using var app = StartWith("{\"schema\":1,\"defaultFormat\":\"webp\"}");
        var want = OutputSettings.Default with { Format = ExportFormat.Webp };
        Check.Equal(want, NewDocumentByPaste(app), "貼り付けの文書");
        Check.Equal("auto", want.Size.Mode, "前提: サイズは auto");
        Check.Equal("none", want.Limit.Kind, "前提: 上限は none");
        Check.True(app.Ui(() => app.Shell.Settings.LastExport) is null, "文書を作っただけで lastExport は書かない");
        app.Ui(() =>
        {
            app.Shell.Session.CloseDocument();
            app.Shell.Session.NewEmptyCanvas(64, 64);
            Check.Equal(want, app.Shell.Session.Document!.Output, "空のキャンバスの文書");
        });
    }

    [Test]
    public void S9_11_2_既定のdefaultFormatはgifで出力設定の既定と同じ()
    {
        using var app = TestApp.Start();
        Check.Equal(OutputSettings.Default, NewDocumentByPaste(app), "既定の設定の新しい文書");
        Check.Equal(ExportFormat.Gif, OutputSettings.Default.Format, "前提: 既定の形式");
    }

    [Test]
    public void S9_11_2_出力設定の変更はlastExportへ書かれ500ms後に1回保存され次の文書と再起動後の文書が引き継ぐ()
    {
        var app = TestApp.Start();
        try
        {
            NewDocumentByPaste(app);
            var saves = app.Ui(() => app.Shell.SettingsStore.SaveCount);
            var want = app.Ui(() =>
            {
                var s = app.Shell.Session;
                Check.True(s.SetOutputFormat(ExportFormat.Jpeg).Changed, "形式");
                Check.True(s.SetOutputSize("width", 640).Changed, "サイズ");
                Check.True(s.SetOutputLimit(new OutputLimit("bytes", null, 5000)).Changed, "上限");
                Check.True(s.SetJpegOptions(new JpegOptions(55, "#102030")).Changed, "JPEG");
                Check.True(s.SetWebpOptions(new WebpOptions(33, true)).Changed, "WebP");
                Check.True(s.SetApngPalette(true).Changed, "APNG");
                Check.True(s.SetGifOptions(new GifOptions(16, "none", "#ABCDEF", false)).Changed, "GIF");
                return s.Document!.Output;
            });
            UiHost.Idle();
            app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(499)));
            Check.Equal(saves, app.Ui(() => app.Shell.SettingsStore.SaveCount), "499 ms では保存しない");
            app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(1)));
            Check.Equal(saves + 1, app.Ui(() => app.Shell.SettingsStore.SaveCount), "500 ms で 1 回だけ保存する");

            var file = SavedLastExport(app);
            Check.Equal("jpeg", file.GetProperty("format").GetString(), "ファイルの format");
            Check.Equal("width", file.GetProperty("size").GetProperty("mode").GetString(), "ファイルの size.mode");
            Check.Equal(640.0, file.GetProperty("size").GetProperty("value").GetDouble(), "ファイルの size.value");
            Check.Equal(5000L, file.GetProperty("limit").GetProperty("bytes").GetInt64(), "ファイルの limit.bytes");
            Check.Equal(55, file.GetProperty("options").GetProperty("jpeg").GetProperty("quality").GetInt32(), "ファイルの options.jpeg.quality");

            Check.Equal(want, NewDocumentByPaste(app), "次の文書");
            var next = app.Restart();
            app = next;
            Check.Equal(want, NewDocumentByPaste(app), "再起動後の文書");
        }
        finally
        {
            app.Dispose();
        }
    }

    [Test]
    public void S9_11_2_UndoとRedoで変わった出力設定もlastExportへ書く()
    {
        using var app = TestApp.Start();
        NewDocumentByPaste(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.SetOutputFormat(ExportFormat.Png);
            s.SetOutputFormat(ExportFormat.Webp);
            Check.Equal("webp", (string?)app.Shell.Settings.LastExport!["format"], "変更のあと");
            Check.True(s.Undo(), "Undo");
            Check.Equal("png", (string?)app.Shell.Settings.LastExport!["format"], "Undo のあと");
            Check.True(s.Redo(), "Redo");
            Check.Equal("webp", (string?)app.Shell.Settings.LastExport!["format"], "Redo のあと");
        });
    }

    [Test]
    public void S9_11_2_出力設定と関係のない編集ではlastExportを書かない()
    {
        using var app = TestApp.Start();
        NewDocumentByPaste(app);
        app.Ui(() =>
        {
            Check.True(app.Shell.Session.SetLength10(30).Changed, "時間の変更");
            Check.True(app.Shell.Settings.LastExport is null, "時間の変更で lastExport が書かれた");
        });
    }

    [Test]
    public void S9_11_2_設定画面で形式を変えると次の文書がその形式で始まり初期化では書き換えない()
    {
        using var app = StartWith("{\"schema\":1," + Gif320 + "}");
        NewDocumentByPaste(app);
        app.Ui(() => app.Shell.Settings.SetDefaultFormatFromSettingsScreen(ExportFormatSetting.Png));
        var o = NewDocumentByPaste(app);
        Check.Equal(ExportFormat.Png, o.Format, "設定画面で変えた形式で始まる");
        Check.Equal("long", o.Size.Mode, "ほかは lastExport のまま");
        Check.Equal("chat_small", o.Limit.Key, "ほかは lastExport のまま");

        app.Ui(() => app.Shell.Settings.ResetToDefaults());
        Check.Equal(ExportFormatSetting.Gif, app.Ui(() => app.Shell.Settings.DefaultFormat), "前提: defaultFormat は既定に戻る");
        Check.Equal(ExportFormat.Png, NewDocumentByPaste(app).Format, "初期化では lastExport.format を書き換えない");
    }

    [Test]
    public void S9_11_2_lastExportが空のとき設定画面で形式を変えるとdefaultFormatだけが変わり次の文書に効く()
    {
        using var app = TestApp.Start();
        app.Ui(() => app.Shell.Settings.SetDefaultFormatFromSettingsScreen(ExportFormatSetting.Apng));
        Check.True(app.Ui(() => app.Shell.Settings.LastExport) is null, "lastExport は空のまま");
        Check.Equal(ExportFormat.Apng, NewDocumentByPaste(app).Format, "defaultFormat で始まる");
    }

    [Test]
    public void S9_11_2_lastExportの範囲外は端に直り知らない値は既定で知らない形式はdefaultFormatになる()
    {
        const string bad = "\"lastExport\":{\"format\":\"bmp\",\"size\":{\"mode\":\"long\",\"value\":99999},\"limit\":{\"kind\":\"bytes\",\"bytes\":-5},"
            + "\"options\":{\"gif\":{\"colors\":9999,\"dither\":\"x\",\"matte\":\"red\"},\"jpeg\":{\"quality\":0,\"background\":\"nope\"},\"webp\":{\"quality\":500},\"apng\":{\"loops\":9}}}";
        using var app = StartWith("{\"schema\":1,\"defaultFormat\":\"jpeg\"," + bad + "}");
        var o = NewDocumentByPaste(app);
        var d = OutputSettings.Default;
        Check.Equal(ExportFormat.Jpeg, o.Format, "知らない形式");
        Check.Equal(new OutputSize("long", OutputSettingsJson.MaxSizePx), o.Size, "サイズ");
        Check.Equal(new OutputLimit("bytes", null, OutputSettingsJson.MinLimitBytes), o.Limit, "上限");
        Check.Equal(new GifOptions(OutputSettingsJson.MaxColors, d.Gif.Dither, null, d.Gif.Diff), o.Gif, "GIF");
        Check.Equal(new JpegOptions(OutputSettingsJson.MinQuality, d.Jpeg.Background), o.Jpeg, "JPEG");
        Check.Equal(OutputSettingsJson.MaxQuality, o.Webp.Quality, "WebP");
        Check.Equal(OutputSettingsJson.MaxApngLoops, o.Apng.Loops, "APNG");
    }

    [Test]
    public void S9_11_2_lastExportに書いた形はそのまま読み戻せる()
    {
        var o = new OutputSettings(ExportFormat.Apng, new OutputSize("scale", 150), new OutputLimit("preset", "chat_small", null),
            new GifOptions(32, "error", "#112233", false), new JpegOptions(70, "#445566"), new WebpOptions(20, true), new ApngOptions(true, 3));
        var json = OutputSettingsJson.ToJsonObject(o);
        Check.Equal(o, OutputSettingsJson.ForNewDocument(json, ExportFormat.Gif), "読み戻した出力設定");
        Check.True(json["options"]?["apng"]?["loops"] is JsonValue, "§9.11 の output の形");
        Check.Equal(3, (int?)json["options"]?["apng"]?["loops"], "apng.loops は 1〜4 をそのまま書く");
    }

    // ---------------------------------------------------------------- .glint を開く

    private static byte[] Project(string outputJson)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Png));
        var session = "{\"schema\":1,\"assets\":[{\"sha256\":\"" + sha + "\",\"ext\":\"png\",\"bytes\":" + Png.Length + "}],"
            + "\"layers\":[{\"name\":\"L\",\"kind\":\"image\",\"asset\":\"" + sha + "\",\"stack\":[]}],\"output\":" + outputJson + "}";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
            Add("manifest.json", Encoding.UTF8.GetBytes("{\"format\":\"glint\",\"version\":1}"));
            Add("session.json", Encoding.UTF8.GetBytes(session));
            Add(GlintProjectFile.AssetEntryName(sha), Png);
        }
        return ms.ToArray();
    }

    [Test]
    public void S9_11_2_glintを開くと文書の出力設定のままでlastExportを書かず変えたときだけ書く()
    {
        using var app = StartWith("{\"schema\":1," + Gif320 + "}");
        using var dir = new TempDir();
        var before = app.Ui(() => app.Shell.Settings.LastExport)!.ToJsonString();
        app.FileDialogs.OpenAnswers.Enqueue([dir.Write("p.glint", Project("{\"format\":\"webp\",\"size\":{\"mode\":\"long\",\"value\":100},\"limit\":{\"kind\":\"none\"}}"))]);
        Check.True(Key(app, System.Windows.Input.Key.O, ModifierKeys.Control), "Ctrl+O が処理されない");
        WaitImports(app);
        var o = app.Ui(() => app.Shell.Session.Document!.Output);
        Check.Equal(ExportFormat.Webp, o.Format, "文書の形式のまま");
        Check.Equal(new OutputSize("long", 100), o.Size, "文書のサイズのまま");
        Check.Equal(before, app.Ui(() => app.Shell.Settings.LastExport)!.ToJsonString(), "開いただけでは lastExport を書かない");
        // lastOpenDir の保存は走るので、保存の回数でなくファイルの中身を見る
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(1)));
        Check.Equal("gif", SavedLastExport(app).GetProperty("format").GetString(), "保存されたファイルの lastExport");
        Check.True(!app.Ui(() => app.Shell.Session.IsUndelivered), "開いた文書は未渡しでない(FR-13)");

        app.Ui(() => app.Shell.Session.SetOutputSize("long", 200));
        Check.Equal("webp", (string?)app.Ui(() => app.Shell.Settings.LastExport)!["format"], "開いた文書で変えると、その文書の出力設定を書く");
        Check.Equal(200, (int?)app.Ui(() => app.Shell.Settings.LastExport)!["size"]?["value"], "変えた値");
    }

    [Test]
    public void S9_11_2_glintを開いたあとの次の新しい文書は開く前のlastExportを引き継ぐ()
    {
        using var app = StartWith("{\"schema\":1," + Gif320 + "}");
        using var dir = new TempDir();
        app.FileDialogs.OpenAnswers.Enqueue([dir.Write("p.glint", Project("{\"format\":\"webp\"}"))]);
        Check.True(Key(app, System.Windows.Input.Key.O, ModifierKeys.Control), "Ctrl+O が処理されない");
        WaitImports(app);
        Check.Equal(ExportFormat.Webp, app.Ui(() => app.Shell.Session.Document!.Output.Format), "前提: 文書の形式");
        var o = NewDocumentByPaste(app);
        Check.Equal(ExportFormat.Gif, o.Format, "形式");
        Check.Equal("chat_small", o.Limit.Key, "上限");
    }
}
