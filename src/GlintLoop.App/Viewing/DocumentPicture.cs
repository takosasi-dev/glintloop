// キャンバスの画像の層に置く 1 枚の合成の表示: 下にキャンバスの背景(FR-20。合成の矩形の中だけ)、上に描画の結果の WriteableBitmap(D-38)。
// 置く矩形は CanvasLayer.SetDocumentRect で外から決める(文書の画素)。画素は Pbgra32 の DocumentImage を UI スレッドで書き写す。同じ大きさなら同じ WriteableBitmap を使い回す。
// ポインターは受けない(パンとフォーカスは下の CanvasSurface が受ける)。分割比較のときは Clip で縦線の片側だけを見せる。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Viewing;

public sealed class DocumentPicture : Grid
{
    private WriteableBitmap? _bitmap;

    public DocumentPicture()
    {
        IsHitTestVisible = false;
        Background = null;
        Backdrop = new Border();
        Image = new Image { Stretch = Stretch.Fill };
        Children.Add(Backdrop);
        Children.Add(Image);
    }

    /// <summary>キャンバスの背景(合成の矩形を塗る)。</summary>
    public Border Backdrop { get; }

    public Image Image { get; }

    /// <summary>今の WriteableBitmap(無ければ null)。</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    /// <summary>描画の結果を載せる(UI スレッドで呼ぶ)。Pbgra32 でないときは例外。</summary>
    public void SetImage(DocumentImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Format != DocumentPixelFormat.Pbgra32) throw new ArgumentException("表示は Pbgra32 で描く", nameof(image));
        if (_bitmap is null || _bitmap.PixelWidth != image.Width || _bitmap.PixelHeight != image.Height)
        {
            _bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null);
            Image.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), image.Pixels, image.Width * 4, 0);
    }

    /// <summary>画像を外す(文書を閉じたとき)。</summary>
    public void ClearImage()
    {
        _bitmap = null;
        Image.Source = null;
    }

    /// <summary>WriteableBitmap の画素(Pbgra32。テスト用)。</summary>
    public byte[]? CopyPixels()
    {
        if (_bitmap is null) return null;
        var px = new byte[_bitmap.PixelWidth * _bitmap.PixelHeight * 4];
        _bitmap.CopyPixels(px, _bitmap.PixelWidth * 4, 0);
        return px;
    }
}
