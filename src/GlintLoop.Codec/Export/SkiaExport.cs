// JPEG・WebP の書き出し(Codec)が共有する、straight RGBA の画素から SkiaSharp の SKBitmap を作る道具。
// 色空間を付けない(null)ので、エンコーダーは ICC プロファイル(JPEG の APP2・WebP の ICCP)を書かない(INV-12)。
// 符号化は SKData に作ってから上限の数えのあるストリームへ書く(Skia に渡すストリームの中で例外を投げないため)。
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GlintLoop.Codec.Export;

internal static class SkiaExport
{
    /// <summary>行を詰めた RGBA(幅×高さ×4)から、色空間なしの SKBitmap を作る。</summary>
    internal static SKBitmap Bitmap(int width, int height, byte[] rgba, SKAlphaType alpha)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, alpha);
        var bmp = new SKBitmap(info);
        var p = bmp.GetPixels();
        for (var y = 0; y < height; y++) Marshal.Copy(rgba, y * width * 4, p + y * bmp.RowBytes, width * 4);
        return bmp;
    }
}
