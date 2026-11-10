// 実効 amount の式(仕様書 §9.7 の規則 4 の 3、FR-35)と、scale モードの実効値(§9.2 の amountMode)を計算する純粋な関数。
// 描画(Render の EffectRunner)と、パラメータ欄・キャッシュのキーを作る側が同じ式を使う。
// 値の範囲の直し(param_clamped)はここでは行わない。
namespace GlintLoop.Core.Effects;

public static class EffectAmount
{
    /// <summary>
    /// 実効 amount a(0〜1 の実数)。a = amount÷100 ×(fromLook かつ mode が none 以外なら lookStrength÷100、それ以外は 1)。
    /// amount と lookStrength は 0〜100(%)。fromLook は、その行がルックの適用で入ったもの(origin=look)のとき true。
    /// mode が none のときも式どおりの値を返すが、描画は使わない。
    /// </summary>
    public static double Effective(double amount, double lookStrength, bool fromLook, AmountMode mode)
    {
        var factor = fromLook && mode != AmountMode.None ? lookStrength / 100.0 : 1.0;
        return amount / 100.0 * factor;
    }

    /// <summary>scale モードの実効値 neutral + (value − neutral) × a。</summary>
    public static double Scale(double value, double neutral, double a) => neutral + (value - neutral) * a;

    /// <summary>
    /// パラメータの neutral。宣言にあればそれ、無ければ範囲(min〜max)に 0 を含めば 0、含まなければ既定値(§9.2)。
    /// 数値の型(float・int・angle)だけに使う。
    /// </summary>
    public static double Neutral(ParamDef p)
    {
        if (p.Neutral is { } n) return n;
        if (p.Min is { } min && p.Max is { } max && min <= 0 && 0 <= max) return 0;
        return p.Default is NumberValue d ? d.Value : 0;
    }

    /// <summary>scaleParams に挙がったパラメータの値を実効値に直す。それ以外の値と、数値でない値はそのまま。</summary>
    public static double ScaleParam(EffectDef def, ParamDef p, double value, double a)
    {
        if (def.AmountMode != AmountMode.Scale) return value;
        foreach (var key in def.ScaleParams)
        {
            if (key == p.Key) return Scale(value, Neutral(p), a);
        }
        return value;
    }
}
