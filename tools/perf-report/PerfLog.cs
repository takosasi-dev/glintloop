// 測定の記録(perf-*.jsonl)を読む。パスをファイルの順に開き、1 つのファイルを 1 回の起動の行の一覧にする(仕様書 §6.8)。
// JSON でない行、k が表に無い行、t か v が 0 以上の整数でない行は読み飛ばす。読めないパスは呼び出し元へ返す。
using System.Globalization;
using System.Text.Json;

namespace GlintLoop.Tools.PerfReport;

/// <summary>記録の 1 行。</summary>
internal readonly record struct PerfEntry(long T, string K, long V);

internal static class PerfLog
{
    private static readonly string[] Kinds =
    [
        "startup", "paste", "gallery", "drag_frame", "ui_stall", "mem", "cpu", "render_calls", "export_begin", "export_end",
    ];

    // 直下の perf-*.jsonl だけを、大文字と小文字を区別して選ぶ(Windows の既定の照合は区別しない)。
    private static readonly EnumerationOptions FolderOptions = new()
    {
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseSensitive,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    /// <summary>パスをファイルの順に読む。読めたファイルごとの行の一覧と、読めなかったパスを返す。</summary>
    public static (List<IReadOnlyList<PerfEntry>> Files, List<string> Unreadable) ReadAll(IEnumerable<string> paths)
    {
        var files = new List<IReadOnlyList<PerfEntry>>();
        var unreadable = new List<string>();
        foreach (var path in paths)
        {
            foreach (var file in Expand(path, unreadable))
            {
                try
                {
                    files.Add(ParseLines(ReadBytes(file)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(file);
                }
            }
        }
        return (files, unreadable);
    }

    private static IReadOnlyList<string> Expand(string path, List<string> unreadable)
    {
        if (File.Exists(path)) return [path];
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.EnumerateFiles(path, "perf-*.jsonl", FolderOptions)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToList();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        unreadable.Add(path);
        return [];
    }

    // 動いているアプリが書いている途中のファイルも読めるように、書き込みを許して開く。
    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static List<PerfEntry> ParseLines(byte[] bytes)
    {
        var entries = new List<PerfEntry>();
        ReadOnlyMemory<byte> rest = bytes;
        while (!rest.IsEmpty)
        {
            var end = rest.Span.IndexOf((byte)'\n');
            var line = end < 0 ? rest : rest[..end];
            rest = end < 0 ? default : rest[(end + 1)..];
            if (TryParseLine(line) is { } entry) entries.Add(entry);
        }
        return entries;
    }

    private static PerfEntry? TryParseLine(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("t", out var t) || !TryGetCount(t, out var time)) return null;
            if (!root.TryGetProperty("v", out var v) || !TryGetCount(v, out var value)) return null;
            if (!root.TryGetProperty("k", out var k) || k.ValueKind != JsonValueKind.String) return null;
            var kind = Array.Find(Kinds, name => k.ValueEquals(name));
            return kind is null ? null : new PerfEntry(time, kind, value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 生の文字列が数字だけのものを整数とする(1.5・1.0・1e3・-1 と、long に入らない値は数えない)。
    private static bool TryGetCount(JsonElement element, out long count)
    {
        count = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        var raw = element.GetRawText();
        return raw.All(char.IsAsciiDigit) && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }
}
