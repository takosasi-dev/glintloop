// 容量上限の自動調整の検査(AC-38 の (1)〜(5)、FR-75・FR-76・§9.9 の 2〜4)。はしごの段の列、探索の手順、打ち切り、調整の文字、プリセットと制約。
// (2) は偽の fits、(3) は偽のエンコーダー(書くたびに上限の数えを通す)を ExportJob に渡して数える。GPU を使わない。
// 期待値は AC の本文の値をそのまま書く。
using System.IO;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Exports;

public sealed class LadderAc38Tests
{
    private static readonly SizeD Square = new(1000, 1000);

    internal static ExportSettings S(string format, int colors = 256, int fps = 20, int length10 = 20, int lout = 480, int quality = 90,
        bool palette = false, bool lossless = false)
    {
        var n = ExportFormat.IsAnimated(format) ? TimeModel.FrameCount(length10, fps) : 1;
        return new ExportSettings
        {
            Format = format,
            Width = lout,
            Height = lout,
            LongSide = lout,
            FrameCount = n,
            TimelineFrames = n,
            Fps = fps,
            Length10 = length10,
            Colors = colors,
            Quality = quality,
            ApngPalette = palette,
            Lossless = lossless,
        };
    }

    private static List<string> Names(ExportSettings r0) => Ladder.Build(r0, Square).Select(s => s.Kind switch
    {
        LadderStepKind.Palette => "パレット化(256 色)",
        LadderStepKind.Colors => $"色数 {s.Value}",
        LadderStepKind.Fps => $"{s.Value} fps(N={s.Settings.FrameCount})",
        LadderStepKind.LongSide => $"長辺 {s.Value}",
        _ => $"品質 {s.Value}",
    }).ToList();

    [Test]
    public void AC38_1_はしごの段の列()
    {
        Check.SequenceEqual(
            ["色数 128", "色数 64", "15 fps(N=30)", "長辺 408", "色数 48", "10 fps(N=20)", "長辺 347", "色数 32", "長辺 295", "5 fps(N=10)", "長辺 251", "色数 24", "長辺 213", "色数 16"],
            Names(S("gif")), "GIF・色数 256・F=20・L=2.0・長辺 480(14 段)");
        Check.SequenceEqual(
            ["15 fps(N=30)", "長辺 408", "色数 48", "10 fps(N=20)", "長辺 347", "色数 32", "長辺 295", "5 fps(N=10)", "長辺 251", "色数 24", "長辺 213", "色数 16"],
            Names(S("gif", colors: 64)), "GIF・色数 64(12 段)");
        var f5 = Names(S("gif", fps: 5));
        Check.Equal(11, f5.Count, "GIF・色数 256・F=5 の段の数");
        Check.True(f5.All(x => !x.Contains("fps")), "F=5 は fps の段をすべて飛ばす");

        var expect = new Dictionary<int, int> { [8] = 5, [10] = 5, [12] = 8, [15] = 10, [20] = 15, [24] = 15, [25] = 15, [30] = 20, [40] = 30, [50] = 30 };
        foreach (var (f, to) in expect)
        {
            Check.Equal<int?>(to, Ladder.LowerFps(f), $"F={f} の fps の段の下げ先");
            var first = Ladder.Build(S("gif", fps: f), Square).First(s => s.Kind == LadderStepKind.Fps);
            Check.Equal(to, first.Value, $"F={f} のはしごの最初の fps の段");
        }
        Check.Equal<int?>(null, Ladder.LowerFps(5), "F=5 は下げ先が無い");

        string[] longs = ["長辺 408", "長辺 347", "長辺 295", "長辺 251", "長辺 213", "長辺 181", "長辺 154", "長辺 131", "長辺 111", "長辺 94", "長辺 80", "長辺 68"];
        Check.SequenceEqual(["品質 85", "品質 80", "品質 75", "品質 70", "品質 60", "品質 50", "品質 40", .. longs], Names(S("jpeg")), "JPEG・品質 90・長辺 480");
        Check.SequenceEqual(["品質 70", "品質 60", "品質 50", "品質 40", .. longs], Names(S("jpeg", quality: 75)), "JPEG・品質 75");
        Check.SequenceEqual([.. longs, "長辺 58", "長辺 49", "長辺 42", "長辺 36"], Names(S("png")), "PNG・長辺 480(16 段)");
        Check.SequenceEqual(["長辺 17", "長辺 16"], Names(S("png", lout: 20)), "PNG・長辺 20");
        Check.Equal(0, Names(S("png", lout: 16)).Count, "PNG・長辺 16 は 0 段");

        var apng = Names(S("apng"));
        Check.Equal("パレット化(256 色)", apng[0], "APNG・32 ビットの最初の段");
        Check.True(Names(S("apng", palette: true)).All(x => !x.StartsWith("パレット", StringComparison.Ordinal)), "パレットの APNG にはパレット化の段が無い");
        Check.SequenceEqual(
            ["パレット化(256 色)", "色数 128", "色数 64", "15 fps(N=30)", "長辺 408", "色数 48", "10 fps(N=20)", "長辺 347", "色数 32", "長辺 295", "5 fps(N=10)", "長辺 251", "色数 16"],
            apng, "APNG・32 ビットの段の列(§9.9 の 3 の表)");
        Check.SequenceEqual(["品質 65", "品質 55", "品質 45", "品質 35", .. longs], Names(S("webp", quality: 70)), "WebP(非可逆)・品質 70");
        Check.Equal(16, Names(S("webp", lossless: true)).Count, "WebP(ロスレス)の段の数");

        // 長辺の段は幅と高さを描画と同じ式で直す
        var wide = Ladder.Build(S("png"), new SizeD(1000, 500))[0].Settings;
        Check.Equal((408, 204), (wide.Width, wide.Height), "長辺 408 の段の幅と高さ(1000×500)");
    }

    private static (List<int> Tried, LimitSearchResult R) Search(int last, Func<int, bool> fits)
    {
        var calls = new List<int>();
        var r = LimitSearch.Run(last, i =>
        {
            calls.Add(i);
            return fits(i);
        });
        Check.SequenceEqual(calls, r.Tried, "fits を呼んだ順と Tried");
        Check.True(calls.Count <= LimitSearch.MaxTrials(last), $"試行の回数 {calls.Count} が上限 {LimitSearch.MaxTrials(last)} を超えた");
        Check.Equal(calls.Count, calls.Distinct().Count(), "同じ段を 2 回調べた");
        return (calls, r);
    }

    [Test]
    public void AC38_2_探索の手順()
    {
        var (t, r) = Search(14, i => i >= 9);
        Check.SequenceEqual([0, 14, 7, 10, 8, 9], t, "R=14・9 以上で真");
        Check.Equal((9, false), (r.Chosen, r.SizeFail), "R=14・9 以上で真の結果");

        (t, r) = Search(14, i => i == 5 || i >= 9);
        Check.SequenceEqual([0, 14, 7, 10, 8, 9], t, "R=14・5 と 9 以上で真");
        Check.Equal((9, false), (r.Chosen, r.SizeFail), "R=14・5 と 9 以上で真の結果(段 5 は使われない)");

        (t, r) = Search(14, _ => false);
        Check.SequenceEqual([0, 14], t, "R=14・すべて偽");
        Check.Equal((14, true), (r.Chosen, r.SizeFail), "R=14・すべて偽は r14 で size_fail");

        (t, r) = Search(14, i => i == 0);
        Check.SequenceEqual([0], t, "R=14・0 で真");
        Check.Equal((0, false), (r.Chosen, r.SizeFail), "R=14・0 で真の結果");

        (t, r) = Search(0, _ => false);
        Check.SequenceEqual([0], t, "R=0・偽");
        Check.Equal((0, true), (r.Chosen, r.SizeFail), "R=0・偽は r0 で size_fail");

        (t, r) = Search(1, i => i == 1);
        Check.SequenceEqual([0, 1], t, "R=1・1 で真");
        Check.Equal((1, false), (r.Chosen, r.SizeFail), "R=1・1 で真の結果");

        (t, r) = Search(12, i => i >= 1);
        Check.SequenceEqual([0, 12, 6, 3, 1], t, "R=12・1 以上で真");
        Check.Equal((1, false), (r.Chosen, r.SizeFail), "R=12・1 以上で真の結果");

        // どの R とどの境目でも、回数は 2+ceil(log2(R)) 以下
        for (var last = 0; last <= 20; last++)
        for (var edge = 0; edge <= last + 1; edge++)
        {
            var e = edge;
            Search(last, i => i >= e);
        }
        Check.Equal(1, LimitSearch.MaxTrials(0), "R=0 の上限");
        Check.Equal(6, LimitSearch.MaxTrials(14), "R=14 の上限");
    }

    /// <summary>段ごとに決まったバイト数を書く偽のエンコーダー。書くたびに上限の数えを通り、打ち切りの通知を数える。</summary>
    private sealed class FakeEncoder(string format, Func<ExportSettings, int> size) : IExportEncoder
    {
        public List<(ExportSettings Settings, long? Limit)> Calls { get; } = [];

        public int Notified { get; private set; }

        public string Format => format;

        public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
        {
            Calls.Add((settings, limitBytes));
            var total = size(settings);
            return ExportEncoding.Run(output, limitBytes, s =>
            {
                for (var written = 0; written < total; written += 100)
                {
                    try
                    {
                        s.Write(new byte[Math.Min(100, total - written)]);
                    }
                    catch (ExportLimitExceededException)
                    {
                        Notified++;
                        throw;
                    }
                }
            });
        }
    }

    private sealed class NoFrames(ExportSettings s) : IExportFrames
    {
        public int Width => s.Width;

        public int Height => s.Height;

        public int FrameCount => s.FrameCount;

        public byte[] GetFrame(int index, CancellationToken ct) => new byte[Width * Height * 4];
    }

    private static ExportRun Run() => new(TaskScheduler.Default, 2, CancellationToken.None);

    private static ExportPlan Plan(ExportSettings r0, long? limit, ExportSettings? user = null, LimitPreset? preset = null) =>
        new(user ?? r0, r0, Square, limit, preset, false);

    private static (ExportJobResult Result, List<MemoryStream> Discarded) Job(ExportPlan plan, IExportEncoder enc)
    {
        var discarded = new List<MemoryStream>();
        var r = ExportJob.Run(plan, enc, s => new NoFrames(s), () => new MemoryStream(), s => discarded.Add((MemoryStream)s), Run());
        return (r, discarded);
    }

    [Test]
    public void AC38_3_試行は上限で打ち切られ結果は使われない()
    {
        // GIF の 14 段。長辺 L の段は L×10 バイト、上限 3000 なら長辺 295 以下の段だけが収まる
        var r0 = S("gif");
        var enc = new FakeEncoder("gif", s => s.LongSide * 10);
        var (r, discarded) = Job(Plan(r0, 3000), enc);
        var steps = Ladder.Build(r0, Square);
        Check.Equal(steps.Count, r.StepCount, "R");
        Check.True(r.Settings.LongSide * 10 <= 3000, "結果は上限に収まる段");
        Check.Equal(false, r.SizeFail, "収まった");
        Check.Equal(r.Bytes, ((MemoryStream)r.Output).Length, "結果の書き先のバイト数");
        Check.True(r.Bytes <= 3000, "結果のバイト数が上限以下");
        Check.Equal(r.Tried.Count, enc.Calls.Count, "試行の回数と符号化の回数");
        Check.Equal(enc.Calls.Count, enc.Calls.Select(c => c.Settings).Distinct().Count(), "同じ段を 2 回符号化した");
        Check.True(enc.Calls.All(c => c.Limit == 3000), "試行はどれも上限つき");
        var overflowed = enc.Calls.Count(c => c.Settings.LongSide * 10 > 3000);
        Check.True(overflowed >= 1, "打ち切られた試行がある");
        Check.Equal(overflowed, enc.Notified, "打ち切りの通知を受けた回数");
        Check.True(discarded.All(d => d.Length <= 3000), "打ち切った試行は上限を超えて書いていない");
        Check.Equal(enc.Calls.Count - 1, discarded.Count, "使わなかった試行の書き先は捨てる");
        Check.True(!discarded.Contains(r.Output), "結果の書き先は捨てない");

        // size_fail: 最後の段だけを打ち切りなしで作り直す
        var all = new FakeEncoder("gif", s => 100_000);
        var (f, fd) = Job(Plan(r0, 3000), all);
        Check.Equal(true, f.SizeFail, "すべて偽は size_fail");
        Check.Equal(steps.Count, f.Step, "最後の段");
        Check.SequenceEqual([0, steps.Count], f.Tried, "size_fail の試行の列");
        Check.Equal(3, all.Calls.Count, "試行 2 回と作り直し 1 回");
        Check.Equal<long?>(null, all.Calls[2].Limit, "作り直しは打ち切りなし");
        Check.Equal(100_000L, f.Bytes, "作り直しの結果のバイト数");
        Check.Equal(2, all.Notified, "打ち切りの通知は試行の 2 回");
        Check.Equal(2, fd.Count, "打ち切った 2 回の書き先を捨てる");

        // 上限なし: はしごを作らず r_0 を打ち切りなしで 1 回
        var none = new FakeEncoder("gif", s => 100_000);
        var (n, _) = Job(Plan(r0, null), none);
        Check.Equal((0, 0, false), (n.Step, n.StepCount, n.SizeFail), "上限なしの段");
        Check.Equal(1, none.Calls.Count, "上限なしの符号化の回数");
        Check.Equal<long?>(null, none.Calls[0].Limit, "上限なしは打ち切りなし");
        Check.Equal<string?>(null, n.Steps, "上限なしは調整の文字が無い");
    }

    [Test]
    public void AC38_4_調整の文字()
    {
        Check.Equal("色数 256→64 / 20→15 fps / 長辺 480→408 px",
            Ladder.Describe(S("gif"), S("gif", colors: 64, fps: 15, lout: 408)), "(256, 20, 480) → (64, 15, 408)");
        Check.Equal("色数 256→64", Ladder.Describe(S("gif"), S("gif", colors: 64)), "(256, 20, 480) → (64, 20, 480)");
        Check.Equal("品質 90→75 / 長辺 480→408 px",
            Ladder.Describe(S("jpeg", quality: 90), S("jpeg", quality: 75, lout: 408)), "JPEG (90, 480) → (75, 408)");
        Check.Equal("パレット化", Ladder.Describe(S("apng"), S("apng", palette: true)), "APNG 32 ビット → パレット");
        Check.Equal<string?>(null, Ladder.Describe(S("gif"), S("gif")), "同じ設定は何も出ない");

        // slack_emoji で L=3.0・F=40: r_0 は F'=15。r_0 のまま収まっても「40→15 fps」が出る
        var preset = LimitPresets.Find("slack_emoji")!;
        var user = S("gif", fps: 40, length10: 30);
        var (fps, _) = LimitPresets.FpsForMaxFrames(30, 40, preset.MaxFrames!.Value);
        Check.Equal(15, fps, "slack_emoji の F'");
        var r0 = OutputPlan.WithFps(user, fps);
        var (r, _) = Job(Plan(r0, preset.LimitBytes, user, preset), new FakeEncoder("gif", _ => 10));
        Check.Equal(0, r.Step, "r_0 のまま収まる");
        Check.Equal("40→15 fps", r.Steps, "r_0 のままの調整の文字");
    }

    [Test]
    public void AC38_5_プリセットと制約()
    {
        string[] keys = ["none", "discord_emoji", "discord_sticker", "slack_emoji", "line_stamp", "chat_small", "chat_medium"];
        Check.SequenceEqual(keys, LimitPresets.All.Select(p => p.Key).ToList(), "7 個のキー");
        void Row(string key, string? fmt, long? bytes, int? lout, LoopMode? loop, LimitConstraint[] cons)
        {
            var p = LimitPresets.Find(key)!;
            Check.Equal(fmt, p.Format, key + " の形式");
            Check.Equal(bytes, p.LimitBytes, key + " の上限");
            Check.Equal(lout, p.LongSide, key + " の長辺");
            Check.Equal(loop, p.Loop, key + " のループ");
            Check.SequenceEqual(cons, p.Constraints, key + " の制約");
        }
        Row("none", null, null, null, null, []);
        Row("discord_emoji", "gif", 255_000, 128, LoopMode.Infinite, []);
        Row("discord_sticker", "apng", 500_000, 320, LoopMode.Infinite, [LimitConstraint.Square, LimitConstraint.Fps]);
        Row("slack_emoji", "gif", 120_000, 128, LoopMode.Infinite, [LimitConstraint.FrameMax]);
        Row("line_stamp", "apng", 990_000, 320, LoopMode.Once,
            [LimitConstraint.FrameRange, LimitConstraint.LoopLength, LimitConstraint.LongSide, LimitConstraint.ShortSide, LimitConstraint.LoopCount]);
        Row("chat_small", "gif", 1_000_000, 320, LoopMode.Infinite, []);
        Row("chat_medium", "gif", 8_000_000, 480, LoopMode.Infinite, []);
        Check.Equal<int?>(50, LimitPresets.Find("slack_emoji")!.MaxFrames, "slack_emoji のフレーム数の上限");

        // 選ぶと形式・長辺・ループが設定される(line_stamp は once で apng.loops が 1、none は変更なし、残りは infinite)
        var output = OutputSettings.Default with { Format = "png", Size = new OutputSize("scale", 50), Apng = new ApngOptions(false, 3) };
        var time = new TimeSettings(20, 20, LoopMode.Once, 0);
        foreach (var p in LimitPresets.All)
        {
            var (o, t) = LimitPresets.Apply(p, output, time);
            if (p.Key == "none")
            {
                Check.Equal(output with { Limit = new OutputLimit("none", null, null) }, o, "none の出力設定");
                Check.Equal(time, t, "none の時間");
                continue;
            }
            Check.Equal(p.Format, o.Format, p.Key + " を選んだ形式");
            Check.Equal(new OutputSize("long", p.LongSide), o.Size, p.Key + " を選んだ長辺");
            Check.Equal(new OutputLimit("preset", p.Key, null), o.Limit, p.Key + " を選んだ容量上限");
            Check.Equal(p.Key == "line_stamp" ? LoopMode.Once : LoopMode.Infinite, t.Loop, p.Key + " を選んだループ");
            Check.Equal(p.Key == "line_stamp" ? 1L : 3L, o.Apng.Loops, p.Key + " を選んだ apng.loops");
        }

        // slack_emoji の fps の自動の下げ(セッションの F は変えない)
        Check.Equal((25, false), LimitPresets.FpsForMaxFrames(20, 40, 50), "(L=2.0, F=40)");
        Check.Equal(50, TimeModel.FrameCount(20, 25), "F'=25 の N");
        Check.Equal((8, false), LimitPresets.FpsForMaxFrames(60, 20, 50), "(L=6.0, F=20)");
        Check.Equal(48, TimeModel.FrameCount(60, 8), "F'=8 の N");
        Check.Equal((5, false), LimitPresets.FpsForMaxFrames(100, 20, 50), "(L=10.0, F=20)");
        Check.Equal(50, TimeModel.FrameCount(100, 5), "F'=5 の N");
        Check.Equal((20, false), LimitPresets.FpsForMaxFrames(20, 20, 50), "収まるときはそのまま");

        // 文書から計画を作ると、r_0 だけが下がり、ユーザーの設定と文書の F は変わらない
        var s = SessionTestKit.NewSession();
        s.AddImportedImages([SessionTestKit.Image(1, 300, 300)]);
        Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足す");
        var doc = s.Document! with
        {
            Time = new TimeSettings(20, 40, LoopMode.Infinite, 0),
            Output = OutputSettings.Default with { Limit = new OutputLimit("preset", "slack_emoji", null) },
        };
        var plan = OutputPlan.Resolve(doc, SessionTestKit.Catalog, _ => new SizeD(300, 300), 0);
        Check.Equal((25, 50), (plan.R0.Fps, plan.R0.FrameCount), "r_0 の fps と N");
        Check.Equal((40, 80), (plan.User.Fps, plan.User.FrameCount), "ユーザーの設定の fps と N");
        Check.Equal(40, doc.Time.Fps, "文書の F");
        Check.Equal<long?>(120_000, plan.LimitBytes, "slack_emoji の上限");

        // 制約に反すると {内容} に名前が表の順に「、」でつながる。値は変わらない
        var sticker = LimitPresets.Find("discord_sticker")!;
        var wide = S("apng", lout: 320) with { Height = 160 };
        Check.Equal("320×320 の正方形", LimitPresets.Condition(sticker, wide), "discord_sticker の正方形");
        Check.Equal<string?>(null, LimitPresets.Condition(sticker, S("apng", lout: 320)), "320×320 は満たす");
        var line = LimitPresets.Find("line_stamp")!;
        var bad = S("apng", fps: 20, length10: 50, lout: 400) with { Loop = LoopMode.Infinite };
        Check.Equal("フレーム数 5〜20、ループ長 4.0 秒以下、長辺 270〜320 px、短辺 270 px 以下、ループ 1 回",
            LimitPresets.Condition(line, bad), "line_stamp のすべての制約");
        var before = bad with { };
        LimitPresets.Condition(line, bad);
        Check.Equal(before, bad, "制約の検査は値を変えない");
        var ok = S("apng", fps: 10, length10: 20, lout: 300) with { Height = 250, Loop = LoopMode.Once, ApngLoops = 1 };
        Check.Equal<string?>(null, LimitPresets.Condition(line, ok), "line_stamp を満たす設定");
        Check.Equal("ループ 1 回", LimitPresets.Condition(line, ok with { ApngLoops = 2 }), "apng.loops が 2");
        var slack = LimitPresets.Find("slack_emoji")!;
        Check.Equal("フレーム数 50 以下", LimitPresets.Condition(slack, S("gif", fps: 5, length10: 200)), "5 fps でも 50 を超える");
    }
}
