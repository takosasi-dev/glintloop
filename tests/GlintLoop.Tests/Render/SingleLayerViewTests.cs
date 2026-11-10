// §9.7 の規則 14(単体の描画)の文書の検査。レイヤー 1 枚を土台にし、配置・不透明度・ブレンド・全体のスタックを通さないこと(WARP)。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Render;

public sealed class SingleLayerViewTests
{
    [Test]
    public void 規則14_レイヤー1枚を配置と不透明度とブレンドと全体のスタックを通さずに描く()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(200, 100, 40, 40, 40), CompositeImg.Solid(60, 30, 250, 10, 10));
        kit.Place(1, 120, -80, 25);
        Check.True(kit.Session.SetLayerOpacity(1, 30).Changed, "不透明度");
        Check.True(kit.Session.SetLayerBlend(1, BlendMode.Multiply).Changed, "ブレンド");
        kit.AddEffect(Target.OfLayer(1), "invert");
        kit.AddEffect(Target.All, "invert");

        var single = SingleLayerView.Of(kit.Doc, 1);
        Check.Equal(1, single.Layers.Count, "レイヤーは 1 枚");
        Check.Equal(0, single.All.Stack.Count, "全体のスタックは空");
        Check.Equal(2, kit.Doc.Layers.Count, "元の文書は変わらない");

        var img = kit.Render(single, 64);
        Check.Equal(64, img.Width, "長辺 64");
        Check.Equal(32, img.Height, "縦横比はレイヤーの画像のまま");
        // レイヤーの invert だけが掛かる(赤 250,10,10 → 5,245,245 付近)。不透明度とブレンドと全体の invert は通さない
        Check.True(img.Pixels[0] < 20 && img.Pixels[1] > 230 && img.Pixels[2] > 230, $"色 {img.Pixels[0]},{img.Pixels[1]},{img.Pixels[2]}");
        Check.Equal(255, (int)img.Pixels[3], "不透明");
    }
}
