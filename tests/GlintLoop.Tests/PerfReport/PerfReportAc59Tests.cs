// AC-59 の (11): 集計ツール perf-report をプロセスとして起動し、標準出力のバイト列と終了コードを比べる。
// 記録のファイルは一時フォルダだけに置く(INV-18)。製品の exe は起動しない。
using System.IO;
using System.Text;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.PerfReport;

public sealed class PerfReportAc59Tests
{
    private static string Line(long t, string k, long v) => $"{{\"t\":{t},\"k\":\"{k}\",\"v\":{v}}}";

    private static byte[] Log(IEnumerable<string> lines) => Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    private static byte[] Log(params string[] lines) => Log((IEnumerable<string>)lines);

    private static string[] Repeat(string k, params (int Count, long V)[] groups) =>
        groups.SelectMany(g => Enumerable.Repeat(g.V, g.Count)).Select((v, i) => Line(1000 * (i + 1), k, v)).ToArray();

    private static string Decode(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);

    /// <summary>標準出力が見出し・区切り・rows だけのバイト列(LF、BOM なし、末尾に改行 1 つ)で、終了コードが 0 で、標準エラーが空である。</summary>
    private static void AssertTable(ToolResult result, params string[] rows)
    {
        var expected = "| NFR | 値 | 基準 | 判定 |\n|---|---|---|---|\n" + string.Concat(rows.Select(r => r + "\n"));
        Check.Equal(expected, Decode(result.Stdout), "標準出力");
        Check.Equal(0, result.ExitCode, "終了コード");
        Check.Equal("", Decode(result.Stderr), "標準エラー");
    }

    /// <summary>標準出力が空で、標準エラーが空でない 1 行で、終了コードが 2 である。</summary>
    private static void AssertInputError(ToolResult result)
    {
        Check.Equal(0, result.Stdout.Length, "標準出力の長さ");
        AssertOneErrorLine(result);
        Check.Equal(2, result.ExitCode, "終了コード");
    }

    private static void AssertOneErrorLine(ToolResult result)
    {
        var text = Decode(result.Stderr);
        Check.True(text.Length > 1 && text.EndsWith('\n') && text.IndexOf('\n') == text.Length - 1 && !text.Contains('\r'), $"標準エラーが 1 行でない: [{text}]");
    }

    [Test]
    public void AC59_11_出力はBOMなしのUTF8で改行はLFで末尾に改行が1つ()
    {
        using var dir = new TempDir();
        var file = dir.Write("a.jsonl", Log(Line(600000, "mem", 1)));

        var bytes = PerfReportTool.Run("ui_block", file).Stdout;

        var expected = Encoding.UTF8.GetBytes("| NFR | 値 | 基準 | 判定 |\n|---|---|---|---|\n| ui_block | 0 回 | 0 回 | OK |\n");
        Check.SequenceEqual(expected, bytes, "標準出力のバイト列");
    }

    private static string StartupFileName(int index) => $"perf-20261014-{10 + index}0000.jsonl";

    private static void WriteStartupFolder(TempDir dir, long[] values)
    {
        for (var i = 0; i < values.Length; i++) dir.Write(StartupFileName(i), Log(Line(350, "startup", values[i])));
    }

    [Test]
    public void AC59_11a_startupは最初のファイルを除いたp95で判定しother_jsonlと下のフォルダを読まない()
    {
        using var dir = new TempDir();
        long[] values = [9999, 1800, 1900, 1700, 1600, 2000, 1500, 1400, 1300, 1200];
        WriteStartupFolder(dir, values);
        dir.Write("other.jsonl", Log(Line(350, "startup", 8888)));
        Directory.CreateDirectory(dir.PathOf("sub"));
        dir.Write(Path.Combine("sub", "perf-20261014-200000.jsonl"), Log(Line(350, "startup", 7777)));

        AssertTable(PerfReportTool.Run("startup", dir.Path), "| startup | 2000 ms | 2000 ms 以下 | OK |");

        values[5] = 2001;
        WriteStartupFolder(dir, values);
        AssertTable(PerfReportTool.Run("startup", dir.Path), "| startup | 2001 ms | 2000 ms 以下 | NG |");

        var firstNine = Enumerable.Range(0, 9).Select(i => dir.PathOf(StartupFileName(i)));
        AssertTable(PerfReportTool.Run(["startup", .. firstNine]), "| startup | - | 2000 ms 以下 | n/a |");
    }

    [Test]
    public void AC59_11_フォルダの中はファイル名の序数比較の昇順で空のファイルも1回の起動に数える()
    {
        using var dir = new TempDir();
        // 序数比較では B が a より前。大文字と小文字を区別しないと perf-a0 が先に来て、9999 が値に入る。
        dir.Write("perf-B.jsonl", Log(Line(350, "startup", 9999)));
        for (var i = 0; i < 9; i++) dir.Write($"perf-a{i}.jsonl", Log(Line(350, "startup", 1000)));
        AssertTable(PerfReportTool.Run("startup", dir.Path), "| startup | 1000 ms | 2000 ms 以下 | OK |");

        // 空のファイルが「最初のファイル」になると、9999 も値に入る。
        using var withEmpty = new TempDir();
        withEmpty.Write("perf-0.jsonl", []);
        withEmpty.Write("perf-1.jsonl", Log(Line(350, "startup", 9999)));
        for (var i = 2; i < 10; i++) withEmpty.Write($"perf-{i}.jsonl", Log(Line(350, "startup", 1000)));
        AssertTable(PerfReportTool.Run("startup", withEmpty.Path), "| startup | 9999 ms | 2000 ms 以下 | NG |");
    }

    [Test]
    public void AC59_11b_paste_latencyは全ファイルのp95で20個未満はn_a()
    {
        using var dir = new TempDir();
        var file = dir.PathOf("perf-a.jsonl");

        File.WriteAllBytes(file, Log(Repeat("paste", (19, 400), (1, 9000))));
        AssertTable(PerfReportTool.Run("paste_latency", file), "| paste_latency | 400 ms | 500 ms 以下 | OK |");

        File.WriteAllBytes(file, Log(Repeat("paste", (18, 400), (2, 9000))));
        AssertTable(PerfReportTool.Run("paste_latency", file), "| paste_latency | 9000 ms | 500 ms 以下 | NG |");

        File.WriteAllBytes(file, Log(Repeat("paste", (19, 400))));
        AssertTable(PerfReportTool.Run("paste_latency", file), "| paste_latency | - | 500 ms 以下 | n/a |");

        // 最初のファイルも数える(startup と違う)。
        var second = dir.Write("perf-b.jsonl", Log(Line(1000, "paste", 400)));
        AssertTable(PerfReportTool.Run("paste_latency", file, second), "| paste_latency | 400 ms | 500 ms 以下 | OK |");
    }

    [Test]
    public void AC59_11c_interactiveはdrag_frameのp95で0個はn_a()
    {
        using var dir = new TempDir();
        var file = dir.PathOf("perf-a.jsonl");

        File.WriteAllBytes(file, Log(Repeat("drag_frame", (95, 40), (5, 60))));
        AssertTable(PerfReportTool.Run("interactive", file), "| interactive | 40 ms | 50 ms 以下 | OK |");

        File.WriteAllBytes(file, Log(Repeat("drag_frame", (94, 40), (6, 60))));
        AssertTable(PerfReportTool.Run("interactive", file), "| interactive | 60 ms | 50 ms 以下 | NG |");

        File.WriteAllBytes(file, Log(Line(1000, "mem", 1)));
        AssertTable(PerfReportTool.Run("interactive", file), "| interactive | - | 50 ms 以下 | n/a |");
    }

    [Test]
    public void AC59_11d_looks_galleryは最大値で10個未満はn_a()
    {
        using var dir = new TempDir();
        var file = dir.PathOf("perf-a.jsonl");

        File.WriteAllBytes(file, Log(Repeat("gallery", (5, 1000), (1, 1500), (4, 1200))));
        AssertTable(PerfReportTool.Run("looks_gallery", file), "| looks_gallery | 1500 ms | 1500 ms 以下 | OK |");

        File.WriteAllBytes(file, Log(Repeat("gallery", (5, 1000), (1, 1501), (4, 1200))));
        AssertTable(PerfReportTool.Run("looks_gallery", file), "| looks_gallery | 1501 ms | 1500 ms 以下 | NG |");

        File.WriteAllBytes(file, Log(Repeat("gallery", (9, 1000))));
        AssertTable(PerfReportTool.Run("looks_gallery", file), "| looks_gallery | - | 1500 ms 以下 | n/a |");
    }

    [Test]
    public void AC59_11e_ui_blockは行の数を数え最後の行のtの合計が600000未満はn_a()
    {
        using var dir = new TempDir();
        var file = dir.PathOf("perf-a.jsonl");

        File.WriteAllBytes(file, Log(Line(1000, "mem", 1), Line(600000, "mem", 1)));
        AssertTable(PerfReportTool.Run("ui_block", file), "| ui_block | 0 回 | 0 回 | OK |");

        File.WriteAllBytes(file, Log(Line(1000, "ui_stall", 100), Line(600000, "mem", 1)));
        AssertTable(PerfReportTool.Run("ui_block", file), "| ui_block | 1 回 | 0 回 | NG |");

        File.WriteAllBytes(file, Log(Line(1000, "mem", 1), Line(599999, "mem", 1)));
        AssertTable(PerfReportTool.Run("ui_block", file), "| ui_block | - | 0 回 | n/a |");

        var half1 = dir.Write("perf-b.jsonl", Log(Line(300000, "mem", 1)));
        var half2 = dir.Write("perf-c.jsonl", Log(Line(300000, "mem", 1)));
        var empty = dir.Write("perf-d.jsonl", []);
        AssertTable(PerfReportTool.Run("ui_block", half1, half2), "| ui_block | 0 回 | 0 回 | OK |");
        AssertTable(PerfReportTool.Run("ui_block", half1, empty, half2), "| ui_block | 0 回 | 0 回 | OK |");
    }

    private static List<string> MemoryLines() =>
    [
        Line(1000, "mem", 3000),
        Line(1500, "paste", 400),
        Line(2000, "mem", 1400),
        Line(3000, "mem", 1500),
        Line(3500, "export_begin", 0),
        Line(4000, "mem", 2500),
        Line(5000, "mem", 2400),
        Line(5500, "export_end", 2000),
        Line(6000, "mem", 1499),
    ];

    [Test]
    public void AC59_11f_memoryは最初のpaste以後を編集と書き出しの区間に分けて最大値を求める()
    {
        using var dir = new TempDir();
        var file = dir.PathOf("perf-a.jsonl");

        File.WriteAllBytes(file, Log(MemoryLines()));
        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | 1500 MB | 1500 MB 以下 | OK |", "| memory_export | 2500 MB | 2500 MB 以下 | OK |");

        var over = MemoryLines();
        over[8] = Line(6000, "mem", 1501);
        File.WriteAllBytes(file, Log(over));
        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | 1501 MB | 1500 MB 以下 | NG |", "| memory_export | 2500 MB | 2500 MB 以下 | OK |");

        // 区間の端(export_begin と同じ t)は書き出しの区間に入る。
        var onEdge = MemoryLines();
        onEdge.Insert(5, Line(3500, "mem", 2600));
        File.WriteAllBytes(file, Log(onEdge));
        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | 1500 MB | 1500 MB 以下 | OK |", "| memory_export | 2600 MB | 2500 MB 以下 | NG |");

        // 区間の端(export_end と同じ t)も書き出しの区間に入る。
        var onEnd = MemoryLines();
        onEnd.Insert(8, Line(5500, "mem", 2600));
        File.WriteAllBytes(file, Log(onEnd));
        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | 1500 MB | 1500 MB 以下 | OK |", "| memory_export | 2600 MB | 2500 MB 以下 | NG |");

        // 組にならない export_begin は最後の行までを区間にする。
        var unpaired = MemoryLines();
        unpaired.Add(Line(7000, "export_begin", 0));
        unpaired.Add(Line(8000, "mem", 2600));
        File.WriteAllBytes(file, Log(unpaired));
        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | 1500 MB | 1500 MB 以下 | OK |", "| memory_export | 2600 MB | 2500 MB 以下 | NG |");

        File.WriteAllBytes(file, Log(Line(1000, "mem", 100), Line(2000, "mem", 200)));
        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | - | 1500 MB 以下 | n/a |", "| memory_export | - | 2500 MB 以下 | n/a |");
    }

    [Test]
    public void AC59_11_書き出しの区間はbeginとendを先に来た順に1対1で組にする()
    {
        using var dir = new TempDir();
        // begin 2 つのあとの end 1 つは 1 つ目と組になり、2 つ目は最後の行までの区間になる。前に来た end は組にならない。
        var file = dir.Write("perf-a.jsonl", Log(
            Line(100, "export_end", 1),
            Line(200, "paste", 400),
            Line(300, "mem", 1000),
            Line(1000, "export_begin", 0),
            Line(1500, "export_begin", 0),
            Line(2000, "export_end", 1000),
            Line(2500, "mem", 2000),
            Line(3000, "mem", 2100)));
        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | 1000 MB | 1500 MB 以下 | OK |", "| memory_export | 2100 MB | 2500 MB 以下 | OK |");
    }

    private static string[] Counter(string k, int count, Func<int, long> value) =>
        Enumerable.Range(1, count).Select(i => Line(1000 * i, k, value(i))).ToArray();

    [Test]
    public void AC59_11g_idleは最後のファイルの最後の行と10行前の差を求める()
    {
        using var dir = new TempDir();
        var file = dir.PathOf("perf-z.jsonl");
        string[] Calm(int count) => [.. Counter("cpu", count, k => 1000 + 10 * k), .. Counter("render_calls", count, _ => 55)];

        File.WriteAllBytes(file, Log(Calm(30)));
        AssertTable(PerfReportTool.Run("idle", file), "| idle_cpu | 100 ms | 100 ms 以下 | OK |", "| idle_render | 0 回 | 0 回 | OK |");

        File.WriteAllBytes(file, Log([.. Counter("cpu", 30, k => k == 30 ? 1301 : 1000 + 10 * k), .. Counter("render_calls", 30, k => k == 30 ? 56 : 55)]));
        AssertTable(PerfReportTool.Run("idle", file), "| idle_cpu | 101 ms | 100 ms 以下 | NG |", "| idle_render | 1 回 | 0 回 | NG |");

        // 11 行のファイルでは 11 行目と 1 行目の差を使う。
        File.WriteAllBytes(file, Log([.. Counter("cpu", 11, k => 1000 + 11 * (k - 1)), .. Counter("render_calls", 11, k => k == 1 ? 50 : 55)]));
        AssertTable(PerfReportTool.Run("idle", file), "| idle_cpu | 110 ms | 100 ms 以下 | NG |", "| idle_render | 5 回 | 0 回 | NG |");

        File.WriteAllBytes(file, Log(Calm(10)));
        AssertTable(PerfReportTool.Run("idle", file), "| idle_cpu | - | 100 ms 以下 | n/a |", "| idle_render | - | 0 回 | n/a |");

        // 1 つ前のファイルは結果に影響しない。順番を入れ替えると、結果も入れ替わる。
        File.WriteAllBytes(file, Log(Calm(30)));
        var previous = dir.Write("perf-a.jsonl", Log([.. Counter("cpu", 30, k => 100000 * k), .. Counter("render_calls", 30, k => k)]));
        AssertTable(PerfReportTool.Run("idle", previous, file), "| idle_cpu | 100 ms | 100 ms 以下 | OK |", "| idle_render | 0 回 | 0 回 | OK |");
        AssertTable(PerfReportTool.Run("idle", file, previous), "| idle_cpu | 1000000 ms | 100 ms 以下 | NG |", "| idle_render | 10 回 | 0 回 | NG |");
    }

    [Test]
    public void AC59_11h_引数なしと未知のキーと存在しないパスだけは標準出力が空で標準エラーが1行で終了コード2()
    {
        using var dir = new TempDir();
        var file = dir.Write("perf-a.jsonl", Log(Line(1000, "mem", 1)));
        var missing = dir.PathOf("missing.jsonl");

        AssertInputError(PerfReportTool.Run());
        AssertInputError(PerfReportTool.Run("startup"));
        AssertInputError(PerfReportTool.Run("foo"));
        AssertInputError(PerfReportTool.Run("foo", file));
        AssertInputError(PerfReportTool.Run("startup", missing));
        AssertInputError(PerfReportTool.Run("startup", missing, dir.PathOf("missing2.jsonl")));

        // ファイルの無いフォルダだけでも、読めるファイルが 1 つも無い。
        Directory.CreateDirectory(dir.PathOf("none"));
        AssertInputError(PerfReportTool.Run("startup", dir.PathOf("none")));
    }

    [Test]
    public void AC59_11h_読めない行をほかの行の間に足しても結果が変わらない()
    {
        using var dir = new TempDir();
        string[] junk =
        [
            "not json",
            "{\"t\":1,\"k\":\"mem\",\"v\":1.5}",
            "{\"t\":1,\"k\":\"zzz\",\"v\":1}",
            "{\"t\":\"1\",\"k\":\"mem\",\"v\":1}",
            "",
        ];
        // v が大きいので、どれかを読むと memory の値か判定が変わる。
        string[] largeJunk =
        [
            "not json",
            "{\"t\":2500,\"k\":\"mem\",\"v\":9999.5}",
            "{\"t\":2500,\"k\":\"zzz\",\"v\":9999}",
            "{\"t\":\"2500\",\"k\":\"mem\",\"v\":9999}",
            "{\"t\":2500,\"k\":\"mem\",\"v\":\"9999\"}",
            "",
        ];
        var mixed = new List<string>();
        foreach (var line in MemoryLines())
        {
            mixed.AddRange(junk);
            mixed.AddRange(largeJunk);
            mixed.Add(line);
        }
        var file = dir.Write("perf-a.jsonl", Log(mixed));

        AssertTable(PerfReportTool.Run("memory", file), "| memory_edit | 1500 MB | 1500 MB 以下 | OK |", "| memory_export | 2500 MB | 2500 MB 以下 | OK |");
    }

    [Test]
    public void AC59_11h_整数の判定は厳密で読み飛ばした行はファイルの最後の行にならない()
    {
        using var dir = new TempDir();
        string[] notCounts =
        [
            "{\"t\":700000,\"k\":\"mem\",\"v\":1.0}",
            "{\"t\":700000,\"k\":\"mem\",\"v\":1e3}",
            "{\"t\":700000,\"k\":\"mem\",\"v\":-1}",
            "{\"t\":700000.0,\"k\":\"mem\",\"v\":1}",
            "{\"t\":99999999999999999999,\"k\":\"mem\",\"v\":1}",
            "{\"t\":700000,\"k\":\"zzz\",\"v\":1}",
            "{\"t\":700000,\"k\":\"MEM\",\"v\":1}",
            "{\"t\":700000,\"k\":1,\"v\":1}",
            "{\"t\":700000,\"v\":1}",
            "{\"k\":\"mem\",\"v\":1}",
            "{\"t\":700000,\"k\":\"mem\"}",
            "[700000]",
            "700000",
            "{\"t\":700000,\"k\":\"mem\",\"v\":1} {",
        ];
        var file = dir.Write("perf-a.jsonl", Log([Line(599999, "mem", 1), .. notCounts]));

        AssertTable(PerfReportTool.Run("ui_block", file), "| ui_block | - | 0 回 | n/a |");
    }

    [Test]
    public void AC59_11h_存在しないパスと読めるファイルを一緒に渡すと標準エラーに1行で表が出て終了コード0()
    {
        using var dir = new TempDir();
        var file = dir.Write("perf-a.jsonl", Log(Line(600000, "mem", 1)));

        var result = PerfReportTool.Run("ui_block", dir.PathOf("missing.jsonl"), file);

        Check.Equal("| NFR | 値 | 基準 | 判定 |\n|---|---|---|---|\n| ui_block | 0 回 | 0 回 | OK |\n", Decode(result.Stdout), "標準出力");
        AssertOneErrorLine(result);
        Check.Equal(0, result.ExitCode, "終了コード");
    }
}
