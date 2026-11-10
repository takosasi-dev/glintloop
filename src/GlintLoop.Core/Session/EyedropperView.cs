// スポイト(FR-29)が色を取る画像の文書を作る。レイヤーが対象のときは、そのレイヤー 1 枚を土台にして、形ステージの回転・切り抜き・余白・反転のうち目がオンのものだけを通す。
// 全体が対象のときは、レイヤーはそのまま重ね、全体のスタックを目がオンの反転だけにする(全体のスタックを通す前の合成に、全体の反転だけを当てる)。
// 描画は呼ぶ側が DocumentRenderer に渡す。元の文書は変えない。
namespace GlintLoop.Core.Session;

public static class EyedropperView
{
    /// <summary>画素の位置を変える 4 つ(回転・切り抜き・余白・反転)。</summary>
    public const string Flip = "flip";

    private static bool MovesPixels(string id) => EffectSchema.IsFixedShape(id) || id == Flip;

    /// <summary>レイヤー layerIndex が対象のときの文書。</summary>
    public static SessionDocument ForLayer(SessionDocument doc, int layerIndex)
    {
        var single = SingleLayerView.Of(doc, layerIndex);
        var layer = single.Layers[0];
        var stack = layer.Stack.Where(e => e.Enabled && MovesPixels(e.Id)).ToArray();
        return single with { Layers = [layer with { Stack = stack }] };
    }

    /// <summary>全体が対象のときの文書。</summary>
    public static SessionDocument ForAll(SessionDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var stack = doc.All.Stack.Where(e => e.Enabled && e.Id == Flip).ToArray();
        return doc with { All = doc.All with { Stack = stack } };
    }
}
