// probe-gpu の追加の測定(Q-21 の測り直し、NFR-4 の見積もり): 実際の文書の描画で、スライダーとレイヤーの枠のドラッグをまねる。
// 12 メガピクセル(4000×3000)の土台にルックを 5 個積み、1 つのパラメータか配置を変えるたびに、プロキシ(長辺 2048)を Pbgra32 で読み戻して
// WriteableBitmap に書くまでを測る。実 GPU を使う。画面への表示と、ドラッグの入力の処理は含まない。
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;

namespace GlintLoop.Probes.Gpu;

internal static class DragTiming
{
    private const int BaseWidth = 4000;
    private const int BaseHeight = 3000;
    private const int TopWidth = 1200;
    private const int TopHeight = 900;
    private const int Warmup = 3;
    private const int HardwareFrames = 100;
    private const int WarpFrames = 10;

    // 積むルックの 5 個(先頭の exposure を動かすので、毎回 5 個とも描き直しになる。いちばん重い場合)
    private static readonly string[] Looks = ["exposure", "contrast", "saturation", "tone", "splittone"];

    public static bool Run(IReadOnlyList<(string Label, GpuAdapterRequest Request)> requests)
    {
        Console.WriteLine($"--- 所要時間(Q-21 の測り直し): {BaseWidth}×{BaseHeight} の文書にルック 5 個({string.Join("・", Looks)})---");
        Console.WriteLine("  1 回 = 文書を 1 つ変える(スライダーなら exposure の ev、枠なら最前面のレイヤーの x)→ プロキシ(長辺 2048)を描いて Pbgra32 で読み戻す → WriteableBitmap に書く。");
        Console.WriteLine($"  実 GPU は {Warmup} 回の空回しのあと {HardwareFrames} 回、WARP は {WarpFrames} 回(遅いため)。中央値と p95(並べた 95% の位置、1 始まり)。単位 ms。NFR-4 の目安は p95 が 50 ms 以内。");
        var ok = true;
        var basePixels = MakeImage(BaseWidth, BaseHeight, 0);
        var topPixels = MakeImage(TopWidth, TopHeight, 1);
        foreach (var (label, request) in requests)
        {
            try
            {
                using var rs = RenderService.Create(request);
                var frames = rs.IsWarp ? WarpFrames : HardwareFrames;
                var where = $"{label}(使ったデバイス: {rs.AdapterName}{(rs.IsWarp ? "、WARP" : "")})";
                var slider = Measure(rs, basePixels, topPixels, layered: false, frames);
                Console.WriteLine($"  {where} 1 枚の文書・スライダー: {slider}");
                var sliderL = Measure(rs, basePixels, topPixels, layered: true, frames);
                Console.WriteLine($"  {where} 2 枚の文書・スライダー: {sliderL}");
                var frameDrag = Measure(rs, basePixels, topPixels, layered: true, frames, dragFrame: true);
                Console.WriteLine($"  {where} 2 枚の文書・枠のドラッグ: {frameDrag}");
            }
            catch (GpuFeatureLevelTooLowException e)
            {
                Console.WriteLine($"  {label}: 測らない: 機能レベルが 11_0 未満のため使わない({e.Level})");
            }
            catch (Exception e)
            {
                Console.WriteLine($"  {label}: 測れない: {e.GetType().Name}: {e.Message}");
                ok = false;
            }
        }
        return ok;
    }

    private static string Measure(RenderService rs, byte[] basePixels, byte[] topPixels, bool layered, int frames, bool dragFrame = false)
    {
        var baseSha = new string('a', 64);
        var topSha = new string('b', 64);
        var sources = new FuncSourceImages(sha =>
            sha == baseSha ? new SourceImage(BaseWidth, BaseHeight, basePixels) :
            sha == topSha ? new SourceImage(TopWidth, TopHeight, topPixels) : null);
        var session = new EditorSession(rs.Effects);
        session.AddImportedImages([new ImportedSource(baseSha, "png", basePixels.Length, BaseWidth, BaseHeight, "土台")]);
        if (layered) session.AddImportedImages([new ImportedSource(topSha, "png", topPixels.Length, TopWidth, TopHeight, "重ね")]);
        int? evRow = null;
        foreach (var id in Looks)
        {
            var o = session.AddEffect(Target.OfLayer(0), id);
            if (!o.Changed) throw new InvalidOperationException($"{id} を足せない({o.Notice?.Key})");
            if (id == "exposure") evRow = o.Row;
        }

        using var renderer = new DocumentRenderer(rs, sources);
        WriteableBitmap? bitmap = null;
        var times = new List<double>(frames);
        session.BeginContinuous();
        for (var i = 0; i < Warmup + frames; i++)
        {
            var sw = Stopwatch.StartNew();
            // 毎回ちがう値にする(同じ値に戻すと、規則 5 の再利用に当たって描き直さないため)
            var outcome = dragFrame
                ? session.SetLayerPlacement(1, -60 + i, 0, 25)
                : session.SetEffectParam(Target.OfLayer(0), evRow!.Value, "ev", new NumberValue(-1.5 + i * 0.01));
            if (!outcome.Changed) throw new InvalidOperationException("文書が変わらない");
            var image = renderer.RenderProxyAsync(session.Document!, DocumentPixelFormat.Pbgra32).GetAwaiter().GetResult();
            if (bitmap is null || bitmap.PixelWidth != image.Width || bitmap.PixelHeight != image.Height)
                bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), image.Pixels, image.Width * 4, 0);
            sw.Stop();
            if (i >= Warmup) times.Add(sw.Elapsed.TotalMilliseconds);
        }
        session.EndContinuous();
        times.Sort();
        var median = times.Count % 2 == 0 ? (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2 : times[times.Count / 2];
        var p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        return $"中央値 {median:F1}、p95 {p95:F1}、最大 {times[^1]:F1}({times.Count} 回、出力 {bitmap!.PixelWidth}×{bitmap.PixelHeight})";
    }

    // 滑らかな変化と細かい模様の不透明な画像(決定的)
    private static byte[] MakeImage(int w, int h, int kind)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            px[i] = (byte)(x * 255 / (w - 1));
            px[i + 1] = (byte)(y * 255 / (h - 1));
            px[i + 2] = (byte)(((x >> 3) ^ (y >> 3) ^ kind) % 2 == 0 ? 64 : 192);
            px[i + 3] = 255;
        }
        return px;
    }
}
