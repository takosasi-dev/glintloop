// AC-31: ログとファイル(FR-91・§9.11)を、一時フォルダを保存先にして調べる。ファイル名・1 行 1 JSON・5,000,000 バイトでの切り替え・起動時の削除・
// 19 個の event の項目と lvl・書けないときに例外を出さない(E-48)・.tmp を経由する書き込みと起動時の .tmp の削除(INV-16)。
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class LogAc31Tests
{
    private static void WithTemp(Action<string> body)
    {
        var dir = Directory.CreateTempSubdirectory("glintloop-log-");
        try
        {
            body(dir.FullName);
        }
        finally
        {
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public void AC31_1_ファイル名と1行1JSONと共通の項目()
    {
        WithTemp(dir =>
        {
            // UTC 2026-10-28 18:30 は、+9 時間のローカルでは 10 月 29 日
            var clock = new FakeClock(new DateTimeOffset(2026, 10, 28, 18, 30, 0, 123, TimeSpan.Zero));
            var log = new JsonlLogWriter(dir, clock, "0.3.0");
            log.Write(LogEvents.AppExit(0));
            log.Write(LogEvents.DeviceLost());
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
            Check.SequenceEqual(["glintloop-20261029-01.jsonl"], files, "ファイル名(ローカル日付と 01)");
            var bytes = File.ReadAllBytes(Path.Combine(dir, files[0]!));
            Check.True(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "BOM がある");
            Check.True(!bytes.Contains((byte)'\r'), "CR がある");
            Check.Equal((byte)'\n', bytes[^1], "最後の改行");
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check.Equal(2, lines.Length, "行数");
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                Check.Equal("2026-10-28T18:30:00.123Z", r.GetProperty("ts").GetString(), "ts(UTC の ISO 8601・ミリ秒)");
                Check.Equal("0.3.0", r.GetProperty("v").GetString(), "v");
                Check.True(r.TryGetProperty("lvl", out _) && r.TryGetProperty("event", out _), "lvl か event が無い");
            }
            Check.Equal("info", JsonDocument.Parse(lines[0]).RootElement.GetProperty("lvl").GetString(), "app_exit の lvl");
            Check.Equal("warn", JsonDocument.Parse(lines[1]).RootElement.GetProperty("lvl").GetString(), "device_lost の lvl");
        });
    }

    [Test]
    public void AC31_1_ログのvはDirectory_Build_propsのVersionそのまま()
    {
        var props = System.Xml.Linq.XDocument.Parse(GlintLoop.Tests.Static.RepoFiles.ReadText(Path.Combine(GlintLoop.Tests.Static.RepoFiles.Root, "Directory.Build.props")));
        var version = props.Descendants().First(e => e.Name.LocalName == "Version").Value.Trim();
        Check.Equal(version, GlintLoop.App.Startup.AppVersion.Current, "製品のログに書く v");
    }

    [Test]
    public void AC31_2_5000000バイトを超えずに次のNNへ移り再起動では上限まで追記する()
    {
        WithTemp(dir =>
        {
            var clock = new FakeClock();
            var gpu = new string('x', 9_000);
            var log = new JsonlLogWriter(dir, clock, "0.3.0");
            var name1 = Path.Combine(dir, "glintloop-20261028-01.jsonl");
            var name2 = Path.Combine(dir, "glintloop-20261028-02.jsonl");
            var lineLen = log.Format(LogEvents.AppStart(26100, LogAdapter.Warp, gpu, 100)).Length;
            var perFile = (int)(JsonlLogWriter.DefaultMaxFileBytes / lineLen);
            for (var i = 0; i < perFile; i++) log.Write(LogEvents.AppStart(26100, LogAdapter.Warp, gpu, 100));
            Check.True(!File.Exists(name2), "上限の前に 02 ができた");
            Check.Equal((long)perFile * lineLen, new FileInfo(name1).Length, "01 の大きさ");
            log.Write(LogEvents.AppStart(26100, LogAdapter.Warp, gpu, 100));
            Check.True(new FileInfo(name1).Length <= JsonlLogWriter.DefaultMaxFileBytes, "01 が 5,000,000 バイトを超えた");
            Check.Equal((long)lineLen, new FileInfo(name2).Length, "次の 1 行は 02 へ");

            // 再起動: 02 は上限に達していないので追記する
            var again = new JsonlLogWriter(dir, clock, "0.3.0");
            again.Write(LogEvents.AppExit(0));
            Check.True(new FileInfo(name2).Length > lineLen, "再起動で 02 に追記しない");
            Check.True(!File.Exists(Path.Combine(dir, "glintloop-20261028-03.jsonl")), "03 ができた");

            // 02 をちょうど上限にしてから再起動すると、03 を作る
            using (var s = new FileStream(name2, FileMode.Open)) s.SetLength(JsonlLogWriter.DefaultMaxFileBytes);
            var third = new JsonlLogWriter(dir, clock, "0.3.0");
            third.Write(LogEvents.AppExit(0));
            Check.Equal(JsonlLogWriter.DefaultMaxFileBytes, new FileInfo(name2).Length, "上限の 02 に追記した");
            Check.True(File.Exists(Path.Combine(dir, "glintloop-20261028-03.jsonl")), "上限の 02 のあとに 03 を作らない");
        });
    }

    private static void Put(string dir, string name, DateTime mtimeUtc)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "{}\n");
        File.SetLastWriteTimeUtc(p, mtimeUtc);
    }

    [Test]
    public void AC31_3_起動時に14日より古いものと31番目以降を消す()
    {
        var clock = new FakeClock();
        var now = clock.UtcNow.UtcDateTime;
        WithTemp(dir =>
        {
            Put(dir, "glintloop-20261014-01.jsonl", now - TimeSpan.FromDays(14) - TimeSpan.FromMinutes(1));
            Put(dir, "glintloop-20261014-02.jsonl", now - TimeSpan.FromDays(13) - TimeSpan.FromHours(23));
            for (var i = 0; i < 34; i++) Put(dir, $"glintloop-20261027-{i + 1:00}.jsonl", now - TimeSpan.FromHours(i));
            Check.Equal(36, Directory.GetFiles(dir).Length, "置いた数");
            JsonlLogWriter.CleanUp(dir, clock);
            var left = Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet();
            Check.Equal(30, left.Count, "残った数");
            Check.True(!left.Contains("glintloop-20261014-01.jsonl"), "14 日と 1 分前のファイルが残った");
            Check.True(!left.Contains("glintloop-20261014-02.jsonl"), "新しい順の 35 番目(13 日と 23 時間前)が残った");
            for (var i = 0; i < 30; i++) Check.True(left.Contains($"glintloop-20261027-{i + 1:00}.jsonl"), $"新しい順の {i + 1} 番目が消えた");
        });
        WithTemp(dir =>
        {
            // 13 日と 23 時間前のファイルが新しい順の 30 番目以内なら残る
            Put(dir, "glintloop-20261014-02.jsonl", now - TimeSpan.FromDays(13) - TimeSpan.FromHours(23));
            for (var i = 0; i < 10; i++) Put(dir, $"glintloop-20261027-{i + 1:00}.jsonl", now - TimeSpan.FromHours(i));
            JsonlLogWriter.CleanUp(dir, clock);
            Check.True(File.Exists(Path.Combine(dir, "glintloop-20261014-02.jsonl")), "30 番目以内の 13 日と 23 時間前のファイルが消えた");
            Check.Equal(11, Directory.GetFiles(dir).Length, "残った数");
        });
        WithTemp(dir =>
        {
            // 最終更新時刻が同じなら、名前の降順で新しい方が残る(31 個のうち名前のいちばん小さいものが消える)
            for (var i = 0; i < 31; i++) Put(dir, $"glintloop-20261027-{i + 1:00}.jsonl", now - TimeSpan.FromHours(1));
            JsonlLogWriter.CleanUp(dir, clock);
            Check.True(!File.Exists(Path.Combine(dir, "glintloop-20261027-01.jsonl")), "同時刻で名前のいちばん小さいファイルが残った");
            Check.Equal(30, Directory.GetFiles(dir).Length, "同時刻の 31 個から残った数");
        });
    }

    [Test]
    public void AC31_4_19個のeventの項目とlvlが表のとおり()
    {
        // §9.11 の 6 の表を書き写したもの
        (string Event, string Lvl, string[] Keys, LogEvent Made)[] table =
        [
            ("app_start", "info", ["os", "adapter", "gpu", "dpi"], LogEvents.AppStart(26100, LogAdapter.Hardware, "GPU", 125)),
            ("app_exit", "info", ["code"], LogEvents.AppExit(1)),
            ("import_ok", "info", ["ms", "w", "h", "fmt", "bytes", "via", "layers"], LogEvents.ImportOk(12, 640, 480, LogImportFormat.Png, 1000, LogImportVia.Paste, 1)),
            ("import_fail", "warn", ["reason"], LogEvents.ImportFail(LogImportFailReason.TooManyLayers)),
            ("export_start", "info", ["fmt", "w", "h", "frames"], LogEvents.ExportStart(LogExportFormat.Gif, 480, 360, 40)),
            ("export_done", "info", ["ms", "bytes", "frames", "fmt", "rungs", "trials"], LogEvents.ExportDone(900, 123456, 40, LogExportFormat.Apng, 2, 3)),
            ("export_cancel", "info", [], LogEvents.ExportCancel()),
            ("export_fail", "error", ["reason"], LogEvents.ExportFail(LogExportFailReason.TooBigGif)),
            ("render_fail", "error", ["target", "reason"], LogEvents.RenderFail(LogRenderTarget.Thumb, LogRenderFailReason.Oom)),
            ("export_handoff", "info", ["kind", "fmt", "bytes", "frames", "mode"], LogEvents.ExportHandoff(LogHandoffKind.Copy, LogExportFormat.Gif, 10, 2, LogCopyMode.Both)),
            ("clipboard_failed", "warn", ["hr"], LogEvents.ClipboardFailed(0x800401D0)),
            ("device_lost", "warn", [], LogEvents.DeviceLost()),
            ("device_recreated", "info", ["ms", "adapter"], LogEvents.DeviceRecreated(30, LogAdapter.Warp)),
            ("warp_fallback", "warn", ["reason"], LogEvents.WarpFallback(LogWarpReason.FloatRtUnsupported)),
            ("param_clamped", "warn", ["effect", "key"], LogEvents.ParamClamped("exposure", "ev")),
            ("effectdef_invalid", "error", ["id", "reason"], LogEvents.EffectDefInvalid("bad", 3)),
            ("cache_trim", "info", ["removed", "bytes"], LogEvents.CacheTrim(2, 4096)),
            ("settings_invalid", "warn", ["key"], LogEvents.SettingsInvalid(LogSettingsKey.CacheLimitMB)),
            ("crash", "error", ["type", "stack"], LogEvents.Crash(MakeException())),
        ];
        Check.Equal(19, table.Length, "event の数");
        var writer = new JsonlLogWriter(Path.GetTempPath(), new FakeClock(), "0.3.0");
        var enumValues = new Regex("^[a-z0-9_]+$|^[a-z][A-Za-z]*$");
        foreach (var (ev, lvl, keys, made) in table)
        {
            using var doc = JsonDocument.Parse(writer.Format(made));
            var r = doc.RootElement;
            var got = r.EnumerateObject().Select(p => p.Name).ToList();
            Check.SequenceEqual(new[] { "ts", "lvl", "event", "v" }.Concat(keys).ToList(), got, $"{ev} のキー");
            Check.Equal(ev, r.GetProperty("event").GetString(), "event");
            Check.Equal(lvl, r.GetProperty("lvl").GetString(), $"{ev} の lvl");
            foreach (var p in r.EnumerateObject().Where(p => keys.Contains(p.Name)))
            {
                if (p.Value.ValueKind == JsonValueKind.String && !(ev == "app_start" && p.Name == "gpu") && !(ev == "crash" && p.Name == "type"))
                    Check.True(enumValues.IsMatch(p.Value.GetString()!) || (ev is "param_clamped" or "effectdef_invalid"), $"{ev}.{p.Name} の値が列挙値でない: {p.Value}");
                if (p.Value.ValueKind == JsonValueKind.Number) Check.True(p.Value.TryGetInt64(out _), $"{ev}.{p.Name} が整数でない");
            }
        }
        var crash = JsonDocument.Parse(writer.Format(LogEvents.Crash(MakeException()))).RootElement;
        Check.Equal(typeof(InvalidOperationException).FullName, crash.GetProperty("type").GetString(), "crash の type");
        var stack = crash.GetProperty("stack").EnumerateArray().Select(e => e.GetString()!).ToList();
        Check.True(stack.Count is > 0 and <= 20, $"stack の数 {stack.Count}");
        Check.True(stack[0] == typeof(LogAc31Tests).FullName + "." + nameof(Throw), $"stack の先頭: {stack[0]}");
        Check.True(stack.All(s => !s.Contains('\\') && !s.Contains(':') && !s.Contains("秘密")), "stack にパスかメッセージが入っている");

        // 表に無い項目は渡せない: 公開の関数は 19 個で、引数に object・配列・辞書の型が無い
        var makers = typeof(LogEvents).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(LogEvent)).ToList();
        Check.Equal(19, makers.Count, "LogEvent を作る関数の数");
        foreach (var m in makers)
        foreach (var p in m.GetParameters())
            Check.True(p.ParameterType != typeof(object) && !p.ParameterType.IsArray && !typeof(System.Collections.IDictionary).IsAssignableFrom(p.ParameterType) &&
                       !p.ParameterType.Name.StartsWith("ValueTuple", StringComparison.Ordinal),
                $"{m.Name} の引数 {p.Name} の型 {p.ParameterType.Name} で任意の項目を渡せる");
        Check.Throws<ArgumentException>(() => LogEvents.ExportHandoff(LogHandoffKind.Save, LogExportFormat.Png, 1, 1, LogCopyMode.File), "save に mode を渡せた");
    }

    private static void Throw() => throw new InvalidOperationException("秘密の C:\\path を含むメッセージ");

    private static Exception MakeException()
    {
        try
        {
            Throw();
        }
        catch (Exception e)
        {
            return e;
        }
        throw new InvalidOperationException("届かない");
    }

    [Test]
    public void AC31_5_書き込み先を読み取り専用にしても例外が出ない()
    {
        WithTemp(dir =>
        {
            var log = new JsonlLogWriter(dir, new FakeClock(), "0.3.0");
            log.Write(LogEvents.AppExit(0));
            var file = log.CurrentPath!;
            File.SetAttributes(file, FileAttributes.ReadOnly);
            log.Write(LogEvents.AppExit(0));
            log.Write(LogEvents.AppExit(0));
            File.SetAttributes(file, FileAttributes.Normal);

            // ログのフォルダの場所にファイルがあって、フォルダを作れない
            var blocked = Path.Combine(dir, "blocked");
            File.WriteAllText(blocked, "x");
            var log2 = new JsonlLogWriter(blocked, new FakeClock(), "0.3.0");
            log2.Write(LogEvents.AppExit(0));
            Check.True(log2.CurrentPath is null, "書けないのにファイルを選んだまま");

            // 起動時の削除で消せないファイルは飛ばす
            var stuck = Path.Combine(dir, "glintloop-20260101-01.jsonl");
            File.WriteAllText(stuck, "{}\n");
            File.SetLastWriteTimeUtc(stuck, DateTime.UtcNow - TimeSpan.FromDays(30));
            using (new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.Read)) JsonlLogWriter.CleanUp(dir, new FakeClock());
        });
    }

    [Test]
    public void AC31_6_保存はtmpを経由し途中の例外で元のファイルが残り起動時にtmpが消える()
    {
        WithTemp(root =>
        {
            var paths = AppPaths.UnderOneRoot(root);
            // 設定・ユーザーのルック・書き出しプリセットの保存先(ルックとプリセットの保存は後の担当が AtomicFile を使う)
            var targets = new[] { paths.SettingsFile, Path.Combine(paths.LooksDir, "look-0123abcd.json"), Path.Combine(paths.PresetsDir, "export-0123abcd.json") };
            foreach (var t in targets)
            {
                AtomicFile.WriteAllBytes(t, Encoding.UTF8.GetBytes("old"));
                var sawTemp = false;
                Check.Throws<IOException>(() => AtomicFile.Write(t, s =>
                {
                    s.Write("new-partial"u8);
                    sawTemp = File.Exists(AtomicFile.TempPathFor(t));
                    throw new IOException("途中で失敗");
                }), $"{Path.GetFileName(t)} の途中の例外");
                Check.True(sawTemp, $"{Path.GetFileName(t)} を .tmp に書いていない");
                Check.Equal("old", File.ReadAllText(t), $"{Path.GetFileName(t)} の元の中身");
                Check.True(!File.Exists(AtomicFile.TempPathFor(t)), $"{Path.GetFileName(t)} の .tmp が残った");
            }

            // 設定の保存も .tmp を経由する(書いている途中に .tmp がある。ファイルの共有を禁じて途中で止める)
            var store = new SettingsStore(paths, new FakeClock(), NullLogSink.Instance);
            File.WriteAllText(AtomicFile.TempPathFor(paths.SettingsFile), "");
            using (new FileStream(AtomicFile.TempPathFor(paths.SettingsFile), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = 0;
                store.SaveFailed += _ => failed++;
                Check.True(!store.SaveNow(), "settings.json.tmp を開けないのに保存できた");
                Check.Equal(1, failed, "SaveFailed の回数");
            }
            Check.Equal("old", File.ReadAllText(paths.SettingsFile), "保存に失敗した settings.json の中身");

            // 起動時に .tmp を消す
            foreach (var d in paths.TempSweepDirs)
            {
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "x.json.tmp"), "x");
            }
            StartupMaintenance.Run(paths);
            foreach (var d in paths.TempSweepDirs)
                Check.True(Directory.GetFiles(d, "*.tmp").Length == 0, $"{d} の .tmp が消えない");
        });
    }
}
