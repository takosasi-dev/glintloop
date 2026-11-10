// §9.12 の通知とダイアログの文言(表の 40 行)と、画面の固定の文言を、埋め込みリソースの notices.tsv・ui.tsv から読む。文言はこの 2 つのファイルにだけ書く。
// Format は {…} を値で置き換える(値の書式は Core の NoticeFormat)。種別がダイアログの行は、表の文言の末尾の括弧(ボタンの説明)を除いた本文を DialogMessage で返す。
using System.IO;
using System.Text;
using GlintLoop.Core.Notices;

namespace GlintLoop.App.Text;

/// <summary>表の種別の列(info・warn・error・ダイアログ・常時表示)。</summary>
public enum NoticeKind { Info, Warn, Error, Dialog, Persistent }

public sealed record NoticeRow(string Key, NoticeKind Kind, string Text);

public static class NoticeTexts
{
    private static readonly Lazy<IReadOnlyList<NoticeRow>> RowsLazy = new(() => ReadNotices(ReadResource("notices.tsv")));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> UiLazy = new(() => ReadUi(ReadResource("ui.tsv")));

    /// <summary>表の 40 行(表の順)。</summary>
    public static IReadOnlyList<NoticeRow> Rows => RowsLazy.Value;

    /// <summary>画面の固定の文言(キーと文言)。</summary>
    public static IReadOnlyDictionary<string, string> Ui => UiLazy.Value;

    public static NoticeRow Row(string key) => Rows.FirstOrDefault(r => r.Key == key) ?? throw new KeyNotFoundException("文言の表に無いキー: " + key);

    public static string UiText(string key) => Ui.TryGetValue(key, out var s) ? s : throw new KeyNotFoundException("画面の文言に無いキー: " + key);

    /// <summary>{名前} を値で置き換える。値の無い {…} は残す。</summary>
    public static string Format(string key, IReadOnlyDictionary<string, string> args)
    {
        var text = Row(key).Text;
        foreach (var (name, value) in args) text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return text;
    }

    public static string Format(string key, params (string Name, string Value)[] args) =>
        Format(key, args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

    public static string Format(Notice notice) => Format(notice.Key, notice.Args);

    /// <summary>ダイアログの行の本文(末尾の「(ボタン…)」などの説明を除く)。ほかの種別はそのまま。</summary>
    public static string DialogMessage(string key)
    {
        var row = Row(key);
        if (row.Kind != NoticeKind.Dialog) return row.Text;
        var at = row.Text.LastIndexOf('(');
        return at > 0 && row.Text.EndsWith(')') ? row.Text[..at] : row.Text;
    }

    /// <summary>Core の NoticeLevel(通知バーの種別)。ダイアログと常時表示の行は通知バーに出さないので null。</summary>
    public static NoticeLevel? LevelOf(string key) => Row(key).Kind switch
    {
        NoticeKind.Info => NoticeLevel.Info,
        NoticeKind.Warn => NoticeLevel.Warn,
        NoticeKind.Error => NoticeLevel.Error,
        _ => null,
    };

    /// <summary>キーの種別で Notice を作る(通知バーに出す行だけ)。</summary>
    public static Notice Notice(string key, params (string Name, string Value)[] args) =>
        Core.Notices.Notice.Of(key, LevelOf(key) ?? throw new ArgumentException("通知バーに出さない行: " + key), args);

    internal static IReadOnlyList<NoticeRow> ReadNotices(string tsv)
    {
        var rows = new List<NoticeRow>();
        foreach (var line in Lines(tsv))
        {
            var cols = line.Split('\t');
            if (cols.Length != 3) throw new FormatException("notices.tsv の列の数が 3 でない: " + line);
            var kind = cols[1] switch
            {
                "info" => NoticeKind.Info,
                "warn" => NoticeKind.Warn,
                "error" => NoticeKind.Error,
                "ダイアログ" => NoticeKind.Dialog,
                "常時表示" => NoticeKind.Persistent,
                _ => throw new FormatException("notices.tsv の種別が不明: " + cols[1]),
            };
            rows.Add(new NoticeRow(cols[0], kind, cols[2]));
        }
        return rows;
    }

    private static IReadOnlyDictionary<string, string> ReadUi(string tsv)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(tsv))
        {
            var at = line.IndexOf('\t');
            if (at <= 0) throw new FormatException("ui.tsv の行の形が違う: " + line);
            map.Add(line[..at], line[(at + 1)..]);
        }
        return map;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    private static string ReadResource(string name)
    {
        var asm = typeof(NoticeTexts).Assembly;
        using var s = asm.GetManifestResourceStream("GlintLoop.App.Text." + name) ?? throw new InvalidOperationException("埋め込みリソースが無い: " + name);
        using var r = new StreamReader(s, new UTF8Encoding(false));
        return r.ReadToEnd();
    }
}
