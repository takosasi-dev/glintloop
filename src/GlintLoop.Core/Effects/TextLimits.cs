// テキストのエフェクトの文字列の上限(FR-37・E-27): 文字 200 個(Unicode のコードポイント。改行は数えない)と改行 5 回。
// 改行は CRLF・LF・CR のそれぞれを 1 回と数え、保存する文字列では LF 1 個にそろえる。入力と貼り付けは、はみ出す分を先頭から切る(サロゲートペアの途中では切らない)。
// 入力部品(App)と、そのほかの経路が同じ規則を使えるよう、画面に依らない関数だけを置く。
using System.Text;

namespace GlintLoop.Core.Effects;

public static class TextLimits
{
    /// <summary>文字の上限(コードポイントの個数。改行は数えない)。</summary>
    public const int MaxChars = 200;

    /// <summary>改行の上限。</summary>
    public const int MaxBreaks = 5;

    /// <summary>CRLF・CR を LF 1 個にそろえる。</summary>
    public static string NormalizeNewlines(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.IndexOf('\r') < 0) return s;
        return s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>文字の数(コードポイント。サロゲートペアは 1 個、対になっていないサロゲートも 1 個。改行の CR と LF は数えない)。</summary>
    public static int CountChars(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '\n' or '\r') continue;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    /// <summary>改行の回数(CRLF・LF・CR のそれぞれを 1 回)。</summary>
    public static int CountBreaks(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\r')
            {
                n++;
                if (i + 1 < s.Length && s[i + 1] == '\n') i++;
            }
            else if (s[i] == '\n')
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>上限に収まっているか。</summary>
    public static bool Fits(string s) => CountChars(s) <= MaxChars && CountBreaks(s) <= MaxBreaks;

    /// <summary>改行を LF にそろえ、先頭から上限(文字 200 個・改行 5 回)までで切る。</summary>
    public static string Clip(string s) => Clip(s, MaxChars);

    /// <summary>改行を LF にそろえ、先頭から文字 maxChars 個・改行 5 回までで切る(EffectDef の maxLength。文字の数え方は E-27 と同じ)。</summary>
    public static string Clip(string s, int maxChars) => Insert("", 0, 0, s, maxChars).Text;

    /// <summary>
    /// 文字列 current の [start, start+length) を inserted で置き換える(入力・貼り付け)。inserted は改行を LF にそろえ、
    /// 置き換えたあとの全体が上限に収まる分だけを先頭から使う(はみ出す文字か改行に当たったら、そこから後ろは捨てる)。
    /// 返すのは置き換えたあとの文字列と、入れた文字の後ろの位置(キャレット)。current の残りの部分は変えない。
    /// </summary>
    public static (string Text, int Caret) Insert(string current, int start, int length, string inserted) =>
        Insert(current, start, length, inserted, MaxChars);

    private static (string Text, int Caret) Insert(string current, int start, int length, string inserted, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(inserted);
        start = Math.Clamp(start, 0, current.Length);
        length = Math.Clamp(length, 0, current.Length - start);
        var head = current[..start];
        var tail = current[(start + length)..];
        var restChars = CountChars(head) + CountChars(tail);
        var restBreaks = CountBreaks(head) + CountBreaks(tail);
        var chars = maxChars - restChars;
        var breaks = MaxBreaks - restBreaks;
        var src = NormalizeNewlines(inserted);
        var taken = new StringBuilder(src.Length);
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c == '\n')
            {
                if (breaks <= 0) break;
                breaks--;
                taken.Append(c);
                continue;
            }
            if (chars <= 0) break;
            chars--;
            taken.Append(c);
            if (char.IsHighSurrogate(c) && i + 1 < src.Length && char.IsLowSurrogate(src[i + 1]))
            {
                taken.Append(src[i + 1]);
                i++;
            }
        }
        return (head + taken + tail, start + taken.Length);
    }
}
