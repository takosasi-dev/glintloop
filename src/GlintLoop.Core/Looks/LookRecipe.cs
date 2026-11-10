// ルック(§9.6・D-40)の型。ルックは、ルックステージと仕上げステージのエフェクトの並びと値の組で、組み込みの 24 個とユーザーのルック(FR-36)がある。
// LookEffect の Params は、その EffectDef の全キーを宣言の並びで持つ(欠けは既定値で埋め、値は宣言の範囲に直してある)。
using GlintLoop.Core.Effects;

namespace GlintLoop.Core.Looks;

/// <summary>ルックの 1 行。Amount は 0〜100(組み込みは 100)。</summary>
public sealed record LookEffect(string Id, double Amount, IReadOnlyList<KeyValuePair<string, ParamValue>> Params)
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
/// ルック 1 つ。Key は組み込みならキー(vivid など)、ユーザーのルックならファイルの名前(拡張子なし。look-xxxxxxxx)。
/// IsBuiltIn は組み込みか。Effects はレシピの順(各ステージの中の順序)。
/// </summary>
public sealed record LookRecipe(string Key, string Name, bool IsBuiltIn, IReadOnlyList<LookEffect> Effects);
