// エンジンで描いたフレームを通す書き出しの検査(WARP): AC-37 の (2) のフレームの位置・(4) の GIF の組み合わせ・(5) のモーションと WebP・(7) のメタデータ、
// AC-35 の (10) の 1 フレームの GIF、AC-38 の (6) の実際のエンコード、GIF の 2 回の供給で同じフレームを 2 回描くこと(Q-I)。
// 文書は EditorSession と埋め込みの EffectDef で組み立て、書き出しは OutputPlan → ExportJob → ExportFrames(RenderExportAsync)の製品の経路を通す。
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using GlintLoop.Codec;
using GlintLoop.Codec.Export;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Gif;
using GlintLoop.Tests.Motions;
using GlintLoop.Tests.Render;
using SkiaSharp;
using static GlintLoop.Tests.Exports.ExportTestKit;

namespace GlintLoop.Tests.Exports;

/// <summary>書き出しのテストの土台: WARP の RenderService・画像の表・DocumentRenderer・EditorSession。</summary>
internal sealed class ExportRenderKit : IDisposable
{
    private readonly Dictionary<string, SourceImage> _images = new(StringComparer.Ordinal);

    public ExportRenderKit()
    {
        Rs = RenderService.Create(GpuAdapterRequest.Warp);
        Sources = new FuncSourceImages(sha => _images.TryGetValue(sha, out var im) ? im : null);
        Renderer = new DocumentRenderer(Rs, Sources);
        Session = new EditorSession(Rs.Effects, new FixedSeedSource(12345));
    }

    public RenderService Rs { get; }
    public ISourceImages Sources { get; }
    public DocumentRenderer Renderer { get; }
    public EditorSession Session { get; }
    public SessionDocument Doc => Session.Document!;

    public static IReadOnlyList<IExportEncoder> Encoders { get; } =
        [new PngExportEncoder(), new JpegExportEncoder(), new GifExportEncoder(), new WebpExportEncoder(), new ApngEncoder()];

    /// <summary>画像を順に取り込む(最初の 1 枚が土台)。足したレイヤーの配置は (0, 0, 100)。</summary>
    public void Import(params (int W, int H, byte[] Rgba)[] imgs)
    {
        var before = Session.Document?.Layers.Count ?? 0;
        var list = imgs.Select(im =>
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(im.Rgba));
            _images[sha] = new SourceImage(im.W, im.H, im.Rgba);
            return new ImportedSource(sha, "png", Math.Max(1, im.Rgba.Length), im.W, im.H, "img");
        }).ToArray();
        Session.AddImportedImages(list);
        for (var i = Math.Max(1, before); i < Doc.Layers.Count; i++) Session.SetLayerPlacement(i, 0, 0, 100);
    }

    public void Import(params CompositeImg[] imgs) => Import(imgs.Select(i => (i.W, i.H, i.Px)).ToArray());

    public void Motion(int layer, string id) => Check.True(Session.AddEffect(Target.OfLayer(layer), id).Changed, id + " を足す");

    public ExportPlan Plan(SessionDocument doc, int scrubK = 0) => OutputPlan.Resolve(doc, Rs.Effects, ExportFrames.SourceSize(Sources), scrubK);

    /// <summary>製品の経路で書き出し、結果とバイト列を返す。</summary>
    public (ExportJobResult Result, byte[] Bytes) Export(SessionDocument doc, int scrubK = 0, ExportRun? run = null)
    {
        var plan = Plan(doc, scrubK);
        var enc = ExportJob.Pick(plan.R0.Format, Encoders);
        var r = ExportJob.Run(plan, enc, ExportFrames.Factory(Renderer, doc), () => new MemoryStream(), s => s.Dispose(), run ?? Run());
        var bytes = ((MemoryStream)r.Output).ToArray();
        r.Output.Dispose();
        return (r, bytes);
    }

    /// <summary>エンジンのフレーム k(N のうち)を書き出しの形(straight RGBA 8 ビット)で描く。</summary>
    public byte[] Frame(SessionDocument doc, int lout, int k, int n) => Renderer.RenderExportAsync(doc, lout, k, n).GetAwaiter().GetResult().Pixels;

    public static SessionDocument With(SessionDocument doc, string format, int length10 = 20, int fps = 20, long holdMs = 0, OutputSize? size = null) =>
        doc with
        {
            Time = new TimeSettings(length10, fps, LoopMode.Infinite, holdMs),
            Output = doc.Output with { Format = format, Size = size ?? new OutputSize("long", 64) },
        };

    public void Dispose()
    {
        Renderer.Dispose();
        Rs.Dispose();
    }
}

public sealed class ExportRenderAc37Tests
{
    [Test]
    public void AC37_2_モーションのPNGはスクラブバーの位置の1枚()
    {
        using var kit = new ExportRenderKit();
        kit.Import(MotionTestKit.SmoothOpaque());
        kit.Motion(0, "spin");
        var doc = ExportRenderKit.With(kit.Doc, "png");
        var (r0, a) = kit.Export(doc, 0);
        var (r10, b) = kit.Export(doc, 10);
        Check.Equal((1, 0, 40), (r0.Settings.FrameCount, r0.Settings.FrameK, r0.Settings.TimelineFrames), "k=0 の設定");
        Check.Equal((1, 10), (r10.Settings.FrameCount, r10.Settings.FrameK), "k=10 の設定");
        var pa = MiniPngParser.Parse(a);
        var pb = MiniPngParser.Parse(b);
        Check.Equal(1, pa.Frames.Count, "1 枚だけ書かれる");
        Check.True(!pa.Frames[0].AsSpan().SequenceEqual(pb.Frames[0]), "k=0 と k=10 で内容が違う");
        Check.SequenceEqual(kit.Frame(doc, 64, 10, 40), pb.Frames[0], "k=10 の PNG がエンジンのフレーム 10 と一致");
    }

    [Test]
    public void AC35_10_モーションが無ければGIFは1フレーム()
    {
        using var kit = new ExportRenderKit();
        kit.Import(MotionTestKit.SmoothOpaque(), MotionTestKit.Translucent());
        var row = kit.Session.AddEffect(Target.OfLayer(0), "spin").Row!.Value;
        Check.True(kit.Session.SetEffectEnabled(Target.OfLayer(0), row, false).Changed, "spin をオフ");
        kit.Motion(1, "slide");
        Check.True(kit.Session.SetLayerVisible(1, false).Changed, "モーションのあるレイヤーの表示をオフ");
        var (r, gif) = kit.Export(ExportRenderKit.With(kit.Doc, "gif", holdMs: 500));
        Check.Equal(1, r.Settings.FrameCount, "フレーム数");
        var f = MiniGifDecoder.Parse(gif);
        Check.Equal(1, f.Frames.Count, "GIF のフレーム数");
        Check.Equal(100, f.Frames[0].Delay, "1 フレームの遅延");
        Check.Equal(0, f.AppExtensions.Count, "NETSCAPE2.0 が無い");
        Check.True(!Contains(gif, "NETSCAPE2.0"), "NETSCAPE2.0 の文字が無い");

        // 表示をオンにするとモーションがあるので N フレーム
        Check.True(kit.Session.SetLayerVisible(1, true).Changed, "表示をオン");
        var (m, _) = kit.Export(ExportRenderKit.With(kit.Doc, "gif", length10: 5, fps: 10));
        Check.Equal(5, m.Settings.FrameCount, "モーションがあるときのフレーム数");
    }

    private static int DistinctOpaqueColors(IEnumerable<byte[]> bgraFrames)
    {
        var set = new HashSet<int>();
        foreach (var f in bgraFrames)
        {
            for (var i = 0; i < f.Length; i += 4)
            {
                if (f[i + 3] == 255) set.Add(f[i] | (f[i + 1] << 8) | (f[i + 2] << 16));
            }
        }
        return set.Count;
    }

    private static double MeanAbsRgb(byte[] rgba, byte[] bgra)
    {
        long s = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            s += Math.Abs(rgba[i] - bgra[i + 2]) + Math.Abs(rgba[i + 1] - bgra[i + 1]) + Math.Abs(rgba[i + 2] - bgra[i]);
        }
        return s / (rgba.Length / 4.0 * 3);
    }

    [Test]
    public void AC37_4_GIFの組み合わせ()
    {
        const int n = 5, fps = 10, lout = 96;
        foreach (var (img, motion, opaque) in new[] { (MotionTestKit.SmoothOpaque(), "slide", true), (MotionTestKit.Translucent(), "spin", false) })
        {
            using var k2 = new ExportRenderKit();
            k2.Import(img);
            k2.Motion(0, motion);
            var doc = ExportRenderKit.With(k2.Doc, "gif", length10: 5, fps: fps, holdMs: 240, size: new OutputSize("long", lout));
            var plan = k2.Plan(doc);
            Check.Equal(n, plan.R0.FrameCount, "N");
            var frames = Enumerable.Range(0, n).Select(k => k2.Frame(doc, lout, k, n)).ToList();
            if (opaque) Check.True(frames.All(f => f.Where((_, i) => i % 4 == 3).All(a => a == 255)), "slide の文書は不透明");
            foreach (var colors in new[] { 2, 16, 256 })
            foreach (var dither in new[] { GifDither.BlueNoise, GifDither.ErrorDiffusion, GifDither.None })
            foreach (var matte in new GifColor?[] { null, new GifColor(40, 200, 90) })
            foreach (var diff in new[] { true, false })
            {
                var what = $"{motion}・色数 {colors}・{dither}・マット {matte is not null}・差分 {diff}";
                var s = plan.R0 with { Colors = colors, Dither = dither, Matte = matte, DiffOptimization = diff };
                var gif = Encode(new GifExportEncoder(), new ArrayFrames(s.Width, s.Height, frames), s);
                var wpf = GifTestKit.ReadWithWpf(gif);
                Check.Equal(n, wpf.Count, what + ": フレーム数");
                Check.Equal(50 + 24, wpf.Sum(f => f.Delay), what + ": 遅延の合計 round(100×N÷F)+停止時間");
                var hasT = MiniGifDecoder.Parse(gif).Frames[0].TransparentFlag;
                var shown = GifTestKit.Composite(wpf, s.Width, s.Height);
                var count = DistinctOpaqueColors(shown);
                Check.True(count <= (hasT ? colors - 1 : colors), $"{what}: 色の種類 {count} が色数 {colors}(透明 {hasT})を超えた");
                if (colors == 256 && dither == GifDither.BlueNoise && opaque)
                {
                    for (var k = 0; k < n; k++)
                    {
                        var d = MeanAbsRgb(frames[k], shown[k]);
                        Check.True(d <= 8.0, $"{what}: フレーム {k} の RGB の絶対差の平均 {d:F2} が 8.0 を超えた");
                    }
                }
            }
        }
    }

    [Test]
    public void AC37_4_GIFは同じフレームを2回描いて供給する()
    {
        using var kit = new ExportRenderKit();
        kit.Import(MotionTestKit.SmoothOpaque());
        kit.Motion(0, "spin");
        var doc = ExportRenderKit.With(kit.Doc, "gif", length10: 5, fps: 10);
        var plan = kit.Plan(doc);
        var frames = new ExportFrames(kit.Renderer, doc, plan.R0);
        var a = Encode(new GifExportEncoder(), frames, plan.R0);
        Check.Equal(2 * plan.R0.FrameCount, frames.Renders, "GIF は各フレームを 2 回描く(Q-I)");
        var cached = Enumerable.Range(0, plan.R0.FrameCount).Select(k => kit.Frame(doc, plan.R0.LongSide, k, plan.R0.FrameCount)).ToList();
        var b = Encode(new GifExportEncoder(), new ArrayFrames(plan.R0.Width, plan.R0.Height, cached), plan.R0);
        Check.SequenceEqual(a, b, "描き直しても同じバイト列(2 回の供給は同じ画素)");
        var (_, c) = kit.Export(doc, 0, Run(1));
        var (_, d) = kit.Export(doc, 0, Run(8));
        Check.SequenceEqual(c, d, "並列度 1 と 8 の GIF のバイト列(INV-8)");
    }

    [Test]
    public void AC37_5_モーションがあってもWebPは1フレームの静止画()
    {
        using var kit = new ExportRenderKit();
        kit.Import(MotionTestKit.SmoothOpaque());
        kit.Motion(0, "spin");
        var webp = ExportRenderKit.With(kit.Doc, "webp");
        var doc = webp with { Output = webp.Output with { Webp = new WebpOptions(85, true) } };
        var (r, bytes) = kit.Export(doc, 7);
        Check.Equal(1, r.Settings.FrameCount, "WebP のフレーム数");
        var chunks = JpegWebpAc37Tests.WebpChunks(bytes);
        Check.True(!chunks.Contains("ANIM") && !chunks.Contains("ANMF"), "アニメーション WebP ではない");
        var dec = ImageDecoder.Decode(bytes);
        var engine = kit.Frame(doc, 64, 7, 40);
        for (var i = 0; i < engine.Length; i += 4)
        {
            if (engine[i + 3] != 255) continue;
            Check.True(engine.AsSpan(i, 4).SequenceEqual(dec.Rgba.AsSpan(i, 4)), $"ロスレスの WebP の画素 {i / 4} がフレーム 7 と一致");
        }
    }

    /// <summary>GPS と撮影機器を含む EXIF(APP1)と ICC(APP2)を持つ JPEG。</summary>
    private static byte[] JpegWithMetadata(int w, int h)
    {
        var rgba = Img.Pattern(w, h, alpha: false);
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var bmp = SkiaBytes.ToBitmap(w, h, rgba, p3);
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = data.ToArray();
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x02,
            0x01, 0x0F, 0x00, 0x02, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 38, // Make(ASCII 8)の値は 38 バイト目
            0x88, 0x25, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 46, // GPSInfo の IFD は 46 バイト目
            0x00, 0x00, 0x00, 0x00,
            (byte)'G', (byte)'P', (byte)'S', (byte)'-', (byte)'C', (byte)'a', (byte)'m', 0x00,
            0x00, 0x01,
            0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, (byte)'N', 0x00, 0x00, 0x00, // GPSLatitudeRef = "N"
            0x00, 0x00, 0x00, 0x00,
        ];
        var o = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        var len = 2 + 6 + tiff.Length;
        o.Add((byte)(len >> 8));
        o.Add((byte)len);
        o.AddRange("Exif\0\0"u8.ToArray());
        o.AddRange(tiff);
        o.AddRange(jpeg.AsSpan(2).ToArray());
        return o.ToArray();
    }

    [Test]
    public void AC37_7_INV12_ソースのメタデータを5形式のどれにも持ち越さない()
    {
        var src = JpegWithMetadata(120, 90);
        foreach (var t in new[] { "Exif", "GPS", "ICC_PROFILE" }) Check.True(Contains(src, t), $"ソースの JPEG に {t} がある");
        var decoded = ImageDecoder.Decode(src);
        using var kit = new ExportRenderKit();
        kit.Import((decoded.Width, decoded.Height, decoded.Rgba), (decoded.Width, decoded.Height, (byte[])decoded.Rgba.Clone()));
        kit.Session.SetLayerPlacement(1, 10, 5, 50);
        kit.Motion(1, "spin");
        Check.Equal(2, kit.Doc.Layers.Count, "土台ともう 1 枚");
        foreach (var format in ExportFormat.All)
        {
            var (r, bytes) = kit.Export(ExportRenderKit.With(kit.Doc, format, length10: 5, fps: 10));
            Check.True(bytes.Length > 0 && r.Bytes == bytes.Length, format + " を書けた");
            foreach (var t in new[] { "Exif", "GPS", "iCCP", "ICC_PROFILE" }) Check.True(!Contains(bytes, t), $"{format} に {t} が含まれる");
        }
    }

    [Test(Slow = true)]
    public void AC38_6_実際のエンコードで上限に収まる()
    {
        // コードで作った 480×480 の画像と spin(L=2.0・F=20 で 40 フレーム)
        var img = CompositeImg.Make(480, 480, (x, y) =>
        {
            var v = Hash.H(77, (uint)(y * 480 + x));
            return ((x + (int)(v & 31)) % 256, (y * 2 + (int)((v >> 8) & 31)) % 256, ((x ^ y) + (int)((v >> 16) & 15)) % 256, 255);
        });
        using var kit = new ExportRenderKit();
        kit.Import(img);
        kit.Motion(0, "spin");
        var preset = LimitPresets.Find("discord_emoji")!;
        var doc = kit.Doc with { Time = new TimeSettings(20, 20, LoopMode.Infinite, 0) };
        var (output, time) = LimitPresets.Apply(preset, doc.Output, doc.Time);
        doc = doc with { Output = output, Time = time };
        Check.Equal(40, kit.Plan(doc).R0.FrameCount, "40 フレーム");

        using var scheduler = new ExportScheduler(ExportScheduler.DefaultThreadCount);
        var run = new ExportRun(scheduler, scheduler.DefaultParallelism, CancellationToken.None);
        var sw = Stopwatch.StartNew();
        var plan = kit.Plan(doc);
        var r = ExportJob.RunAsync(plan, new GifExportEncoder(), ExportFrames.Factory(kit.Renderer, doc), () => new MemoryStream(), s => s.Dispose(), run)
            .GetAwaiter().GetResult();
        Console.WriteLine($"MEASURE AC38_6 discord_emoji: {sw.ElapsedMilliseconds} ms、{r.Bytes} バイト、段 {r.Step}/{r.StepCount}、試行 {string.Join(",", r.Tried)}、調整「{r.Steps}」");
        Check.True(r.Bytes <= 255_000, $"discord_emoji の出力 {r.Bytes} が 255,000 以下");
        Check.Equal(false, r.SizeFail, "収まった");
        Check.True(MiniGifDecoder.Parse(((MemoryStream)r.Output).ToArray()).Frames.Count >= 1, "GIF として読める");
        if (r.Step > 0) Check.True(r.Steps is not null, "調整の内容が表示される");

        // 上限 2,000 バイトの直接入力: 最後の段の結果を作り、size_fail({limit}=2000、{size}=最後の段の実際のバイト数)
        var tiny = doc with { Output = doc.Output with { Limit = new OutputLimit("bytes", null, 2000) } };
        var p2 = kit.Plan(tiny);
        sw.Restart();
        var f = ExportJob.RunAsync(p2, new GifExportEncoder(), ExportFrames.Factory(kit.Renderer, tiny), () => new MemoryStream(), s => s.Dispose(), run)
            .GetAwaiter().GetResult();
        Console.WriteLine($"MEASURE AC38_6 2000 バイト: {sw.ElapsedMilliseconds} ms、{f.Bytes} バイト、段 {f.Step}/{f.StepCount}、試行 {string.Join(",", f.Tried)}");
        Check.Equal(true, f.SizeFail, "size_fail");
        Check.Equal(f.StepCount, f.Step, "最後の段");
        Check.Equal<long?>(2000, p2.LimitBytes, "{limit}");
        Check.Equal(((MemoryStream)f.Output).Length, f.Bytes, "{size} は最後の段の実際のバイト数");
        Check.True(f.Bytes > 2000, "最後の段も 2000 を超える");
        Check.True(f.Steps is not null, "最後の段の調整の内容");
    }

    [Test(Slow = true)]
    public void AC37_4_測定_GIFの2回の供給の所要時間()
    {
        // Q-I: 同じフレームを 2 回描く。WARP で 128×128・40 フレームの spin(長辺 128)と、2048 相当の負荷の目安に 480
        using var kit = new ExportRenderKit();
        kit.Import(MotionTestKit.SmoothOpaque());
        kit.Motion(0, "spin");
        foreach (var lout in new[] { 128, 480 })
        {
            var doc = ExportRenderKit.With(kit.Doc, "gif", size: new OutputSize("long", lout));
            var plan = kit.Plan(doc);
            var frames = new ExportFrames(kit.Renderer, doc, plan.R0);
            var sw = Stopwatch.StartNew();
            for (var k = 0; k < plan.R0.FrameCount; k++) frames.GetFrame(k, CancellationToken.None);
            var once = sw.ElapsedMilliseconds;
            sw.Restart();
            var gif = Encode(new GifExportEncoder(), frames, plan.R0);
            var total = sw.ElapsedMilliseconds;
            Console.WriteLine($"MEASURE GIF 長辺 {lout}・{plan.R0.FrameCount} フレーム: 1 回描く {once} ms、2 回の供給の書き出し全体 {total} ms({gif.Length} バイト、描いた回数 {frames.Renders})");
            Check.True(gif.Length > 0, "書けた");
        }
    }
}
