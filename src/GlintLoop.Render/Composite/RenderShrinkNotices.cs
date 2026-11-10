// 描画の上限で縮めたレイヤーの通知(§9.7 の規則 11、E-19): layer.render_shrunk(info)を、レイヤーごとに同じ文書では最初の 1 回だけ出す。
// 文書のレイヤーは番号のほかに同一性を持たないので、仮にレイヤーの番号で数える。新しい文書を始めたら Reset を呼ぶ。
// 描画の結果(DocumentImage.ShrunkLayers)を受け取るだけで GPU を使わない。表示と書き出しの経路で 1 つを共有する(AppShell.ShrinkNotices)。どのスレッドからも使えるが、同時には呼ばない。
using GlintLoop.Core.Notices;

namespace GlintLoop.Render.Composite;

public sealed class RenderShrinkNotices
{
    private readonly HashSet<int> _notified = new();

    /// <summary>新しい文書を始めたとき(FR-43 の新規・開く・取り込みで文書を作ったとき)に呼ぶ。</summary>
    public void Reset() => _notified.Clear();

    /// <summary>描画の結果を受け取り、まだ出していない縮めたレイヤーの 1 枚ごとに 1 件の通知を返す(無ければ空)。</summary>
    public IReadOnlyList<Notice> Observe(DocumentImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Observe(image.ShrunkLayers);
    }

    /// <summary>縮めたレイヤーの番号を受け取り、まだ出していないものの 1 枚ごとに 1 件の通知を返す(書き出しの完了が渡す)。</summary>
    public IReadOnlyList<Notice> Observe(IReadOnlyList<int> shrunkLayers)
    {
        ArgumentNullException.ThrowIfNull(shrunkLayers);
        var list = new List<Notice>();
        foreach (var index in shrunkLayers)
        {
            if (_notified.Add(index)) list.Add(Notice.Of(NoticeKeys.LayerRenderShrunk, NoticeLevel.Info));
        }
        return list;
    }
}
