// ソースの文字列検査の道具(AC-9)。語の出現を行ごとに数え、色の直書きとクリップボードの形式名を調べる。
// 検索語そのものはここに書かない(語は StaticInvariantTests.cs の中で組み立てて渡す)。
using System.Text.RegularExpressions;

namespace GlintLoop.Tests.Static;

public static partial class SourceScan
{
    /// <summary>1 件の出現(ファイルのルートからの相対パス・行番号・語)。</summary>
    public readonly record struct Hit(string File, int Line, string Term)
    {
        public override string ToString() => $"{File}:{Line} [{Term}]";
    }

    /// <summary>文字列の中の語の出現を、行ごとに数える(大文字と小文字を区別。コメントの中も数える)。</summary>
    public static List<Hit> FindTerms(string fileLabel, string text, IReadOnlyList<string> terms)
    {
        var hits = new List<Hit>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var term in terms)
            {
                if (lines[i].Contains(term, StringComparison.Ordinal)) hits.Add(new Hit(fileLabel, i + 1, term));
            }
        }
        return hits;
    }

    /// <summary>ファイルの一覧から、除外の相対パスを除いて語の出現を集める。</summary>
    public static List<Hit> FindTermsInFiles(IEnumerable<string> fullPaths, IReadOnlyList<string> terms, IReadOnlyCollection<string> excludedRelative)
    {
        var hits = new List<Hit>();
        foreach (var path in fullPaths)
        {
            var rel = RepoFiles.Relative(path);
            if (excludedRelative.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
            hits.AddRange(FindTerms(rel, RepoFiles.ReadText(path), terms));
        }
        return hits;
    }

    // #RRGGBB と #AARRGGBB。前が & のもの(XAML の文字参照 &#123456;)と、後ろに 16 進の文字が続くものは数えない。
    [GeneratedRegex("(?<!&)#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})(?![0-9A-Fa-f])")]
    private static partial Regex HexColorRegex();

    /// <summary>#RRGGBB・#AARRGGBB の色の直書きを行ごとに返す。</summary>
    public static List<Hit> FindHexColors(string fileLabel, string text)
    {
        var hits = new List<Hit>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match m in HexColorRegex().Matches(lines[i])) hits.Add(new Hit(fileLabel, i + 1, m.Value));
        }
        return hits;
    }

    [GeneratedRegex(@"\bSetData\s*\(")]
    private static partial Regex SetDataCallRegex();

    /// <summary>
    /// SetData の呼び出しごとに、最初の引数(形式名)を取り出す。引数が 1 つだけの呼び出し
    /// (形式を値の型から決める形)は形式名が無いので null を返す。
    /// </summary>
    public static List<(int Line, string? Format)> FindSetDataFormats(string text)
    {
        var result = new List<(int, string?)>();
        foreach (Match m in SetDataCallRegex().Matches(text))
        {
            var line = 1 + text.AsSpan(0, m.Index).Count('\n');
            var start = m.Index + m.Length;
            var depth = 0;
            string? format = null;
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}')
                {
                    if (depth == 0) break; // 引数が 1 つだけ
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    format = text[start..i].Trim();
                    break;
                }
            }
            result.Add((line, format));
        }
        return result;
    }

    /// <summary>INV-10 で SetData に渡してよい形式名(FileDrop・PNG・DIB・DIBV5)の書き方。</summary>
    public static readonly string[] AllowedClipboardFormats =
    [
        "DataFormats.FileDrop", "\"FileDrop\"",
        "\"PNG\"",
        "DataFormats.Dib", "\"DeviceIndependentBitmap\"",
        "\"Format17\"",
    ];

    /// <summary>許されない形式名の SetData の呼び出しを返す(空なら INV-10 の形式名の検査を満たす)。</summary>
    public static List<string> ClipboardFormatProblems(string fileLabel, string text)
    {
        var problems = new List<string>();
        foreach (var (line, format) in FindSetDataFormats(text))
        {
            if (format is null) problems.Add($"{fileLabel}:{line} SetData に形式名が無い(引数 1 つの形)");
            else if (!AllowedClipboardFormats.Contains(format, StringComparer.Ordinal))
                problems.Add($"{fileLabel}:{line} SetData の形式名 {format} は FileDrop・PNG・DIB・DIBV5 のどれでもない");
        }
        return problems;
    }

    /// <summary>バイト列に ASCII の語が(大文字と小文字を区別せずに)含まれるか。UTF-16LE の形も調べる。</summary>
    public static bool ContainsAsciiIgnoreCase(byte[] data, string term)
    {
        var lower = term.ToLowerInvariant();
        return IndexOfIgnoreCase(data, System.Text.Encoding.ASCII.GetBytes(lower)) >= 0
            || IndexOfIgnoreCase(data, System.Text.Encoding.Unicode.GetBytes(lower)) >= 0;
    }

    private static int IndexOfIgnoreCase(byte[] data, byte[] lowerPattern)
    {
        for (var i = 0; i + lowerPattern.Length <= data.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < lowerPattern.Length; j++)
            {
                var b = data[i + j];
                if (b is >= (byte)'A' and <= (byte)'Z') b = (byte)(b + 32);
                if (b != lowerPattern[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
