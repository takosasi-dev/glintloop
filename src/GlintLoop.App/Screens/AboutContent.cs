// 「このソフトについて」(FR-103)に出す中身: 埋め込みリソースの LICENSE(MIT の全文)と THIRD-PARTY-NOTICES.md(依存ライブラリの表)を読む。
// THIRD-PARTY-NOTICES.md は、## の見出しごとに、その下の最初の Markdown の表を読む(列の名前・各行の欄)。表の中身は書き換えずにそのまま出す。
using System.IO;
using System.Text;

namespace GlintLoop.App.Screens;

/// <summary>見出しと、その下の表(列の名前と各行の欄)。</summary>
public sealed record NoticeTable(string Heading, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

public static class AboutContent
{
    public const string LicenseResource = "GlintLoop.App.LICENSE";
    public const string NoticesResource = "GlintLoop.App.THIRD-PARTY-NOTICES.md";

    private static readonly Lazy<string> LicenseLazy = new(() => TryRead(LicenseResource) ?? throw new InvalidOperationException("埋め込みリソースが無い: " + LicenseResource));
    private static readonly Lazy<string?> NoticesLazy = new(() => TryRead(NoticesResource));
    private static readonly Lazy<IReadOnlyList<NoticeTable>> TablesLazy = new(() => NoticesLazy.Value is { } md ? ReadTables(md) : []);

    /// <summary>LICENSE の全文(改行は LF)。</summary>
    public static string LicenseText => LicenseLazy.Value;

    /// <summary>THIRD-PARTY-NOTICES.md の全文。GlintLoop.App.csproj で埋め込む。埋め込みが無いときは null。</summary>
    public static string? NoticesText => NoticesLazy.Value;

    /// <summary>THIRD-PARTY-NOTICES.md の表(見出しの順)。埋め込んでいないときは空。</summary>
    public static IReadOnlyList<NoticeTable> Tables => TablesLazy.Value;

    private static string? TryRead(string name)
    {
        var asm = typeof(AboutContent).Assembly;
        using var s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var r = new StreamReader(s, new UTF8Encoding(false));
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>## の見出しの下の最初の表を、見出しごとに読む(表の無い見出しは数えない)。</summary>
    public static IReadOnlyList<NoticeTable> ReadTables(string markdown)
    {
        var result = new List<NoticeTable>();
        string? heading = null;
        List<string>? columns = null;
        List<IReadOnlyList<string>>? rows = null;
        var done = false;
        void Flush()
        {
            if (heading is not null && columns is not null && rows is not null) result.Add(new NoticeTable(heading, columns, rows));
            columns = null;
            rows = null;
        }
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
                done = false;
                continue;
            }
            if (heading is null || done) continue;
            if (line.StartsWith('|'))
            {
                var cells = Cells(line);
                if (columns is null)
                {
                    columns = cells;
                    rows = new List<IReadOnlyList<string>>();
                }
                else if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
                {
                    // 区切りの行
                }
                else
                {
                    rows!.Add(cells);
                }
            }
            else if (columns is not null)
            {
                Flush();
                done = true;
            }
        }
        Flush();
        return result;
    }

    private static List<string> Cells(string line)
    {
        var inner = line.Trim().Trim('|');
        return inner.Split('|').Select(c => c.Trim()).ToList();
    }
}
