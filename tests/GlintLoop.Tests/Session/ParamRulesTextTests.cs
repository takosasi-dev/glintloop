// text のパラメータの値の直し(ParamRules.Normalize)と EffectDef の既定値の検査が、E-27・FR-37 の数え方(TextLimits)を使うことのテスト。
// 文字は Unicode のコードポイントで数え(サロゲートペアは 1 文字)、改行は数えず 5 回まで、改行は LF にそろえる。サロゲートペアの途中で切らない。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Session;

public sealed class ParamRulesTextTests
{
    private const string Emoji = "\U0001F600"; // サロゲートペア 1 個(1 文字)

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static ParamDef Text(int? maxLength) =>
        new("text", "文字", ParamType.Text, null, null, null, new StringValue(""), "", null, null, false, true, null, maxLength);

    private static string Normalize(string s, out bool outOfRange) =>
        ((StringValue)ParamRules.Normalize(Text(200), new StringValue(s), out outOfRange)).Value;

    [Test]
    public void E27_FR37_textの値はコードポイントで200文字まで残りサロゲートペアを割らない()
    {
        var emoji = Repeat(Emoji, 200);
        Check.Equal(emoji, Normalize(emoji, out var o1), "絵文字 200 個(UTF-16 で 400 単位)");
        Check.True(!o1, "絵文字 200 個を範囲外とした");

        var over = Repeat(Emoji, 201);
        Check.Equal(emoji, Normalize(over, out var o2), "絵文字 201 個は 200 個に切る");
        Check.True(o2, "切ったのに範囲外としない");

        var mixed = Repeat("a", 199) + Emoji + Emoji;
        var cut = Normalize(mixed, out _);
        Check.Equal(Repeat("a", 199) + Emoji, cut, "199 文字 + 絵文字 2 個");
        Check.True(!char.IsHighSurrogate(cut[^1]), "サロゲートペアの途中で切った");
    }

    [Test]
    public void E27_FR37_textの値の改行は数えず5回までで改行はLFにそろえる()
    {
        var five = Repeat("a\n", 5) + Repeat("b", 195);
        Check.Equal(five, Normalize(five, out var o1), "改行 5 回と 200 文字");
        Check.True(!o1, "改行 5 回と 200 文字を範囲外とした");

        Check.Equal(Repeat("a\n", 5) + "a", Normalize(Repeat("a\n", 6) + "a", out var o2), "6 回目の改行から後ろは捨てる");
        Check.True(o2, "改行を切ったのに範囲外としない");

        Check.Equal("a\nb\nc", Normalize("a\r\nb\rc", out var o3), "CRLF・CR は LF 1 個");
        Check.True(!o3, "改行をそろえただけで範囲外とした");
    }

    [Test]
    public void E27_textの値はmaxLengthが無ければ切らない()
    {
        var s = Repeat(Emoji, 300);
        var v = ParamRules.Normalize(Text(null), new StringValue(s), out var o);
        Check.Equal(s, ((StringValue)v).Value, "maxLength が無いのに切った");
        Check.True(!o, "範囲外とした");
    }

    private static string Json(string def) =>
        "{\"schema\": 1, \"id\": \"t\", \"name\": \"t\", \"stage\": \"finish\", \"group\": \"g\", \"keywords\": [], \"amountMode\": \"mix\"," +
        " \"timeDependent\": false, \"sizeRule\": \"same\", \"aux\": \"text\"," +
        " \"params\": [{\"key\": \"text\", \"label\": \"文字\", \"type\": \"text\", \"default\": " + def + ", \"maxLength\": 3}]," +
        " \"passes\": [{\"id\": \"main\", \"shader\": \"text\", \"inputs\": [\"src\", \"aux\"], \"scale\": 1, \"bind\": {}}]}";

    [Test]
    public void E27_EffectDefの既定値はmaxLengthをコードポイントで数え改行を数えない()
    {
        // 結合文字(e + U+0301)は書記素では 1 個だがコードポイントでは 2 個。改行は数えない
        static EffectCatalog Load(string json) =>
            EffectCatalog.LoadFromSources([("t.json", json)], _ => true, new GlintLoop.Core.Diagnostics.MemoryLogSink());
        var ok = Load(Json("\"\\n" + Emoji + Emoji + Emoji + "\\n\""));
        Check.Equal(1, ok.Effects.Count, "絵文字 3 個と改行 2 回が通らない: " + string.Join(", ", ok.Rejected.Select(r => r.Detail)));
        var ng = Load(Json("\"e\u0301ab\""));
        Check.Equal(0, ng.Effects.Count, "結合文字を書記素で数えた(コードポイントでは 4 個)");
        Check.Equal(2, ng.Rejected.Single().Reason, "理由の番号");
    }
}
