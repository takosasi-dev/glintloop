// probe-handoff が載せるデータ(先頭フレームの PNG・DIB・DIBV5 のバイト列)を作り、データオブジェクトを 1 か所で組み立てる。
// DIB と DIBV5 は仕様書 §9.10 の表のとおりに自作のコードで並べる。置く形式は FileDrop・PNG・DIB・DIBV5 の 4 つだけ(INV-10)。
// 値は FileDrop が StringCollection、ほかは MemoryStream(生のバイト列)。シリアライズを使う設定はしない。
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Probes.Handoff;

[Flags]
enum Mode { File = 1, Image = 2, Both = File | Image }

sealed class FirstFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>上の行から並べた straight の BGRA。</summary>
    public required byte[] Bgra { get; init; }
    public required byte[] Png { get; init; }
    public required byte[] Dib { get; init; }
    public required byte[] DibV5 { get; init; }
}

static class HandoffData
{
    /// <summary>DIBV5(CF_DIBV5 = 17)の WPF での形式名。GetDataFormat(17) が返す名前をそのまま使う(Q-16 の材料)。</summary>
    public static string DibV5FormatName => DataFormats.GetDataFormat(17).Name;

    /// <summary>GIF の先頭フレームを WPF のデコーダーで読み、PNG・DIB・DIBV5 を作る。</summary>
    public static FirstFrame FromGif(byte[] gif)
    {
        using var ms = new MemoryStream(gif);
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource f0 = dec.Frames[0];
        var bgra = new FormatConvertedBitmap(f0, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        return new FirstFrame
        {
            Width = w,
            Height = h,
            Bgra = px,
            Png = EncodePng(w, h, px),
            Dib = MakeDib24(w, h, px),
            DibV5 = MakeDibV5(w, h, px),
        };
    }

    static byte[] EncodePng(int w, int h, byte[] bgra)
    {
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static void U16(List<byte> o, int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
    static void U32(List<byte> o, uint v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24)); }
    static void I32(List<byte> o, int v) => U32(o, unchecked((uint)v));

    // 白の上に線形光で over 合成する: c' = enc(dec(c)×a + (1−a))、floor(255×c'+0.5)。a=1 は c のまま、a=0 は 255。
    static byte OverWhite(byte c, byte a)
    {
        if (a == 255) return c;
        if (a == 0) return 255;
        double af = a / 255.0;
        double v = Srgb.Enc(Srgb.Lin8(c) * af + (1 - af));
        return (byte)Math.Clamp(MathX.RoundHalfUpToInt(255 * v), 0, 255);
    }

    /// <summary>BITMAPINFOHEADER(40 バイト)+ 24 ビットの行(下の行から、4 バイト境界まで 0 で埋める)。</summary>
    public static byte[] MakeDib24(int w, int h, byte[] bgra)
    {
        int row = (w * 3 + 3) / 4 * 4;
        var o = new List<byte>(40 + row * h);
        U32(o, 40); I32(o, w); I32(o, h); U16(o, 1); U16(o, 24); U32(o, 0); U32(o, (uint)(row * h));
        I32(o, 0); I32(o, 0); U32(o, 0); U32(o, 0);
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte a = bgra[i + 3];
                o.Add(OverWhite(bgra[i], a)); o.Add(OverWhite(bgra[i + 1], a)); o.Add(OverWhite(bgra[i + 2], a));
            }
            for (int p = w * 3; p < row; p++) o.Add(0);
        }
        return o.ToArray();
    }

    /// <summary>BITMAPV5HEADER(124 バイト)+ 32 ビット BGRA の行(straight、下の行から)。</summary>
    public static byte[] MakeDibV5(int w, int h, byte[] bgra)
    {
        var o = new List<byte>(124 + w * 4 * h);
        U32(o, 124); I32(o, w); I32(o, h); U16(o, 1); U16(o, 32); U32(o, 3); U32(o, (uint)(w * 4 * h));
        I32(o, 0); I32(o, 0); U32(o, 0); U32(o, 0);
        U32(o, 0x00FF0000); U32(o, 0x0000FF00); U32(o, 0x000000FF); U32(o, 0xFF000000);
        U32(o, 0x73524742);                    // bV5CSType = LCS_sRGB
        for (int i = 0; i < 36; i++) o.Add(0); // bV5Endpoints
        U32(o, 0); U32(o, 0); U32(o, 0);       // bV5GammaRed/Green/Blue
        U32(o, 4);                             // bV5Intent = LCS_GM_IMAGES
        U32(o, 0); U32(o, 0); U32(o, 0);       // ProfileData・ProfileSize・Reserved
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                o.Add(bgra[i]); o.Add(bgra[i + 1]); o.Add(bgra[i + 2]); o.Add(bgra[i + 3]);
            }
        return o.ToArray();
    }

    /// <summary>
    /// データオブジェクトを作る、ただ 1 つの場所。setFormats には SetData に渡した形式名を順に入れて返す(INV-10 の自己点検用)。
    /// fileDropNoConvert が真のときだけ、比較用に FileDrop を SetData(FileDrop, string[], autoConvert: false) で置く
    /// (SetFileDropList は自動変換が有効で、FileNameW・FileName も見えるため。測定の比較用で、仕様の既定ではない)。
    /// </summary>
    public static DataObject Build(Mode mode, string? file, FirstFrame? frame, out List<string> setFormats, bool fileDropNoConvert = false)
    {
        var o = new DataObject();
        setFormats = new List<string>();
        if (mode.HasFlag(Mode.File))
        {
            if (file is null) throw new ArgumentNullException(nameof(file));
            if (fileDropNoConvert)
            {
                o.SetData(DataFormats.FileDrop, new[] { file }, false);
                setFormats.Add(DataFormats.FileDrop + "(SetData、string[]、autoConvert=false。比較用)");
            }
            else
            {
                var list = new StringCollection { file };
                o.SetFileDropList(list);
                setFormats.Add(DataFormats.FileDrop + "(SetFileDropList、StringCollection)");
            }
        }
        if (mode.HasFlag(Mode.Image))
        {
            if (frame is null) throw new ArgumentNullException(nameof(frame));
            o.SetData("PNG", new MemoryStream(frame.Png), false);
            setFormats.Add("PNG(MemoryStream)");
            o.SetData(DataFormats.Dib, new MemoryStream(frame.Dib), false);
            setFormats.Add(DataFormats.Dib + "(MemoryStream)");
            o.SetData(DibV5FormatName, new MemoryStream(frame.DibV5), false);
            setFormats.Add(DibV5FormatName + "(MemoryStream、CF_DIBV5)");
        }
        return o;
    }
}
