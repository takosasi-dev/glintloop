// 読んだ記録から NFR の表の文字列を作る(仕様書 §6.8 の「集計」の表)。キーごとに、値の求め方と基準と n/a の条件を持つ。
// p95 は n 個を小さい順に並べた ceil(0.95×n) 番目で、順位は小数を通さず整数で求める。
using System.Globalization;
using System.Text;

namespace GlintLoop.Tools.PerfReport;

internal static class ReportTable
{
    /// <summary>表の 1 行。Value が null のとき判定は n/a で、値は - になる。</summary>
    private readonly record struct Row(string Name, long? Value, string Unit, string Criterion, long Limit)
    {
        public override string ToString()
        {
            if (Value is not { } value) return $"| {Name} | - | {Criterion} | n/a |";
            var text = value.ToString(CultureInfo.InvariantCulture);
            return $"| {Name} | {text} {Unit} | {Criterion} | {(value <= Limit ? "OK" : "NG")} |";
        }
    }

    private static readonly string[] Keys = ["startup", "paste_latency", "interactive", "looks_gallery", "ui_block", "memory", "idle"];

    public static bool IsKnownKey(string key) => Keys.Contains(key, StringComparer.Ordinal);

    /// <summary>キーの表を、見出しと区切りの行から末尾の改行まで作る。</summary>
    public static string Render(string key, IReadOnlyList<IReadOnlyList<PerfEntry>> files)
    {
        Row[] rows = key switch
        {
            "startup" => [new("startup", Percentile95(Values(files.Skip(1), "startup"), 9), "ms", "2000 ms 以下", 2000)],
            "paste_latency" => [new("paste_latency", Percentile95(Values(files, "paste"), 20), "ms", "500 ms 以下", 500)],
            "interactive" => [new("interactive", Percentile95(Values(files, "drag_frame"), 1), "ms", "50 ms 以下", 50)],
            "looks_gallery" => [new("looks_gallery", GalleryMax(files), "ms", "1500 ms 以下", 1500)],
            "ui_block" => [new("ui_block", UiBlockCount(files), "回", "0 回", 0)],
            "memory" =>
            [
                new("memory_edit", MemoryEdit(files), "MB", "1500 MB 以下", 1500),
                new("memory_export", MemoryExport(files), "MB", "2500 MB 以下", 2500),
            ],
            "idle" =>
            [
                new("idle_cpu", LastTenDelta(files[^1], "cpu"), "ms", "100 ms 以下", 100),
                new("idle_render", LastTenDelta(files[^1], "render_calls"), "回", "0 回", 0),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        var text = new StringBuilder("| NFR | 値 | 基準 | 判定 |\n|---|---|---|---|\n");
        foreach (var row in rows) text.Append(row.ToString()).Append('\n');
        return text.ToString();
    }

    private static List<long> Values(IEnumerable<IReadOnlyList<PerfEntry>> files, string kind) =>
        files.SelectMany(file => file).Where(e => e.K == kind).Select(e => e.V).ToList();

    private static long? Percentile95(List<long> values, int minimumCount)
    {
        if (values.Count < minimumCount) return null;
        values.Sort();
        var rank = (95L * values.Count + 99) / 100;
        return values[(int)rank - 1];
    }

    private static long? GalleryMax(IReadOnlyList<IReadOnlyList<PerfEntry>> files)
    {
        var values = Values(files, "gallery");
        return values.Count < 10 ? null : values.Max();
    }

    private static long? UiBlockCount(IReadOnlyList<IReadOnlyList<PerfEntry>> files)
    {
        var recorded = files.Where(file => file.Count > 0).Sum(file => file[^1].T);
        return recorded < 600000 ? null : Values(files, "ui_stall").Count;
    }

    private static long? MemoryEdit(IReadOnlyList<IReadOnlyList<PerfEntry>> files)
    {
        long? max = null;
        foreach (var file in files)
        {
            var paste = file.Where(e => e.K == "paste").Take(1).ToList();
            if (paste.Count == 0) continue;
            var ranges = ExportRanges(file);
            foreach (var e in file)
            {
                if (e.K == "mem" && e.T >= paste[0].T && !IsInside(ranges, e.T)) max = Larger(max, e.V);
            }
        }
        return max;
    }

    private static long? MemoryExport(IReadOnlyList<IReadOnlyList<PerfEntry>> files)
    {
        long? max = null;
        foreach (var file in files)
        {
            var ranges = ExportRanges(file);
            foreach (var e in file)
            {
                if (e.K == "mem" && IsInside(ranges, e.T)) max = Larger(max, e.V);
            }
        }
        return max;
    }

    private static long Larger(long? current, long value) => current is { } c && c > value ? c : value;

    // export_begin を、後ろに続く export_end と先に来た順に組にする。組にならない begin は最後の行までの区間にする。
    private static List<(long From, long To)> ExportRanges(IReadOnlyList<PerfEntry> file)
    {
        var ranges = new List<(long, long)>();
        var open = new Queue<long>();
        foreach (var e in file)
        {
            if (e.K == "export_begin") open.Enqueue(e.T);
            else if (e.K == "export_end" && open.TryDequeue(out var from)) ranges.Add((from, e.T));
        }
        foreach (var from in open) ranges.Add((from, file[^1].T));
        return ranges;
    }

    private static bool IsInside(List<(long From, long To)> ranges, long t) => ranges.Any(r => r.From <= t && t <= r.To);

    private static long? LastTenDelta(IReadOnlyList<PerfEntry> file, string kind)
    {
        var values = file.Where(e => e.K == kind).Select(e => e.V).ToList();
        return values.Count < 11 ? null : values[^1] - values[^11];
    }
}
