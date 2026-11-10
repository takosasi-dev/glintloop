// テキストの文字列の上限(FR-37・E-27・AC-24 の (1))の規則の単体のテスト。入力部品はこの規則を使う(画面の上の確かめは Ui/TextParamUiTests.cs)。
// 文字は Unicode のコードポイントで数え(絵文字 1 個は 1 文字)、改行は数えない。貼り付けは先頭から 200 文字・改行 5 回までで切り、改行は LF にそろえる。
using GlintLoop.Core.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Core;

public sealed class TextLimitsTests
{
    private const string Emoji = "\U0001F600"; // サロゲートペア 1 個(1 文字)

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    [Test]
    public void AC24_1_200文字までが入り201文字目は受け付けない()
    {
        var s = Repeat("あ", 200);
        Check.True(TextLimits.Fits(s), "200 文字が上限に収まらない");
        var (after, caret) = TextLimits.Insert(s, s.Length, 0, "い");
        Check.Equal(s, after, "201 文字目を入れたあと");
        Check.Equal(s.Length, caret, "キャレット");
        var (filled, _) = TextLimits.Insert(Repeat("あ", 199), 199, 0, "い");
        Check.Equal(Repeat("あ", 199) + "い", filled, "200 文字目");
    }

    [Test]
    public void AC24_1_改行5回までが入り6回目は受け付けない()
    {
        var s = "a\nb\nc\nd\ne\nf";
        Check.Equal(5, TextLimits.CountBreaks(s), "改行の数");
        Check.True(TextLimits.Fits(s), "改行 5 回が上限に収まらない");
        Check.Equal(s, TextLimits.Insert(s, s.Length, 0, "\n").Text, "6 回目の改行");
        Check.Equal(s + "g", TextLimits.Insert(s, s.Length, 0, "g").Text, "改行 5 回のあとの文字");
    }

    [Test]
    public void AC24_1_絵文字は1文字で200個が入り201個目は入らずサロゲートペアの途中で切らない()
    {
        var s = Repeat(Emoji, 200);
        Check.Equal(200, TextLimits.CountChars(s), "絵文字 200 個の文字数");
        Check.True(TextLimits.Fits(s), "絵文字 200 個が上限に収まらない");
        Check.Equal(s, TextLimits.Insert(s, s.Length, 0, Emoji).Text, "201 個目の絵文字");
        var clipped = TextLimits.Clip(Repeat(Emoji, 201));
        Check.Equal(s, clipped, "絵文字 201 個の貼り付け");
        Check.Equal(400, clipped.Length, "UTF-16 の長さ(サロゲートペアを割らない)");
        // 残り 1 文字のところへ「絵文字 2 個」を貼ると、1 個目のサロゲートペアがまるごと入る
        var (one, _) = TextLimits.Insert(Repeat("x", 199), 199, 0, Emoji + Emoji);
        Check.Equal(Repeat("x", 199) + Emoji, one, "残り 1 文字への絵文字 2 個");
    }

    [Test]
    public void AC24_1_改行は文字に数えずCRLFとLFとCRがそれぞれ1回()
    {
        Check.Equal(3, TextLimits.CountChars("a\r\nb\nc"), "改行を除いた文字数");
        Check.Equal(3, TextLimits.CountBreaks("a\r\nb\rc\nd"), "CRLF・CR・LF の改行の回数");
        var s = Repeat("a", 200) + "\n\n\n\n\n";
        Check.True(TextLimits.Fits(s), "200 文字と改行 5 回が上限に収まらない");
    }

    [Test]
    public void AC24_1_E27_貼り付けは先頭から6行で切られ改行はLF1個にそろう()
    {
        var (text, caret) = TextLimits.Insert("", 0, 0, "A\r\nB\nC\rD\r\nE\nF\r\nG");
        Check.Equal("A\nB\nC\nD\nE\nF", text, "保存される値");
        Check.Equal(text.Length, caret, "キャレット");
    }

    [Test]
    public void AC24_1_E27_貼り付けは残りの文字数の分だけ先頭から入り選択の範囲を置き換える()
    {
        var current = Repeat("a", 190) + "ZZ";
        // 末尾の "ZZ" を選んで 15 文字を貼る: 残りの 190 文字に足せるのは 10 文字
        var (text, caret) = TextLimits.Insert(current, 190, 2, "0123456789ABCDE");
        Check.Equal(Repeat("a", 190) + "0123456789", text, "置き換えたあと");
        Check.Equal(200, caret, "キャレット");
        // 先頭に挿入しても、後ろの部分は変えない
        var (head, _) = TextLimits.Insert("xy", 0, 0, "\r\n\r\n");
        Check.Equal("\n\nxy", head, "先頭への改行の貼り付け");
    }

    [Test]
    public void E27_上限を超えた文字列のときは削除だけが通り挿入は入らない()
    {
        var over = Repeat("a", 205);
        Check.True(!TextLimits.Fits(over), "205 文字が上限に収まってしまう");
        Check.Equal(over, TextLimits.Insert(over, 0, 0, "b").Text, "超えているときの挿入");
        Check.Equal(over[1..], TextLimits.Insert(over, 0, 1, "").Text, "超えているときの削除");
    }
}
