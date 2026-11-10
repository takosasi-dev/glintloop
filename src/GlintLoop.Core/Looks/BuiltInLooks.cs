// 組み込みのルック 24 個(§9.6 の表。キー・名前・構成)。値は表のとおりで変えない(Q-10)。書かれていないパラメータは既定値、amount は 100%。
// 表は C# の表で持つ(csproj に埋め込みリソースを足せず、src の .cs に #RRGGBBAA を書けない INV-14 の検査があるため、色は ColorText.Rgba で組み立てる)。
// Build はカタログの宣言で全キーの値の並びに直し、Validate は AC-13 の (2)(存在する id・ステージ・キー・型・範囲)を調べる。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Looks;

/// <summary>表に書いた 1 行のパラメータ(数は double、bool、列挙と色は string)。</summary>
public sealed record LookRawParam(string Key, object Value);

/// <summary>表に書いた 1 つのエフェクト(id と、書かれたパラメータだけ)。</summary>
public sealed record LookRawEffect(string Id, IReadOnlyList<LookRawParam> Params);

/// <summary>表の 1 行(キー・名前・構成)。</summary>
public sealed record LookRawRecipe(string Key, string Name, IReadOnlyList<LookRawEffect> Effects);

public static class BuiltInLooks
{
    private static LookRawParam P(string key, double v) => new(key, v);

    private static LookRawParam P(string key, bool v) => new(key, v);

    private static LookRawParam P(string key, string v) => new(key, v);

    /// <summary>色(sRGB の RRGGBBAA)。</summary>
    private static LookRawParam C(string key, uint rgba) => new(key, ColorText.Rgba(rgba));

    private static LookRawEffect E(string id, params LookRawParam[] ps) => new(id, ps);

    private static LookRawRecipe L(string key, string name, params LookRawEffect[] es) => new(key, name, es);

    /// <summary>§9.6 の表(上から順)。gradmap の n は列挙(選択肢 "2"・"3")なので、表の n=3 を文字列 "3" で書く。</summary>
    public static IReadOnlyList<LookRawRecipe> Table { get; } =
    [
        L("vivid", "ビビッド",
            E("saturation", P("saturation", 25), P("vibrance", 30)), E("contrast", P("c", 15)),
            E("sharpen", P("radius", 2), P("strength", 80))),
        L("clear", "クリア",
            E("exposure", P("ev", 0.15)), E("tone", P("shadows", 25), P("highlights", -15)), E("saturation", P("vibrance", 15)),
            E("sharpen", P("radius", 2.5), P("strength", 60))),
        L("dream", "ドリーミー",
            E("exposure", P("ev", 0.2)), E("fade", P("lift", 6)), E("saturation", P("saturation", -10)),
            E("glow", P("threshold", 45), P("radius", 60), P("intensity", 90), C("color", 0xFFE8F5FFu))),
        L("film", "フィルム",
            E("contrast", P("c", 12)), E("wb", P("temp", 12), P("tint", 4)), E("fade", P("lift", 8), P("drop", 3)),
            E("splittone", C("shadow", 0x2F4F6BFFu), C("highlight", 0xFFC48AFFu), P("strength", 35)),
            E("grain", P("strength", 30), P("size", 2), P("mono", true)), E("vignette", P("strength", 25))),
        L("retro", "レトロ",
            E("fade", P("lift", 14), P("drop", 6)), E("saturation", P("saturation", -25)), E("wb", P("temp", 18), P("tint", 8)),
            E("splittone", C("shadow", 0x3C5A5AFFu), C("highlight", 0xF2C98AFFu), P("strength", 40)),
            E("grain", P("strength", 25)), E("vignette", P("strength", 35), P("round", 40))),
        L("mono", "モノクロ",
            E("mono"), E("contrast", P("c", 15))),
        L("noir", "ノワール",
            E("mono", P("rw", 35), P("gw", 55), P("bw", 10)), E("contrast", P("c", 55)),
            E("levels", P("black", 6), P("white", 94), P("gamma", 0.9)), E("grain", P("strength", 30)),
            E("vignette", P("strength", 50), P("size", 40))),
        L("sepia", "セピア",
            E("mono"), E("gradmap", P("n", "3"), C("c0", 0x2A1A10FFu), C("c1", 0xA8774AFFu), C("c2", 0xF6E7C8FFu)),
            E("vignette", P("strength", 30))),
        L("cine", "シネマ",
            E("contrast", P("c", 18)), E("splittone", C("shadow", 0x0E5F73FFu), C("highlight", 0xFF9A4DFFu), P("strength", 55)),
            E("saturation", P("saturation", 8)), E("vignette", P("strength", 35))),
        L("neon", "ネオン",
            E("contrast", P("c", 30)), E("saturation", P("saturation", 40)),
            E("splittone", C("shadow", 0x6A00FFFFu), C("highlight", 0x00E5FFFFu), P("strength", 60)),
            E("glow", P("threshold", 50), P("radius", 50), P("intensity", 140), C("color", 0xFF4FD8FFu)), E("vignette", P("strength", 45))),
        L("pastel", "パステル",
            E("exposure", P("ev", 0.35)), E("saturation", P("saturation", -25), P("vibrance", 10)), E("fade", P("lift", 10)),
            E("tint", C("color", 0xFFD6E8FFu), P("mode", "softlight"), P("opacity", 40))),
        L("pop", "ポップ",
            E("contrast", P("c", 40)), E("saturation", P("saturation", 50), P("vibrance", 20)),
            E("sharpen", P("radius", 3), P("strength", 120))),
        L("matte", "マット",
            E("fade", P("lift", 18), P("drop", 8)), E("contrast", P("c", 10)), E("saturation", P("saturation", -10)),
            E("tone", P("shadows", 10), P("highlights", -10))),
        L("sunset", "夕焼け",
            E("wb", P("temp", 45), P("tint", 10)),
            E("gradient", C("c0", 0xFF6B35FFu), C("c1", 0x8E2DE2FFu), P("angle", 90), P("mode", "softlight"), P("opacity", 55)),
            E("contrast", P("c", 15)), E("vignette", P("strength", 30))),
        L("midnight", "ミッドナイト",
            E("exposure", P("ev", -0.5)), E("wb", P("temp", -55), P("tint", -5)),
            E("tint", C("color", 0x1A3A8FFFu), P("mode", "multiply"), P("opacity", 45)), E("contrast", P("c", 20)),
            E("glow", P("threshold", 70), P("radius", 40), P("intensity", 60), C("color", 0x9FC4FFFFu))),
        L("pixel", "ドット絵",
            E("pixelate", P("size", 25)), E("palette", P("kind", "p16"), P("dither", 40))),
        L("gb4", "4階調の緑",
            E("mono"), E("contrast", P("c", 20)), E("pixelate", P("size", 10)), E("palette", P("kind", "g4"), P("dither", 60))),
        L("comic", "コミック",
            E("contrast", P("c", 30)), E("saturation", P("saturation", 30)), E("halftone", P("mode", "cmy"), P("size", 22), P("angle", 45))),
        L("sticker", "ステッカー",
            E("saturation", P("vibrance", 25)), E("contrast", P("c", 10)),
            E("outline", P("width", 24), P("pos", "center"), C("color", 0xFFFFFFFFu)),
            E("shadow", P("dx", 0), P("dy", 12), P("blur", 20), C("color", 0x00000080u))),
        L("miniature", "ミニチュア",
            E("saturation", P("saturation", 30)), E("contrast", P("c", 15)),
            E("tiltshift", P("angle", 0), P("pos", 50), P("width", 22), P("blur", 35))),
        L("classic", "クラシック",
            E("wb", P("temp", 6)), E("contrast", P("c", 8)), E("vignette", P("strength", 55), P("size", 35), P("soft", 70))),
        L("sakura", "桜",
            E("exposure", P("ev", 0.25)), E("wb", P("temp", 10), P("tint", 18)), E("saturation", P("saturation", -5)),
            E("tint", C("color", 0xFFB7D5FFu), P("mode", "softlight"), P("opacity", 60)),
            E("glow", P("threshold", 55), P("radius", 45), P("intensity", 50), C("color", 0xFFC8E0FFu))),
        L("crossproc", "クロスプロセス",
            E("crossproc"), E("saturation", P("saturation", 15)), E("vignette", P("strength", 25)), E("grain", P("strength", 15))),
        L("bleach", "銀残し",
            E("bleach", P("contrast", 60)), E("contrast", P("c", 15)), E("grain", P("strength", 18))),
    ];

    /// <summary>
    /// 表の 24 個を、カタログの宣言で全キーの値の並びにしたルックにする(表の順)。表の値を変えずに入れられないもの
    /// (無い id・ルックと仕上げ以外のステージ・無いキー・型違い・範囲外・列挙に無い値)は入れず、Validate がその内容を返す。
    /// </summary>
    public static IReadOnlyList<LookRecipe> Build(IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var list = new List<LookRecipe>(Table.Count);
        foreach (var r in Table)
        {
            var effects = new List<LookEffect>(r.Effects.Count);
            foreach (var e in r.Effects)
            {
                var def = catalog.Find(e.Id);
                if (def is null || !LookStacks.IsLookStage(def)) continue;
                var values = EffectSchema.DefaultParams(def).ToArray();
                foreach (var p in e.Params)
                {
                    var pd = EffectSchema.FindParam(def, p.Key);
                    if (pd is null || ToValue(pd, p.Value) is not { } v || Problem(pd, v) is not null) continue;
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (values[i].Key == p.Key) values[i] = new(p.Key, v);
                    }
                }
                effects.Add(new LookEffect(def.Id, 100, ParamRules.ApplyConstraints(def.Id, values, null, null)));
            }
            list.Add(new LookRecipe(r.Key, r.Name, true, effects));
        }
        return list;
    }

    /// <summary>
    /// AC-13 の (2): 表の構成が、存在する id(ルックか仕上げのステージ)と、宣言にあるキー・型・範囲内の値だけを使うかを調べる。
    /// 問題の一覧(空なら問題なし)を返す。キーの重複と 24 個の数も調べる。
    /// </summary>
    public static IReadOnlyList<string> Validate(IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var problems = new List<string>();
        if (Table.Count != 24) problems.Add($"ルックの数が {Table.Count}(24 のはず)");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Table)
        {
            if (!keys.Add(r.Key)) problems.Add($"{r.Key}: キーが重複している");
            if (r.Effects.Count == 0) problems.Add($"{r.Key}: エフェクトが無い");
            var lastKey = -1;
            foreach (var e in r.Effects)
            {
                var def = catalog.Find(e.Id);
                if (def is null)
                {
                    problems.Add($"{r.Key}: id {e.Id} がカタログに無い");
                    continue;
                }
                if (!LookStacks.IsLookStage(def)) problems.Add($"{r.Key}: {e.Id} のステージが {def.Stage}(ルックか仕上げのはず)");
                var orderKey = EffectSchema.OrderKey(def);
                if (orderKey < lastKey) problems.Add($"{r.Key}: {e.Id} がステージの順に並んでいない");
                lastKey = Math.Max(lastKey, orderKey);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.Params)
                {
                    if (!seen.Add(p.Key)) problems.Add($"{r.Key}: {e.Id}.{p.Key} が 2 回ある");
                    var pd = EffectSchema.FindParam(def, p.Key);
                    if (pd is null)
                    {
                        problems.Add($"{r.Key}: {e.Id} にパラメータ {p.Key} が無い");
                        continue;
                    }
                    if (ToValue(pd, p.Value) is not { } v)
                    {
                        problems.Add($"{r.Key}: {e.Id}.{p.Key} の型が {pd.Type} と合わない");
                        continue;
                    }
                    if (Problem(pd, v) is { } why) problems.Add($"{r.Key}: {e.Id}.{p.Key} {why}");
                }
            }
        }
        return problems;
    }

    /// <summary>表の値をパラメータの型の値にする。型が合わなければ null。</summary>
    private static ParamValue? ToValue(ParamDef pd, object raw) => pd.Type switch
    {
        ParamType.Float or ParamType.Int or ParamType.Angle => raw is double d ? new NumberValue(d) : null,
        ParamType.Bool => raw is bool b ? new BoolValue(b) : null,
        ParamType.Enum or ParamType.Color => raw is string s ? new StringValue(s) : null,
        _ => null,
    };

    /// <summary>値が宣言の範囲(min・max・int の整数・列挙の選択肢・色の形)に入らないときの説明。入れば null。</summary>
    private static string? Problem(ParamDef pd, ParamValue v)
    {
        switch (v)
        {
            case NumberValue n:
                if (double.IsNaN(n.Value) || double.IsInfinity(n.Value)) return "が有限の数でない";
                if (pd.Min is { } min && n.Value < min) return $"= {n.Value} が最小 {min} より小さい";
                if (pd.Max is { } max && n.Value > max) return $"= {n.Value} が最大 {max} より大きい";
                if (pd.Type == ParamType.Int && n.Value != Math.Floor(n.Value)) return $"= {n.Value} が整数でない";
                return null;
            case StringValue s when pd.Type == ParamType.Enum:
                return pd.Options is not null && pd.Options.Any(o => o.Value == s.Value) ? null : $"= {s.Value} が列挙に無い";
            case StringValue s when pd.Type == ParamType.Color:
                return ColorText.TryNormalizeRgba(s.Value, out var norm) && norm == s.Value ? null : $"= {s.Value} が色の形でない";
            default:
                return null;
        }
    }
}
