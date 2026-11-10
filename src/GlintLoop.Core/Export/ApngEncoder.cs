// APNG の書き出し(FR-72・§9.9 の 6): シグネチャ、IHDR、acTL、(PLTE・tRNS)、fcTL(0)・IDAT、fcTL(k)・fdAT(k)、IEND。全フレームを全面で書き、dispose_op=0・blend_op=0。
// 遅延は分子 d_k(§9.8 の 7 の式。GifEncoder.FrameDelays)・分母 100。sequence_number は fcTL と fdAT を通して 0 から連番、fdAT は 1 チャンク 8192 バイトまで。
// 色は 32 ビット RGBA か、§9.8 の手順のパレット(alpha は二値、透明の番号は tRNS で 0)。フレームの圧縮は ExportRun.Scheduler の上で並列にし、書く順はフレームの順。
using System.Buffers.Binary;
using GlintLoop.Core.Gif;

namespace GlintLoop.Core.Export;

public sealed class ApngEncoder : IExportEncoder
{
    /// <summary>fdAT のチャンクの中身(sequence_number の 4 バイトを含む)の上限。</summary>
    public const int MaxFdatBytes = 8192;

    public string Format => ExportFormat.Apng;

    public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var n = settings.FrameCount;
        ExportEncoding.CheckFrames(frames, settings, n);
        if (!OutputPlan.IsWithinLimits(settings)) throw new ExportTooBigException(settings);
        var w = settings.Width;
        var h = settings.Height;
        var ct = run.Cancel;
        var parallelism = Math.Max(1, run.Parallelism);
        var delays = GifEncoder.FrameDelays(n, settings.Fps, (int)settings.HoldMs);

        // パレット: 1 回目の供給で §9.8 の手順のパレットを作る(差分の最適化は使わないので、透明の番号は α<128 の画素があるときだけ)
        GifNearest? nearest = null;
        GifColor[] colors = [];
        var tIndex = -1;
        if (settings.ApngPalette)
        {
            var options = new GifEncoderOptions
            {
                Colors = settings.Colors,
                Dither = settings.Dither,
                Matte = null,
                DiffOptimization = false,
                Fps = Math.Max(1, settings.Fps),
                Parallelism = parallelism,
                Scheduler = run.Scheduler,
            };
            var plan = GifEncoder.Analyze(new FrameSource(frames, n, ct), options, ct);
            colors = plan.Palette.Colors;
            tIndex = plan.TransparentIndex;
            nearest = new GifNearest(colors);
        }

        return ExportEncoding.Run(output, limitBytes, s =>
        {
            s.Write(PngWriter.Signature);
            PngWriter.WriteChunk(s, "IHDR", PngWriter.Ihdr(w, h, settings.ApngPalette ? PngWriter.ColorPalette : PngWriter.ColorRgba));
            var actl = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)n);
            BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(4), settings.Loop == Session.LoopMode.Infinite ? 0u : (uint)Math.Clamp(settings.ApngLoops, 1, 4));
            PngWriter.WriteChunk(s, "acTL", actl);
            if (settings.ApngPalette) WritePalette(s, colors, tIndex);

            var seq = 0u;
            GifPipeline.RunOrdered<byte[]>(n, parallelism + 2, run.Scheduler, ct,
                (k, token) =>
                {
                    var px = ExportEncoding.Frame(frames, k, token);
                    return Task.Factory.StartNew(() => settings.ApngPalette
                            ? PngWriter.ImageData(GifDitherer.MapFrame(px, w, h, nearest!, settings.Dither, GifPixel.Matte.None, tIndex), w, h, 1)
                            : PngWriter.ImageData(px, w, h, 4),
                        token, TaskCreationOptions.DenyChildAttach, run.Scheduler);
                },
                (k, data) =>
                {
                    PngWriter.WriteChunk(s, "fcTL", Fctl(seq++, w, h, delays[k]));
                    if (k == 0)
                    {
                        PngWriter.WriteChunk(s, "IDAT", data);
                        return;
                    }
                    const int part = MaxFdatBytes - 4;
                    // zlib のデータは空にならない(ヘッダーの 2 バイトがある)
                    for (var o = 0; o < data.Length; o += part)
                    {
                        var len = Math.Min(part, data.Length - o);
                        var body = new byte[4 + len];
                        BinaryPrimitives.WriteUInt32BigEndian(body, seq++);
                        data.AsSpan(o, len).CopyTo(body.AsSpan(4));
                        PngWriter.WriteChunk(s, "fdAT", body);
                    }
                });
            PngWriter.WriteChunk(s, "IEND", []);
        });
    }

    // PLTE(色の並びと、透明の番号があればその枠の黒)と tRNS(透明の番号だけ 0、ほかは 255)。
    private static void WritePalette(Stream s, GifColor[] colors, int tIndex)
    {
        var count = colors.Length + (tIndex >= 0 ? 1 : 0);
        var plte = new byte[count * 3];
        for (var i = 0; i < colors.Length; i++)
        {
            plte[i * 3] = colors[i].R;
            plte[i * 3 + 1] = colors[i].G;
            plte[i * 3 + 2] = colors[i].B;
        }
        PngWriter.WriteChunk(s, "PLTE", plte);
        var trns = new byte[count];
        Array.Fill(trns, (byte)255);
        if (tIndex >= 0) trns[tIndex] = 0;
        PngWriter.WriteChunk(s, "tRNS", trns);
    }

    // fcTL の中身(26 バイト): sequence_number・幅・高さ・x=0・y=0・遅延の分子と分母 100・dispose_op=0・blend_op=0。
    private static byte[] Fctl(uint seq, int w, int h, int delay)
    {
        var d = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(d, seq);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), (uint)h);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(20), (ushort)delay);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(22), 100);
        d[24] = 0;
        d[25] = 0;
        return d;
    }

    /// <summary>パレットの集計に渡すフレームの供給元(書き出しのフレームの列をそのまま渡す)。</summary>
    private sealed class FrameSource(IExportFrames frames, int count, CancellationToken ct) : IGifFrameSource
    {
        public int Width => frames.Width;

        public int Height => frames.Height;

        public int FrameCount => count;

        public byte[] GetFrame(GifPass pass, int index) => frames.GetFrame(index, ct);
    }
}
