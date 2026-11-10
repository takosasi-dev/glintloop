// AC-39 のうち担当 band の分(FR-78・FR-79・§9.9 の 7 と 8)を、偽の時計(FakeClock)・偽の書き出しの中身(FakePipeline)・一時フォルダのキャッシュで調べる。
// (1) 600 ms の待ちと取り消し・.tmp の削除・やり直し、(2) 状態欄の文、(3) キャッシュのキー、(4) ファイルと完成の合図、(5) 使い回し、(6) 書き出しのスレッドが BelowNormal(偽のスケジューラー)。
// (7) の実出力は画面のテスト(BandUiAc39Tests)。UI スレッドの代わりに QueuePost を使い、結果は Pump で受け取る。
using System.IO;
using GlintLoop.App.Export;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Session.SessionTestKit;

namespace GlintLoop.Tests.Exports;

public sealed class BandAc39Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Rig : IDisposable
    {
        public Rig(EditorSession? session = null, string? cacheDir = null, int k = 0)
        {
            Dir = new TempDir();
            Session = session ?? WithBase();
            Cache = new ExportCache(cacheDir ?? System.IO.Path.Combine(Dir.Path, "cache"), Clock);
            K = k;
            Auto = new AutoExport(Session, Catalog, Cache, Clock, Post.Post, () => K);
            Session.Changed += _ => Post.Post(Auto.NotifyEdit);
        }

        public TempDir Dir { get; }
        public EditorSession Session { get; }
        public FakeClock Clock { get; } = new();
        public QueuePost Post { get; } = new();
        public ExportCache Cache { get; }
        public AutoExport Auto { get; }
        public int K { get; set; }

        public void Advance(int ms)
        {
            Post.Pump();
            Clock.Advance(TimeSpan.FromMilliseconds(ms));
            Post.Pump();
        }

        /// <summary>並んでいる仕事が全部終わるまで待ち、結果を受け取る。</summary>
        public void Settle()
        {
            Check.True(Auto.Idle.Wait(Wait), "書き出しの仕事が終わらない");
            Post.Pump();
        }

        public void Dispose()
        {
            Auto.Dispose();
            Dir.Dispose();
        }
    }

    private static string[] Files(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir).Select(f => System.IO.Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToArray() : [];

    [Test]
    public void AC39_1_編集が止まって600msで始まり599msでは始まらない()
    {
        using var rig = new Rig();
        var pipe = new FakePipeline();
        rig.Auto.Environment = BandTestKit.Env();
        rig.Auto.Pipeline = pipe;
        rig.Session.SetLayerOpacity(0, 50);
        rig.Advance(599);
        Check.Equal(ExportState.Idle, rig.Auto.State, "599 ms の状態");
        rig.Settle();
        Check.Equal(0, pipe.Calls, "599 ms で書き出しを呼んだ");
        // 途中で編集が入ると、そこから数え直す
        rig.Session.SetLayerOpacity(0, 60);
        rig.Advance(599);
        rig.Settle();
        Check.Equal(0, pipe.Calls, "編集から 599 ms で書き出しを呼んだ");
        rig.Advance(1);
        Check.Equal(ExportState.Running, rig.Auto.State, "600 ms で計算中");
        rig.Settle();
        Check.Equal(1, pipe.Calls, "600 ms で 1 回");
        Check.Equal(ExportState.Done, rig.Auto.State, "書き終えたら完了");
        Check.Equal(60, pipe.Jobs.Single().Document.Layers[0].Opacity, "始めた時点の文書を書く");
    }

    [Test]
    public void AC39_1_実行中の編集で取り消しの合図が立ちフレームの切れ目で止まり_tmpを消して600ms後にやり直す()
    {
        using var rig = new Rig();
        using var gate = new SemaphoreSlim(0);
        var pipe = new FakePipeline { FrameCount = 10, Gate = gate };
        rig.Auto.Environment = BandTestKit.Env();
        rig.Auto.Pipeline = pipe;
        rig.Advance(600);
        Check.Equal(ExportState.Running, rig.Auto.State, "始まった");
        var key = rig.Auto.Key!;
        var tmp = rig.Cache.TempPath(key, ExportFormat.Gif);
        gate.Release(2);
        Check.True(UiHost.WaitUntil(() => pipe.FramesDone == 2 && pipe.Waiting.IsSet, Wait), "2 枚目まで進まない");
        Check.True(File.Exists(tmp), "書いている間は .tmp がある");
        Check.True(rig.Cache.TryGet(key, ExportFormat.Gif) is null, "書いている間は完成として扱わない");

        // 新しい編集: 取り消しの合図を立て、状態は待機に戻る
        rig.Session.SetLayerOpacity(0, 40);
        rig.Post.Pump();
        Check.Equal(ExportState.Idle, rig.Auto.State, "編集のあとの状態");
        gate.Release(10);
        Check.True(rig.Auto.Idle.Wait(Wait), "取り消した書き出しが止まらない");
        rig.Post.Pump();
        Check.Equal(3, pipe.FramesDone, "門を出たフレームを書き終えたところ(次の切れ目)で止まる");
        Check.True(!File.Exists(tmp), "止めた書き出しの .tmp が残っている");
        Check.True(!File.Exists(rig.Cache.FilePath(key, ExportFormat.Gif)), "止めた書き出しが完成の名前で残っている");
        Check.Equal(1, pipe.Calls, "やり直しの前");

        // その編集の 600 ms 後にやり直す
        rig.Advance(599);
        Check.Equal(1, pipe.Calls, "599 ms ではやり直さない");
        rig.Advance(1);
        Check.Equal(ExportState.Running, rig.Auto.State, "600 ms でやり直す");
        gate.Release(20);
        rig.Settle();
        Check.Equal(2, pipe.Calls, "やり直しの回数");
        Check.Equal(ExportState.Done, rig.Auto.State, "やり直しが完了");
        Check.True(rig.Auto.Key != key, "やり直しは新しい文書のキー");
        Check.True(Files(rig.Cache.Directory).All(f => !f.EndsWith(".tmp", StringComparison.Ordinal)), "完了のあとに .tmp が残っている");
    }

    [Test]
    public void AC39_1_書き出しの中身か環境が無い間は始めない()
    {
        using var rig = new Rig();
        var pipe = new FakePipeline();
        rig.Session.SetLayerOpacity(0, 50);
        rig.Advance(600);
        Check.Equal(ExportState.Idle, rig.Auto.State, "中身も環境も無い");
        rig.Auto.Pipeline = pipe;
        rig.Advance(600);
        Check.Equal(ExportState.Idle, rig.Auto.State, "環境が無い");
        rig.Auto.Environment = BandTestKit.Env();
        rig.Advance(599);
        Check.Equal(ExportState.Idle, rig.Auto.State, "環境を入れてから 599 ms");
        rig.Advance(1);
        rig.Settle();
        Check.Equal(ExportState.Done, rig.Auto.State, "環境を入れてから 600 ms で書く");
    }

    [Test]
    public void AC39_2_状態欄は待機計算中完了失敗と進みと完了の値を出す()
    {
        Check.Equal("待機", BandTexts.Line1(ExportState.Idle, null, null, false), "待機");
        Check.Equal("計算中 3 / 40", BandTexts.Line1(ExportState.Running, new ExportProgress(3, 40, 0, 0), null, false), "計算中(調整なし)");
        Check.Equal("計算中 3 / 40・段 2 / 14", BandTexts.Line1(ExportState.Running, new ExportProgress(3, 40, 2, 14), null, false), "計算中(容量調整あり)");
        Check.Equal("書き出し中 3 / 40", BandTexts.Line1(ExportState.Running, new ExportProgress(3, 40, 0, 0), null, true), "F5 を完成前に押したとき");
        var done = new ExportOutcome("x.gif", ExportFormat.Gif, 123_456, 480, 360, 30, 15, "20→15 fps", false, null);
        Check.Equal("完了 123.5 KB・480×360・30 フレーム・15 fps", BandTexts.Line1(ExportState.Done, null, done, false), "完了(実効 fps ははしごで下げたあとの値)");
        Check.Equal("20→15 fps", BandTexts.Line2(ExportState.Done, done, null), "2 行目は調整の内容");
        Check.Equal("失敗", BandTexts.Line1(ExportState.Failed, null, null, false), "失敗");
        Check.Equal("書き出せませんでした。もう一度試してください", BandTexts.Line2(ExportState.Failed, null, null), "失敗の 2 行目");
        var fail = done with { SizeFail = true, Steps = null, Condition = "正方形" };
        Check.Equal("上限(2000 バイト)に収まりませんでした。最小の設定で 123456 バイトです  条件:正方形を満たしていません",
            BandTexts.Line2(ExportState.Done, fail, 2000), "上限に収まらない・制約の警告");

        // 自動の書き出しの状態と進み
        using var rig = new Rig();
        using var gate = new SemaphoreSlim(0);
        rig.Auto.Environment = BandTestKit.Env();
        rig.Auto.Pipeline = new FakePipeline { FrameCount = 5, Gate = gate, Result = new ExportPipelineResult(64, 48, 5, 12.5, "色数 256→64", false, null) };
        Check.Equal(ExportState.Idle, rig.Auto.State, "最初は待機");
        rig.Advance(600);
        Check.Equal(ExportState.Running, rig.Auto.State, "計算中");
        Check.Equal(new ExportProgress(0, 1, 0, 0), rig.Auto.Progress!, "始めた直後の進み(GIF でモーションなし)");
        gate.Release(3);
        Check.True(UiHost.WaitUntil(() => { rig.Post.Pump(); return rig.Auto.Progress?.FramesDone == 3; }, Wait), "進みが 3 にならない");
        Check.Equal(5, rig.Auto.Progress!.FrameCount, "進みの N");
        gate.Release(10);
        rig.Settle();
        Check.Equal(ExportState.Done, rig.Auto.State, "完了");
        var o = rig.Auto.Outcome!;
        Check.Equal((64, 48, 5, 12.5, "色数 256→64"), (o.Width, o.Height, o.Frames, o.FpsEff, o.Steps), "完了の値");
        Check.Equal(new FileInfo(o.Path).Length, o.Bytes, "ファイルサイズ");

        using var failing = new Rig();
        failing.Auto.Environment = BandTestKit.Env();
        failing.Auto.Pipeline = new FakePipeline { Throw = new InvalidOperationException("偽の失敗") };
        failing.Advance(600);
        failing.Settle();
        Check.Equal(ExportState.Failed, failing.Auto.State, "中身が投げたら失敗");
        Check.True(Files(failing.Cache.Directory).Length == 0, "失敗のあとにファイルが残っている");
    }

    // ---------------------------------------------------------------- (3) キャッシュのキー

    private static SessionDocument TwoLayers()
    {
        var s = WithBase(100, 80);
        s.AddImportedImages([Image(2, 40, 30)]);
        s.AddEmptyLayer();
        s.AddEffect(Target.OfLayer(1), "invert");
        s.AddEffect(Target.All, "exposure");
        return s.Document!;
    }

    private static SessionDocument WithLayer(SessionDocument d, int i, Func<Layer, Layer> f)
    {
        var layers = d.Layers.ToArray();
        layers[i] = f(layers[i]);
        return d with { Layers = layers };
    }

    [Test]
    public void AC39_3_キャッシュのキーは項目を1つ変えると変わり名前と選択では変わらない()
    {
        var env = BandTestKit.Env();
        var doc = TwoLayers();
        var key = ExportCacheKey.Of(doc, 0, env);
        Check.Equal(ExportCacheKey.Length, key.Length, "キーの桁数");
        Check.True(key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "16 進の小文字");
        Check.Equal(key, ExportCacheKey.Of(TwoLayers(), 0, env), "同じ設定から同じキー");

        var stack1 = doc.Layers[1].Stack;
        var changed = new (string What, SessionDocument Doc)[]
        {
            ("レイヤーの順序", doc with { Layers = [doc.Layers[0], doc.Layers[2], doc.Layers[1]] }),
            ("表示", WithLayer(doc, 1, l => l with { Visible = false })),
            ("不透明度", WithLayer(doc, 1, l => l with { Opacity = 70 })),
            ("ブレンド", WithLayer(doc, 1, l => l with { Blend = BlendMode.Screen })),
            ("配置", WithLayer(doc, 1, l => l with { Place = new Placement(3, 4, 100) })),
            ("ルックの強さ", WithLayer(doc, 1, l => l with { LookStrength = 50 })),
            ("スタックの salt", WithLayer(doc, 1, l => l with { Stack = [stack1[0] with { Salt = stack1[0].Salt + 1 }] })),
            ("スタックのオン・オフ", WithLayer(doc, 1, l => l with { Stack = [stack1[0] with { Enabled = false }] })),
            ("表示がオフのレイヤーの項目", WithLayer(WithLayer(doc, 1, l => l with { Visible = false }), 1, l => l with { Opacity = 30 })),
            ("空のレイヤーの大きさ", WithLayer(doc, 2, l => l with { Size = new EmptySize(10, 11) })),
            ("空のレイヤーの塗り", WithLayer(doc, 2, l => l with { Fill = ColorText.Rgba(0x11223344u) })),
            ("全体のルックの強さ", doc with { All = doc.All with { LookStrength = 40 } }),
            ("全体のスタック", doc with { All = GlobalStack.Empty }),
            ("seed", doc with { Seed = doc.Seed + 1 }),
            ("時間の設定", doc with { Time = doc.Time with { Fps = 12 } }),
            ("停止時間", doc with { Time = doc.Time with { HoldMs = 300 } }),
            ("ソースの SHA-256", WithLayer(doc, 1, l => l with { Asset = Sha(9) })),
            ("出力の形式", doc with { Output = doc.Output with { Format = ExportFormat.Apng } }),
            ("出力のサイズ", doc with { Output = doc.Output with { Size = new OutputSize("long", 200) } }),
            ("容量上限", doc with { Output = doc.Output with { Limit = new OutputLimit("bytes", null, 2000) } }),
            ("GIF の色数", doc with { Output = doc.Output with { Gif = doc.Output.Gif with { Colors = 64 } } }),
            ("ループ", doc with { Time = doc.Time with { Loop = LoopMode.Once } }),
        };
        foreach (var (what, d) in changed) Check.True(ExportCacheKey.Of(d, 0, env) != key, $"{what}を変えてもキーが変わらない");

        Check.True(ExportCacheKey.Of(doc, 0, BandTestKit.Env(version: "0.4.1")) != key, "アプリのバージョン");
        Check.True(ExportCacheKey.Of(doc, 0, BandTestKit.Env(warp: false)) != key, "アダプターの種類");
        Check.True(ExportCacheKey.Of(doc, 0, BandTestKit.Env(adapter: "別の GPU")) != key, "アダプターの説明");
        Check.True(ExportCacheKey.Of(doc, 0, BandTestKit.Env(blueNoise: new string('0', 64))) != key, "青色雑音のタイルの SHA-256");
        Check.True(ExportCacheKey.Of(doc, 0, BandTestKit.Env(mvid: Guid.NewGuid())) != key, "本体アセンブリの ModuleVersionId");

        // 静止形式のときだけフレーム番号 k が入る
        var png = doc with { Output = doc.Output with { Format = ExportFormat.Png } };
        Check.True(ExportCacheKey.Of(png, 3, env) != ExportCacheKey.Of(png, 0, env), "静止形式のフレーム番号 k");
        Check.Equal(key, ExportCacheKey.Of(doc, 3, env), "動く形式ではフレーム番号を入れない");

        var same = new (string What, SessionDocument Doc)[]
        {
            ("レイヤーの名前", WithLayer(doc, 1, l => l with { Name = "別の名前" })),
            ("選んでいる対象", doc with { Selected = Target.OfLayer(2) }),
            ("選んでいる対象(全体)", doc with { Selected = Target.All }),
        };
        foreach (var (what, d) in same) Check.Equal(key, ExportCacheKey.Of(d, 0, env), $"{what}でキーが変わった");
    }

    [Test]
    public void AC39_3_青色雑音のタイルのSHA256は埋め込みの表から作る()
    {
        var sha = ExportEnvironment.BlueNoiseTileSha256;
        Check.Equal(64, sha.Length, "SHA-256 の桁数");
        var json = ExportCacheKey.CanonicalJson(TwoLayers(), 0, BandTestKit.Env());
        Check.True(json.Contains("\"blueNoise\":\"" + sha + "\"", StringComparison.Ordinal), "キーの JSON に青色雑音の SHA-256 が入る");
        Check.True(!json.Contains("\"name\":\"img", StringComparison.Ordinal), "キーの JSON にレイヤーの名前が入っている");
    }

    // ---------------------------------------------------------------- (4)(5) ファイルと使い回し

    [Test]
    public void AC39_4_完成したファイルはキーの名前とjsonで置かれリネームの前は完成でない()
    {
        using var rig = new Rig();
        using var gate = new SemaphoreSlim(0);
        rig.Auto.Environment = BandTestKit.Env();
        rig.Auto.Pipeline = new FakePipeline { FrameCount = 3, Gate = gate, Result = new ExportPipelineResult(48, 32, 3, 20, "色数 256→128", false, null) };
        rig.Clock.UtcNow = new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.Zero);
        rig.Advance(600);
        var key = rig.Auto.Key!;
        gate.Release(1);
        Check.True(UiHost.WaitUntil(() => File.Exists(rig.Cache.TempPath(key, ExportFormat.Gif)), Wait), ".tmp ができない");
        Check.True(!File.Exists(rig.Cache.FilePath(key, ExportFormat.Gif)), "書いている間に完成の名前のファイルがある");
        Check.True(rig.Cache.TryGet(key, ExportFormat.Gif) is null, "リネームの前は完成として扱わない");
        Check.True(!rig.Auto.IsReady, "リネームの前は実出力を出せない");
        gate.Release(10);
        rig.Settle();
        Check.SequenceEqual([key + ".gif", key + ".json"], Files(rig.Cache.Directory), "置かれたファイル");
        Check.True(rig.Auto.IsReady, "完成したら出せる");

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(rig.Cache.MetaPath(key)));
        var r = json.RootElement;
        Check.SequenceEqual(["bytes", "width", "height", "frames", "fpsEff", "steps", "createdAt", "lastUsed"],
            r.EnumerateObject().Select(p => p.Name).ToArray(), "json の項目");
        Check.Equal(new FileInfo(rig.Cache.FilePath(key, ExportFormat.Gif)).Length, r.GetProperty("bytes").GetInt64(), "bytes");
        Check.Equal((48, 32, 3, 20.0, "色数 256→128"),
            (r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), r.GetProperty("frames").GetInt32(), r.GetProperty("fpsEff").GetDouble(),
                r.GetProperty("steps").GetString()), "json の値");
        Check.Equal("2026-10-29T01:02:03.600Z", r.GetProperty("createdAt").GetString(), "createdAt(時計を 600 ms 進めて始めた時刻)");

        // .tmp だけ、json だけのものは完成として扱わない
        File.WriteAllBytes(rig.Cache.TempPath("0123456789abcdef0123456789abcdef", ExportFormat.Png), [1, 2, 3]);
        Check.True(rig.Cache.TryGet("0123456789abcdef0123456789abcdef", ExportFormat.Png) is null, ".tmp だけのもの");
        File.WriteAllBytes(rig.Cache.MetaPath("fedcba9876543210fedcba9876543210"), File.ReadAllBytes(rig.Cache.MetaPath(key)));
        Check.True(rig.Cache.TryGet("fedcba9876543210fedcba9876543210", ExportFormat.Gif) is null, "json だけのもの");
    }

    [Test]
    public void AC39_5_同じ設定の2回目はエンコーダーを呼ばずにキャッシュを使いバイト列が一致する()
    {
        using var dir = new TempDir();
        var cacheDir = System.IO.Path.Combine(dir.Path, "cache");
        byte[] first;
        string path;
        using (var rig = new Rig(cacheDir: cacheDir))
        {
            var pipe = new FakePipeline();
            rig.Auto.Environment = BandTestKit.Env();
            rig.Auto.Pipeline = pipe;
            rig.Advance(600);
            rig.Settle();
            path = rig.Auto.Outcome!.Path;
            first = File.ReadAllBytes(path);
            Check.Equal(1, pipe.Calls, "1 回目");

            // 変えて戻すと同じキーになり、ファイルを使い回す
            rig.Session.SetLayerOpacity(0, 50);
            rig.Advance(600);
            rig.Settle();
            Check.Equal(2, pipe.Calls, "変えたあと");
            rig.Session.Undo();
            rig.Clock.UtcNow += TimeSpan.FromMinutes(5);
            rig.Advance(600);
            rig.Settle();
            Check.Equal(2, pipe.Calls, "戻したあとはエンコーダーを呼ばない");
            Check.Equal(ExportState.Done, rig.Auto.State, "使い回しで完了");
            Check.Equal(path, rig.Auto.Outcome!.Path, "同じファイル");
            Check.True(File.ReadAllBytes(rig.Auto.Outcome.Path).AsSpan().SequenceEqual(first), "バイト列が 1 回目と一致する");
            var meta = ExportCache.ReadMeta(File.ReadAllBytes(rig.Cache.MetaPath(rig.Auto.Key!)))!;
            Check.True(meta.LastUsed > meta.CreatedAt, "使い回すと lastUsed を書き直す");
        }

        // 起動し直しても(同じキャッシュのフォルダ)使い回す
        var session = WithBase();
        using (var again = new Rig(session, cacheDir))
        {
            var pipe = new FakePipeline();
            again.Auto.Environment = BandTestKit.Env();
            again.Auto.Pipeline = pipe;
            again.Advance(600);
            again.Settle();
            Check.Equal(0, pipe.Calls, "起動し直したあとの 2 回目");
            Check.True(File.ReadAllBytes(again.Auto.Outcome!.Path).AsSpan().SequenceEqual(first), "起動し直したあとのバイト列");
        }
    }

    [Test]
    public void AC39_5_使い回しの完了も上限と制約の警告を出し直す()
    {
        using var rig = new Rig();
        var pipe = new FakePipeline { CheckResult = new ExportCheck(10, "正方形"), Result = new ExportPipelineResult(48, 32, 1, 20, null, true, "正方形") };
        rig.Auto.Environment = BandTestKit.Env();
        rig.Auto.Pipeline = pipe;
        rig.Advance(600);
        rig.Settle();
        rig.Session.SetLayerOpacity(0, 50);
        rig.Session.Undo();
        rig.Advance(600);
        rig.Settle();
        Check.Equal(1, pipe.Calls, "2 回目は使い回し");
        Check.True(rig.Auto.Outcome!.SizeFail, "上限(10 バイト)を超えるファイルは size_fail");
        Check.Equal("正方形", rig.Auto.Outcome.Condition, "制約の警告");
    }

    [Test]
    public void AC39_6_書き出しの仕事は渡したBelowNormalのスケジューラーのスレッドで動く()
    {
        using var scheduler = new BelowNormalTestScheduler();
        using var rig = new Rig();
        var pipe = new FakePipeline();
        rig.Auto.Scheduler = scheduler;
        rig.Auto.Environment = BandTestKit.Env();
        rig.Auto.Pipeline = pipe;
        rig.Advance(600);
        rig.Settle();
        Check.Equal(ExportState.Done, rig.Auto.State, "完了");
        Check.SequenceEqual([ThreadPriority.BelowNormal], pipe.Priorities.ToArray(), "書き出しのスレッドの優先度");
    }

    [Test]
    public void AC39_3_静止形式はスクラブバーの位置のkを書き動く形式はkを使わない()
    {
        var session = WithBase();
        session.AddEffect(Target.OfLayer(0), "spin");
        session.SetOutputFormat(ExportFormat.Png);
        using var rig = new Rig(session, k: 7);
        var pipe = new FakePipeline();
        rig.Auto.Environment = BandTestKit.Env();
        rig.Auto.Pipeline = pipe;
        rig.Advance(600);
        rig.Settle();
        var job = pipe.Jobs.Single();
        Check.Equal((7, 40), (job.FrameK, job.TimelineFrames), "静止形式の k と N");
        rig.Session.SetOutputFormat(ExportFormat.Gif);
        rig.Advance(600);
        rig.Settle();
        Check.Equal(0, pipe.Jobs.Last().FrameK, "動く形式の k");
    }
}
