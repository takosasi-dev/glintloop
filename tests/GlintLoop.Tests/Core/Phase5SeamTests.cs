// Phase 5 の差し込み口(§11 Phase 5 の前書き)の検査: IFileSystem の本物の開き方(INV-2)・偽のファイルシステムの失敗の起こし方・書き手が口を通ること・
// 偽のクリップボードと偽のドラッグ・デバイスの作り方・キャッシュの管理の形・書き出しの完了のイベント・偽の時計の Phase 5 の時刻。
// 機能そのものの受け入れ基準(AC-42〜AC-50)は担当ごとのテストが調べる。
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GlintLoop.App.Export;
using GlintLoop.App.Platform;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Exports;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Core;

public sealed class Phase5SeamTests
{
    // ---------------------------------------------------------------- IFileSystem の本物

    [Test]
    public void INV2_本物の読み取りはほかのアプリが書いている間も読め元のファイルを変えない()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.bin", [1, 2, 3, 4]);
        var written = File.GetLastWriteTimeUtc(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            using (var s = RealFileSystem.Instance.OpenRead(path))
            {
                Check.True(s.CanRead && !s.CanWrite, "読み取り専用で開く");
            }
            Check.SequenceEqual<byte>([1, 2, 3, 4], RealFileSystem.Instance.ReadAllBytes(path), "全体を読む");
        }
        Check.Equal(written, File.GetLastWriteTimeUtc(path), "最終更新時刻が変わらない");
    }

    [Test]
    public void 本物の各操作は説明のとおりに動く()
    {
        using var dir = new TempDir();
        var fs = RealFileSystem.Instance;
        var sub = dir.PathOf("x");
        Check.True(!fs.DirectoryExists(sub), "はじめはフォルダが無い");
        fs.CreateDirectory(Path.Combine(sub, "y"));
        Check.True(fs.DirectoryExists(sub), "途中のフォルダも作る");

        var a = Path.Combine(sub, "a.txt");
        using (var s = fs.Create(a)) s.Write([1, 2, 3]);
        using (var s = fs.OpenAppend(a)) s.Write([4]);
        Check.Equal(4L, fs.GetLength(a), "追記した長さ");
        var b = Path.Combine(sub, "b.txt");
        using (var s = fs.Create(b)) s.Write([9]);

        fs.Move(a, b);
        Check.True(!fs.Exists(a) && fs.Exists(b), "移動で元が消える");
        Check.Equal(4L, fs.GetLength(b), "移動先を置き換える");
        Check.SequenceEqual([b], fs.EnumerateFiles(sub, "*.txt").ToArray(), "直下だけを列挙する");

        var when = new DateTime(2026, 10, 14, 11, 38, 5, DateTimeKind.Utc);
        fs.SetLastWriteUtc(b, when);
        Check.Equal(when, fs.GetLastWriteUtc(b), "最終更新時刻");

        fs.Delete(b);
        Check.True(!fs.Exists(b), "消える");
        fs.Delete(b); // 無くても例外にしない
    }

    // ---------------------------------------------------------------- 偽のファイルシステム

    [Test]
    public void 偽のファイルシステムは指定の操作とパスで投げ操作ごとに数える()
    {
        using var dir = new TempDir();
        var fake = new FakeFileSystem();
        var a = dir.Write("a.bin", [1]);
        var b = dir.Write("b.bin", [2]);
        fake.FailOn(FileOps.ReadAllBytes, p => p == b, () => new UnauthorizedAccessException());
        Check.SequenceEqual<byte>([1], fake.ReadAllBytes(a), "条件に合わないパスは素通し");
        Check.Throws<UnauthorizedAccessException>(() => fake.ReadAllBytes(b), "条件に合うパス");
        Check.Equal(2, fake.Count(FileOps.ReadAllBytes), "投げた呼び出しも数える");
        Check.Equal(0, fake.Count(FileOps.Move), "呼んでいない操作は 0");
        fake.ClearFailures();
        Check.SequenceEqual<byte>([2], fake.ReadAllBytes(b), "消したあとは素通し");

        // skip 回は通し、そのあと投げ続ける
        fake.FailOn(FileOps.Exists, skip: 1);
        Check.True(fake.Exists(a), "1 回目は通す");
        Check.Throws<IOException>(() => fake.Exists(a), "2 回目");
        Check.Throws<IOException>(() => fake.Exists(a), "3 回目");
    }

    [Test]
    public void INV16_偽のファイルシステムの失敗で最終的な名前は変わらず_tmpも残らない()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.json", [7]);
        foreach (var op in new[] { FileOps.Create, FileOps.Write, FileOps.Move })
        {
            var fake = new FakeFileSystem();
            fake.FailOn(op);
            Check.Throws<IOException>(() => AtomicFile.WriteAllBytes(path, [8, 8], fake), op);
            Check.SequenceEqual<byte>([7], File.ReadAllBytes(path), $"{op} の失敗で元のファイルが変わらない");
            Check.True(!File.Exists(AtomicFile.TempPathFor(path)), $"{op} の失敗で .tmp が残らない");
        }
        var ok = new FakeFileSystem();
        AtomicFile.WriteAllBytes(path, [8, 8], ok);
        Check.SequenceEqual<byte>([8, 8], File.ReadAllBytes(path), "失敗が無ければ置き換わる");
        Check.Equal(1, ok.Count(FileOps.Move), "リネームは 1 回");
    }

    [Test]
    public void 書き手は渡した偽のファイルシステムを通る()
    {
        using var dir = new TempDir();
        var fake = new FakeFileSystem();
        var clock = new FakeClock();

        var paths = AppPaths.UnderOneRoot(dir.Path);
        using (var store = new SettingsStore(paths, clock, new MemoryLogSink(), fake))
        {
            store.Load();
            Check.Equal(1, fake.Count(FileOps.Exists), "設定の読み込み");
            store.SaveNow();
            Check.Equal(1, fake.Count(FileOps.Create), "設定の保存");
        }

        var cache = new ExportCache(dir.PathOf("exports"), clock, fake);
        var before = fake.Count(FileOps.Move);
        var entry = cache.Write(new string('a', 32), ExportFormat.Gif, s =>
        {
            s.WriteByte(1);
            return new ExportWritten(1, 1, 1, 1, null);
        });
        Check.Equal(before + 2, fake.Count(FileOps.Move), "キャッシュの json と本体のリネーム");
        Check.True(fake.Calls.Any(c => c.Operation == FileOps.Write && c.Path.EndsWith(".gif.tmp", StringComparison.Ordinal)), "キャッシュの .tmp に書く");
        var reads = fake.Count(FileOps.ReadAllBytes);
        Check.True(cache.TryGet(entry.Key, ExportFormat.Gif) is not null, "完成したファイルが読める");
        Check.Equal(reads + 1, fake.Count(FileOps.ReadAllBytes), "キャッシュの json を読む");

        var looks = new UserLookStore(dir.PathOf("looks"), SessionTestKit.Catalog, fileSystem: fake);
        var creates = fake.Count(FileOps.Create);
        looks.Save("ルック", []);
        Check.Equal(creates + 1, fake.Count(FileOps.Create), "ルックの保存");

        var presets = new UserExportPresetStore(dir.PathOf("presets"), fileSystem: fake);
        creates = fake.Count(FileOps.Create);
        presets.Save("プリセット", OutputSettings.Default);
        Check.Equal(creates + 1, fake.Count(FileOps.Create), "プリセットの保存");

        var log = new JsonlLogWriter(dir.PathOf("logs"), clock, "0", fileSystem: fake);
        log.Write(LogEvents.AppExit(0));
        Check.Equal(1, fake.Count(FileOps.OpenAppend), "ログの追記");
        var lastWrites = fake.Count(FileOps.GetLastWriteUtc);
        Check.Equal(0, JsonlLogWriter.CleanUp(dir.PathOf("logs"), SystemClock.Instance, fake), "新しいログは消さない");
        Check.Equal(lastWrites + 1, fake.Count(FileOps.GetLastWriteUtc), "ログの片付けは偽物で時刻を読む");

        var png = dir.Write("p.png", SkiaBytes.Png(4, 4, Img.Solid(4, 4, 1, 2, 3)));
        var opens = fake.Count(FileOps.OpenRead);
        var importer = new ImageImporter(new SessionImportTarget(SessionTestKit.NewSession()), new MemoryLogSink(), fileSystem: fake);
        Check.Equal(1, importer.Open([png]).Added, "取り込める");
        Check.Equal(opens + 1, fake.Count(FileOps.OpenRead), "取り込みの読み取り");

        foreach (var n in Enumerable.Range(1, 6)) dir.Write($"recovery-2026101{n}-000000.glint", [0]);
        Check.Equal(1, StartupMaintenance.TrimRecovery(dir.Path, fake), "退避の整理は偽物を通る");
    }

    [Test]
    public void 起動の組み立ては偽のファイルシステムを書き手に渡す()
    {
        using var app = TestApp.Start(show: false);
        Check.True(app.FileSystem.Count(FileOps.DirectoryExists) > 0, "起動時の片付け");
        Check.True(app.FileSystem.Calls.Any(c => c.Operation == FileOps.Exists && c.Path == app.Paths.SettingsFile), "設定の読み込み");
        app.Ui(() => app.Shell.Log.Write(LogEvents.AppExit(0)));
        Check.True(app.FileSystem.Count(FileOps.OpenAppend) > 0, "ログ");
        var band = app.Ui(() => app.Shell.Modules.OfType<ExportBandModule>().Single());
        Check.True(app.FileSystem.Calls.Any(c => c.Operation == FileOps.DirectoryExists && c.Path == app.Paths.PresetsDir), "書き出しプリセット");
        Check.True(app.FileSystem.Calls.Any(c => c.Operation == FileOps.DirectoryExists && c.Path == app.Paths.LooksDir), "ルック");
        Check.Equal(app.Paths.CacheHandoffDir, band.CacheManager.HandoffDirectory, "キャッシュの管理の置き場");
        Check.Equal(1024 * CacheManager.BytesPerMB, band.CacheManager.LimitBytes, "上限は設定から");
    }

    // ---------------------------------------------------------------- クリップボードとドラッグ

    [Test]
    public void 偽のクリップボードは置かれたものを記録しHRESULTを指定して投げ続ける()
    {
        var clip = new FakeClipboard();
        var data = new DataObject();
        clip.Place(data, true);
        Check.Equal(1, clip.Placed.Count, "記録");
        Check.True(ReferenceEquals(data, clip.Placed[0].Data), "置かれたデータ");
        Check.True(clip.Placed[0].KeepAfterExit, "第 2 引数");

        clip.PlaceHResult = unchecked((int)0x800401D0);
        for (var i = 0; i < 3; i++)
        {
            var e = Check.Throws<COMException>(() => clip.Place(new DataObject(), false), "投げ続ける");
            Check.Equal(unchecked((int)0x800401D0), e.HResult, "HRESULT");
        }
        Check.Equal(1, clip.Placed.Count, "投げた回は置かれない");
        Check.Equal(4, clip.PlaceCalls, "呼ばれた回数");
    }

    [Test]
    public void 偽のドラッグは引数を記録し指定の値を返す()
    {
        var drag = new FakeDragRunner { Result = DragDropEffects.None };
        var data = new DataObject();
        var source = UiHost.Invoke(() => new DependencyObject());
        Check.Equal(DragDropEffects.None, drag.Run(source, data, DragDropEffects.Copy), "戻り値");
        Check.True(ReferenceEquals(source, drag.Runs.Single().Source) && ReferenceEquals(data, drag.Runs[0].Data), "渡した部品とデータ");
        Check.Equal(DragDropEffects.Copy, drag.Runs[0].Allowed, "許可する効果");
    }

    // ---------------------------------------------------------------- デバイスの作り方

    [Test]
    public void 描画は渡したデバイスの作り方を通り失敗するとWARPも同じ口で頼む()
    {
        var factory = new FakeDeviceFactory();
        using (var render = RenderService.Create(GpuAdapterRequest.HighPerformance, deviceFactory: factory))
        {
            Check.SequenceEqual([GpuAdapterRequest.HighPerformance], factory.Requests, "要求のアダプターを 1 回頼む");
            Check.True(render.FallbackReason is null, "切り替えていない");
        }

        var failing = new FakeDeviceFactory { Fail = r => r.Kind == GpuAdapterKind.HighPerformance };
        using (var render = RenderService.Create(GpuAdapterRequest.HighPerformance, deviceFactory: failing))
        {
            Check.SequenceEqual([GpuAdapterRequest.HighPerformance, GpuAdapterRequest.Warp], failing.Requests, "失敗したら WARP を 1 回頼む");
            Check.True(render.IsWarp && render.FallbackReason is not null, "WARP に切り替えて理由を持つ");
        }
    }

    [Test]
    public void 起動の組み立ては描画にデバイスの作り方を渡す()
    {
        var factory = new FakeDeviceFactory();
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp, show: false, deviceFactory: factory);
        Check.True(app.Shell.RenderReady!.Wait(TimeSpan.FromSeconds(60)), "描画の準備が終わらない");
        Check.SequenceEqual([GpuAdapterRequest.Warp], factory.Requests, "WARP を 1 回頼む");
    }

    // ---------------------------------------------------------------- キャッシュの管理の形

    [Test]
    public void AC45_1_上限のバイト数の換算と差し替え()
    {
        Check.Equal(256_000_000L, CacheManager.LimitBytesFromMB(256), "256");
        Check.Equal(1_024_000_000L, CacheManager.LimitBytesFromMB(1024), "1024");
        Check.Equal(4_096_000_000L, CacheManager.LimitBytesFromMB(4096), "4096");
        using var dir = new TempDir();
        var m = new CacheManager(dir.PathOf("e"), dir.PathOf("h"), new FakeClock(), new MemoryLogSink(), () => 10_000);
        Check.Equal(10_000L, m.LimitBytes, "関数の値");
        m.LimitBytesSource = () => 5;
        Check.Equal(5L, m.LimitBytes, "差し替え");
    }

    // ---------------------------------------------------------------- 書き出しの完了のイベント

    [Test]
    public void 書き出しの完了のイベントは完了1回につき1回で使い回しも印を付けて鳴る()
    {
        using var dir = new TempDir();
        var session = SessionTestKit.WithBase();
        var clock = new FakeClock();
        var post = new QueuePost();
        using var auto = new AutoExport(session, SessionTestKit.Catalog, new ExportCache(dir.PathOf("cache"), clock), clock, post.Post, () => 0);
        var seen = new List<ExportCompletion>();
        auto.Completed += seen.Add;
        var pipe = new FakePipeline();
        auto.Environment = BandTestKit.Env();
        auto.Pipeline = pipe;

        void Run()
        {
            clock.Advance(AutoExport.Delay);
            Check.True(auto.Idle.Wait(TimeSpan.FromSeconds(20)), "書き出しの仕事が終わらない");
            post.Pump();
        }

        Run();
        Check.Equal(1, seen.Count, "1 回目の完了");
        Check.True(!seen[0].Reused, "1 回目は使い回しでない");
        Check.Equal(auto.Entry, seen[0].Entry, "完了したキャッシュのファイル");

        auto.NotifyEdit();
        Run();
        Check.Equal(1, pipe.Calls, "2 回目はエンコーダーを呼ばない");
        Check.Equal(2, seen.Count, "2 回目の完了");
        Check.True(seen[1].Reused, "2 回目は使い回し");

        // 取り消しでは鳴らない
        auto.NotifyEdit();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        auto.NotifyEdit();
        post.Pump();
        Check.Equal(2, seen.Count, "完了していなければ鳴らない");
    }

    // ---------------------------------------------------------------- 偽の時計

    [Test]
    public void 偽の時計はPhase5の時刻からローカル時刻で始まる()
    {
        var clock = FakeClock.ForPhase5();
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalZone);
        Check.Equal(new DateTime(2026, 10, 14, 20, 38, 5), local.DateTime, "ローカル時刻");
        Check.Equal(TimeSpan.Zero, clock.UtcNow.Offset, "UTC で持つ");
    }
}
