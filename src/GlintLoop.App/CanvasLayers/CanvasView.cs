// キャンバスの領域の中身(MainWindow の CanvasHost に入れる): 下から画像の層(ImageLayer)と重ね描きの層(OverlayLayer)を重ね、どちらも同じ CanvasTransform で置く。
// 画像の層には合成の表示(WriteableBitmap など)を、重ね描きの層にはエフェクトのハンドル・レイヤーの枠とハンドルを、各担当が自分のファイルで作った要素として足す。
// 拡大縮小・パン・背景・入力の処理はここに書かず、後の担当がこの部品のイベントや層を外から使う。
using System.Windows.Controls;

namespace GlintLoop.App.CanvasLayers;

public sealed class CanvasView : Grid
{
    public CanvasView(CanvasTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Transform = transform;
        ImageLayer = new CanvasLayer(transform);
        OverlayLayer = new CanvasLayer(transform);
        Children.Add(ImageLayer);
        Children.Add(OverlayLayer);
    }

    /// <summary>文書の画素 ↔ このキャンバスの DIP の変換(左上が原点)。</summary>
    public CanvasTransform Transform { get; }

    /// <summary>画像を出す層(下)。</summary>
    public CanvasLayer ImageLayer { get; }

    /// <summary>重ね描きの層(上)。ハンドルや枠の要素を足す。</summary>
    public CanvasLayer OverlayLayer { get; }
}
