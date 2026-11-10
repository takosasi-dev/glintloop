// 自作の GIF エンコーダー(仕様書 §9.8、D-5・D-21〜D-27・D-29)。2 回の供給で、集計→パレット→ディザ→差分→LZW→コンテナの順に作る。
// フレームごとの計算は並列(Task)にし、結果はフレームの順に連結するので、並列度に依らず同じバイト列になる(INV-8)。
// 上限(§9.8 の 10)を超える指定は、フレームを読む前に GifTooBigException で拒否する。
namespace GlintLoop.Core.Gif;

public static class GifEncoder
{
    /// <summary>NETSCAPE2.0 のループ回数 0(無限)の拡張(19 バイト)。</summary>
    internal static ReadOnlySpan<byte> NetscapeLoopForever =>
    [
        0x21, 0xFF, 0x0B, 0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30, 0x03, 0x01, 0x00, 0x00, 0x00,
    ];

    /// <summary>フレームの列を GIF89a にして output に書く。</summary>
    public static void Encode(IGifFrameSource source, Stream output, GifEncoderOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var plan = Analyze(source, options, ct);
        WriteFile(source, output, options, plan, ct);
    }

    /// <summary>フレームの列を GIF89a のバイト列にする。</summary>
    public static byte[] EncodeToArray(IGifFrameSource source, GifEncoderOptions options, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        Encode(source, ms, options, ct);
        return ms.ToArray();
    }

    /// <summary>フレーム k の遅延(1/100 秒)。§9.8 の 7。</summary>
    public static int[] FrameDelays(int frameCount, int fps, int finalHoldMs)
    {
        if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (fps < 1) throw new ArgumentOutOfRangeException(nameof(fps));
        if (finalHoldMs < 0) throw new ArgumentOutOfRangeException(nameof(finalHoldMs));
        var d = new int[frameCount];
        if (frameCount == 1)
        {
            d[0] = 100;
            return d;
        }
        for (var k = 0; k < frameCount; k++)
        {
            var v = (int)(RoundRatio(100L * (k + 1), fps) - RoundRatio(100L * k, fps));
            d[k] = Math.Max(2, v);
        }
        var hold = (finalHoldMs + 5L) / 10; // round(ms÷10)。ms は 0 以上
        d[^1] = (int)Math.Min(65535L, d[^1] + hold);
        return d;
    }

    // round(a÷b)=floor(a÷b+0.5) を整数で計算する(a≥0, b>0)。浮動小数の誤差で 0.5 ちょうどがずれないように。
    private static long RoundRatio(long a, long b) => (2 * a + b) / (2 * b);

    // ---- 1 回目: 集計とパレット ----

    /// <summary>符号化の計画(1 回目の供給の結果)。</summary>
    internal sealed record Plan(
        GifPalette Palette,
        bool HasAlphaTransparency,
        int TransparentIndex,
        int TableSize,
        int MinCodeSize,
        bool UseDiff,
        int Disposal,
        int[] Delays);

    internal static void ValidateOptions(IGifFrameSource source, GifEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        GifLimits.Validate(source.Width, source.Height, source.FrameCount);
        if (options.Colors is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(options), $"色数 {options.Colors} が 2〜256 の外");
        if (options.Fps < 1) throw new ArgumentOutOfRangeException(nameof(options), $"fps {options.Fps} が 1 未満");
        if (options.FinalHoldMs < 0) throw new ArgumentOutOfRangeException(nameof(options), $"停止時間 {options.FinalHoldMs} が負");
        if (options.Parallelism < 1) throw new ArgumentOutOfRangeException(nameof(options), $"並列度 {options.Parallelism} が 1 未満");
        if (!Enum.IsDefined(options.Dither)) throw new ArgumentOutOfRangeException(nameof(options), "ディザの種類が不正");
        if (!Enum.IsDefined(options.Loop)) throw new ArgumentOutOfRangeException(nameof(options), "ループの種類が不正");
    }

    /// <summary>1 回目の供給で集計し、パレット・透明インデックス・テーブルの大きさを決める。</summary>
    internal static Plan Analyze(IGifFrameSource source, GifEncoderOptions options, CancellationToken ct)
    {
        ValidateOptions(source, options);
        var w = source.Width;
        var h = source.Height;
        var n = source.FrameCount;
        var matte = GifPixel.Matte.From(options.Matte);

        var total = GifHistogram.CreateTotal();
        var pool = new Stack<GifHistogram>();
        var anyTransparent = false;
        using (var pair = new LimitedScheduler(options.Parallelism, options.Scheduler))
        {
            GifPipeline.RunOrdered<(GifHistogram Hist, bool Transparent)>(n, options.Parallelism + 2, options.Scheduler, ct,
                (k, token) =>
                {
                    var pixels = Fetch(source, GifPass.Collect, k, w, h);
                    GifHistogram hist;
                    lock (pool) hist = pool.Count > 0 ? pool.Pop() : GifHistogram.CreateFrame();
                    return Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            var t = hist.AddFrame(pixels, matte);
                            return (hist, t);
                        }
                        finally
                        {
                            source.ReleaseFrame(GifPass.Collect, k, pixels);
                        }
                    }, token, TaskCreationOptions.DenyChildAttach, pair.Scheduler);
                },
                (k, r) =>
                {
                    // フレーム番号の順に足す
                    total.AddFrameTotals(r.Hist);
                    r.Hist.ClearFrame();
                    lock (pool) pool.Push(r.Hist);
                    anyTransparent |= r.Transparent;
                });
        }

        // 規則 2: 透明インデックスを置くか
        var useDiff = !anyTransparent && n >= 2 && options.DiffOptimization;
        var hasT = anyTransparent || useDiff;
        var nc = hasT ? options.Colors - 1 : options.Colors;
        if (nc < 1) nc = 1;
        var palette = GifPalette.Build(total, nc);
        var tIndex = hasT ? palette.Count : -1;
        var needed = palette.Count + (hasT ? 1 : 0);
        var m = 2;
        while (m < needed) m <<= 1;
        var log2M = Log2(m);
        return new Plan(
            palette,
            anyTransparent,
            tIndex,
            m,
            Math.Max(2, log2M),
            useDiff,
            anyTransparent ? 2 : 1,
            FrameDelays(n, options.Fps, options.FinalHoldMs));
    }

    private static int Log2(int m)
    {
        var r = 0;
        while ((1 << r) < m) r++;
        return r;
    }

    private static byte[] Fetch(IGifFrameSource source, GifPass pass, int k, int w, int h)
    {
        var pixels = source.GetFrame(pass, k) ?? throw new InvalidDataException($"フレーム {k} が null");
        if (pixels.Length != w * h * 4)
        {
            source.ReleaseFrame(pass, k, pixels);
            throw new InvalidDataException($"フレーム {k} の大きさが {w}×{h}×4 バイトではない: {pixels.Length}");
        }
        return pixels;
    }

    // ---- 2 回目: 符号化とファイルの構造 ----

    private static void WriteFile(IGifFrameSource source, Stream output, GifEncoderOptions options, Plan plan, CancellationToken ct)
    {
        var w = source.Width;
        var h = source.Height;
        var n = source.FrameCount;
        var matte = GifPixel.Matte.From(options.Matte);
        var nearest = new GifNearest(plan.Palette.Colors);

        output.Write(Header(w, h, plan, writeNetscape: options.Loop == GifLoop.Infinite && n >= 2));

        using (var pair = new LimitedScheduler(options.Parallelism, options.Scheduler))
        {
            Task<byte[]>? previous = null;
            GifPipeline.RunOrdered<byte[]>(n, options.Parallelism + 2, options.Scheduler, ct,
                (k, token) =>
                {
                    var pixels = Fetch(source, GifPass.Encode, k, w, h);
                    var mapTask = Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            return GifDitherer.MapFrame(pixels, w, h, nearest, options.Dither, matte, plan.TransparentIndex);
                        }
                        finally
                        {
                            source.ReleaseFrame(GifPass.Encode, k, pixels);
                        }
                    }, token, TaskCreationOptions.DenyChildAttach, pair.Scheduler);

                    Task<byte[]> encodeTask;
                    if (plan.UseDiff && previous is not null)
                    {
                        var prev = previous;
                        encodeTask = Task.Factory.ContinueWhenAll([prev, mapTask],
                            _ => FrameBlock(k, w, h, plan, prev.GetAwaiter().GetResult(), mapTask.GetAwaiter().GetResult()),
                            token, TaskContinuationOptions.DenyChildAttach, pair.Scheduler);
                    }
                    else
                    {
                        encodeTask = mapTask.ContinueWith(
                            t => FrameBlock(k, w, h, plan, null, t.GetAwaiter().GetResult()),
                            token, TaskContinuationOptions.DenyChildAttach, pair.Scheduler);
                    }
                    previous = mapTask;
                    return encodeTask;
                },
                (_, block) => output.Write(block));
        }

        output.WriteByte(0x3B);
    }

    /// <summary>ヘッダー・論理スクリーン記述子・グローバルカラーテーブル・NETSCAPE2.0(あれば)。</summary>
    private static byte[] Header(int w, int h, Plan plan, bool writeNetscape)
    {
        var s = new GifByteSink(13 + plan.TableSize * 3 + 19);
        s.Add("GIF89a"u8);
        s.AddUInt16(w);
        s.AddUInt16(h);
        s.Add((byte)(0xF0 | (Log2(plan.TableSize) - 1)));
        s.Add(0); // 背景色の番号
        s.Add(0); // 画素の縦横比
        var colors = plan.Palette.Colors;
        for (var i = 0; i < plan.TableSize; i++)
        {
            // 使わない枠(透明インデックスを含む)は黒
            var c = i < colors.Length ? colors[i] : new GifColor(0, 0, 0);
            s.Add(c.R);
            s.Add(c.G);
            s.Add(c.B);
        }
        if (writeNetscape) s.Add(NetscapeLoopForever);
        return s.ToArray();
    }

    /// <summary>1 フレーム分(グラフィック制御拡張・画像記述子・画像データ)。差分は前のフレームの番号の配列と比べる。</summary>
    private static byte[] FrameBlock(int k, int w, int h, Plan plan, byte[]? prev, byte[] cur)
    {
        int x0 = 0, y0 = 0, rw = w, rh = h;
        byte[] data = cur;
        if (plan.UseDiff && k >= 1)
        {
            if (prev is null) throw new InvalidOperationException("差分に前のフレームが無い");
            (x0, y0, rw, rh, data) = DiffRect(w, h, prev, cur, (byte)plan.TransparentIndex);
        }

        var lzw = GifLzw.EncodeImageData(data, plan.MinCodeSize);
        var s = new GifByteSink(18 + lzw.Length);
        var hasT = plan.TransparentIndex >= 0;
        s.Add(0x21);
        s.Add(0xF9);
        s.Add(0x04);
        s.Add((byte)((plan.Disposal << 2) | (hasT ? 1 : 0)));
        s.AddUInt16(plan.Delays[k]);
        s.Add((byte)(hasT ? plan.TransparentIndex : 0));
        s.Add(0x00);
        s.Add(0x2C);
        s.AddUInt16(x0);
        s.AddUInt16(y0);
        s.AddUInt16(rw);
        s.AddUInt16(rh);
        s.Add(0x00);
        s.Add(lzw);
        return s.ToArray();
    }

    /// <summary>
    /// 前と異なる画素の外接矩形と、その中で前と同じ画素を T にした番号の列。異なる画素が無ければ (0,0) の 1×1 で値は T。
    /// </summary>
    internal static (int X, int Y, int W, int H, byte[] Data) DiffRect(int w, int h, byte[] prev, byte[] cur, byte t)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                if (prev[row + x] == cur[row + x]) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return (0, 0, 1, 1, [t]);
        var rw = maxX - minX + 1;
        var rh = maxY - minY + 1;
        var data = new byte[rw * rh];
        for (var y = 0; y < rh; y++)
        {
            var src = (minY + y) * w + minX;
            for (var x = 0; x < rw; x++)
            {
                var c = cur[src + x];
                data[y * rw + x] = c == prev[src + x] ? t : c;
            }
        }
        return (minX, minY, rw, rh, data);
    }

    /// <summary>同時に走る作業を parallelism 個までにするスケジューラー。</summary>
    private sealed class LimitedScheduler(int parallelism, TaskScheduler? scheduler) : IDisposable
    {
        private readonly ConcurrentExclusiveSchedulerPair _pair = new(scheduler ?? TaskScheduler.Default, parallelism);

        internal TaskScheduler Scheduler => _pair.ConcurrentScheduler;

        public void Dispose() => _pair.Complete();
    }
}
