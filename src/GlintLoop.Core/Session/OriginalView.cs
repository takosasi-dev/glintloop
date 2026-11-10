// 加工前の合成(FR-17 の「元画像」と FR-18 の分割比較の左側)の文書を作る。すべてのレイヤーのスタックと全体のスタックを空にする。
// 配置・不透明度・ブレンド・表示のオンオフ・選択・出力設定は今のまま。土台の回転・切り抜き・余白も外れるので、合成の大きさは土台のソースの大きさに戻る。
// 元の文書は変えない(履歴にも積まない)。
namespace GlintLoop.Core.Session;

public static class OriginalView
{
    /// <summary>doc のすべてのスタックを空にした文書を返す。</summary>
    public static SessionDocument Of(SessionDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var layers = new Layer[doc.Layers.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            var l = doc.Layers[i];
            layers[i] = l.Stack.Count == 0 ? l : l with { Stack = Array.Empty<StackEntry>() };
        }
        return doc with
        {
            Layers = layers,
            All = doc.All.Stack.Count == 0 ? doc.All : doc.All with { Stack = Array.Empty<StackEntry>() },
        };
    }
}
