// データオブジェクトを作る、ただ 1 つの場所(INV-10・§9.10 の 2): SetData に渡す形式は FileDrop(SetFileDropList)・PNG・DIB・DIBV5 の 4 つだけで、値は StringCollection と MemoryStream に限る。
// 画像の 3 形式は、キャッシュの静止画の PNG から作る(PNG はバイト列のまま、DIB と DIBV5 は SkiaSharp でデコードした straight の値から自作のコードで並べる)。
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using GlintLoop.Codec.Preview;
using GlintLoop.Core.Export;

namespace GlintLoop.App.Export;

/// <summary>載せる画像の 3 形式のバイト列。</summary>
public sealed record HandoffImage(byte[] Png, byte[] Dib, byte[] DibV5)
{
    /// <summary>キャッシュの静止画の PNG から作る(UI スレッドの外で呼ぶ)。読めないときは OutputDecodeException。</summary>
    public static HandoffImage FromPng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        var d = OutputFrameDecoder.Decode(png, ExportFormat.Png);
        var bgra = d.Frames[0].Bgra;
        return new HandoffImage(png, ClipboardImage.Dib(d.Width, d.Height, bgra), ClipboardImage.DibV5(d.Width, d.Height, bgra));
    }
}

public static class HandoffData
{
    /// <summary>file があれば FileDrop(実体 1 個)を、image があれば PNG・DIB・DIBV5 を載せる。</summary>
    public static DataObject Build(string? file, HandoffImage? image)
    {
        var data = new DataObject();
        if (file is not null) data.SetFileDropList(new StringCollection { file });
        if (image is not null)
        {
            data.SetData("PNG", new MemoryStream(image.Png), false);
            data.SetData(DataFormats.Dib, new MemoryStream(image.Dib), false);
            data.SetData("Format17", new MemoryStream(image.DibV5), false);
        }
        return data;
    }
}
