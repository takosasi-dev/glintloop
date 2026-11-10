// §9.7 の規則 14(単体の描画)の文書を作る。ルックのギャラリー(対象がレイヤーのとき)とレイヤーの行のサムネイル(FR-55)が使う。
// そのレイヤー 1 枚だけを土台にした文書: 配置は (0, 0, 100)、表示はオン・不透明度 100・ブレンドは通常、全体のスタックは空にする。
// モーションステージは通さないので、catalog を渡す形でモーションの行を外す(Q-B: 描画を分けずに文書の側で外す)。大きさは呼ぶ側が決める。
using GlintLoop.Core.Effects;

namespace GlintLoop.Core.Session;

public static class SingleLayerView
{
    /// <summary>
    /// レイヤー layerIndex だけを土台にした文書を返す(スタックはそのまま。モーションの行も残す)。元の文書は変えない。
    /// 規則 14 の単体の描画には、モーションの行を外す Of(doc, layerIndex, catalog) を使う。
    /// </summary>
    public static SessionDocument Of(SessionDocument doc, int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentOutOfRangeException.ThrowIfNegative(layerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layerIndex, doc.Layers.Count);
        var layer = doc.Layers[layerIndex] with
        {
            Visible = true,
            Opacity = 100,
            Blend = BlendMode.Normal,
            Place = Placement.Identity,
        };
        return doc with
        {
            Selected = new Target(0),
            Layers = [layer],
            All = doc.All with { Stack = [] },
        };
    }

    /// <summary>規則 14 の単体の描画の文書: Of(doc, layerIndex) から、モーションステージの行(目のオン・オフを問わない)を除いたもの。</summary>
    public static SessionDocument Of(SessionDocument doc, int layerIndex, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var single = Of(doc, layerIndex);
        var layer = single.Layers[0];
        var stack = MotionInfo.WithoutMotion(layer.Stack, catalog);
        return ReferenceEquals(stack, layer.Stack) ? single : single with { Layers = [layer with { Stack = stack }] };
    }
}
