// レイヤーの画面(右ペインの見出し・一覧・レイヤーの設定・空のキャンバスのダイアログ)の固定の文言を ui.tsv から引く。
// 文言は仕様書 §9.3・FR-44・FR-48・FR-49 の語をそのまま使い、ui.tsv にだけ書く。
using GlintLoop.App.Text;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Layers;

public static class LayerTexts
{
    /// <summary>ui.tsv のキーの文言(キーは layers.*・empty_canvas.*)。</summary>
    public static string Get(string key) => NoticeTexts.UiText(key);

    public static string Header => Get("layers.header");
    public static string ResetAll => Get("layers.reset_all");
    public static string All => Get("layers.all");
    public static string Visible => Get("layers.visible");
    public static string Thumbnail => Get("layers.thumbnail");
    public static string List => Get("layers.list");
    public static string Opacity => Get("layers.opacity");
    public static string X => Get("layers.x");
    public static string Y => Get("layers.y");
    public static string Scale => Get("layers.scale");
    public static string Center => Get("layers.center");
    public static string Scale100 => Get("layers.scale100");
    public static string Blend => Get("layers.blend");
    public static string Name => Get("layers.name");
    public static string Fill => Get("layers.fill");
    public static string FillHex => Get("layers.fill_hex");

    /// <summary>ブレンドの表示名(FR-49)。</summary>
    public static string BlendName(BlendMode b) => Get("layers.blend." + DocumentRules.BlendName(b));
}
