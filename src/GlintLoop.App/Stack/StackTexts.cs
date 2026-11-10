// スタック・パラメータ欄・エフェクトのタブの固定の文言を ui.tsv から引く(キーは stack.*・effects.*・params.*・left.tab.effects)。文言は ui.tsv にだけ書く。
// ステージの名前・見出し・ボタンの名前・ツールチップ・パラメータ欄の空の表示を持つ。文言は §6.3・§9.2・§9.3 の本文のとおり。
using GlintLoop.App.Text;
using GlintLoop.Core.Effects;

namespace GlintLoop.App.Stack;

public static class StackTexts
{
    private static string T(string key) => NoticeTexts.UiText(key);

    public static string StageShape => T("stack.stage.shape");
    public static string StageLook => T("stack.stage.look");
    public static string StageFinish => T("stack.stage.finish");
    public static string StageMotion => T("stack.stage.motion");

    /// <summary>スタックの一覧の名前(UIA)。</summary>
    public static string StackName => T("stack.name");

    /// <summary>パラメータ欄の名前(UIA)。</summary>
    public static string ParamsName => T("params.name");

    /// <summary>対象が全体で行を選んでいないときのパラメータ欄(FR-24)。</summary>
    public static string ParamsEmptyAll => T("params.empty_all");

    /// <summary>全体のときの回転・切り抜き・余白の項目のツールチップ(FR-26)。</summary>
    public static string FixedShapeNotForAll => T("effects.fixed_shape_not_for_all");

    public static string EffectsTab => T("left.tab.effects");
    public static string Search => T("effects.search");
    public static string Advanced => T("params.advanced");
    public static string ResetToDefault => T("params.reset_to_default");
    public static string Reset => T("params.reset");
    public static string SquareCrop => T("params.square_crop");
    public static string SquarePad => T("params.square_pad");
    public static string AutoTrim => T("params.auto_trim");
    public static string Amount => T("params.amount");
    public static string Visible => T("stack.visible");
    public static string Duplicate => T("stack.duplicate");
    public static string Delete => T("stack.delete");
    public static string DragGrip => T("stack.drag_grip");
    public static string Hex => T("params.hex");
    public static string Eyedropper => T("params.eyedropper");
    public static string ColorPicker => T("params.color_picker");

    /// <summary>rect の 4 辺(左・上・右・下)の数値入力の名前。</summary>
    public static IReadOnlyList<string> RectEdges { get; } = [T("params.rect.l"), T("params.rect.t"), T("params.rect.r"), T("params.rect.b")];

    /// <summary>point の X・Y の数値入力の名前。</summary>
    public static IReadOnlyList<string> PointAxes { get; } = [T("params.point.x"), T("params.point.y")];

    /// <summary>色の選択の R・G・B・A のスライダーの名前。</summary>
    public static IReadOnlyList<string> ColorChannels { get; } = [T("params.color.r"), T("params.color.g"), T("params.color.b"), T("params.color.a")];

    public static string StageName(EffectStage stage) => stage switch
    {
        EffectStage.Shape => StageShape,
        EffectStage.Look => StageLook,
        EffectStage.Finish => StageFinish,
        _ => StageMotion,
    };

    /// <summary>単位の表示(§9.2 の unit。deg は °)。単位の記号は文言でないので、ここに置く。</summary>
    public static string UnitText(string unit) => unit switch
    {
        "deg" => "°",
        "‰辺" => "‰",
        _ => unit,
    };
}
