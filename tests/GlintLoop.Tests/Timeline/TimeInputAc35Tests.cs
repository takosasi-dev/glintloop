// 時間の設定の入力の直し(TimeInput)と、EditorSession の時間の操作・振り直しのテスト(AC-35 の (1)(4)、(9) の振り直しと seed、Q-G の読み込み)。
// L は decimal で読み 0.1 秒刻みへ round して端に直す。停止時間は 100 ms 刻みへ round して 0〜5000 に直す。保存した文書の読み込みも同じ規則にする(Q-G)。
// GPU を使わない(文書はコードで組み立てる)。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Timelines;

public sealed class TimeInputAc35Tests
{
    // ---- AC-35 の (1): L の入力 ----

    [Test]
    public void AC35_1_Lの入力はdecimalで読み0_1秒刻みへroundして端に直す()
    {
        // round の例(AC の本文)。0.05 ちょうどは大きい方へ
        foreach (var (input, rounded) in new[] { ("0.25", 0.3m), ("0.35", 0.4m), ("2.04", 2.0m), ("2.05", 2.1m), ("0.4", 0.4m), ("10.1", 10.1m) })
            Check.Equal(rounded, TimeInput.RoundTenths(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)), $"{input} の round");
        // 範囲の端(0.5〜10.0)まで含めた L10。0.25→0.3 と 0.35→0.4 は範囲の外なので 0.5 に直る(報告済みの食い違い)
        foreach (var (input, l10) in new[] { ("2.04", 20), ("2.05", 21), ("0.4", 5), ("10.1", 100), ("0.25", 5), ("0.35", 5), ("0.45", 5), ("0.55", 6), ("9.95", 100), ("2", 20), (" 1.5 ", 15) })
            Check.Equal<int?>(l10, TimeInput.ParseLength10(input), $"「{input}」の L10");
        // 浮動小数を経由すると誤る値(1.15 は double では 1.149999…)
        Check.Equal<int?>(12, TimeInput.ParseLength10("1.15"), "1.15 は 1.2");
        Check.Equal<int?>(100, TimeInput.ParseLength10("1e30"), "大きすぎる値は端");
        Check.Equal<int?>(5, TimeInput.ParseLength10("-3"), "負の値は端");
        foreach (var bad in new[] { "", " ", "abc", "1..2", null })
            Check.Equal<int?>(null, TimeInput.ParseLength10(bad), $"「{bad}」は読めない");
        Check.Equal("2.0", TimeInput.FormatLength10(20), "表示");
        Check.Equal("0.5", TimeInput.FormatLength10(5), "表示");
        Check.Equal("10.0", TimeInput.FormatLength10(100), "表示");
        // L の全 96 通りが表示と読み直しで同じ L10 に戻り、N が 3〜500 に入る
        for (var l10 = TimeModel.MinLength10; l10 <= TimeModel.MaxLength10; l10++)
        {
            Check.Equal<int?>(l10, TimeInput.ParseLength10(TimeInput.FormatLength10(l10)), $"L10={l10} の往復");
            foreach (var f in TimeModel.FpsChoices) Check.InRange(3, 500, TimeModel.FrameCount(l10, f), $"L10={l10} F={f}");
        }
    }

    // ---- AC-35 の (4): 停止時間 ----

    [Test]
    public void AC35_4_停止時間は100ms刻みへroundして0から5000に直る()
    {
        foreach (var (input, ms) in new[] { ("149", 100L), ("150", 200L), ("-5", 0L), ("5001", 5000L), ("0", 0L), ("5000", 5000L), ("49.9", 0L), ("50", 100L), ("4950", 5000L), ("99999999999999999999999", 5000L) })
            Check.Equal<long?>(ms, TimeInput.ParseHoldMs(input), $"「{input}」の停止時間");
        Check.Equal<long?>(null, TimeInput.ParseHoldMs("ms"), "読めない");
        // 既定は無限・停止時間 0
        Check.Equal(LoopMode.Infinite, TimeSettings.Default.Loop, "既定のループ");
        Check.Equal(0L, TimeSettings.Default.HoldMs, "既定の停止時間");
    }

    [Test]
    public void AC35_4_停止時間は無限と1回の両方で最後の遅延に足される()
    {
        var s = SessionTestKit.WithBase();
        foreach (var loop in new[] { LoopMode.Infinite, LoopMode.Once })
        {
            s.SetLoop(loop);
            s.SetHoldMs(500);
            var t = s.Time!;
            Check.Equal(new TimeSettings(20, 20, loop, 500), t, $"{loop} の時間の設定");
            var n = TimeModel.FrameCount(t);
            var gif = GifEncoder.FrameDelays(n, t.Fps, (int)t.HoldMs);
            Check.Equal(55, gif[n - 1], $"{loop}: F=20 の最後の遅延 5 に 50 が足されて 55");
            Check.Equal(550L, TimeModel.DurationMs(n - 1, t), $"{loop}: 表示の長さ");
            s.SetHoldMs(150);
            var t2 = s.Time!;
            Check.Equal(200L, t2.HoldMs, $"{loop}: 150 ms は 200 ms");
            Check.Equal(5 + 20, GifEncoder.FrameDelays(n, t2.Fps, (int)t2.HoldMs)[n - 1], $"{loop}: 150 ms を入れると最後の遅延に 20 が足される");
        }
    }

    // ---- Q-G: 保存した文書の停止時間を FR-61 と同じ規則で直す ----

    private static string DocJson(string holdMs) =>
        $$$"""{"schema": 1, "seed": 5, "saltCounter": 0, "assets": [{"sha256": "{{{SessionTestKit.Sha(1)}}}", "ext": "png", "bytes": 1000}], "layers": [{"name": "a", "kind": "image", "asset": "{{{SessionTestKit.Sha(1)}}}"}], "time": {"length": 2.0, "fps": 20, "loop": "infinite", "holdMs": {{{holdMs}}} }}""";

    [Test]
    public void QG_FR61_読み込みの停止時間は100ms刻みへroundし0から5000に直す()
    {
        foreach (var (json, ms) in new[] { ("149", 100L), ("150", 200L), ("5001", 5000L), ("-5", 0L), ("300", 300L), ("0", 0L), ("1e300", 5000L), ("-1e300", 0L), ("249.5", 200L), ("250", 300L) })
        {
            var r = DocumentJson.Read(DocJson(json), SessionTestKit.Catalog, new MemoryLogSink());
            Check.True(r.IsValid, $"holdMs={json} の文書が読めない: {r.InvalidReason}");
            Check.Equal(ms, r.Document!.Time.HoldMs, $"holdMs={json}");
        }
    }

    [Test]
    public void FR58_読み込みの長さは入力欄と同じ規則で直し大きな値でも読める()
    {
        foreach (var (json, l10) in new[] { ("1e28", 100), ("1e300", 100), ("-1e28", 5), ("-1e300", 5), ("2.25", 23), ("0.25", 5), ("10.04", 100) })
        {
            var r = DocumentJson.Read(DocJson("0").Replace("\"length\": 2.0", "\"length\": " + json, StringComparison.Ordinal), SessionTestKit.Catalog, new MemoryLogSink());
            Check.True(r.IsValid, $"length={json} の文書が読めない: {r.InvalidReason}");
            Check.Equal(l10, r.Document!.Time.Length10, $"length={json}");
        }
    }

    // ---- EditorSession の時間の操作 ----

    [Test]
    public void FR58_FR61_時間の操作はUndoの1件でTimeの通知を上げる()
    {
        var s = SessionTestKit.WithBase();
        var changes = new List<SessionChange>();
        s.Changed += c => changes.Add(c);
        var undo = s.UndoCount;

        Check.True(s.SetLength10(15).Changed, "L を変える");
        Check.Equal(15, s.Time!.Length10, "L10");
        Check.True(changes[^1].HasFlag(SessionChange.Time), "Time が立つ");
        Check.True(!changes[^1].HasFlag(SessionChange.Other), "Other は立たない");
        Check.Equal(undo + 1, s.UndoCount, "Undo の 1 件");
        Check.True(s.SetLength10(500).Changed, "範囲外");
        Check.Equal(100, s.Time!.Length10, "範囲外は端(100)");
        Check.True(s.SetLength10(-1).Changed, "範囲外");
        Check.Equal(5, s.Time!.Length10, "範囲外は端(5)");

        var before = changes.Count;
        Check.True(!s.SetFps(21).Changed, "選択肢に無い fps は変えない");
        Check.Equal(before, changes.Count, "変わらない操作は通知しない");
        foreach (var f in TimeModel.FpsChoices)
        {
            s.SetFps(f);
            Check.Equal(f, s.Time!.Fps, $"fps {f}");
        }
        Check.True(s.SetLoop(LoopMode.Once).Changed, "ループを 1 回に");
        Check.Equal(LoopMode.Once, s.Time!.Loop, "ループ");
        Check.True(!s.SetLoop(LoopMode.Once).Changed, "同じ値は変わらない");
        Check.True(s.SetHoldMs(149).Changed, "停止時間");
        Check.Equal(100L, s.Time!.HoldMs, "149 は 100");
        Check.True(!s.SetHoldMs(120).Changed, "120 も 100 なので変わらない");

        s.Undo();
        Check.Equal(0L, s.Time!.HoldMs, "Undo で停止時間が戻る");
        Check.True(changes[^1].HasFlag(SessionChange.Time), "Undo でも Time が立つ");
        Check.True(DocumentJson.Write(s.Document!).Contains("\"loop\":\"once\"", StringComparison.Ordinal), "文書の JSON に入る");
    }

    // ---- AC-35 の (9): 振り直しと seed ----

    private sealed class QueueSeeds(params int[] seeds) : ISeedSource
    {
        private readonly Queue<int> _q = new(seeds);

        public int Calls { get; private set; }

        public int NextSeed()
        {
            Calls++;
            return _q.Count > 0 ? _q.Dequeue() : 7;
        }
    }

    [Test]
    public void AC35_9_FR66_振り直しでseedが新しい値になり文書を始める取り込みのたびに偽の乱数の値が入る()
    {
        var seeds = new QueueSeeds(111, 111, 222, 333, 999999);
        var s = new EditorSession(SessionTestKit.Catalog, seeds, NullLogSink.Instance);
        s.AddImportedImages([SessionTestKit.Image(1, 100, 100)]);
        Check.Equal(111, s.Document!.Seed, "最初の取り込みの seed は偽の乱数の値");
        Check.True(DocumentJson.Write(s.Document!).Contains("\"seed\":111", StringComparison.Ordinal), "seed は文書に保存される");
        s.AddImportedImages([SessionTestKit.Image(2, 50, 50)]);
        s.AddEmptyLayer();
        Check.Equal(111, s.Document!.Seed, "2 枚目以降のレイヤーを足しても seed は変わらない");
        Check.Equal(1, seeds.Calls, "レイヤーを足しても乱数を引かない");

        var changes = new List<SessionChange>();
        s.Changed += c => changes.Add(c);
        var undo = s.UndoCount;
        Check.True(s.RerollSeed().Changed, "振り直し");
        // 同じ値(111)は引き直して、222 になる
        Check.Equal(222, s.Document!.Seed, "振り直しの seed(いまの値と同じものは引き直す)");
        Check.InRange(0, DocumentRules.MaxSeed, s.Document!.Seed, "seed の範囲");
        Check.True(changes[^1].HasFlag(SessionChange.Other), "seed の変更は Other");
        Check.True(!changes[^1].HasFlag(SessionChange.Time), "seed の変更は Time でない");
        Check.Equal(undo + 1, s.UndoCount, "振り直しは Undo の 1 件");
        s.RerollSeed();
        Check.Equal(333, s.Document!.Seed, "2 回目の振り直し");
        s.RerollSeed();
        Check.Equal(999999, s.Document!.Seed, "上の端");

        // 空のキャンバス(文書を始める操作)でも、偽の乱数の値が seed になる
        var seeds2 = new QueueSeeds(424242);
        var s2 = new EditorSession(SessionTestKit.Catalog, seeds2, NullLogSink.Instance);
        s2.NewEmptyCanvas(64, 32);
        Check.Equal(424242, s2.Document!.Seed, "空のキャンバスの seed");

        // 出所がいつも同じ値を返すときは何も変えない(引き直しは上限まで)
        var fixedSession = SessionTestKit.WithBase();
        var seed = fixedSession.Document!.Seed;
        Check.True(!fixedSession.RerollSeed().Changed, "同じ値しか出ない出所では変えない");
        Check.Equal(seed, fixedSession.Document!.Seed, "seed はそのまま");
    }

    [Test]
    public void AC35_9_振り直しはシステムの乱数で0から999999の値になる()
    {
        var s = new EditorSession(SessionTestKit.Catalog, SystemSeedSource.Instance, NullLogSink.Instance);
        s.AddImportedImages([SessionTestKit.Image(1, 10, 10)]);
        var seen = new HashSet<int> { s.Document!.Seed };
        for (var i = 0; i < 20; i++)
        {
            var before = s.Document!.Seed;
            Check.True(s.RerollSeed().Changed, "振り直しで変わる");
            Check.True(s.Document!.Seed != before, "前と違う値");
            Check.InRange(0, DocumentRules.MaxSeed, s.Document!.Seed, "範囲");
            seen.Add(s.Document!.Seed);
        }
        Check.True(seen.Count > 10, $"値がばらつかない({seen.Count} 通り)");
    }
}
