// THIRD-PARTY-NOTICES.md の表を読み、依存のライセンスが INV-4 の許可リストに入るかを判定する(AC-9)。
// 判定は文字列を受け取る純粋な関数にしてあり、失敗する入力を与えるテストで確かめられる。
namespace GlintLoop.Tests.Static;

public static class NoticesCheck
{
    /// <summary>INV-4 の許可リスト(ライセンスの欄と完全に一致したものだけを許す)。</summary>
    public static readonly string[] AllowedLicenses = ["MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "zlib"];

    /// <summary>フォントにだけ許すライセンス(SIL OFL 1.1)の書き方。</summary>
    public static readonly string[] AllowedFontLicenses = ["OFL-1.1", "SIL OFL 1.1"];

    /// <summary>ネイティブ部品の表で、許可リスト以外のライセンスを記録してよい「扱い」の書き出し。</summary>
    public static readonly string[] AllowedTreatmentPrefixes = ["例外として許可", "通知に載っているが使わない部品として記録"];

    public const string NuGetSection = "## NuGet パッケージ";
    public const string NativeSection = "## SkiaSharp のネイティブ部品";

    /// <summary>表の 1 行(見出しの名前 → 欄の値)。</summary>
    public sealed record Row(int Line, IReadOnlyDictionary<string, string> Cells)
    {
        public string this[string column] => Cells.TryGetValue(column, out var v) ? v : "";
    }

    /// <summary>
    /// 見出し(## で始まる行)の下にある最初の Markdown の表を読む。見出しか表が無ければ null。
    /// 1 行目を列の名前、2 行目(|---|)を区切りとして読み飛ばす。
    /// </summary>
    public static List<Row>? ReadTable(string markdown, string heading)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var at = Array.FindIndex(lines, l => l.TrimEnd() == heading);
        if (at < 0) return null;
        string[]? header = null;
        var rows = new List<Row>();
        var inTable = false;
        for (var i = at + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!line.StartsWith('|'))
            {
                if (inTable) break;
                continue;
            }
            inTable = true;
            var cells = SplitRow(line);
            if (header is null) { header = cells; continue; }
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'))) continue;
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Length; c++) dict[header[c]] = c < cells.Length ? cells[c] : "";
            rows.Add(new Row(i + 1, dict));
        }
        return header is null ? null : rows;
    }

    private static string[] SplitRow(string line)
    {
        var inner = line.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|')) inner = inner[..^1];
        return inner.Split('|').Select(c => c.Trim()).ToArray();
    }

    /// <summary>
    /// THIRD-PARTY-NOTICES.md の中身と、dotnet list package で得た (パッケージ, 版) の一覧を照らし合わせ、
    /// INV-4 に反する点を返す。空なら INV-4 の表の検査を満たす。
    /// </summary>
    public static List<string> Evaluate(string noticesMarkdown, IReadOnlyCollection<(string Id, string Version)> packages)
    {
        var problems = new List<string>();

        var nuget = ReadTable(noticesMarkdown, NuGetSection);
        if (nuget is null)
        {
            problems.Add($"「{NuGetSection}」の表が無い");
        }
        else
        {
            foreach (var col in new[] { "パッケージ", "バージョン", "ライセンス" })
            {
                if (nuget.Count > 0 && !nuget[0].Cells.ContainsKey(col)) problems.Add($"NuGet の表に「{col}」の列が無い");
            }
            foreach (var row in nuget)
            {
                var license = row["ライセンス"];
                var isFont = row["使う場所"].Contains("フォント", StringComparison.Ordinal);
                var ok = AllowedLicenses.Contains(license, StringComparer.Ordinal)
                    || (isFont && AllowedFontLicenses.Contains(license, StringComparer.Ordinal));
                if (!ok) problems.Add($"{row.Line} 行目: {row["パッケージ"]} のライセンス「{license}」は許可リストに無い");
            }
            foreach (var (id, version) in packages.Distinct())
            {
                var same = nuget.Where(r => string.Equals(r["パッケージ"], id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count == 0) problems.Add($"{id} {version} が NuGet の表に無い");
                else if (!same.Any(r => r["バージョン"] == version))
                    problems.Add($"{id} の版 {version} が NuGet の表に無い(表は {string.Join("・", same.Select(r => r["バージョン"]))})");
            }
        }

        var native = ReadTable(noticesMarkdown, NativeSection);
        if (native is not null)
        {
            foreach (var row in native)
            {
                var license = row["ライセンス"];
                var treatment = row["扱い"];
                if (treatment == "許可リスト")
                {
                    if (!AllowedLicenses.Contains(license, StringComparer.Ordinal))
                        problems.Add($"{row.Line} 行目: 部品 {row["部品"]} の扱いは「許可リスト」だが、ライセンス「{license}」は許可リストに無い");
                }
                else if (!AllowedTreatmentPrefixes.Any(p => treatment.StartsWith(p, StringComparison.Ordinal)))
                {
                    problems.Add($"{row.Line} 行目: 部品 {row["部品"]} の扱い「{treatment}」が決まった書き方でない");
                }
            }
        }
        return problems;
    }
}
