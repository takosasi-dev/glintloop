// エフェクトのパラメータの値を、その EffectDef の宣言で直す(§9.7 の規則 4 の 5、E-25、FR-27)。
// 範囲外は端、型違い・欠けは既定値、NaN は既定値(ログの対象)。切り抜きの l+r・t+b と levels の white−black の直しも行う(§9.4)。
// 読み込みは、ログ param_clamped まで書く形(effectId と log を渡す Normalize)を使う。編集の経路はログを書かない形を使う。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Session;

public static class ParamRules
{
    /// <summary>切り抜きの l+r・t+b の上限(‰辺)。</summary>
    public const double CropSumLimit = 980;

    /// <summary>levels の white−black の下限(%)。</summary>
    public const double LevelsMinGap = 5;

    /// <summary>point の範囲(画像の幅と高さを 0〜1 にした値)。</summary>
    private const double PointMin = 0, PointMax = 1;

    /// <summary>値を宣言で直し、範囲外か NaN を直したときは log に param_clamped を 1 行書く(読み込みの経路。E-25)。</summary>
    public static ParamValue Normalize(string effectId, ParamDef def, ParamValue? value, ILogSink log)
    {
        var v = Normalize(def, value, out var outOfRange);
        if (outOfRange) log.Write(LogEvents.ParamClamped(effectId, def.Key));
        return v;
    }

    /// <summary>
    /// 値を宣言で直す。value が null(欠け)か型違いなら既定値。outOfRange は、範囲外を端に直したか、NaN を既定値にしたか、
    /// 列挙に無い値を既定値にしたか、文字列を最大の長さで切ったときに true(§9.7 の規則 4 の 5 のログの対象)。
    /// </summary>
    public static ParamValue Normalize(ParamDef def, ParamValue? value, out bool outOfRange)
    {
        outOfRange = false;
        switch (def.Type)
        {
            case ParamType.Float:
            case ParamType.Int:
            case ParamType.Angle:
            {
                if (value is not NumberValue n) return def.Default;
                var v = n.Value;
                if (double.IsNaN(v))
                {
                    outOfRange = true;
                    return def.Default;
                }
                if (def.Type == ParamType.Int && double.IsFinite(v)) v = MathX.RoundHalfUp(v);
                var c = Clamp(v, def.Min, def.Max);
                if (c != v) outOfRange = true;
                if (!double.IsFinite(c))
                {
                    outOfRange = true;
                    return def.Default;
                }
                return c == n.Value ? n : new NumberValue(c);
            }
            case ParamType.Bool:
                return value is BoolValue ? value : def.Default;
            case ParamType.Enum:
            {
                if (value is not StringValue s) return def.Default;
                if (def.Options is not null)
                {
                    foreach (var o in def.Options)
                    {
                        if (o.Value == s.Value) return s;
                    }
                }
                outOfRange = true;
                return def.Default;
            }
            case ParamType.Color:
            {
                if (value is not StringValue s || !ColorText.TryNormalizeRgba(s.Value, out var norm)) return def.Default;
                return norm == s.Value ? s : new StringValue(norm);
            }
            case ParamType.Point:
            {
                if (value is not PointValue p) return def.Default;
                if (double.IsNaN(p.X) || double.IsNaN(p.Y))
                {
                    outOfRange = true;
                    return def.Default;
                }
                var x = Clamp(p.X, PointMin, PointMax);
                var y = Clamp(p.Y, PointMin, PointMax);
                if (x != p.X || y != p.Y) outOfRange = true;
                return outOfRange ? new PointValue(x, y) : p;
            }
            case ParamType.Rect:
            {
                if (value is not RectValue r) return def.Default;
                var d = def.Default as RectValue ?? new RectValue(0, 0, 0, 0);
                var changed = false;
                var l = Edge(r.L, d.L, def, ref changed);
                var t = Edge(r.T, d.T, def, ref changed);
                var rr = Edge(r.R, d.R, def, ref changed);
                var b = Edge(r.B, d.B, def, ref changed);
                outOfRange = changed;
                return changed ? new RectValue(l, t, rr, b) : r;
            }
            case ParamType.Text:
            {
                if (value is not StringValue s) return def.Default;
                if (def.MaxLength is not int max) return s;
                // E-27・FR-37 の数え方(コードポイント。改行は数えず 5 回まで。サロゲートペアの途中で切らない)。改行は LF にそろえる
                var clipped = TextLimits.Clip(s.Value, max);
                if (clipped.Length != TextLimits.NormalizeNewlines(s.Value).Length) outOfRange = true;
                return clipped == s.Value ? s : new StringValue(clipped);
            }
            case ParamType.Font:
                return value is StringValue ? value : def.Default;
            default:
                return def.Default;
        }
    }

    private static double Edge(double v, double fallback, ParamDef def, ref bool changed)
    {
        if (double.IsNaN(v))
        {
            changed = true;
            return fallback;
        }
        var c = Clamp(v, def.Min, def.Max);
        if (c != v) changed = true;
        return c;
    }

    private static double Clamp(double v, double? min, double? max)
    {
        if (min is double lo && v < lo) return lo;
        if (max is double hi && v > hi) return hi;
        return v;
    }

    /// <summary>
    /// エフェクトに固有の、組み合わせの直し(§9.4)。edited が null のときは、ファイルから読み込んだときの直し
    /// (切り抜きは r=980−l・b=980−t、levels は black=white−5)。edited があるときは、その値を動かしたときの直し
    /// (切り抜きは動かした辺を 980−反対側の辺に止める、levels は white を動かしたとき white=black+5)。
    /// previous は動かす前の行の値(edited のときだけ使う)。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, ParamValue>> ApplyConstraints(
        string effectId,
        IReadOnlyList<KeyValuePair<string, ParamValue>> values,
        string? editedKey,
        IReadOnlyList<KeyValuePair<string, ParamValue>>? previous)
    {
        if (effectId == EffectSchema.Crop) return FixCrop(values, editedKey, previous);
        if (effectId == EffectSchema.Levels) return FixLevels(values, editedKey);
        return values;
    }

    private static IReadOnlyList<KeyValuePair<string, ParamValue>> FixCrop(
        IReadOnlyList<KeyValuePair<string, ParamValue>> values, string? editedKey,
        IReadOnlyList<KeyValuePair<string, ParamValue>>? previous)
    {
        var idx = IndexOf(values, "rect");
        if (idx < 0 || values[idx].Value is not RectValue r) return values;
        RectValue? old = null;
        if (editedKey == "rect" && previous is not null && Find(previous, "rect") is RectValue pr) old = pr;
        var (l, rr) = FixPair(r.L, r.R, old?.L, old?.R);
        var (t, b) = FixPair(r.T, r.B, old?.T, old?.B);
        if (l == r.L && rr == r.R && t == r.T && b == r.B) return values;
        return Replace(values, idx, new RectValue(l, t, rr, b));
    }

    /// <summary>a+b が 980 を超えるとき、動かした辺(片方だけが変わったとき)を止める。それ以外は b=980−a。</summary>
    private static (double A, double B) FixPair(double a, double b, double? oldA, double? oldB)
    {
        if (a + b <= CropSumLimit) return (a, b);
        if (oldA is double oa && oldB is double ob)
        {
            var movedA = a != oa;
            var movedB = b != ob;
            if (movedA && !movedB) return (CropSumLimit - b, b);
        }
        return (a, CropSumLimit - a);
    }

    private static IReadOnlyList<KeyValuePair<string, ParamValue>> FixLevels(
        IReadOnlyList<KeyValuePair<string, ParamValue>> values, string? editedKey)
    {
        var bi = IndexOf(values, "black");
        var wi = IndexOf(values, "white");
        if (bi < 0 || wi < 0 || values[bi].Value is not NumberValue black || values[wi].Value is not NumberValue white)
            return values;
        if (white.Value - black.Value >= LevelsMinGap) return values;
        if (editedKey == "white") return Replace(values, wi, new NumberValue(black.Value + LevelsMinGap));
        return Replace(values, bi, new NumberValue(white.Value - LevelsMinGap));
    }

    private static int IndexOf(IReadOnlyList<KeyValuePair<string, ParamValue>> values, string key)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Key == key) return i;
        }
        return -1;
    }

    private static ParamValue? Find(IReadOnlyList<KeyValuePair<string, ParamValue>> values, string key)
    {
        var i = IndexOf(values, key);
        return i < 0 ? null : values[i].Value;
    }

    private static IReadOnlyList<KeyValuePair<string, ParamValue>> Replace(
        IReadOnlyList<KeyValuePair<string, ParamValue>> values, int index, ParamValue value)
    {
        var copy = values.ToArray();
        copy[index] = new KeyValuePair<string, ParamValue>(copy[index].Key, value);
        return copy;
    }
}
