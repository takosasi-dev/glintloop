// 左ペインとルックのギャラリーの画面の固定の文言を ui.tsv から引く(キーは left.*・looks.*・name.*)。文言は ui.tsv にだけ書く。
// 文言は §9.3 と §9.12 の本文のとおりで、ここで決め直さない。
using GlintLoop.App.Text;

namespace GlintLoop.App.Looks;

public static class LooksText
{
    /// <summary>ui.tsv のキーの文言。</summary>
    public static string Get(string key) => NoticeTexts.UiText(key);
}
