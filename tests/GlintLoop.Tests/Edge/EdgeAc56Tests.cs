// AC-56: 編集・設定・ファイルの状態のエッジケース(E-17・E-18・E-22〜E-29・E-42〜E-45・E-48・E-49)の (1)〜(10) を、画面の外のアプリ・一時フォルダ・偽のファイルシステム・偽のモニター・UIA・WARP で調べる。
// すでに詳しく調べているテストがある部分は該当の項目の説明に名前を書き、AC-56 に書かれた数値と手順はここでも確かめる。
// (10) は「ほかの項目で詳しく調べる行」で、ここはそれらのテストが有ることだけを確かめる(通るかどうかは全体の実行で見る)。
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Looks;
using GlintLoop.App.Platform;
using GlintLoop.App.MotionTab;
using GlintLoop.App.Screens;
using GlintLoop.App.Shell;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Exports;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Gif;
using GlintLoop.Tests.Render;
using GlintLoop.Tests.Stack;
using GlintLoop.Tests.Ui;
using K = System.Windows.Input.Key;
using static GlintLoop.Tests.Ui.LayersTestKit;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc56Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static readonly (int, int, int, int) Gray = (128, 128, 128, 255);
    private static readonly (int, int, int, int) Red = (255, 0, 0, 255);
    private static readonly (int, int, int, int) Clear = (0, 0, 0, 0);

    private static EditorSession S(TestApp app) => app.Shell.Session;

    private static string Json(TestApp app) => app.Ui(() => S(app).Json) ?? throw new CheckFailedException("文書が無い");

    private static Notice? CurrentNotice(TestApp app) => app.Ui<Notice?>(() => app.Shell.NoticeBar.Current);

    /// <summary>通知が key・level で出ていることを確かめて閉じる(次の確認を前の通知と取り違えないため)。</summary>
    private static void ExpectNotice(TestApp app, string key, NoticeLevel level, string what)
    {
        var n = CurrentNotice(app);
        Check.Equal(key, n?.Key, what + " の通知");
        Check.Equal(level, n!.Level, what + " の通知の重さ");
        CloseNotice(app);
    }

    private static void CloseNotice(TestApp app)
    {
        app.Ui(() => app.Shell.NoticeBar.Close());
        Check.True(CurrentNotice(app) is null, "通知を閉じられない");
    }

    // ---------------------------------------- ログ(ファイルをそのまま読む)

    private sealed record LogLine(string Lvl, string Event, string? Effect, string? Key);

    private static List<LogLine> Logs(TestApp app)
    {
        var lines = new List<LogLine>();
        if (!Directory.Exists(app.Paths.LogsDir)) return lines;
        foreach (var f in Directory.GetFiles(app.Paths.LogsDir, "*.jsonl"))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                using var d = JsonDocument.Parse(line);
                var root = d.RootElement;
                string? Opt(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                lines.Add(new LogLine(Opt("lvl") ?? "", Opt("event") ?? "", Opt("effect"), Opt("key")));
            }
        }
        return lines;
    }

    /// <summary>ログの行数。起動の GPU の行が後から入らないよう、レンダラーができてから数える。</summary>
    private static int LogCount(TestApp app)
    {
        _ = RendererOf(app);
        return Logs(app).Count;
    }

    // ---------------------------------------- .glint を組み立てる(画像 1 つを各レイヤーで使う)

    private static readonly byte[] Png = SkiaBytes.Png(40, 30, Img.Solid(40, 30, 200, 100, 50));

    private static string ShaOfPng => Convert.ToHexStringLower(SHA256.HashData(Png));

    private static string Row(string id, int salt, string parameters = "{}") =>
        "{\"id\":\"" + id + "\",\"enabled\":true,\"origin\":\"user\",\"salt\":" + salt + ",\"amount\":100,\"params\":" + parameters + "}";

    private static string Rows(string id, int n) => string.Join(",", Enumerable.Range(1, n).Select(i => Row(id, i)));

    private static string SessionJson(string allStack, params string[] layerStacks)
    {
        var sha = ShaOfPng;
        var layers = layerStacks.Select((st, i) => "{\"name\":\"L" + i + "\",\"kind\":\"image\",\"asset\":\"" + sha + "\",\"stack\":[" + st + "]}");
        return "{\"schema\":1,\"all\":{\"stack\":[" + allStack + "]},\"assets\":[{\"sha256\":\"" + sha + "\",\"ext\":\"png\",\"bytes\":" + Png.Length + "}],"
            + "\"layers\":[" + string.Join(",", layers) + "]}";
    }

    private static byte[] Project(string sessionJson)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
            Add("manifest.json", Encoding.UTF8.GetBytes("{\"format\":\"glint\",\"version\":1}"));
            Add("session.json", Encoding.UTF8.GetBytes(sessionJson));
            Add(GlintProjectFile.AssetEntryName(ShaOfPng), Png);
        }
        return ms.ToArray();
    }

    private static void CtrlO(TestApp app, string path)
    {
        app.FileDialogs.OpenAnswers.Enqueue([path]);
        Check.True(Key(app, K.O, ModifierKeys.Control), "Ctrl+O が処理されない");
        EditingScreenKit.WaitImports(app);
    }

    // ================================================================ (1) 満杯(E-22)

    private static LookEffect LookRow(string id) => new(id, 100, EffectSchema.DefaultParams(StackTestKit.Def(id)));

    private static void AddFromEffectsTab(TestApp app, string id) => app.Ui(() =>
    {
        var item = StackTestKit.Module(app).EffectsTab.Items.Single(i => i.Def.Id == id).Item;
        Keyboard.Focus(item); // キーはフォーカスのある要素へ届く(前の操作のタイルに残っていると、そちらへ行く)
        Check.True(UiInput.Key(item, K.Enter), "エフェクトのタブの Enter を処理しない");
    });

    private static void PressTile(TestApp app, System.Windows.Controls.ListBoxItem tile) => app.Ui(() =>
    {
        Keyboard.Focus(tile);
        Check.True(UiInput.Key(tile, K.Enter), "タイルの Enter を処理しない");
    });

    private static LookTile LookTileOf(TestApp app, string key) => app.Ui(() => EditingScreenKit.M<LooksModule>(app).Gallery.Tiles.Single(t => t.Key == key));

    private static MotionTile MotionTileOf(TestApp app, string key) => app.Ui(() => EditingScreenKit.M<MotionTabModule>(app).View.Tiles.Single(t => t.Key == key));

    private static void AddMany(TestApp app, Target t, string id, int n) => app.Ui(() =>
    {
        for (var i = 0; i < n; i++) Check.True(S(app).AddEffect(t, id).Changed, $"{id} の {i + 1} 個目を足せない");
    });

    private static IReadOnlyList<StackEntry> StackOf(TestApp app, Target t) => app.Ui(() => S(app).Document!.StackOf(t));

    // 同じ数え方のセッション単体の検査は LookSessionAc26Tests・PresetsTests・StackAc23Tests にある。ここはタブとタイルの操作から AC-56 の個数で通す
    [Test]
    public void AC56_1_満杯のルックと追加は取り除く分を引いて数え取りやめるとJSONが変わらない()
    {
        using var app = TestApp.Start();
        StackTestKit.Import(app, 64, 48, Img.Solid(64, 48, 10, 20, 30), "base");
        var t = Target.OfLayer(0);
        Check.Equal(t, app.Ui(() => S(app).Selected), "土台を選んでいる");
        app.Ui(() => Check.True(S(app).ApplyLook(t, [LookRow("exposure"), LookRow("contrast")]).Changed, "ルック由来の 2 個を入れられない"));
        AddMany(app, t, "exposure", 62);
        Check.Equal(64, StackOf(app, t).Count, "準備: 62+2");
        Check.Equal(2, StackOf(app, t).Count(e => e.Origin == EffectOrigin.Look), "準備: ルック由来は 2 個");

        // エフェクトのタブから 1 個
        EditingScreenKit.SelectTab(app, LeftPaneTab.Effects);
        var before = Json(app);
        AddFromEffectsTab(app, "exposure");
        Check.Equal(before, Json(app), "タブからの追加で JSON が変わった");
        ExpectNotice(app, NoticeKeys.StackFull, NoticeLevel.Warn, "タブからの 65 個目");

        // ビビッド(3 個): 62+3=65 は取りやめ
        EditingScreenKit.SelectTab(app, LeftPaneTab.Looks);
        var vivid = LookTileOf(app, "vivid");
        Check.Equal(3, app.Ui(() => vivid.Effects.Count), "ビビッドは 3 個");
        PressTile(app, vivid);
        Check.Equal(before, Json(app), "62+3 の適用で JSON が変わった");
        ExpectNotice(app, NoticeKeys.StackFull, NoticeLevel.Warn, "62+3 の適用");

        // ユーザー 61 個とルック由来 2 個: 61+3=64 は適用
        var userRow = app.Ui(() => S(app).Document!.StackOf(t).ToList().FindLastIndex(e => e.Origin == EffectOrigin.User));
        app.Ui(() => Check.True(S(app).RemoveEffect(t, userRow).Changed, "1 行を消せない"));
        Check.Equal(63, StackOf(app, t).Count, "準備: 61+2");
        PressTile(app, vivid);
        Check.Equal(64, StackOf(app, t).Count, "61+3");
        Check.Equal(3, StackOf(app, t).Count(e => e.Origin == EffectOrigin.Look), "ルック由来が 3 個");
        Check.Equal(61, StackOf(app, t).Count(e => e.Origin == EffectOrigin.User), "ユーザーの行はそのまま");
        Check.True(CurrentNotice(app)?.Key != NoticeKeys.StackFull, "入ったのに stack.full が出た");
    }

    [Test]
    public void AC56_1_満杯のモーションの適用は取り除く分を引いて数え土台が64個でもほかの対象には足せる()
    {
        using var app = TestApp.Start();
        StackTestKit.Import(app, 64, 48, Img.Solid(64, 48, 10, 20, 30), "base");
        var t = Target.OfLayer(0);
        AddMany(app, t, "exposure", 63);
        AddMany(app, t, "spin", 1);
        Check.Equal(64, StackOf(app, t).Count, "準備: spin を含む合計 64 個");

        // フィルム(2 個): 63+2=65 は取りやめ
        EditingScreenKit.SelectTab(app, LeftPaneTab.Motion);
        var film = MotionTileOf(app, "film");
        Check.SequenceEqual(["grainmove", "leaks"], app.Ui(() => film.Effects.Select(e => e.Id).ToList()), "フィルムは 2 個");
        var before = Json(app);
        PressTile(app, film);
        Check.Equal(before, Json(app), "63+2 の適用で JSON が変わった");
        ExpectNotice(app, NoticeKeys.StackFull, NoticeLevel.Warn, "63+2 の適用");
        Check.Equal(1, StackOf(app, t).Count(e => e.Id == "spin"), "取りやめたのに spin が消えた");

        // spin を含む合計 63 個: 62+2=64 は適用
        app.Ui(() => Check.True(S(app).RemoveEffect(t, 0).Changed, "1 行を消せない"));
        Check.Equal(63, StackOf(app, t).Count, "準備: spin を含む合計 63 個");
        PressTile(app, film);
        Check.Equal(64, StackOf(app, t).Count, "62+2");
        Check.Equal(0, StackOf(app, t).Count(e => e.Id == "spin"), "spin は取り除かれる");
        Check.Equal(1, StackOf(app, t).Count(e => e.Id == "grainmove"), "grainmove");
        Check.Equal(1, StackOf(app, t).Count(e => e.Id == "leaks"), "leaks");

        // 土台が 64 個のままでも、2 枚目と全体には 1 個ずつ足せる(上限は対象ごと)
        CloseIfAny(app);
        StackTestKit.Import(app, 32, 32, Img.Solid(32, 32, 1, 2, 3), "second");
        CloseIfAny(app);
        EditingScreenKit.SelectTab(app, LeftPaneTab.Effects);
        Check.Equal(Target.OfLayer(1), app.Ui(() => S(app).Selected), "2 枚目を選んでいる");
        AddFromEffectsTab(app, "exposure");
        Check.Equal(1, StackOf(app, Target.OfLayer(1)).Count, "2 枚目に 1 個");
        app.Ui(() => S(app).Select(Target.All));
        AddFromEffectsTab(app, "exposure");
        Check.Equal(1, StackOf(app, Target.All).Count, "全体に 1 個");
        Check.Equal(64, StackOf(app, t).Count, "土台は 64 個のまま");
        Check.True(CurrentNotice(app)?.Key != NoticeKeys.StackFull, "stack.full が出た");
    }

    private static void CloseIfAny(TestApp app)
    {
        if (CurrentNotice(app) is not null) CloseNotice(app);
    }

    // ================================================================ (2) 読み込みで多い(E-23)

    // 1 枚の .glint は ProjectUiTests.AC56_2_3_… と GlintProjectSafetyTests.AC56_2_… が、並べ直しのあとの 64 個は DocumentJsonTests.E23_… が調べる
    [Test]
    public void AC56_2_65個のstackは土台と2枚目と全体のどれも先頭の64個だけ読みstack_fullが出る()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start();
        var rows = Rows("exposure", 65);
        CtrlO(app, dir.Write("full.glint", Project(SessionJson(rows, rows, rows))));
        ExpectNotice(app, NoticeKeys.StackFull, NoticeLevel.Warn, "65 個の .glint");
        var expected = Enumerable.Range(1, 64).Select(i => (long)i).ToList();
        foreach (var (target, what) in new[] { (Target.OfLayer(0), "土台"), (Target.OfLayer(1), "2 枚目"), (Target.All, "全体") })
            Check.SequenceEqual(expected, StackOf(app, target).Select(e => e.Salt).ToList(), what + " は先頭の 64 個");
    }

    [Test]
    public void AC56_2_JSONから作る関数に70個のstackを与えると先頭の64個になる()
    {
        var rows = Rows("exposure", 70);
        var r = DocumentJson.Read(SessionJson(rows, rows), StackTestKit.Catalog, new MemoryLogSink());
        Check.True(r.IsValid, "読めない: " + r.InvalidReason);
        var expected = Enumerable.Range(1, 64).Select(i => (long)i).ToList();
        Check.SequenceEqual(expected, r.Document!.Layers[0].Stack.Select(e => e.Salt).ToList(), "レイヤーは先頭の 64 個");
        Check.SequenceEqual(expected, r.Document!.All.Stack.Select(e => e.Salt).ToList(), "全体は先頭の 64 個");
        var n = r.Notices.Single();
        Check.Equal(NoticeKeys.StackFull, n.Key, "通知");
        Check.Equal(NoticeLevel.Warn, n.Level, "通知の重さ");
    }

    // ================================================================ (3) 未知の id(E-24)

    private static readonly string UnknownRows = Row("exposure", 1) + "," + Row("no_such_effect", 2) + "," + Row("contrast", 3);

    [Test]
    public void AC56_3_未知のidの行だけ読まずほかは元の順でレイヤーでも全体でも通知が出る()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start();
        CtrlO(app, dir.Write("layer.glint", Project(SessionJson("", UnknownRows))));
        ExpectNotice(app, NoticeKeys.StackUnknownEffect, NoticeLevel.Warn, "レイヤーの未知の id");
        Check.SequenceEqual(["exposure", "contrast"], StackOf(app, Target.OfLayer(0)).Select(e => e.Id).ToList(), "レイヤーの行");
        Check.SequenceEqual([1L, 3L], StackOf(app, Target.OfLayer(0)).Select(e => e.Salt).ToList(), "レイヤーの行の salt");

        CtrlO(app, dir.Write("all.glint", Project(SessionJson(UnknownRows, ""))));
        ExpectNotice(app, NoticeKeys.StackUnknownEffect, NoticeLevel.Warn, "全体の未知の id");
        Check.SequenceEqual(["exposure", "contrast"], StackOf(app, Target.All).Select(e => e.Id).ToList(), "全体の行");
        Check.SequenceEqual([1L, 3L], StackOf(app, Target.All).Select(e => e.Salt).ToList(), "全体の行の salt");
    }

    [Test]
    public void AC56_3_ユーザーのルックの未知のidは適用で入らず同じ通知が出る()
    {
        const string look = "{\"schema\":1,\"name\":\"未知入り\",\"effects\":["
            + "{\"id\":\"exposure\",\"amount\":100,\"params\":{}},"
            + "{\"id\":\"no_such_effect\",\"amount\":100,\"params\":{}},"
            + "{\"id\":\"contrast\",\"amount\":100,\"params\":{}}]}";
        using var app = TestApp.Start(paths =>
        {
            Directory.CreateDirectory(paths.LooksDir);
            File.WriteAllText(Path.Combine(paths.LooksDir, "look-12345678.json"), look, new UTF8Encoding(false));
        });
        StackTestKit.Import(app, 64, 48, Img.Solid(64, 48, 10, 20, 30), "base");
        CloseIfAny(app);
        EditingScreenKit.SelectTab(app, LeftPaneTab.Looks);
        PressTile(app, LookTileOf(app, "look-12345678"));
        Check.SequenceEqual(["exposure", "contrast"], StackOf(app, Target.OfLayer(0)).Select(e => e.Id).ToList(), "適用で入るエフェクト");
        Check.True(StackOf(app, Target.OfLayer(0)).All(e => e.Origin == EffectOrigin.Look), "origin=look");
        ExpectNotice(app, NoticeKeys.StackUnknownEffect, NoticeLevel.Warn, "ユーザーのルックの未知の id");
    }

    // ================================================================ (4) パラメータの範囲(E-25)

    private static double RadiusOf(ParamValue v) => ((NumberValue)v).Value;

    [Test]
    public void AC56_4_パラメータを正規化する関数は端と既定値に直しparam_clampedを1行ずつ書く()
    {
        var def = StackTestKit.Def("blur");
        var radius = EffectSchema.AllParams(def).Single(p => p.Key == "radius");
        Check.Equal(0.0, radius.Min, "blur の radius の最小");
        Check.Equal(100.0, radius.Max, "blur の radius の最大");
        Check.Equal(10.0, RadiusOf(radius.Default), "blur の radius の既定");

        foreach (var (value, want, what) in new[]
                 {
                     (9999.0, 100.0, "9999"),
                     (-5.0, 0.0, "-5"),
                     (double.NaN, 10.0, "NaN"),
                     (double.PositiveInfinity, 100.0, "+∞"),
                 })
        {
            var log = new MemoryLogSink();
            var got = ParamRules.Normalize("blur", radius, new NumberValue(value), log);
            Check.Equal(want, RadiusOf(got), what + " の値");
            var line = log.Events.Single();
            Check.Equal("param_clamped", line.Name, what + " のログ");
            Check.Equal("blur", (string?)line.Get("effect"), what + " のログの effect");
            Check.Equal("radius", (string?)line.Get("key"), what + " のログの key");
        }

        foreach (var (value, what) in new (ParamValue? Value, string What)[] { (new StringValue("abc"), "文字列 abc"), (null, "欠け") })
        {
            var log = new MemoryLogSink();
            Check.Equal(10.0, RadiusOf(ParamRules.Normalize("blur", radius, value, log)), what + " の値");
            Check.Equal(0, log.Events.Count, what + " はログを書かない");
        }
    }

    [Test]
    public void AC56_4_文書のJSONでも範囲外と型違いと欠けを直し未知のキーを保存し直したJSONに残さない()
    {
        foreach (var (parameters, want, lines, what) in new[]
                 {
                     ("{\"radius\":9999,\"foo\":1}", 100.0, 1, "9999 と foo"),
                     ("{\"radius\":-5}", 0.0, 1, "-5"),
                     ("{\"radius\":\"abc\"}", 10.0, 0, "abc"),
                     ("{}", 10.0, 0, "欠け"),
                 })
        {
            var log = new MemoryLogSink();
            var r = DocumentJson.Read(SessionJson("", Row("blur", 1, parameters)), StackTestKit.Catalog, log);
            Check.True(r.IsValid, what + " が読めない: " + r.InvalidReason);
            var row = r.Document!.Layers[0].Stack.Single();
            Check.Equal(want, RadiusOf(row.Get("radius")!), what + " の radius");
            Check.True(row.Params.All(p => p.Key != "foo"), what + " の正規化後の値に foo がある");
            Check.True(!DocumentJson.Write(r.Document!).Contains("\"foo\"", StringComparison.Ordinal), what + " の保存し直した JSON に foo がある");
            var clamped = log.Events.Where(e => e.Name == "param_clamped").ToList();
            Check.Equal(lines, clamped.Count, what + " の param_clamped の行数");
            foreach (var e in clamped)
            {
                Check.Equal("blur", (string?)e.Get("effect"), what + " の effect");
                Check.Equal("radius", (string?)e.Get("key"), what + " の key");
            }
        }
    }

    [Test]
    public void AC56_4_radius9999とfooのプロジェクトを開くとradiusが100でfooが保存し直したJSONに無い()
    {
        using var dir = new TempDir();
        using var app = TestApp.Start();
        CtrlO(app, dir.Write("p.glint", Project(SessionJson("", Row("blur", 1, "{\"radius\":9999,\"foo\":1}")))));
        var row = StackOf(app, Target.OfLayer(0)).Single();
        Check.Equal(100.0, RadiusOf(row.Get("radius")!), "radius");
        var json = Json(app);
        Check.True(!json.Contains("\"foo\"", StringComparison.Ordinal), "保存し直した JSON に foo がある");
        Check.True(json.Contains("\"radius\":100", StringComparison.Ordinal), "保存し直した JSON の radius が 100 でない");
        var line = Logs(app).Single(l => l.Event == "param_clamped");
        Check.Equal(("blur", "radius"), (line.Effect, line.Key), "param_clamped");
    }

    // ================================================================ (5) 設定が壊れている(E-42)

    private static TestApp StartWithSettings(byte[] bytes) => TestApp.Start(paths =>
    {
        Directory.CreateDirectory(paths.RoamingRoot);
        File.WriteAllBytes(paths.SettingsFile, bytes);
    });

    private static void CheckDefaults(TestApp app, string what) => app.Ui(() =>
    {
        var s = app.Shell.Settings;
        Check.Equal(ThemeSetting.System, s.Theme, what + " の theme");
        Check.Equal(CopyModeSetting.Both, s.CopyMode, what + " の copyMode");
        Check.Equal(ExportFormatSetting.Gif, s.DefaultFormat, what + " の defaultFormat");
        Check.Equal(1024, s.CacheLimitMB, what + " の cacheLimitMB");
        Check.True(app.Shell.Window is not null, what + " で起動しない");
    });

    private static List<string?> SettingsInvalidKeys(TestApp app) => Logs(app).Where(l => l.Event == "settings_invalid").Select(l => l.Key).ToList();

    [Test]
    public void AC56_5_JSONとして壊れたsettingsは既定で起動しバイト列のままbadに改名されkeyがfile()
    {
        var bytes = Encoding.UTF8.GetBytes("{ \"schema\": 1, \"theme\": 壊れた");
        using var app = StartWithSettings(bytes);
        CheckDefaults(app, "壊れた JSON");
        Check.SequenceEqual(["file"], SettingsInvalidKeys(app), "settings_invalid の key");
        var bad = app.Paths.SettingsFile + ".bad";
        Check.True(File.Exists(bad), "settings.json.bad が無い");
        Check.SequenceEqual(bytes, File.ReadAllBytes(bad), "settings.json.bad のバイト列");
        Check.True(!File.Exists(app.Paths.SettingsFile), "元の settings.json が残っている");
    }

    [Test]
    public void AC56_5_schemaが2のsettingsは既定で起動しバイト列のままbadに改名されkeyがschema()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":2,\"theme\":\"dark\"}");
        using var app = StartWithSettings(bytes);
        CheckDefaults(app, "schema が 2");
        Check.SequenceEqual(["schema"], SettingsInvalidKeys(app), "settings_invalid の key");
        var bad = app.Paths.SettingsFile + ".bad";
        Check.True(File.Exists(bad), "settings.json.bad が無い");
        Check.SequenceEqual(bytes, File.ReadAllBytes(bad), "settings.json.bad のバイト列");
        Check.True(!File.Exists(app.Paths.SettingsFile), "元の settings.json が残っている");
    }

    [Test]
    public void AC56_5_一部のキーが不正なsettingsはそのキーだけ既定に直し改名しない()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":1,\"theme\":\"neon\",\"cacheLimitMB\":100,\"copyMode\":\"file\"}");
        using var app = StartWithSettings(bytes);
        Check.Equal(ThemeSetting.System, app.Ui(() => app.Shell.Settings.Theme), "theme は system");
        Check.Equal(CopyModeSetting.File, app.Ui(() => app.Shell.Settings.CopyMode), "copyMode は file のまま");
        Check.SequenceEqual(["theme", "cacheLimitMB"], SettingsInvalidKeys(app), "settings_invalid の key");
        Check.True(File.Exists(app.Paths.SettingsFile), "settings.json が無くなった");
        Check.SequenceEqual(bytes, File.ReadAllBytes(app.Paths.SettingsFile), "settings.json のバイト列");
        Check.True(!File.Exists(app.Paths.SettingsFile + ".bad"), "改名された");
        Check.Equal(1024, app.Ui(() => app.Shell.Settings.CacheLimitMB), "cacheLimitMB は既定の 1024");
    }

    // ================================================================ (6) 設定を保存できない(E-43)

    private static bool IsSettingsFile(string path) => Path.GetFileName(path).StartsWith("settings.json", StringComparison.Ordinal);

    private static int SettingsWriteTries(FakeFileSystem fs) => fs.Calls.Count(c => c.Operation == FileOps.Create && IsSettingsFile(c.Path));

    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    [Test]
    public void AC56_6_設定画面でテーマを変えて保存できないと帯が出て画面はダークのままで次の変更で両方が保存される()
    {
        var fs = new FakeFileSystem();
        using var app = TestApp.Start(fileSystem: fs, os: new FakeOsInfo { AppsUseLightTheme = true });
        var m = EditingScreenKit.M<ScreensModule>(app);
        fs.FailOn(FileOps.Create, IsSettingsFile);
        Check.True(Key(app, K.OemComma, ModifierKeys.Control), "Ctrl+カンマが処理されない");
        Check.True(UiHost.WaitUntil(() => m.Settings is not null, Wait), "設定の画面が出ない");
        UiHost.Idle();

        var themeList = Uia.Root(app.Hwnd).FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, "テーマ"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List))) ?? throw new CheckFailedException("「テーマ」が見つからない");
        var dark = themeList.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "ダーク")) ?? throw new CheckFailedException("「ダーク」が見つからない");
        var tries = SettingsWriteTries(fs);
        Uia.Select(dark);
        UiHost.Idle();
        Check.Equal(ThemeSetting.Dark, app.Ui(() => app.Shell.Settings.Theme), "テーマの設定");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(500)));
        Check.Equal(tries + 1, SettingsWriteTries(fs), "500 ms 後の書き込みの試行");
        var band = app.Ui(() => m.Settings!.SaveFailedBand.Current);
        Check.Equal(NoticeKeys.SettingsSaveFailed, band?.Key, "設定画面の上端の帯");
        Check.Equal(NoticeLevel.Error, band!.Level, "帯の重さ");

        app.Ui(() =>
        {
            Check.Equal(ThemeSetting.Dark, app.Shell.Theme.Setting, "テーマの適用");
            Check.Equal((Color)AppResources.DarkTokens["BgBase"], BrushColor("BgBaseBrush"), "画面はダークのまま");
        });

        // 偽の時計を 10 秒進めても、やり直さない
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(10)));
        Check.Equal(tries + 1, SettingsWriteTries(fs), "10 秒後の書き込みの試行");

        // 正常に戻して次の設定を変えると、両方の変更が入る
        fs.ClearFailures();
        app.Ui(() =>
        {
            app.Shell.Settings.CopyMode = CopyModeSetting.Image;
            app.Clock.Advance(TimeSpan.FromMilliseconds(500));
        });
        using var saved = JsonDocument.Parse(File.ReadAllBytes(app.Paths.SettingsFile));
        Check.Equal("dark", saved.RootElement.GetProperty("theme").GetString(), "ファイルのテーマ");
        Check.Equal("image", saved.RootElement.GetProperty("copyMode").GetString(), "ファイルのコピーの載せ方");
    }

    [Test]
    public void AC56_6_設定画面を開いていないときの保存の失敗は通知バーにerrorで出る()
    {
        var fs = new FakeFileSystem();
        using var app = TestApp.Start(fileSystem: fs, os: new FakeOsInfo { AppsUseLightTheme = true });
        fs.FailOn(FileOps.Create, IsSettingsFile);
        var tries = SettingsWriteTries(fs);
        app.Ui(() =>
        {
            app.Shell.Settings.Theme = ThemeSetting.Dark;
            app.Clock.Advance(TimeSpan.FromMilliseconds(500));
        });
        Check.Equal(tries + 1, SettingsWriteTries(fs), "書き込みの試行");
        Check.Equal((Color)AppResources.DarkTokens["BgBase"], app.Ui(() => BrushColor("BgBaseBrush")), "画面はダークのまま");
        var n = CurrentNotice(app);
        Check.Equal(NoticeKeys.SettingsSaveFailed, n?.Key, "通知バーの settings.save_failed");
        Check.Equal(NoticeLevel.Error, n!.Level, "通知の重さ");
    }

    // ================================================================ (7) ウィンドウの位置(E-49)

    private static readonly Rect Center = new(320, 140, 1280, 800);

    private static readonly MonitorInfo Primary = new(new Rect(0, 0, 1920, 1080), true);

    private static readonly MonitorInfo Second = new(new Rect(1920, 0, 1920, 1080), false);

    private static WindowSettings Saved(double x, double y) => new(x, y, 1000, 700, false, null, null);

    private static Rect PlacementAtStartup(WindowSettings saved, params MonitorInfo[] monitors)
    {
        using var app = TestApp.Start(paths =>
        {
            var s = new SettingsStore(paths, new FakeClock(), NullLogSink.Instance);
            s.Settings.Window = saved;
            Check.True(s.SaveNow(), "準備の settings.json を書けない");
        }, os: new FakeOsInfo { Monitors = monitors });
        return app.Ui(() => app.Shell.InitialPlacement);
    }

    // 形の違う値は SettingsAc30Tests.AC30_5_… が調べる。ここは AC-56 の値で、純粋な関数と起動の両方を通す
    [Test]
    public void AC56_7_保存した矩形が作業領域と100DIP以上重ならなければプライマリの中央に1280x800で開く()
    {
        var one = new[] { Primary };
        var two = new[] { Primary, Second };
        foreach (var (saved, monitors, want, what) in new[]
                 {
                     (Saved(5000, 3000), one, Center, "(5000, 3000) は重ならない"),
                     (Saved(-5000, 100), one, Center, "(-5000, 100) は重ならない"),
                     (Saved(1850, 500), one, Center, "(1850, 500) は重なりの幅が 70 で 100 未満"),
                     (Saved(1800, 500), one, new Rect(1800, 500, 1000, 700), "(1800, 500) は重なりが 120×580"),
                     (Saved(2000, 100), two, new Rect(2000, 100, 1000, 700), "2 台の (2000, 100) は 2 台目に重なる"),
                     (Saved(2000, 100), one, Center, "2 台目を外した (2000, 100)"),
                 })
        {
            Check.Equal(want, WindowPlacement.Restore(saved, monitors, new Size(1280, 800)), what + " の関数");
            Check.Equal(want, PlacementAtStartup(saved, monitors), what + " の起動");
        }
    }

    // ================================================================ (8) レイヤーの枠が範囲の外(E-17)

    private static DocumentRenderer RendererOf(TestApp app) => app.Shell.RendererReady!.GetAwaiter().GetResult();

    private static DocumentImage Output(TestApp app, int lout) =>
        RendererOf(app).RenderExportAsync(app.Ui(() => S(app).Document!), lout).GetAwaiter().GetResult();

    private static int LongSide(TestApp app) => app.Ui(() => OutputSizing.LongSide(S(app).Document!, ExportFrames.SourceSize(app.Shell.Sources)));

    private static void EnterX(TestApp app, string text) => app.Ui(() =>
    {
        var box = Module(app).Settings.XBox;
        box.Text = text;
        box.Commit();
    });

    // 計算の値は CompositeGeometryTests.AC17_2_…、描画の値は CompositeTests.AC17_2_… が調べる。ここは画面の操作から AC-56 の手順を通す
    [Test]
    public void AC56_8_枠が範囲の外のレイヤーはエラーにせず文書に残り土台の入れ替えでも通知が出ない()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        StackTestKit.Import(app, 100, 100, Img.Solid(100, 100, 128, 128, 128), "gray");
        StackTestKit.Import(app, 20, 10, Img.Solid(20, 10, 255, 0, 0), "red");
        var m = Module(app);
        app.Ui(() =>
        {
            StackTestKit.Module(app).ClearRowSelection();
            Check.Equal(Target.OfLayer(1), S(app).Selected, "赤を選んでいる");
            S(app).SetLayerPlacement(1, 0, 0, 100);
        });
        UiHost.Idle();
        CloseIfAny(app);
        var lines = LogCount(app);

        // (200, 0, 100): 出力は土台と全画素で一致し、通知もログも出ず、文書に残る
        EnterX(app, "200");
        UiHost.Idle();
        Check.Equal(new Placement(200, 0, 100), app.Ui(() => S(app).Document!.Layers[1].Place), "配置");
        Check.Equal(100, LongSide(app), "出力の長辺");
        var outside = Output(app, 100);
        CompositeCheck.Size(outside, 100, 100, "(200, 0, 100) の出力");
        CompositeCheck.All(outside, Gray, 0, "(200, 0, 100) の出力");
        Check.True(CurrentNotice(app) is null, "通知が出た: " + CurrentNotice(app)?.Key);
        Check.Equal(lines, LogCount(app), "ログの行数");
        Check.Equal(2, app.Ui(() => S(app).Document!.Layers.Count), "赤のレイヤーが文書に残る");
        Check.True(Json(app).Contains("\"x\":200", StringComparison.Ordinal), "JSON の配置の x");

        // 設定の位置 X は 200 と出て、行のサムネイルは赤のまま(BGRA)。欄は小数第 2 位までの固定の桁で「200.00」と出すので、数として比べる(報告に書いた仮の決め)
        Check.Equal(200.0, double.Parse(app.Ui(() => m.Settings.XBox.Text), System.Globalization.CultureInfo.InvariantCulture), "設定の位置 X");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(300)));
        Check.True(UiHost.WaitUntil(() => RowOf(m, 1).ShownThumbnail is not null, Wait), "行のサムネイルが出ない");
        var thumb = app.Ui(() => RowOf(m, 1).ShownThumbnail!);
        Check.True(thumb.Pixels.Length > 0, "サムネイルが空");
        for (var i = 0; i < thumb.Pixels.Length; i += 4)
            Check.Equal((0, 0, 255, 255), ((int)thumb.Pixels[i], (int)thumb.Pixels[i + 1], (int)thumb.Pixels[i + 2], (int)thumb.Pixels[i + 3]), $"サムネイルの画素 {i / 4}");

        // (45, 0, 100): 赤は x が 85〜99 の 15 列だけ
        EnterX(app, "45");
        UiHost.Idle();
        var edge = Output(app, 100);
        CompositeCheck.Region(edge, (x, y) => x is >= 85 and <= 99 && y is >= 45 and <= 54, Red, Gray, 0, "(45, 0, 100) の出力");

        // 10×10 を 3 枚目に足し、Alt+下 2 回で新しい土台にする。合成は 10×10、出力の長辺は 16(s=1.6)
        StackTestKit.Import(app, 10, 10, Img.Solid(10, 10, 0, 0, 255), "small");
        UiHost.Idle();
        CloseIfAny(app);
        var assets = app.Ui(() => S(app).Document!.Layers.Select(l => l.Asset).ToList());
        var before = Output(app, 100);
        lines = LogCount(app);
        app.Ui(() => m.List.FocusSelected());
        Check.True(Key(app, K.Down, ModifierKeys.Alt), "Alt+下が一覧に届かない");
        Check.True(Key(app, K.Down, ModifierKeys.Alt), "2 回目の Alt+下が一覧に届かない");
        UiHost.Idle();
        var moved = app.Ui(() => S(app).Document!);
        Check.SequenceEqual([assets[2], assets[0], assets[1]], moved.Layers.Select(l => l.Asset).ToList(), "番号 0 が 10×10 になる");
        Check.Equal(16, LongSide(app), "出力の長辺");
        var layout = RendererOf(app).Layout(moved, 16);
        Check.Equal(1.6, layout.Output.S, "s");
        Check.Equal((16, 16), (layout.Output.Wo, layout.Output.Ho), "出力の大きさ");
        Check.Equal((160, 160), (layout.Layers[1].Image.Wl, layout.Layers[1].Image.Hl), "もとの土台は 160×160");
        Check.Equal(64, layout.Layers[2].Frame.Qx, "赤の枠の x(round(8+1.6×45−16))");
        Check.True(layout.Layers[2].Frame.ClipTo(16, 16).IsEmpty, "赤の枠が範囲の外");
        var small = Output(app, 16);
        CompositeCheck.Size(small, 16, 16, "入れ替えたあとの出力");
        CompositeCheck.All(small, Gray, 0, "入れ替えたあとの出力");
        Check.True(CurrentNotice(app) is null, "通知が出た: " + CurrentNotice(app)?.Key);
        Check.Equal(lines, LogCount(app), "ログの行数");

        // Ctrl+Z 2 回で元の並びと出力に戻る
        Check.True(Key(app, K.Z, ModifierKeys.Control), "Ctrl+Z が処理されない");
        Check.True(Key(app, K.Z, ModifierKeys.Control), "2 回目の Ctrl+Z が処理されない");
        UiHost.Idle();
        Check.SequenceEqual(assets, app.Ui(() => S(app).Document!.Layers.Select(l => l.Asset).ToList()), "元の並び");
        CompositeCheck.Same(before.Pixels, Output(app, 100).Pixels, "元の出力");
    }

    // ================================================================ (9) すべて非表示(E-18)

    private static void ToggleVisible(TestApp app, string rowName)
    {
        var row = LayersTestKit.Rows(app).Single(r => r.Current.Name == rowName);
        Uia.Toggle(Child(row, "表示") ?? throw new CheckFailedException(rowName + " の行に「表示」が無い"));
        UiHost.Idle();
    }

    // 描画の値は CompositeTests の AC17 の (4) が調べる。ここは UIA のトグルから AC-56 の手順を通す
    [Test]
    public void AC56_9_すべて非表示の出力は透明で通知も枠も出ず1枚オンに戻すとその内容になる()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        StackTestKit.Import(app, 100, 100, Img.Solid(100, 100, 128, 128, 128), "gray");
        StackTestKit.Import(app, 50, 50, Img.Solid(50, 50, 255, 0, 0), "red");
        var m = Module(app);
        var stack = StackTestKit.Module(app);
        var (grayRow, redRow) = app.Ui(() =>
        {
            var d = S(app).Document!;
            stack.ClearRowSelection();
            S(app).SetLayerPlacement(1, 0, 0, 100);
            return (d.Layers[0].Name, d.Layers[1].Name);
        });
        UiHost.Idle();
        CloseIfAny(app);
        var lines = LogCount(app);

        ToggleVisible(app, redRow);
        ToggleVisible(app, grayRow);
        Check.True(app.Ui(() => S(app).Document!.Layers.All(l => !l.Visible)), "2 枚ともオフ");

        var none = Output(app, 100);
        CompositeCheck.Size(none, 100, 100, "すべて非表示の出力");
        CompositeCheck.All(none, Clear, 0, "すべて非表示の出力");
        var proxy = RendererOf(app).RenderProxyAsync(app.Ui(() => S(app).Document!)).GetAwaiter().GetResult();
        Check.Equal((100, 100), (proxy.Width, proxy.Height), "プロキシの大きさ");
        Check.True(proxy.Pixels.All(b => b == 0), "プロキシの描画の結果が透明でない");
        Check.True(CurrentNotice(app) is null, "通知が出た: " + CurrentNotice(app)?.Key);
        Check.Equal(lines, LogCount(app), "ログの行数");

        // どの対象を選んでも、枠とハンドルは出ない
        foreach (var target in new[] { Target.OfLayer(1), Target.OfLayer(0), Target.All })
        {
            app.Ui(() =>
            {
                S(app).Select(target);
                Check.Equal(null, m.Frame.ShownLayer, $"{target} を選んだときの枠");
                Check.Equal(0, stack.Handles.Current.Count, $"{target} を選んだときのハンドル");
            });
        }

        // 1 枚だけオンに戻すと、その 1 枚だけの内容になる
        ToggleVisible(app, redRow);
        var redOnly = Output(app, 100);
        CompositeCheck.Region(redOnly, (x, y) => x is >= 25 and <= 74 && y is >= 25 and <= 74, Red, Clear, 0, "赤だけオン");
        ToggleVisible(app, redRow);
        ToggleVisible(app, grayRow);
        CompositeCheck.All(Output(app, 100), Gray, 0, "灰だけオン");
        Check.True(CurrentNotice(app) is null, "通知が出た: " + CurrentNotice(app)?.Key);
    }

    [Test]
    public void AC56_9_すべて非表示で全体にspinを付けたGIFは2枠のカラーテーブルで全画素が透明インデックス()
    {
        using var kit = new ExportRenderKit();
        kit.Import(new CompositeImg(100, 100, Img.Solid(100, 100, 128, 128, 128)), new CompositeImg(50, 50, Img.Solid(50, 50, 255, 0, 0)));
        Check.True(kit.Session.SetLayerVisible(0, false).Changed, "土台をオフにできない");
        Check.True(kit.Session.SetLayerVisible(1, false).Changed, "赤をオフにできない");
        Check.True(kit.Session.AddEffect(Target.All, "spin").Changed, "全体に spin を足せない");

        var (result, gif) = kit.Export(ExportRenderKit.With(kit.Doc, "gif", size: new OutputSize("auto", null)));
        Check.True(result.Settings.FrameCount > 1, "spin で複数のフレームにならない");

        var parsed = MiniGifDecoder.Parse(gif);
        Check.Equal(2, parsed.GlobalTableEntries, "カラーテーブルの枠");
        Check.Equal(((byte)0, (byte)0, (byte)0), parsed.TableColor(0), "番号 0 の色");
        Check.Equal(((byte)0, (byte)0, (byte)0), parsed.TableColor(1), "番号 1 の色");
        Check.Equal(result.Settings.FrameCount, parsed.Frames.Count, "フレームの数");
        for (var k = 0; k < parsed.Frames.Count; k++)
        {
            var f = parsed.Frames[k];
            Check.True(f.TransparentFlag, $"フレーム {k} の透明フラグ");
            Check.Equal(1, f.TransparentIndexField, $"フレーム {k} の透明インデックス");
            Check.Equal(2, f.Disposal, $"フレーム {k} の disposal");
            Check.True(f.Indices.Length > 0 && f.Indices.All(i => i == 1), $"フレーム {k} の全画素が透明インデックスでない");
        }

        // 別実装のデコーダーでも読める
        using var ms = new MemoryStream(gif);
        var decoder = new GifBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check.Equal(parsed.Frames.Count, decoder.Frames.Count, "GifBitmapDecoder のフレームの数");
    }

    // ================================================================ (10) ほかの項目で詳しく調べる行

    [Test]
    public void AC56_10_ほかの項目で詳しく調べる行のテストが有る()
    {
        var names = typeof(EdgeAc56Tests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();
        foreach (var (prefix, row) in new[]
                 {
                     ("AC24_1_", "E-27 テキストの制限"),
                     ("AC24_2_", "E-26 フォントが無いとき"),
                     ("AC23_8_", "E-28 履歴の端"),
                     ("AC26_4_", "E-29 ルックの名前"),
                     ("AC46_5_", "E-44 と E-45 プロジェクトのエラー"),
                     ("AC47_5_", "E-45 プロジェクトのエラー"),
                     ("AC47_6_", "E-45 プロジェクトのエラー"),
                     ("AC31_5_", "E-48 ログを書けないとき"),
                     ("AC50_7_", "E-48 ログを書けないとき"),
                 })
        {
            Check.True(names.Any(n => n.StartsWith(prefix, StringComparison.Ordinal)), $"{row} のテスト({prefix}…)が無い");
        }
    }
}
