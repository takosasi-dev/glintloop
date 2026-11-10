// 組み込みのモーションのプリセット 28 個(§9.5 の表。キー・名前・構成・タイルの t)。値は表のとおりで変えない(Q-10)。書かれていないパラメータ(phase など)は既定値。
// タイルの t は整数の組 K÷N で持つ(0.25=1/4、0.125=1/8、0.1=1/10、0.05=1/20。描画の frameK・frameN にそのまま渡す)。
// Build はカタログの宣言で全キーの値の並びに直し、Validate は AC-13 の (2)(存在する id・モーションのステージ・キー・型・範囲)を調べる。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Motion;

/// <summary>表に書いた 1 つのパラメータ(数は double、bool、列挙は string、点は PointValue)。</summary>
public sealed record PresetRawParam(string Key, object Value);

/// <summary>表に書いた 1 つのエフェクト(id と、書かれたパラメータだけ。書かれた順)。</summary>
public sealed record PresetRawEffect(string Id, IReadOnlyList<PresetRawParam> Params);

/// <summary>表の 1 行(キー・名前・構成・タイルの t=TileK÷TileN)。</summary>
public sealed record PresetRaw(string Key, string Name, int TileK, int TileN, IReadOnlyList<PresetRawEffect> Effects);

/// <summary>プリセットの 1 つのエフェクト。Params はその EffectDef の全キーを宣言の並び(cycles・phase・wave が先)で持つ。</summary>
public sealed record MotionPresetEffect(string Id, IReadOnlyList<KeyValuePair<string, ParamValue>> Params)
{
    public ParamValue? Get(string key)
    {
        foreach (var kv in Params)
        {
            if (kv.Key == key) return kv.Value;
        }
        return null;
    }
}

/// <summary>
/// プリセット 1 つ(Build の結果)。Effects は表の順。MissingIds はカタログに無かった id(入れていない)で、
/// 全エフェクトがそろったあとは空になる。
/// </summary>
public sealed record MotionPreset(string Key, string Name, int TileK, int TileN, IReadOnlyList<MotionPresetEffect> Effects, IReadOnlyList<string> MissingIds);

public static class BuiltInMotionPresets
{
    private static PresetRawParam P(string key, double v) => new(key, v);

    private static PresetRawParam P(string key, bool v) => new(key, v);

    private static PresetRawParam P(string key, string v) => new(key, v);

    /// <summary>点 (x, y)。</summary>
    private static PresetRawParam Pt(string key, double x, double y) => new(key, new PointValue(x, y));

    private static PresetRawEffect E(string id, params PresetRawParam[] ps) => new(id, ps);

    private static PresetRaw M(string key, string name, int tileK, int tileN, params PresetRawEffect[] es) => new(key, name, tileK, tileN, es);

    /// <summary>§9.5 の表(上から順)。</summary>
    public static IReadOnlyList<PresetRaw> Table { get; } =
    [
        M("spin", "くるくる", 1, 4, E("spin", P("cycles", 1))),
        M("spin_ccw", "逆回転", 1, 4, E("spin", P("dir", "ccw"), P("cycles", 1))),
        M("bounce", "ぴょんぴょん", 1, 4, E("bounce", P("cycles", 2), P("height", 80))),
        M("hop", "ゆるジャンプ", 1, 4, E("bounce", P("cycles", 1), P("height", 120), P("squash", 20))),
        M("hover", "ふわふわ", 1, 4, E("hover", P("cycles", 1))),
        M("pulse", "ぷくぷく", 1, 8, E("pulse", P("shape", "sine"), P("cycles", 2), P("amp", 10))),
        M("heartbeat", "ドキドキ", 1, 10, E("pulse", P("shape", "heartbeat"), P("cycles", 2), P("amp", 14))),
        M("shake", "ブルブル", 1, 4, E("shake", P("cycles", 1), P("rate", 12), P("amp", 12))),
        M("quake", "大揺れ", 1, 4, E("shake", P("cycles", 1), P("rate", 8), P("amp", 40), P("rot", 8), P("smooth", true))),
        M("wobble", "ゆらゆら", 1, 4, E("wobble", P("cycles", 1), P("angle", 10))),
        M("swing", "ブランコ", 1, 4, E("wobble", P("cycles", 1), P("angle", 25), Pt("pivot", 0.5, 0.0))),
        M("jelly", "ぷるぷる", 1, 4, E("jelly", P("cycles", 3))),
        M("coin", "コイン", 1, 8, E("coinflip", P("axis", "y"), P("cycles", 1))),
        M("marquee", "流れる", 1, 4, E("slide", P("dir", "left"), P("cycles", 1))),
        M("blink", "ピカピカ", 1, 20, E("blink", P("mode", "opacity"), P("shape", "square"), P("cycles", 3))),
        M("flash", "フラッシュ", 1, 4, E("blink", P("mode", "flash"), P("shape", "square"), P("duty", 20), P("cycles", 2))),
        M("rainbow", "虹色", 1, 4, E("huecycle", P("mode", "full"), P("cycles", 1))),
        M("neon", "ネオン", 1, 4, E("glowpulse", P("cycles", 2))),
        M("glint", "キラッ", 1, 4, E("shine", P("cycles", 1))),
        M("leak", "ライトリーク", 1, 4, E("leaks", P("palette", "warm"), P("cycles", 1))),
        M("glitch", "グリッチ", 1, 4, E("glitch", P("cycles", 1))),
        M("rgb", "RGBずれ", 1, 8, E("rgbsplit", P("cycles", 2))),
        M("ripple", "波紋", 1, 4, E("ripple", P("cycles", 1))),
        M("sparkle", "キラキラ", 1, 4, E("sparkle", P("cycles", 2))),
        M("snow", "雪", 1, 4, E("particles", P("kind", "snow"), P("dir", "down"), P("cycles", 1))),
        M("confetti", "紙吹雪", 1, 4, E("particles", P("kind", "confetti"), P("tone", "rainbow"), P("count", 60), P("cycles", 1))),
        M("bubbles", "泡", 1, 4, E("particles", P("kind", "bubbles"), P("dir", "up"), P("cycles", 1))),
        M("film", "フィルム", 1, 4,
            E("grainmove", P("cycles", 1), P("rate", 6)), E("leaks", P("palette", "warm"), P("intensity", 40), P("cycles", 1))),
    ];

    /// <summary>
    /// 表の 28 個を、カタログの宣言で全キーの値の並びにしたプリセットにする(表の順)。表の値を変えずに入れられないもの
    /// (無い id・モーション以外のステージ・無いキー・型違い・範囲外・列挙に無い値)は入れず、Validate がその内容を返す。
    /// 無い id は MissingIds に残す(move・light・noise の担当の id がそろうまでは、そのプリセットのエフェクトが欠ける)。
    /// </summary>
    public static IReadOnlyList<MotionPreset> Build(IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var list = new List<MotionPreset>(Table.Count);
        foreach (var r in Table)
        {
            var effects = new List<MotionPresetEffect>(r.Effects.Count);
            var missing = new List<string>();
            foreach (var e in r.Effects)
            {
                var def = catalog.Find(e.Id);
                if (def is null)
                {
                    missing.Add(e.Id);
                    continue;
                }
                if (def.Stage != EffectStage.Motion) continue;
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
                effects.Add(new MotionPresetEffect(def.Id, values));
            }
            list.Add(new MotionPreset(r.Key, r.Name, r.TileK, r.TileN, effects, missing));
        }
        return list;
    }

    /// <summary>表が使う id(重複なし、最初に出た順)。</summary>
    public static IReadOnlyList<string> UsedIds() => Table.SelectMany(r => r.Effects).Select(e => e.Id).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>表が使う id のうち、カタログに無いもの(最初に出た順)。</summary>
    public static IReadOnlyList<string> MissingIds(IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return UsedIds().Where(id => catalog.Find(id) is null).ToList();
    }

    /// <summary>Validate の文のうち「id がカタログに無い」を表す文の印(テストが、そろう前の欠けとほかの問題を分けるため)。</summary>
    public const string MissingMark = "がカタログに無い";

    /// <summary>
    /// AC-13 の (2): 表の構成が、存在する id(モーションのステージ)と、宣言にあるキー・型・範囲内の値だけを使うかを調べる。
    /// 問題の一覧(空なら問題なし)を返す。キーの重複・28 個の数・タイルの t(0&lt;K≤N)・phase と wave を書いていないことも調べる。
    /// </summary>
    public static IReadOnlyList<string> Validate(IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var problems = new List<string>();
        if (Table.Count != 28) problems.Add($"プリセットの数が {Table.Count}(28 のはず)");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Table)
        {
            if (!keys.Add(r.Key)) problems.Add($"{r.Key}: キーが重複している");
            if (r.Effects.Count == 0) problems.Add($"{r.Key}: エフェクトが無い");
            if (r.TileN < 1 || r.TileK < 0 || r.TileK > r.TileN) problems.Add($"{r.Key}: タイルの t={r.TileK}/{r.TileN} が 0〜1 に入らない");
            foreach (var e in r.Effects)
            {
                var def = catalog.Find(e.Id);
                if (def is null)
                {
                    problems.Add($"{r.Key}: id {e.Id} {MissingMark}");
                    continue;
                }
                if (def.Stage != EffectStage.Motion) problems.Add($"{r.Key}: {e.Id} のステージが {def.Stage}(モーションのはず)");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.Params)
                {
                    if (!seen.Add(p.Key)) problems.Add($"{r.Key}: {e.Id}.{p.Key} が 2 回ある");
                    if (p.Key is "phase" or "wave") problems.Add($"{r.Key}: {e.Id}.{p.Key} は既定値を使う(表に書かない)");
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
        ParamType.Enum => raw is string s ? new StringValue(s) : null,
        ParamType.Point => raw as PointValue,
        _ => null,
    };

    /// <summary>値が宣言の範囲(min・max・int の整数・列挙の選択肢・点の有限)に入らないときの説明。入れば null。</summary>
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
            case PointValue p:
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return "が有限の点でない";
                if (pd.Min is { } pmin && (p.X < pmin || p.Y < pmin)) return $"= ({p.X}, {p.Y}) が最小 {pmin} より小さい";
                if (pd.Max is { } pmax && (p.X > pmax || p.Y > pmax)) return $"= ({p.X}, {p.Y}) が最大 {pmax} より大きい";
                return null;
            default:
                return null;
        }
    }
}
