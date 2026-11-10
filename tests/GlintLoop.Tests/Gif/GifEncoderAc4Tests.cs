// 自作の GifEncoder の検査(仕様書 AC-4 の (1)〜(6))。出力を、別実装の最小のデコーダー(MiniGifDecoder)と WPF の GifBitmapDecoder で読む。
// (2) の LZW は、エンコーダーの内部の関数に番号の列を直接渡し、別実装のデコーダーで戻した列と比べる。
// 4096×4096 の LZW の検査も 1 秒かからないので、毎回走らせる。
using GlintLoop.Core.Gif;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Gif.GifTestKit;

namespace GlintLoop.Tests.Gif;

public sealed class GifEncoderAc4Tests
{
    private static readonly byte[] Netscape =
    [
        0x21, 0xFF, 0x0B, 0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30, 0x03, 0x01, 0x00, 0x00, 0x00,
    ];

    private static int Log2(int m)
    {
        var r = 0;
        while ((1 << r) < m) r++;
        return r;
    }

    private static int MinPow2(int needed)
    {
        var m = 2;
        while (m < needed) m <<= 1;
        return m;
    }

    /// <summary>規則 8 の構造を、デコーダーで読んだ結果について確かめる。</summary>
    private static void CheckStructure(byte[] gif, MiniGifFile f, int w, int h, int n, GifEncoderOptions o,
        int colorCount, int tIndex, int disposal, string what)
    {
        Check.Equal("GIF89a", f.Signature, what + " ヘッダー");
        Check.Equal(w, f.Width, what + " 幅");
        Check.Equal(h, f.Height, what + " 高さ");
        var m = MinPow2(colorCount + (tIndex >= 0 ? 1 : 0));
        Check.Equal(m, f.GlobalTableEntries, what + " M");
        Check.Equal((byte)(0xF0 | (Log2(m) - 1)), f.Packed, what + " パックフィールド");
        Check.Equal((byte)0, f.BackgroundIndex, what + " 背景色の番号");
        Check.Equal((byte)0, f.AspectRatio, what + " 縦横比");
        // 使わない枠は黒
        for (var i = colorCount; i < m; i++) Check.Equal(((byte)0, (byte)0, (byte)0), f.TableColor(i), $"{what} 使わない枠 {i}");

        var loop = o.Loop == GifLoop.Infinite && n >= 2;
        if (loop)
        {
            Check.Equal(1, f.AppExtensions.Count, what + " アプリケーション拡張の数");
            Check.SequenceEqual(Netscape, f.AppExtensions[0], what + " NETSCAPE2.0");
            Check.Equal(13 + 3 * m, f.FirstAppExtensionOffset, what + " NETSCAPE2.0 の位置(カラーテーブルの直後)");
            Check.True(f.FirstAppExtensionOffset < f.FirstFrameOffset, what + " NETSCAPE2.0 が最初のフレームの前");
        }
        else
        {
            Check.Equal(0, f.AppExtensions.Count, what + " アプリケーション拡張が無い");
        }

        Check.Equal(n, f.Frames.Count, what + " フレーム数");
        var delays = GifEncoder.FrameDelays(n, o.Fps, o.FinalHoldMs);
        var mcs = Math.Max(2, Log2(m));
        for (var k = 0; k < n; k++)
        {
            var fr = f.Frames[k];
            var hasT = tIndex >= 0;
            var pk = (byte)((disposal << 2) | (hasT ? 1 : 0));
            Check.SequenceEqual(
                new byte[] { 0x21, 0xF9, 0x04, pk, (byte)(delays[k] & 0xFF), (byte)(delays[k] >> 8), (byte)(hasT ? tIndex : 0), 0x00 },
                fr.ControlRaw, $"{what} フレーム {k} のグラフィック制御拡張");
            Check.Equal(delays[k], fr.Delay, $"{what} フレーム {k} の遅延");
            Check.Equal(disposal, fr.Disposal, $"{what} フレーム {k} の disposal");
            Check.Equal(hasT, fr.TransparentFlag, $"{what} フレーム {k} の透明フラグ");
            Check.Equal(hasT ? tIndex : 0, fr.TransparentIndexField, $"{what} フレーム {k} の透明番号");
            Check.Equal((byte)0, fr.DescriptorPacked, $"{what} フレーム {k} の画像記述子のパック");
            Check.Equal(mcs, fr.MinCodeSize, $"{what} フレーム {k} の最小コードサイズ");
            Check.True(fr.MaxDataBlockLength <= 255, $"{what} フレーム {k} の塊が 255 バイト以下");
            Check.True(fr.PaddingIsZero && fr.BytesAfterEnd == 0, $"{what} フレーム {k} の終了コードのあとの詰め物");
            Check.True(fr.Left + fr.Width <= w && fr.Top + fr.Height <= h, $"{what} フレーム {k} の矩形が画面の内側");
            if (k == 0 || !(tIndex >= 0 && disposal == 1))
            {
                Check.Equal((0, 0, w, h), (fr.Left, fr.Top, fr.Width, fr.Height), $"{what} フレーム {k} は全面");
            }
            foreach (var v in fr.Indices) Check.True(v < m, $"{what} フレーム {k} の番号 {v} が M 未満");
        }
        Check.True(f.EndsWithTrailer && gif[^1] == 0x3B, what + " 末尾が 3B");
    }

    [Test]
    public void AC4_1_構造_不透明で差分あり_無限ループ()
    {
        const int w = 40, h = 30, n = 5;
        var frames = Enumerable.Range(0, n).Select(k => Gradient(w, h, k, 11)).ToList();
        var o = new GifEncoderOptions { Colors = 64, Fps = 12, Parallelism = 3 };
        var src = new GifFrameList(w, h, frames);
        var plan = GifEncoder.Analyze(src, o, default);
        var gif = GifEncoder.EncodeToArray(src, o);
        var f = MiniGifDecoder.Parse(gif);
        Check.True(plan.UseDiff && plan.TransparentIndex == plan.Palette.Count, "差分ありで T は色の直後");
        Check.True(plan.Palette.Count <= 63, "色の数は n−1 以下");
        CheckStructure(gif, f, w, h, n, o, plan.Palette.Count, plan.TransparentIndex, 1, "不透明・差分あり");
    }

    [Test]
    public void AC4_1_構造_不透明で差分なし_1回()
    {
        const int w = 33, h = 17, n = 4;
        var frames = Enumerable.Range(0, n).Select(k => Gradient(w, h, k, 12)).ToList();
        var o = new GifEncoderOptions { Colors = 200, DiffOptimization = false, Loop = GifLoop.Once, Fps = 24 };
        var src = new GifFrameList(w, h, frames);
        var plan = GifEncoder.Analyze(src, o, default);
        var gif = GifEncoder.EncodeToArray(src, o);
        var f = MiniGifDecoder.Parse(gif);
        Check.Equal(-1, plan.TransparentIndex, "差分なしの不透明は T を置かない");
        CheckStructure(gif, f, w, h, n, o, plan.Palette.Count, -1, 1, "不透明・差分なし・1 回");
    }

    [Test]
    public void AC4_1_構造_透明を含むアニメ()
    {
        const int w = 48, h = 48, n = 6;
        var frames = Enumerable.Range(0, n)
            .Select(k => WithTransparentOutsideCircle(Gradient(w, h, k, 13), w, h, 10 + 5 * k, 24, 9)).ToList();
        var o = new GifEncoderOptions { Colors = 16, Fps = 8 };
        var src = new GifFrameList(w, h, frames);
        var plan = GifEncoder.Analyze(src, o, default);
        var gif = GifEncoder.EncodeToArray(src, o);
        var f = MiniGifDecoder.Parse(gif);
        Check.True(!plan.UseDiff && plan.HasAlphaTransparency, "透明を含むときは差分を使わない");
        Check.Equal(plan.Palette.Count, plan.TransparentIndex, "T は色の直後");
        CheckStructure(gif, f, w, h, n, o, plan.Palette.Count, plan.TransparentIndex, 2, "透明を含む");
    }

    [Test]
    public void AC4_1_構造_1フレームは遅延100で拡張なし()
    {
        const int w = 20, h = 10;
        var o = new GifEncoderOptions { Colors = 8 };
        var src = new GifFrameList(w, h, [RandomOpaque(w, h, 14, 5)]);
        var plan = GifEncoder.Analyze(src, o, default);
        var gif = GifEncoder.EncodeToArray(src, o);
        var f = MiniGifDecoder.Parse(gif);
        Check.Equal(5, plan.Palette.Count, "5 色の画像は 5 色");
        Check.Equal(-1, plan.TransparentIndex, "1 フレームの不透明は T を置かない");
        CheckStructure(gif, f, w, h, 1, o, 5, -1, 1, "1 フレーム");
        Check.Equal(100, f.Frames[0].Delay, "1 フレームの遅延");
        Check.Equal(8, f.GlobalTableEntries, "5 色なら M=8");
    }

    [Test]
    public void AC4_1_構造_Mは最小の2のべき乗()
    {
        // 色の種類が k 個の不透明な 1 フレームで、M が k 以上の最小の 2 のべき乗(2 以上)。
        // 色は、Oklab のセル(§9.8 の 3 の 1)が互いに異なるものを選ぶ(同じセルの色は 1 色にまとまるため)。
        var distinct = new List<(byte R, byte G, byte B)>();
        var cells = new HashSet<int>();
        for (var v = 0; distinct.Count < 256; v++)
        {
            var c = ((byte)(v % 7 * 42), (byte)(v / 7 % 7 * 42), (byte)(v / 49 % 7 * 42));
            if (cells.Add(GifHistogram.CellOf(LabOf(c.Item1, c.Item2, c.Item3)))) distinct.Add(c);
        }
        foreach (var k in new[] { 1, 2, 3, 4, 5, 8, 9, 16, 17, 100, 129, 256 })
        {
            const int w = 64, h = 64;
            var px = new byte[w * h * 4];
            for (var p = 0; p < w * h; p++)
            {
                var (r, g, b) = distinct[p % k];
                px[p * 4] = r;
                px[p * 4 + 1] = g;
                px[p * 4 + 2] = b;
                px[p * 4 + 3] = 255;
            }
            var o = new GifEncoderOptions { Colors = 256, Dither = GifDither.None };
            var src = new GifFrameList(w, h, [px]);
            var plan = GifEncoder.Analyze(src, o, default);
            Check.Equal(k, plan.Palette.Count, $"{k} 色の画像のパレットの色数");
            var gif = GifEncoder.EncodeToArray(src, o);
            var f = MiniGifDecoder.Parse(gif);
            Check.Equal(MinPow2(k), f.GlobalTableEntries, $"{k} 色の M");
            CheckStructure(gif, f, w, h, 1, o, k, -1, 1, $"{k} 色");
        }
    }

    // ---- (2) LZW ----

    private static (byte[] Indices, int MidClears) RoundTrip(byte[] indices, int mcs)
    {
        var data = GifLzw.EncodeImageData(indices, mcs);
        Check.Equal((byte)mcs, data[0], "最小コードサイズのバイト");
        // 塊をつなぐ
        var raw = new List<byte>();
        var pos = 1;
        while (true)
        {
            var len = data[pos++];
            if (len == 0) break;
            Check.True(len <= 255, "塊の長さ");
            for (var i = 0; i < len; i++) raw.Add(data[pos + i]);
            pos += len;
        }
        Check.Equal(data.Length, pos, "終端の 00 のあとに余りが無い");
        var r = MiniLzw.Decode(raw.ToArray(), mcs, indices.Length);
        Check.True(r.PaddingIsZero && r.BytesAfterEnd == 0, "終了コードのあとの詰め物");
        Check.True(r.ClearCodes == r.MidClearCodes + 1, "先頭にクリアコードが 1 つ");
        CheckClearAt4095(r.NextAtMidClears, $"{indices.Length} 画素・最小コードサイズ {mcs}");
        return (r.Pixels, r.MidClearCodes);
    }

    /// <summary>
    /// 規則 A の (b): free≥4095 でクリアし、項目 4095 を作らない。デコーダーは項目を符号 1 つ分遅れて足すので、
    /// エンコーダーが最後に作る項目が 4094 なら、途中のクリアコードを読んだ時点のデコーダーの next はちょうど 4095 になる
    /// (閾値が 4096 なら 4096、4095 より小さければそれより小さい)。
    /// </summary>
    private static void CheckClearAt4095(IReadOnlyList<int> nextAtMidClears, string what)
    {
        for (var i = 0; i < nextAtMidClears.Count; i++)
            Check.Equal(4095, nextAtMidClears[i], $"{what}: {i + 1} 個目の途中のクリアコードを読んだ時点の next");
    }

    private static int CheckRoundTrip(byte[] indices, int mcs, string what)
    {
        var (back, mid) = RoundTrip(indices, mcs);
        Check.SequenceEqual(indices, back, what + " の往復");
        return mid;
    }

    [Test]
    public void AC4_2_LZW_同じ番号の1x1から1024x1024()
    {
        foreach (var side in new[] { 1, 16, 256, 1024 })
        {
            var mid = CheckRoundTrip(new byte[side * side], 8, $"0 の {side}×{side}");
            Check.Equal(0, mid, $"0 の {side}×{side} の途中のクリアコード");
        }
    }

    [Test]
    public void AC4_2_LZW_同じ番号の4096x4096は途中のクリアが2個()
    {
        var mid = CheckRoundTrip(new byte[4096 * 4096], 8, "0 の 4096×4096");
        Check.Equal(2, mid, "0 の 4096×4096 の途中のクリアコード");
    }

    [Test]
    public void AC4_2_LZW_市松模様と交互の列()
    {
        const int s = 300;
        var checker = new byte[s * s];
        for (var y = 0; y < s; y++)
            for (var x = 0; x < s; x++)
                checker[y * s + x] = (byte)((x + y) & 1);
        CheckRoundTrip(checker, 2, "2 色の市松模様");

        var alt = new byte[4096];
        for (var i = 0; i < alt.Length; i++) alt[i] = (byte)(i & 1);
        CheckRoundTrip(alt, 2, "0 と 1 が交互の 4096×1");
    }

    [Test]
    public void AC4_2_LZW_擬似乱数の列_4色16色256色()
    {
        foreach (var (colors, mcs) in new[] { (4, 2), (16, 4), (256, 8) })
        {
            const int count = 120_000;
            var data = new byte[count];
            for (var i = 0; i < count; i++) data[i] = (byte)(Rand(0xA11CEu + (uint)colors, (uint)i) % (uint)colors);
            var mid = CheckRoundTrip(data, mcs, $"{colors} 色の擬似乱数");
            Console.WriteLine($"  {colors} 色({count} 画素、最小コードサイズ {mcs}): 途中のクリアコード {mid} 個");
            Check.True(mid >= 1, $"{colors} 色の擬似乱数で途中のクリアコードが 1 個以上");
        }
    }

    [Test]
    public void AC4_2_LZW_256色の乱数の4096x4096()
    {
        var data = new byte[4096 * 4096];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)Rand(0x256u, (uint)i);
        var mid = CheckRoundTrip(data, 8, "256 色の乱数の 4096×4096");
        Console.WriteLine($"  途中のクリアコード {mid} 個");
        Check.True(mid >= 1, "途中のクリアコードが 1 個以上");
    }

    [Test]
    public void AC4_2_LZW_GIFの全フレームを別実装で戻せる()
    {
        // 符号化したファイルの各フレームの番号が、エンコーダーの内部で作った番号(ディザの結果)と一致する。
        const int w = 70, h = 50, n = 4;
        var frames = Enumerable.Range(0, n).Select(k => Gradient(w, h, k, 15)).ToList();
        var o = new GifEncoderOptions { Colors = 256, DiffOptimization = false };
        var src = new GifFrameList(w, h, frames);
        var plan = GifEncoder.Analyze(src, o, default);
        var nearest = new GifNearest(plan.Palette.Colors);
        var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(src, o));
        for (var k = 0; k < n; k++)
        {
            var expect = GifDitherer.MapFrame(frames[k], w, h, nearest, o.Dither, GifPixel.Matte.None, plan.TransparentIndex);
            Check.SequenceEqual(expect, f.Frames[k].Indices, $"フレーム {k} の番号");
        }
    }

    // ---- (3) 遅延 ----

    [Test]
    public void AC4_3_遅延の先頭の値()
    {
        Check.SequenceEqual(new[] { 13, 12, 13, 12 }, GifEncoder.FrameDelays(16, 8, 0).Take(4).ToArray(), "F=8");
        Check.SequenceEqual(new[] { 8, 9, 8, 8, 9, 8 }, GifEncoder.FrameDelays(24, 12, 0).Take(6).ToArray(), "F=12");
        Check.SequenceEqual(new[] { 4, 4, 5, 4, 4, 4 }, GifEncoder.FrameDelays(48, 24, 0).Take(6).ToArray(), "F=24");
        Check.SequenceEqual(new[] { 3, 2, 3, 2 }, GifEncoder.FrameDelays(80, 40, 0).Take(4).ToArray(), "F=40");
    }

    [Test]
    public void AC4_3_遅延の合計はL1で100()
    {
        foreach (var fps in new[] { 5, 8, 10, 12, 15, 20, 24, 25, 30, 40, 50 })
        {
            var d = GifEncoder.FrameDelays(fps, fps, 0);
            Check.Equal(100, d.Sum(), $"F={fps}、N={fps} の遅延の合計");
            Check.True(d.All(v => v >= 2), $"F={fps} の遅延が 2 以上");
        }
    }

    [Test]
    public void AC4_3_最後のフレームの停止時間()
    {
        var d0 = GifEncoder.FrameDelays(8, 8, 0);
        var d1 = GifEncoder.FrameDelays(8, 8, 1500);
        Check.SequenceEqual(d0.Take(7).ToArray(), d1.Take(7).ToArray(), "最後以外は変わらない");
        Check.Equal(d0[^1] + 150, d1[^1], "1500 ms は最後の遅延に 150 を足す");
        Check.Equal(d0[^1] + 1, GifEncoder.FrameDelays(8, 8, 5)[^1], "5 ms は round(0.5)=1");
        Check.Equal(d0[^1], GifEncoder.FrameDelays(8, 8, 4)[^1], "4 ms は round(0.4)=0");
        Check.Equal(65535, GifEncoder.FrameDelays(8, 8, 700_000)[^1], "65535 を超えない");

        // ファイルの中でも、最後のフレームの遅延に足されている
        const int w = 8, h = 8, n = 3;
        var frames = Enumerable.Range(0, n).Select(k => Solid(w, h, (byte)(k * 80), 10, 10)).ToList();
        var o = new GifEncoderOptions { Fps = 10, FinalHoldMs = 5000 };
        var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o));
        Check.SequenceEqual(new[] { 10, 10, 510 }, f.Frames.Select(x => x.Delay).ToArray(), "ファイルの遅延");
    }

    // ---- (4) 上限 ----

    private sealed class SizeOnlySource(int w, int h, int n) : IGifFrameSource
    {
        public int Calls;

        public int Width => w;

        public int Height => h;

        public int FrameCount => n;

        public byte[] GetFrame(GifPass pass, int index)
        {
            Calls++;
            throw new InvalidOperationException("上限の外の指定でフレームを読んだ");
        }
    }

    [Test]
    public void AC4_4_上限の外は符号化せずに拒否する()
    {
        var cases = new (int W, int H, int N)[]
        {
            (4096, 4096, 10), // N×W×H=167,772,160
            (4000, 4000, 11),
            (0, 100, 3), (100, 0, 3), (4097, 100, 3), (100, 4097, 3),
            (100, 100, 0), (100, 100, 501),
        };
        foreach (var (w, h, n) in cases)
        {
            var src = new SizeOnlySource(w, h, n);
            var e = Check.Throws<GifTooBigException>(() => GifEncoder.EncodeToArray(src, new GifEncoderOptions()), $"{w}×{h}×{n}");
            Check.Equal(0, src.Calls, $"{w}×{h}×{n} でフレームを読まない");
            Check.Equal("export.gif_too_big", GifTooBigException.MessageKey, "文言のキー");
            Check.Equal("GIF", e.FormatName, "{fmt}");
            Check.True(!GifLimits.IsWithin(w, h, n), $"{w}×{h}×{n} は上限の外");
        }
        Check.True(GifLimits.IsWithin(4000, 4000, 10), "N×W×H=160,000,000 ちょうどは内側");
        Check.True(GifLimits.IsWithin(4096, 4096, 9), "4096×4096×9 は内側");
        Check.True(GifLimits.IsWithin(16, 16, 500), "N=500 は内側");
    }

    [Test]
    public void AC4_4_1x4096と4096x1は受け付ける()
    {
        foreach (var (w, h) in new[] { (1, 4096), (4096, 1) })
        {
            var frames = Enumerable.Range(0, 3).Select(k => Gradient(w, h, k, 16)).ToList();
            var o = new GifEncoderOptions { Colors = 32 };
            var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o);
            var f = MiniGifDecoder.Parse(gif);
            Check.Equal((w, h), (f.Width, f.Height), $"{w}×{h} の大きさ");
            Check.Equal(3, f.Frames.Count, $"{w}×{h} のフレーム数");
            var wpf = ReadWithWpf(gif);
            Check.Equal(3, wpf.Count, $"{w}×{h} を WPF で読んだフレーム数");
        }
    }

    // ---- (5) 決定論 ----

    [Test]
    public void AC4_5_並列度1と8で同じバイト列()
    {
        const int w = 64, h = 48, n = 12;
        var opaque = Enumerable.Range(0, n).Select(k => Gradient(w, h, k, 17)).ToList();
        var transparent = Enumerable.Range(0, n)
            .Select(k => WithTransparentOutsideCircle(Gradient(w, h, k, 18), w, h, 20 + 2 * k, 24, 15)).ToList();
        foreach (var (frames, name) in new[] { (opaque, "不透明"), (transparent, "透明を含む") })
        {
            foreach (var dither in new[] { GifDither.BlueNoise, GifDither.ErrorDiffusion, GifDither.None })
            {
                var hashes = new List<string>();
                foreach (var p in new[] { 1, 1, 8, 8 })
                {
                    var o = new GifEncoderOptions { Colors = 96, Dither = dither, Parallelism = p, FinalHoldMs = 300 };
                    hashes.Add(Sha256(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o)));
                }
                Check.True(hashes.Distinct().Count() == 1, $"{name}・{dither} の SHA-256 が一致しない: {string.Join(" ", hashes)}");
            }
        }
    }

    [Test]
    public void AC4_5_同時に保持するフレームは並列度足す2枚まで()
    {
        const int w = 32, h = 32, n = 40;
        foreach (var p in new[] { 1, 2, 8 })
        {
            var src = new CountingFrameSource(w, h, k => Gradient(w, h, k, 19), n);
            GifEncoder.EncodeToArray(src, new GifEncoderOptions { Parallelism = p });
            Check.Equal(2 * n, src.Gets, $"並列度 {p}: 供給は 2 回");
            Check.Equal(src.Gets, src.Releases, $"並列度 {p}: 返した数");
            Check.True(src.MaxOutstanding <= p + 2, $"並列度 {p}: 同時に保持したフレーム {src.MaxOutstanding} 枚");
            var expected = Enumerable.Range(0, n).Select(k => (GifPass.Collect, k))
                .Concat(Enumerable.Range(0, n).Select(k => (GifPass.Encode, k))).ToList();
            Check.SequenceEqual(expected, src.Order, $"並列度 {p}: 供給の順");
        }
    }

    // ---- (6) WPF で読んだ画素 ----

    private static void CheckWpfMatchesExpected(List<byte[]> frames, int w, int h, GifEncoderOptions o, string what)
    {
        var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o);
        var f = MiniGifDecoder.Parse(gif);
        var tIndex = f.Frames[0].TransparentFlag ? f.Frames[0].TransparentIndexField : -1;
        var colorCount = tIndex >= 0 ? tIndex : f.GlobalTableEntries;
        var labs = TableLabs(f, colorCount);
        var wpf = ReadWithWpf(gif);
        Check.Equal(frames.Count, wpf.Count, what + " WPF のフレーム数");
        for (var k = 0; k < frames.Count; k++)
        {
            var fr = wpf[k];
            Check.Equal((0, 0, w, h), (fr.Left, fr.Top, fr.Width, fr.Height), $"{what} フレーム {k} は全面");
            var px = frames[k];
            for (var p = 0; p < w * h; p++)
            {
                var o4 = p * 4;
                var b = fr.Bgra.AsSpan(o4, 4);
                if (px[o4 + 3] < 128)
                {
                    Check.True(b[3] == 0, $"{what} フレーム {k} 画素 {p} は透明");
                    continue;
                }
                var idx = BruteNearest(LabOf(px[o4], px[o4 + 1], px[o4 + 2]), labs);
                var (r, g, bl) = f.TableColor(idx);
                if (b[2] != r || b[1] != g || b[0] != bl || b[3] != 255)
                    throw new CheckFailedException($"{what} フレーム {k} 画素 {p}: 期待 ({r},{g},{bl}) 実際 BGRA=({b[0]},{b[1]},{b[2]},{b[3]})");
            }
        }
    }

    [Test]
    public void AC4_6_GifBitmapDecoderの画素がパレットと透明の規則どおり()
    {
        const int w = 50, h = 40, n = 4;
        var opaque = Enumerable.Range(0, n).Select(k => Gradient(w, h, k, 20)).ToList();
        CheckWpfMatchesExpected(opaque, w, h,
            new GifEncoderOptions { Colors = 64, Dither = GifDither.None, DiffOptimization = false }, "不透明");
        var transparent = Enumerable.Range(0, n)
            .Select(k => WithTransparentOutsideCircle(Gradient(w, h, k, 21), w, h, 15 + 6 * k, 20, 12)).ToList();
        CheckWpfMatchesExpected(transparent, w, h, new GifEncoderOptions { Colors = 32, Dither = GifDither.None }, "透明を含む");
        var random = Enumerable.Range(0, 2).Select(k => RandomOpaque(w, h, 22 + (uint)k)).ToList();
        CheckWpfMatchesExpected(random, w, h,
            new GifEncoderOptions { Colors = 256, Dither = GifDither.None, DiffOptimization = false }, "乱数の色");
    }

    [Test]
    public void AC4_6_途中のクリアコードを含むフレームもGifBitmapDecoderで読める()
    {
        // 256 色の乱数の 320×320 は、1 フレームの中で辞書が何度も満杯になる(規則 A の (b) を WIC のデコーダーでも確かめる)。
        const int w = 320, h = 320;
        var frames = new List<byte[]> { RandomOpaque(w, h, 23, 256), RandomOpaque(w, h, 24, 256) };
        var o = new GifEncoderOptions { Colors = 256, Dither = GifDither.None, DiffOptimization = false };
        var f = MiniGifDecoder.Parse(GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), o));
        Check.True(f.Frames.All(fr => fr.MidClearCodes >= 1), "途中のクリアコードがある");
        for (var k = 0; k < f.Frames.Count; k++) CheckClearAt4095(f.Frames[k].NextAtMidClears, $"フレーム {k}");
        Check.Equal(8, f.Frames[0].MinCodeSize, "最小コードサイズ");
        CheckWpfMatchesExpected(frames, w, h, o, "乱数の 320×320");
    }
}
