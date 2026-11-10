// AC-45(キャッシュの管理。FR-105)を、偽の時計・一時フォルダ・上限を直接与える差し込み口で調べる。(1) の換算は Phase5SeamTests にある。
// 前半は CacheManager だけ、後半は画面の外のアプリで配線(起動時と完了のときの確認・UI スレッドの外・設定の画面・実出力の Touch)を見る。
// コピー・保存の Touch と、コピー・ドラッグ・保存で確認しないことは、担当 handoff の範囲なのでここでは調べない。
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.App.Screens;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Platform;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Gif;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class CacheAc45Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    // ---------------------------------------------------------------- ファイルの置き方

    /// <summary>書き出しのキャッシュの 1 組を、2 ファイルの合計がちょうど total バイトになるように作る。戻り値は本体のパス。</summary>
    private static string WritePair(string dir, IClock clock, string key, int total, TimeSpan age, string ext = "gif")
    {
        Directory.CreateDirectory(dir);
        var used = clock.UtcNow - age;
        byte[] json = [];
        var body = 0L;
        // bytes の桁数で json の長さが変わるので、落ち着くまで直す
        for (var i = 0; i < 3; i++)
        {
            json = ExportCache.WriteMeta(new ExportCacheMeta(body, 1, 1, 1, 10, null, used, used));
            body = total - json.Length;
        }
        var path = Path.Combine(dir, key + "." + ext);
        File.WriteAllBytes(path, new byte[body]);
        File.WriteAllBytes(Path.Combine(dir, key + ".json"), json);
        Check.Equal((long)total, new FileInfo(path).Length + json.Length, "1 組の大きさ");
        return path;
    }

    /// <summary>渡すための実体を作り、最終更新時刻を age だけ前にする。</summary>
    private static string WriteHandoff(string dir, IClock clock, string name, int bytes, TimeSpan age)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, (clock.UtcNow - age).UtcDateTime);
        return path;
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();

        public Rig(long limit = 10_000, IFileSystem? fileSystem = null)
        {
            ExportsDir = _dir.PathOf("exports");
            HandoffDir = _dir.PathOf("handoff");
            Manager = new CacheManager(ExportsDir, HandoffDir, Clock, Log, () => limit, fileSystem);
        }

        public FakeClock Clock { get; } = FakeClock.ForPhase5();

        public MemoryLogSink Log { get; } = new();

        public string ExportsDir { get; }

        public string HandoffDir { get; }

        public CacheManager Manager { get; }

        public string Pair(string key, int total, TimeSpan age, string ext = "gif") => WritePair(ExportsDir, Clock, key, total, age, ext);

        public string Handoff(string name, int bytes, TimeSpan age) => WriteHandoff(HandoffDir, Clock, name, bytes, age);

        public string MetaOf(string key) => Path.Combine(ExportsDir, key + ".json");

        public LogEvent[] Trims => Log.Events.Where(e => e.Name == "cache_trim").ToArray();

        public void Dispose() => _dir.Dispose();
    }

    private static TimeSpan Days(int d) => TimeSpan.FromDays(d);

    private static TimeSpan Hours(int h, int m = 0) => new(h, m, 0);

    private static void Gone(string path, string what) => Check.True(!File.Exists(path), what + " が残った");

    private static void Kept(string path, string what) => Check.True(File.Exists(path), what + " が消えた");

    // ---------------------------------------------------------------- (2) 古い順

    [Test]
    public void AC45_2_上限を超えると古い順に1組ずつ消しログに個数とバイト数を書く()
    {
        using var r = new Rig();
        var e1 = r.Pair("e1", 4000, Days(3));
        var e2 = r.Pair("e2", 4000, Days(2));
        var e3 = r.Pair("e3", 4000, Days(1));
        r.Manager.Trim();
        Gone(e1, "E1 の本体");
        Gone(r.MetaOf("e1"), "E1 の json");
        Kept(e2, "E2 の本体");
        Kept(r.MetaOf("e2"), "E2 の json");
        Kept(e3, "E3 の本体");
        Kept(r.MetaOf("e3"), "E3 の json");
        var log = r.Trims.Single();
        Check.Equal(2, (int)log.Get("removed")!, "removed は 1 組で 2 個");
        Check.Equal(4000L, (long)log.Get("bytes")!, "bytes");
    }

    [Test]
    public void AC45_2_合計が上限以下なら何も消さずログも書かない()
    {
        using var r = new Rig();
        var e1 = r.Pair("e1", 4000, Days(3));
        var h1 = r.Handoff("glint-a.gif", 6000, Days(3));
        r.Manager.Trim(); // ちょうど上限
        Kept(e1, "E1");
        Kept(h1, "H1");
        Check.Equal(0, r.Trims.Length, "ログ");
    }

    [Test]
    public void AC45_2_フォルダが無くても確認できる()
    {
        using var r = new Rig();
        r.Manager.Trim();
        r.Manager.ClearFromSettings();
        Check.Equal(0, r.Trims.Length, "ログ");
    }

    // ---------------------------------------------------------------- (3)(4) 保護

    [Test]
    public void AC45_3_書き出しのキャッシュを先に消しまだ超えていれば24時間以内の実体を古い順に消す()
    {
        using var r = new Rig();
        var h1 = r.Handoff("glint-h1.gif", 6000, Hours(1));
        var h2 = r.Handoff("glint-h2.gif", 6000, Hours(2));
        var e4 = r.Pair("e4", 3000, Days(5));
        r.Manager.Trim();
        Gone(e4, "E4 の本体");
        Gone(r.MetaOf("e4"), "E4 の json");
        Gone(h2, "H2");
        Kept(h1, "H1");
        var log = r.Trims.Single();
        Check.Equal(3, (int)log.Get("removed")!, "removed");
        Check.Equal(9000L, (long)log.Get("bytes")!, "bytes");
    }

    [Test]
    public void AC45_3_保護された実体より新しい書き出しのキャッシュも先に消える()
    {
        using var r = new Rig();
        var h1 = r.Handoff("glint-h1.gif", 9000, Hours(5));
        var e1 = r.Pair("e1", 3000, Hours(1));
        r.Manager.Trim();
        Gone(e1, "1 時間前の書き出しのキャッシュ");
        Kept(h1, "5 時間前の実体");
    }

    [Test]
    public void AC45_4_23時間59分は保護され24時間1分は保護されず古い順に比べる()
    {
        using var r = new Rig();
        var old = r.Handoff("glint-old.gif", 6000, Hours(24, 1));
        var e5 = r.Pair("e5", 2000, Hours(24));
        var h4 = r.Handoff("glint-h4.gif", 6000, Hours(23, 59));
        r.Manager.Trim();
        Gone(old, "24 時間 1 分の実体");
        Kept(e5, "E5 の本体");
        Kept(r.MetaOf("e5"), "E5 の json");
        Kept(h4, "H4");
        var log = r.Trims.Single();
        Check.Equal(1, (int)log.Get("removed")!, "removed");
        Check.Equal(6000L, (long)log.Get("bytes")!, "bytes");
    }

    [Test]
    public void AC45_4_ちょうど24時間の実体は保護される()
    {
        using var r = new Rig();
        var exact = r.Handoff("glint-exact.gif", 7000, Hours(24));
        var newer = r.Pair("newer", 5000, Hours(1));
        r.Manager.Trim();
        Gone(newer, "1 時間前の書き出しのキャッシュ");
        Kept(exact, "ちょうど 24 時間の実体");
    }

    // ---------------------------------------------------------------- (5) ドラッグ中

    [Test]
    public void AC45_5_ドラッグ中の実体はほかに消すものが無くても消えず終わると消せる()
    {
        using var r = new Rig();
        var h = r.Handoff("glint-drag.gif", 12_000, Hours(1));
        var guard = r.Manager.ProtectFromTrim(h);
        r.Manager.Trim();
        Kept(h, "ドラッグ中の実体");
        Check.Equal(0, r.Trims.Length, "消していないのでログなし");
        guard.Dispose();
        guard.Dispose(); // 2 回呼んでもよい
        r.Manager.Trim();
        Gone(h, "ドラッグが終わった実体");
    }

    [Test]
    public void AC45_5_ドラッグ中の実体は24時間を超えていても消えず合計には数える()
    {
        using var r = new Rig();
        var h = r.Handoff("glint-drag.gif", 9000, Days(3));
        var e1 = r.Pair("e1", 2000, Days(1));
        var e2 = r.Pair("e2", 2000, Hours(1));
        using (r.Manager.ProtectFromTrim(h))
        {
            r.Manager.Trim();
            r.Manager.ClearFromSettings();
        }
        Kept(h, "ドラッグ中の実体");
        Gone(e1, "E1");
        Gone(e2, "E2");
    }

    [Test]
    public void AC45_5_同じ実体を2回守ると両方が終わるまで消えず綴りの大小も同じ物とみなす()
    {
        using var r = new Rig();
        var h = r.Handoff("glint-drag.gif", 12_000, Hours(1));
        var a = r.Manager.ProtectFromTrim(h);
        var b = r.Manager.ProtectFromTrim(h.ToUpperInvariant());
        a.Dispose();
        r.Manager.Trim();
        Kept(h, "まだ 1 つ守っている実体");
        b.Dispose();
        r.Manager.Trim();
        Gone(h, "守りが無くなった実体");
    }

    // ---------------------------------------------------------------- (6) 同時刻

    [Test]
    public void AC45_6_同時刻は名前の昇順の方が先に消える()
    {
        using var r = new Rig();
        // 作る順と名前の順を逆にして、作った順で通らないようにする
        var z = r.Pair("bbbb", 6000, Days(1));
        var a = r.Pair("aaaa", 6000, Days(1));
        r.Manager.Trim();
        Gone(a, "名前の小さい組");
        Gone(r.MetaOf("aaaa"), "名前の小さい組の json");
        Kept(z, "名前の大きい組");
    }

    [Test]
    public void AC45_6_同時刻は書き出しのキャッシュと実体をまたいでも名前の昇順()
    {
        using var r = new Rig();
        var h = r.Handoff("glint-x.gif", 6000, Days(2));
        var e = r.Pair("zzzz", 6000, Days(2));
        r.Manager.Trim();
        Gone(h, "名前の小さい実体");
        Kept(e, "名前の大きい書き出しのキャッシュ");
    }

    // ---------------------------------------------------------------- 実行中の .tmp・消せないファイル・片方だけの組

    [Test]
    public void AC45_実行中の書き出しのtmpとjsonは消さず合計には数える()
    {
        using var r = new Rig();
        Directory.CreateDirectory(r.ExportsDir);
        var tmp = Path.Combine(r.ExportsDir, "busy.gif.tmp");
        var json = r.MetaOf("busy");
        File.WriteAllBytes(tmp, new byte[9000]);
        File.WriteAllBytes(json, ExportCache.WriteMeta(new ExportCacheMeta(0, 1, 1, 1, 10, null, r.Clock.UtcNow - Days(9), r.Clock.UtcNow - Days(9))));
        var handoffTmp = Path.Combine(Directory.CreateDirectory(r.HandoffDir).FullName, "glint-a.gif.tmp");
        File.WriteAllBytes(handoffTmp, new byte[100]);
        var e1 = r.Pair("e1", 4000, Days(1));
        r.Manager.Trim();
        Kept(tmp, "書いている .tmp");
        Kept(json, "書いている途中の json");
        Kept(handoffTmp, "実体の .tmp");
        Gone(e1, "E1(.tmp を合計に数えると超える)");
        r.Manager.ClearFromSettings();
        Kept(tmp, "削除のボタンでも .tmp");
        Kept(json, "削除のボタンでも書いている途中の json");
        Kept(handoffTmp, "削除のボタンでも実体の .tmp");
    }

    [Test]
    public void AC45_本物のキャッシュが書いている最中の確認で書き出しが壊れない()
    {
        using var r = new Rig(limit: 1);
        var cache = new ExportCache(r.ExportsDir, r.Clock);
        var key = new string('a', 32);
        var entry = cache.Write(key, ExportFormat.Gif, s =>
        {
            s.Write(new byte[5000]);
            s.Flush();
            r.Manager.Trim();
            r.Manager.ClearFromSettings();
            Check.True(File.Exists(cache.TempPath(key, ExportFormat.Gif)), "書いている .tmp が消えた");
            return new ExportWritten(1, 1, 1, 10, null);
        });
        Check.True(cache.TryGet(key, ExportFormat.Gif) is not null, "書き終えたファイルが完成として読めない");
        Check.Equal(entry.Path, cache.FilePath(key, ExportFormat.Gif), "完成のパス");
    }

    [Test]
    public void AC45_消せないファイルは数えず次へ進む()
    {
        var fake = new FakeFileSystem();
        using var r = new Rig(fileSystem: fake);
        var e1 = r.Pair("e1", 2000, Days(3));
        var e2 = r.Pair("e2", 6000, Days(2));
        var e3 = r.Pair("e3", 6000, Days(1));
        var e1Json = new FileInfo(r.MetaOf("e1")).Length;
        fake.FailOn(FileOps.Delete, p => p == e1);
        r.Manager.Trim();
        Kept(e1, "消せない本体");
        Gone(r.MetaOf("e1"), "E1 の json(消せる方)");
        Gone(e2, "E2 の本体");
        Gone(r.MetaOf("e2"), "E2 の json");
        Kept(e3, "E3 の本体");
        var log = r.Trims.Single();
        Check.Equal(3, (int)log.Get("removed")!, "消せた個数だけ数える");
        Check.Equal(6000 + e1Json, (long)log.Get("bytes")!, "消せたバイト数だけ数える");
    }

    [Test]
    public void AC45_列挙に失敗しても例外にしない()
    {
        var fake = new FakeFileSystem();
        using var r = new Rig(fileSystem: fake);
        r.Pair("e1", 12_000, Days(1));
        fake.FailOn(FileOps.EnumerateFiles, make: () => new UnauthorizedAccessException());
        r.Manager.Trim();
        r.Manager.ClearFromSettings();
        Check.Equal(0, r.Trims.Length, "ログ");
    }

    [Test]
    public void AC45_本体の無いjsonと読めないjsonの組も合計に数えて消せる()
    {
        using var r = new Rig();
        var orphan = r.Pair("orphan", 5000, Days(9));
        File.Delete(orphan);
        var bad = r.Pair("bad", 5000, Days(9));
        File.WriteAllText(r.MetaOf("bad"), "{");
        // 読めない json の組の時刻はファイルの最終更新時刻(偽の時計より古くする)
        File.SetLastWriteTimeUtc(bad, (r.Clock.UtcNow - Days(8)).UtcDateTime);
        File.SetLastWriteTimeUtc(r.MetaOf("bad"), (r.Clock.UtcNow - Days(8)).UtcDateTime);
        var keep = r.Pair("keep", 9000, Days(1));
        r.Manager.Trim();
        Gone(r.MetaOf("orphan"), "本体の無い json");
        Gone(bad, "読めない json の本体");
        Gone(r.MetaOf("bad"), "読めない json");
        Kept(keep, "新しい組");
    }

    // ---------------------------------------------------------------- (8) 更新

    [Test]
    public void AC45_8_Touchはlastusedだけを偽の時計の時刻にし再利用も同じ()
    {
        using var r = new Rig();
        var cache = new ExportCache(r.ExportsDir, r.Clock);
        var key = new string('b', 32);
        var entry = cache.Write(key, ExportFormat.Gif, s =>
        {
            s.Write(new byte[321]);
            return new ExportWritten(7, 8, 9, 12.5, "色数 256→64");
        });
        var before = ExportCache.ReadMeta(File.ReadAllBytes(cache.MetaPath(key)))!;

        r.Clock.Advance(TimeSpan.FromHours(3));
        r.Manager.Touch(entry);
        var touched = ExportCache.ReadMeta(File.ReadAllBytes(cache.MetaPath(key)))!;
        Check.Equal(r.Clock.UtcNow, touched.LastUsed, "表示・コピー・保存の lastUsed");
        Check.Equal(before with { LastUsed = touched.LastUsed }, touched, "lastUsed のほかは変わらない");
        Check.True(!File.Exists(AtomicFile.TempPathFor(cache.MetaPath(key))), ".tmp が残った");

        // 触った組は古い順で後ろになる
        var other = r.Pair("c000", 9000, Hours(1));
        WritePair(r.ExportsDir, r.Clock, "c001", 1000, Hours(2));
        r.Manager.Trim();
        Kept(entry.Path, "いま触った組");
        Gone(r.MetaOf("c001"), "2 時間前の組");
        Kept(other, "1 時間前の組は消さなくても収まる");

        r.Clock.Advance(TimeSpan.FromHours(1));
        Check.True(cache.TryGet(key, ExportFormat.Gif) is not null, "再利用");
        Check.Equal(r.Clock.UtcNow, ExportCache.ReadMeta(File.ReadAllBytes(cache.MetaPath(key)))!.LastUsed, "再利用の lastUsed");
    }

    [Test]
    public void AC45_8_消えた組のTouchは何も作らず例外にしない()
    {
        var fake = new FakeFileSystem();
        using var r = new Rig(fileSystem: fake);
        var cache = new ExportCache(r.ExportsDir, r.Clock);
        var key = new string('d', 32);
        var entry = cache.Write(key, ExportFormat.Gif, s =>
        {
            s.WriteByte(1);
            return new ExportWritten(1, 1, 1, 1, null);
        });

        File.Delete(entry.Path);
        r.Manager.Touch(entry);
        Check.Equal(entry.Meta.LastUsed, ExportCache.ReadMeta(File.ReadAllBytes(cache.MetaPath(key)))!.LastUsed, "本体が無ければ更新しない");

        File.Delete(cache.MetaPath(key));
        File.WriteAllBytes(entry.Path, [1]);
        r.Manager.Touch(entry);
        Check.True(!File.Exists(cache.MetaPath(key)), "json を作り直した");

        File.WriteAllText(cache.MetaPath(key), "{");
        r.Manager.Touch(entry);
        Check.Equal("{", File.ReadAllText(cache.MetaPath(key)), "読めない json を書き換えた");

        File.WriteAllBytes(cache.MetaPath(key), ExportCache.WriteMeta(entry.Meta));
        fake.FailOn(FileOps.Move);
        r.Manager.Touch(entry); // 書けなくても例外にしない
        Check.True(!File.Exists(AtomicFile.TempPathFor(cache.MetaPath(key))), "失敗で .tmp が残った");
    }

    // ---------------------------------------------------------------- (9) 削除ボタン

    [Test]
    public void AC45_9_削除はキャッシュのすべてと24時間を超えた実体を消す()
    {
        using var r = new Rig(limit: 1_000_000);
        var e1 = r.Pair("e1", 4000, Hours(1));
        var e2 = r.Pair("e2", 4000, Days(3), "png");
        var h23 = r.Handoff("glint-23.gif", 100, Hours(23));
        var h24 = r.Handoff("glint-24.gif", 100, Hours(24));
        var h25 = r.Handoff("glint-25.gif", 100, Hours(25));
        r.Manager.ClearFromSettings();
        Gone(e1, "E1 の本体");
        Gone(r.MetaOf("e1"), "E1 の json");
        Gone(e2, "E2 の本体");
        Gone(r.MetaOf("e2"), "E2 の json");
        Gone(h25, "25 時間前の実体");
        Kept(h24, "ちょうど 24 時間の実体");
        Kept(h23, "23 時間前の実体");
        Check.Equal(0, r.Log.Events.Count, "削除のボタンはログを書かない");
    }

    // ---------------------------------------------------------------- アプリの中の配線

    /// <summary>実出力で読める GIF を書く偽の中身を入れた帯。</summary>
    private static BandUiAc39Tests.Band StartBand()
    {
        var frames = new[] { new byte[48 * 32 * 4], new byte[48 * 32 * 4] };
        Array.Fill(frames[0], (byte)255);
        Array.Fill(frames[1], (byte)200);
        for (var i = 3; i < frames[1].Length; i += 4) frames[1][i] = 255;
        var gif = GifEncoder.EncodeToArray(new GifFrameList(48, 32, frames), new GifEncoderOptions { Fps = 10, Parallelism = 1 });
        return BandUiAc39Tests.Start(new FakePipeline { FrameCount = 2, Content = _ => gif, Result = new ExportPipelineResult(48, 32, 2, 10, null, false, null) });
    }

    private static void ExportOnce(BandUiAc39Tests.Band b, double opacity, string what)
    {
        b.App.Ui(() => b.App.Shell.Session.SetLayerOpacity(0, opacity));
        b.Advance(600);
        b.WaitDone(what);
    }

    private static void WaitCacheWork(BandUiAc39Tests.Band b)
    {
        UiHost.Idle();
        Check.True(b.Module.CacheWork.Wait(Wait), "キャッシュの仕事が終わらない");
    }

    [Test]
    public void AC45_7_起動1回と書き出し3回で確認は4回で表示と設定の変更では増えない()
    {
        using var b = StartBand();
        {
            WaitCacheWork(b);
            Check.Equal(1, b.Module.CacheManager.TrimCalls, "起動時");

            b.Advance(600);
            b.WaitDone("1 回目");
            WaitCacheWork(b);
            Check.Equal(2, b.Module.CacheManager.TrimCalls, "1 回目の完了");
            ExportOnce(b, 50, "2 回目");
            WaitCacheWork(b);
            Check.Equal(3, b.Module.CacheManager.TrimCalls, "2 回目の完了");
            // 同じ設定の使い回しも書き出しの完了(AutoExport.Completed の説明のとおり)
            b.App.Ui(() => b.Module.Auto.NotifyEdit());
            b.Advance(600);
            b.WaitDone("3 回目");
            WaitCacheWork(b);
            Check.Equal(4, b.Module.CacheManager.TrimCalls, "3 回目の完了");

            b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));
            Check.True(UiHost.WaitUntil(() => b.Module.PendingShow is null && b.Canvas.IsActualOutput, Wait), "実出力に切り替わらない");
            b.App.Ui(() => b.App.Shell.Settings.CacheLimitMB = 256);
            WaitCacheWork(b);
            Check.Equal(4, b.Module.CacheManager.TrimCalls, "表示と設定の変更では確認しない");
        }
    }

    [Test]
    public void AC45_7_完了の確認で古いキャッシュが実際に消え起動時の確認も上限を見る()
    {
        string? oldPath = null;
        string? tooMany = null;
        var clock = FakeClock.ForPhase5();
        using var app = TestApp.Start(prepare: p =>
        {
            // 起動時の確認で消える: 上限 256 MB を 1 バイト超える古い組と、新しい組
            var settings = "{\"schema\":1,\"cacheLimitMB\":256}";
            Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
            File.WriteAllText(p.SettingsFile, settings);
            tooMany = WritePair(p.CacheExportsDir, clock, "aaaa", 1000, Days(9));
            using (var s = new FileStream(tooMany, FileMode.Open, FileAccess.Write)) s.SetLength(256_000_001);
            oldPath = WritePair(p.CacheExportsDir, clock, "bbbb", 4000, Days(5));
        }, clock: clock);
        var module = app.Ui(() => app.Shell.Modules.OfType<ExportBandModule>().Single());
        UiHost.Idle();
        Check.True(module.CacheWork.Wait(Wait), "起動時の確認が終わらない");
        Gone(tooMany!, "起動時の確認で古い組");
        Kept(oldPath!, "収まったあとの組");

        // 差し替えた上限で、完了のときの確認が消す
        app.Ui(() => module.CacheManager.LimitBytesSource = () => 100);
        using var dir = new TempDir();
        var png = SkiaBytes.Png(8, 8, Img.Pattern(8, 8, alpha: false));
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        Check.Equal(1, importer.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", png))).Added, "取り込めた枚数");
        UiHost.Idle();
        app.Ui(() =>
        {
            module.Auto.Environment = BandTestKit.Env();
            module.Pipeline = new FakePipeline { FrameCount = 1, Content = _ => new byte[10] };
        });
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => module.Auto.State == ExportState.Done, Wait), "書き出しが完了しない");
        UiHost.Idle();
        Check.True(module.CacheWork.Wait(Wait), "完了のときの確認が終わらない");
        Gone(oldPath!, "完了のときの確認で古い組");
    }

    [Test]
    public void AC45_7_INV5_確認と削除はUIスレッドの外で動く()
    {
        var threads = new System.Collections.Concurrent.ConcurrentBag<(string Op, int Thread)>();
        var fake = new FakeFileSystem
        {
            Observer = (op, path) =>
            {
                if (op is FileOps.GetLength or FileOps.Delete && path.Contains("cache", StringComparison.Ordinal)) threads.Add((op, Environment.CurrentManagedThreadId));
            },
        };
        var clock = FakeClock.ForPhase5();
        using var app = TestApp.Start(prepare: p => WritePair(p.CacheExportsDir, clock, "aaaa", 1000, Days(1)), clock: clock, fileSystem: fake);
        var module = app.Ui(() => app.Shell.Modules.OfType<ExportBandModule>().Single());
        var ui = app.Ui(() => Environment.CurrentManagedThreadId);
        UiHost.Idle();
        Check.True(module.CacheWork.Wait(Wait), "起動時の確認が終わらない");
        Check.True(threads.Any(t => t.Op == FileOps.GetLength), "起動時の確認でファイルを見ていない");

        app.Ui(() =>
        {
            var screens = app.Shell.Modules.OfType<ScreensModule>().Single();
            screens.ShowSettings();
            screens.Settings!.ClearCache.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        });
        UiHost.Idle();
        Check.True(module.CacheWork.Wait(Wait), "削除が終わらない");
        Check.True(threads.Any(t => t.Op == FileOps.Delete), "削除のボタンでファイルを消していない");
        Check.True(threads.All(t => t.Thread != ui), "UI スレッドでファイルを触った");
    }

    [Test]
    public void AC45_8_実出力の表示でlastUsedが偽の時計の時刻になる()
    {
        using var b = StartBand();
        {
            b.Advance(600);
            b.WaitDone("書き出し");
            var entry = b.App.Ui(() => b.Module.Auto.Entry!);
            b.Advance(5000);
            var meta = b.Module.Cache.MetaPath(entry.Key);
            Check.True(ExportCache.ReadMeta(File.ReadAllBytes(meta))!.LastUsed < b.App.Clock.UtcNow, "表示の前は古い");
            b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));
            Check.True(UiHost.WaitUntil(() => b.Module.PendingShow is null && b.Canvas.IsActualOutput, Wait), "実出力に切り替わらない");
            Check.Equal(b.App.Clock.UtcNow, ExportCache.ReadMeta(File.ReadAllBytes(meta))!.LastUsed, "表示の lastUsed");
        }
    }

    [Test]
    public void AC45_9_設定の画面の削除ボタンが消し上限の項目が上限のバイト数に届く()
    {
        var clock = FakeClock.ForPhase5();
        string? pair = null, h23 = null, h25 = null;
        using var app = TestApp.Start(prepare: p =>
        {
            pair = WritePair(p.CacheExportsDir, clock, "aaaa", 1000, Hours(1));
            h23 = WriteHandoff(p.CacheHandoffDir, clock, "glint-23.gif", 10, Hours(23));
            h25 = WriteHandoff(p.CacheHandoffDir, clock, "glint-25.gif", 10, Hours(25));
        }, clock: clock);
        var module = app.Ui(() => app.Shell.Modules.OfType<ExportBandModule>().Single());
        var screens = app.Ui(() => app.Shell.Modules.OfType<ScreensModule>().Single());
        app.Ui(() =>
        {
            screens.ShowSettings();
            var view = screens.Settings!;
            view.CacheLimit.Text = "2048";
            view.CacheLimit.Commit();
            Check.Equal(2_048_000_000L, module.CacheManager.LimitBytes, "上限の項目の値が届く");
            Check.True(view.ClearCache.IsEnabled, "削除のボタンが無効");
            view.ClearCache.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        });
        UiHost.Idle();
        Check.True(module.CacheWork.Wait(Wait), "削除が終わらない");
        Gone(pair!, "書き出しのキャッシュ");
        Kept(h23!, "23 時間前の実体");
        Gone(h25!, "25 時間前の実体");
    }
}
