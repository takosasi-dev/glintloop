// 画面(ウェルカム・設定・このソフトについて・ドロップの受け入れ表示)の固定の文言を ui.tsv から引く(キーは welcome.*・drop.*・open.*・settings.*・about.*)。
// 文言は §9.3・FR-3・FR-90・FR-94・FR-103 の本文の言葉をそのまま写したもので、ui.tsv にだけ書く。
using GlintLoop.App.Text;

namespace GlintLoop.App.Screens;

public static class ScreenTexts
{
    /// <summary>ui.tsv のキーの文言。</summary>
    public static string Get(string key) => NoticeTexts.UiText(key);
}
