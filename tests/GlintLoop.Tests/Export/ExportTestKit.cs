// 書き出しのテストの道具: メモリ上のフレームの列(偽の IExportFrames)、実行の道具(ExportRun)、コードで作る画像、バイト列の検索、設定の組み立て。
// 画像は Hash だけで作る擬似乱数と式で作り、ファイルは書かない(すべてメモリの上)。
// 名前空間は GlintLoop.Tests.Exports。
using System.IO;
using GlintLoop.Core.Export;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Tests.Exports;

/// <summary>メモリ上のフレームの列。GetFrame を呼んだ回数を数える。</summary>
internal sealed class ArrayFrames(int width, int height, IReadOnlyList<byte[]> frames) : IExportFrames
{
    private int _calls;

    public int Width => width;

    public int Height => height;

    public int FrameCount => frames.Count;

    public int Calls => Volatile.Read(ref _calls);

    public byte[] GetFrame(int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        return frames[index];
    }
}

internal static class ExportTestKit
{
    public static ExportRun Run(int parallelism = 2, TaskScheduler? scheduler = null) =>
        new(scheduler ?? TaskScheduler.Default, parallelism, CancellationToken.None);

    /// <summary>式 f(x, y, k) の RGBA のフレームを n 枚。</summary>
    public static List<byte[]> Frames(int w, int h, int n, Func<int, int, int, (int R, int G, int B, int A)> f)
    {
        var list = new List<byte[]>(n);
        for (var k = 0; k < n; k++)
        {
            var px = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b, a) = f(x, y, k);
                var o = (y * w + x) * 4;
                px[o] = (byte)r;
                px[o + 1] = (byte)g;
                px[o + 2] = (byte)b;
                px[o + 3] = (byte)a;
            }
            list.Add(px);
        }
        return list;
    }

    /// <summary>擬似乱数の色に半透明と透明の画素を混ぜたフレーム(圧縮が効きにくい)。</summary>
    public static List<byte[]> Noisy(int w, int h, int n) => Frames(w, h, n, (x, y, k) =>
    {
        var v = Hash.H(0xE0u + (uint)k, (uint)(y * w + x));
        var a = (x + y + k) % 7 == 0 ? 0 : (x * 3 + y) % 5 == 0 ? 90 : 255;
        return ((int)(v & 255), (int)((v >> 8) & 255), (int)((v >> 16) & 255), a);
    });

    /// <summary>なめらかに動く不透明のフレーム。</summary>
    public static List<byte[]> Smooth(int w, int h, int n) => Frames(w, h, n, (x, y, k) =>
        ((x * 255 / Math.Max(1, w - 1) + k * 9) % 256, y * 255 / Math.Max(1, h - 1), (x + y + k * 5) % 256, 255));

    public static ExportSettings Settings(string format, int w, int h, int frames = 1, int fps = 20) => new()
    {
        Format = format,
        Width = w,
        Height = h,
        LongSide = Math.Max(w, h),
        FrameCount = frames,
        TimelineFrames = frames,
        Fps = fps,
        Length10 = Math.Max(1, frames * 10 / fps),
    };

    public static byte[] Encode(IExportEncoder enc, IExportFrames frames, ExportSettings s, long? limit = null, ExportRun? run = null)
    {
        using var ms = new MemoryStream();
        var r = enc.Encode(frames, s, ms, limit, run ?? Run());
        if (r.Overflowed) throw new InvalidOperationException("打ち切られた");
        if (r.Bytes != ms.Length) throw new InvalidOperationException($"Bytes {r.Bytes} が書いた長さ {ms.Length} と違う");
        return ms.ToArray();
    }

    /// <summary>ASCII の語がバイト列に含まれるか(大文字と小文字を区別する)。</summary>
    public static bool Contains(byte[] data, string term)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(term);
        return data.AsSpan().IndexOf(t) >= 0;
    }

    /// <summary>フレーム k の遅延(テストの側で §9.8 の 7 の式を書いたもの)。</summary>
    public static int[] Delays(int n, int fps, long holdMs)
    {
        static long R(long a, long b) => (long)Math.Floor((double)a / b + 0.5);
        var d = new int[n];
        if (n == 1)
        {
            d[0] = 100;
            return d;
        }
        for (var k = 0; k < n; k++) d[k] = (int)Math.Max(2, R(100L * (k + 1), fps) - R(100L * k, fps));
        d[^1] = (int)Math.Min(65535, d[^1] + (long)Math.Floor(holdMs / 10.0 + 0.5));
        return d;
    }
}
