// GIF の検査(AC-4・AC-5)で共有する道具。擬似乱数の画像(Hash だけで作る)、数を数えるフレームの供給元、
// WPF の GifBitmapDecoder でフレームと位置・disposal を読む関数、Oklab の全探索(テスト側の別実装)を置く。
// ファイルは書かない(すべてメモリ上で扱う)。
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Numerics;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Gif;

internal static class GifTestKit
{
    /// <summary>擬似乱数(0〜2^32−1)。乱数は Hash だけで作る(INV-8)。</summary>
    public static uint Rand(uint stream, uint i) => Hash.H(stream, i);

    /// <summary>不透明な単色のフレーム。</summary>
    public static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            px[p * 4] = r;
            px[p * 4 + 1] = g;
            px[p * 4 + 2] = b;
            px[p * 4 + 3] = a;
        }
        return px;
    }

    /// <summary>擬似乱数の色の不透明なフレーム(色の種類は colors 通りまで)。</summary>
    public static byte[] RandomOpaque(int w, int h, uint seed, int colors = 0)
    {
        var px = new byte[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            var v = Rand(seed, (uint)p);
            if (colors > 0)
            {
                var c = v % (uint)colors;
                v = Rand(seed ^ 0xC0105u, c);
            }
            px[p * 4] = (byte)v;
            px[p * 4 + 1] = (byte)(v >> 8);
            px[p * 4 + 2] = (byte)(v >> 16);
            px[p * 4 + 3] = 255;
        }
        return px;
    }

    /// <summary>なめらかな色の変化に少しの揺らぎを足したフレーム(写真に近い分布)。k でずらす。</summary>
    public static byte[] Gradient(int w, int h, int k, uint seed)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = y * w + x;
                var n = (int)(Rand(seed, (uint)p) % 9) - 4;
                px[p * 4] = Clamp8(((x + 3 * k) * 255 / Math.Max(1, w - 1)) % 256 + n);
                px[p * 4 + 1] = Clamp8(y * 255 / Math.Max(1, h - 1) + n);
                px[p * 4 + 2] = Clamp8(128 + (int)(100 * Math.Sin((x + y + 5 * k) * 0.07)) + n);
                px[p * 4 + 3] = 255;
            }
        }
        return px;
    }

    public static byte Clamp8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>丸の外の α を 0 にする(透明を含むアニメ)。半径の外側 1 画素は α=100 の縁。</summary>
    public static byte[] WithTransparentOutsideCircle(byte[] px, int w, int h, double cx, double cy, double radius)
    {
        var o = (byte[])px.Clone();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                var a = d <= radius ? 255 : d <= radius + 1 ? 100 : 0;
                o[(y * w + x) * 4 + 3] = (byte)a;
            }
        }
        return o;
    }

    public static Lab LabOf(byte r, byte g, byte b) => Oklab.ToOk(Srgb.Lin8(r), Srgb.Lin8(g), Srgb.Lin8(b));

    /// <summary>全探索(テスト側の別実装)。二乗距離が最小、同じなら番号の小さい方。exclude は除く番号。</summary>
    public static int BruteNearest(Lab p, IReadOnlyList<Lab> palette, int exclude = -1)
    {
        var best = -1;
        var bestD = 0.0;
        for (var i = 0; i < palette.Count; i++)
        {
            if (i == exclude) continue;
            var c = palette[i];
            var d = (p.L - c.L) * (p.L - c.L) + (p.A - c.A) * (p.A - c.A) + (p.B - c.B) * (p.B - c.B);
            if (best < 0 || d < bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>ファイルのグローバルカラーテーブルの先頭 count 色の Oklab。</summary>
    public static List<Lab> TableLabs(MiniGifFile f, int count)
    {
        var list = new List<Lab>();
        for (var i = 0; i < count; i++)
        {
            var (r, g, b) = f.TableColor(i);
            list.Add(LabOf(r, g, b));
        }
        return list;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    // ---- WPF の GifBitmapDecoder ----

    public sealed record WpfFrame(int Left, int Top, int Width, int Height, int Disposal, int Delay, byte[] Bgra);

    /// <summary>GifBitmapDecoder で各フレームを読む(合成はしない)。画素は Bgra32 に変換する(透明の番号は α=0 になる)。</summary>
    public static List<WpfFrame> ReadWithWpf(byte[] gif)
    {
        using var ms = new MemoryStream(gif);
        var dec = new GifBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var list = new List<WpfFrame>();
        foreach (var fr in dec.Frames)
        {
            var md = (BitmapMetadata)fr.Metadata;
            var left = Convert.ToInt32(md.GetQuery("/imgdesc/Left"));
            var top = Convert.ToInt32(md.GetQuery("/imgdesc/Top"));
            var disposal = Convert.ToInt32(md.GetQuery("/grctlext/Disposal"));
            var delay = Convert.ToInt32(md.GetQuery("/grctlext/Delay"));
            var conv = new FormatConvertedBitmap(fr, PixelFormats.Bgra32, null, 0);
            var buf = new byte[fr.PixelWidth * fr.PixelHeight * 4];
            conv.CopyPixels(buf, fr.PixelWidth * 4, 0);
            list.Add(new WpfFrame(left, top, fr.PixelWidth, fr.PixelHeight, disposal, delay, buf));
        }
        return list;
    }

    /// <summary>
    /// WPF で読んだフレームを、disposal に従って論理スクリーンの上に重ねる(GifBitmapDecoder は合成をしないため)。
    /// 返すのは各フレームを表示したときの画面(Bgra32)。最初の画面は全面が透明。
    /// </summary>
    public static List<byte[]> Composite(List<WpfFrame> frames, int w, int h)
    {
        var canvas = new byte[w * h * 4];
        var shown = new List<byte[]>();
        foreach (var f in frames)
        {
            var before = (byte[])canvas.Clone();
            for (var y = 0; y < f.Height; y++)
            {
                for (var x = 0; x < f.Width; x++)
                {
                    var s = (y * f.Width + x) * 4;
                    if (f.Bgra[s + 3] == 0) continue; // 透明の番号は前の画面を残す
                    var d = ((f.Top + y) * w + f.Left + x) * 4;
                    Buffer.BlockCopy(f.Bgra, s, canvas, d, 4);
                }
            }
            shown.Add((byte[])canvas.Clone());
            if (f.Disposal == 2)
            {
                for (var y = 0; y < f.Height; y++)
                    Array.Clear(canvas, ((f.Top + y) * w + f.Left) * 4, f.Width * 4);
            }
            else if (f.Disposal == 3)
            {
                canvas = before;
            }
        }
        return shown;
    }

    /// <summary>straight RGBA と Bgra32 の画面が、不透明の画素の色と透明の位置で一致するか。</summary>
    public static void CheckSameOpaque(byte[] rgba, byte[] bgra, string what)
    {
        Check.Equal(rgba.Length, bgra.Length, what + " の長さ");
        for (var p = 0; p < rgba.Length / 4; p++)
        {
            var o = p * 4;
            if (bgra[o + 2] != rgba[o] || bgra[o + 1] != rgba[o + 1] || bgra[o] != rgba[o + 2] || bgra[o + 3] != 255)
                throw new CheckFailedException(
                    $"{what}: 画素 {p} 期待 RGB=({rgba[o]},{rgba[o + 1]},{rgba[o + 2]}) 実際 BGRA=({bgra[o]},{bgra[o + 1]},{bgra[o + 2]},{bgra[o + 3]})");
        }
    }
}

/// <summary>GetFrame と ReleaseFrame の回数と、同時に手元にあるフレームの最大数を数える供給元。</summary>
internal sealed class CountingFrameSource(int width, int height, Func<int, byte[]> make, int frameCount) : IGifFrameSource
{
    private readonly object _lock = new();
    private int _outstanding;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int FrameCount { get; } = frameCount;

    public int Gets;
    public int Releases;
    public int MaxOutstanding;
    public readonly List<(GifPass Pass, int Index)> Order = [];

    public byte[] GetFrame(GifPass pass, int index)
    {
        lock (_lock)
        {
            Gets++;
            _outstanding++;
            MaxOutstanding = Math.Max(MaxOutstanding, _outstanding);
            Order.Add((pass, index));
        }
        return make(index);
    }

    public void ReleaseFrame(GifPass pass, int index, byte[] pixels)
    {
        lock (_lock)
        {
            Releases++;
            _outstanding--;
        }
    }
}
