// エフェクトのカタログの受け口。id から読み込み済みの EffectDef を引く(仕様書 §9.2)。
// 製品は EffectCatalog(埋め込みリソースから読んだもの)を、テストは DictionaryEffectCatalog を渡す。
namespace GlintLoop.Core.Effects;

public interface IEffectCatalog
{
    EffectDef? Find(string id);

    /// <summary>読み込み済みの全エフェクト(エフェクトのタブの一覧などに使う)。並びはカタログの順。</summary>
    IReadOnlyList<EffectDef> Effects { get; }
}
