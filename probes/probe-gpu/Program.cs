// probe-gpu: 仕様書 §8.4 の測定 1(GPU と Direct3D 11)。作者が自分の PC で実行し、出力をそのまま貼る。
// アダプター一覧・機能レベル・形式の対応フラグ・AC-1 の検査・d3dcompiler_47.dll・Vortice の版・2048×1365 の所要時間と、文書のドラッグの所要時間(Q-21)を出す。
// 実 GPU を使う。製品(src/)からは参照しない。WriteableBitmap は WPF のものをこの STA スレッドで使う。
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.Render;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlintLoop.Probes.Gpu;

public static class Program
{
    private const int TimingWidth = 2048;
    private const int TimingHeight = 1365;
    private const int Warmup = 5;
    private const int Iterations = 100;

    [STAThread]
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var exitCode = 0;
        Console.WriteLine("=== probe-gpu(GlintLoop 測定 1: GPU と Direct3D 11)===");
        Console.WriteLine($"実行日時: {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}({Environment.OSVersion.Version})");
        Console.WriteLine($".NET: {RuntimeInformation.FrameworkDescription}、{RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        PrintPackageVersions();
        PrintD3DCompiler();

        IReadOnlyList<GpuAdapterInfo> adapters;
        long? highPerf;
        try
        {
            adapters = GpuAdapters.Enumerate();
            highPerf = GpuAdapters.HighPerformanceLuid();
        }
        catch (Exception e)
        {
            Console.WriteLine($"アダプターを列挙できない: {e.GetType().Name}: {e.Message}");
            return 1;
        }

        Console.WriteLine("--- アダプター一覧(EnumAdapters1 の順)---");
        foreach (var a in adapters)
        {
            var mark = a.Luid == highPerf ? "  ← 高性能の先頭(既定で使う)" : "";
            Console.WriteLine($"[{a.Index}] {a.Description}{mark}");
            Console.WriteLine($"    VendorId 0x{a.VendorId:X4}  DeviceId 0x{a.DeviceId:X4}  専用 VRAM {a.DedicatedVideoMemory / (1024 * 1024)} MB  共有 {a.SharedSystemMemory / (1024 * 1024)} MB  ソフトウェア: {(a.IsSoftware ? "はい" : "いいえ")}");
        }
        if (highPerf is null) Console.WriteLine("(IDXGIFactory6 が無く、高性能の先頭を取れない)");
        Console.WriteLine();

        var requests = new List<(string Label, GpuAdapterRequest Request)> { ("WARP", GpuAdapterRequest.Warp) };
        requests.AddRange(adapters.Select(a => ($"[{a.Index}] {a.Description}", GpuAdapterRequest.ByIndex(a.Index))));

        using var thread = new RenderThread("GlintLoop.Render.Probe");
        foreach (var (label, request) in requests)
        {
            Console.WriteLine($"--- デバイス: {label} ---");
            try
            {
                var lines = thread.Invoke(() => DescribeDevice(request));
                foreach (var line in lines) Console.WriteLine(line);
                if (lines.Any(l => l.Contains("不一致", StringComparison.Ordinal))) exitCode = 1;
            }
            catch (GpuFeatureLevelTooLowException e)
            {
                // 11_0 未満は仕様どおりに使わないだけ(NFR-11)で、失敗ではない。終了コードは変えない
                Console.WriteLine($"  使わない: 機能レベルが 11_0 未満のため使わない(このアダプターは {LevelName(e.Level)} まで。NFR-11。製品では WARP になる)");
            }
            catch (Exception e)
            {
                // デバイスを作れないアダプターがある(WARP を含む)のは測定の失敗なので、終了コードを 1 にする
                Console.WriteLine($"  作れない: {e.GetType().Name}: {e.Message}");
                exitCode = 1;
            }
            Console.WriteLine();
        }

        Console.WriteLine("--- FR-94 の起動時の選択(高性能の先頭で作って自己検査。だめなら WARP)---");
        try
        {
            var text = thread.Invoke(() =>
            {
                var (device, renderer, usedWarp, reason) = GpuSelfTest.CreateWithFallback(GpuAdapterRequest.HighPerformance);
                using (device)
                using (renderer)
                {
                    return $"  使うデバイス: {device.Name}、ソフトウェア描画: {(usedWarp ? "はい" : "いいえ")}、機能レベル {Level(device)}" +
                           (reason is null ? "" : $"、理由: {reason}");
                }
            });
            Console.WriteLine(text);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  WARP も作れない: {e.GetType().Name}: {e.Message}");
            exitCode = 1;
        }
        Console.WriteLine();

        Console.WriteLine($"--- 所要時間: {TimingWidth}×{TimingHeight} の RGBA16F に 3 パス → 8 ビットへの変換 → CPU へ読み戻し → WriteableBitmap へ書き込み ---");
        Console.WriteLine($"  ({Warmup} 回の空回しのあと {Iterations} 回。中央値は並べた 50 番目と 51 番目の平均、p95 は並べた 95 番目(1 始まり)。単位 ms)");
        Console.WriteLine("  パス 1 は定数の塗り(テクスチャの入力なし)、パス 2・3 は前の出力を Load、4 パス目で sRGB の B8G8R8A8_UNORM にする。");
        Console.WriteLine("  GPU+読み戻し は専用スレッドでの描画から byte[] への写しまで、書き込み は STA スレッドでの WritePixels。画面への表示は含まない。");
        foreach (var (label, request) in requests)
        {
            try
            {
                var (text, pixelMatched) = MeasureTiming(thread, request);
                Console.WriteLine($"  {label}: {text}");
                // 結果の画素が期待と違うのは描画の失敗なので、終了コードを 1 にする
                if (!pixelMatched) exitCode = 1;
            }
            catch (GpuFeatureLevelTooLowException e)
            {
                Console.WriteLine($"  {label}: 測らない: 機能レベルが 11_0 未満のため使わない({LevelName(e.Level)})");
            }
            catch (Exception e)
            {
                // 測定の途中で失敗したのは測定の失敗なので、デバイス一覧と同じく終了コードを 1 にする(11_0 未満は上で 0 のまま)
                Console.WriteLine($"  {label}: 測れない: {e.GetType().Name}: {e.Message}");
                exitCode = 1;
            }
        }

        Console.WriteLine();
        // Q-21 の測り直し(Phase 2): 実際の文書の描画でドラッグをまねる。測れないアダプターがあれば終了コードを 1 にする
        if (!DragTiming.Run(requests)) exitCode = 1;

        Console.WriteLine();
        Console.WriteLine("=== probe-gpu ここまで ===");
        return exitCode;
    }

    private static void PrintPackageVersions()
    {
        Console.WriteLine("--- パッケージの版 ---");
        var assemblies = new (string Name, Assembly Assembly)[]
        {
            ("Vortice.Direct3D11", typeof(ID3D11Device).Assembly),
            ("Vortice.DXGI", typeof(IDXGIFactory1).Assembly),
            ("Vortice.DirectX", typeof(Vortice.Direct3D.FeatureLevel).Assembly),
            ("Vortice.Mathematics", typeof(Vortice.Mathematics.Color4).Assembly),
            ("SharpGen.Runtime", typeof(SharpGen.Runtime.Result).Assembly),
            ("SharpGen.Runtime.COM", typeof(SharpGen.Runtime.ComObject).Assembly),
        };
        foreach (var (name, asm) in assemblies)
        {
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(不明)";
            // 版の後ろの +コミット は長いので切る
            var plus = info.IndexOf('+');
            Console.WriteLine($"{name}: {(plus > 0 ? info[..plus] : info)}(アセンブリ {asm.GetName().Version})");
        }
        Console.WriteLine();
    }

    private static void PrintD3DCompiler()
    {
        Console.WriteLine("--- d3dcompiler_47.dll ---");
        var path = Path.Combine(Environment.SystemDirectory, "d3dcompiler_47.dll");
        if (!File.Exists(path))
        {
            Console.WriteLine($"{path}: 無い");
        }
        else
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            Console.WriteLine($"{path}: ある  ファイルの版 {v.FileVersion}  製品の版 {v.ProductVersion}  {new FileInfo(path).Length} バイト");
        }
        Console.WriteLine();
    }

    private static List<string> DescribeDevice(GpuAdapterRequest request)
    {
        var lines = new List<string>();
        using var device = GpuDevice.Create(request);
        lines.Add($"  機能レベル: {Level(device)}");
        foreach (var format in new[] { Format.R16G16B16A16_Float, Format.R8G8B8A8_UNorm, Format.B8G8R8A8_UNorm })
        {
            var s = device.CheckFormatSupport(format);
            var key = new[] { FormatSupport.Texture2D, FormatSupport.RenderTarget, FormatSupport.Blendable, FormatSupport.ShaderLoad, FormatSupport.ShaderSample, FormatSupport.CpuLockable }
                .Select(f => $"{f}={((s & f) != 0 ? "○" : "×")}");
            lines.Add($"  {format}: 0x{(uint)s:X8}  {string.Join(" ", key)}");
            lines.Add($"    全フラグ: {s}");
        }

        using var renderer = new PassRenderer(device);
        foreach (var r in new[] { GpuSelfTest.StartupCheck(renderer), GpuSelfTest.ThreePass(renderer), GpuSelfTest.Over(renderer) })
        {
            lines.Add($"  {r.Name}: {(r.Passed ? "一致" : "不一致")}(期待 {Fmt(r.Expected)}、最大の差 {r.MaxError:G4}、許容 {GpuCheckResult.Tolerance})");
            var distinct = Enumerable.Range(0, GpuSelfTest.Size * GpuSelfTest.Size).Select(i => Fmt(r.Pixel(i))).Distinct().ToArray();
            lines.Add(distinct.Length == 1
                ? $"    16 画素すべて {distinct[0]}"
                : $"    16 画素: {string.Join(" ", Enumerable.Range(0, 16).Select(i => Fmt(r.Pixel(i))))}");
        }
        return lines;
    }

    // 所要時間の測定の結果の先頭の画素の期待(BGRA の並び)。根拠: 3 パスで (0.5, 0.25, 2.0, 1.0) → ×0.5 → (0.25, 0.125, 1.0) → +0.5 → (0.75, 0.625, 1.5)、
    // どれも RGBA16F でちょうど表せる。α = 1 なので straight でも同じ値で、0〜1 に切ると (0.75, 0.625, 1.0)。§9.4 の enc で 0.88083, 0.81237, 1.0、
    // ×255 で 224.61, 207.15, 255.0 → 最も近い整数で R225 G207 B255、α は 255。いちばん近い .5 の境目まで 0.11 あり、float32 の pow の誤差では動かない。
    private static readonly byte[] TimingExpectedBgra = [255, 207, 225, 255];

    private static (string Text, bool PixelMatched) MeasureTiming(RenderThread thread, GpuAdapterRequest request)
    {
        var stride = TimingWidth * 4;
        var buffer = new byte[stride * TimingHeight];
        var bitmap = new WriteableBitmap(TimingWidth, TimingHeight, 96, 96, PixelFormats.Bgra32, null);
        var rect = new Int32Rect(0, 0, TimingWidth, TimingHeight);

        // デバイスと描き先は専用スレッドの上で作り、測定のあいだ使い回す。途中で例外になったら、それまでに作った物をこのスレッドの上で放してから投げ直す
        var state = thread.Invoke(() =>
        {
            GpuDevice? device = null;
            PassRenderer? renderer = null;
            GpuTexture? a = null, b = null, display = null;
            try
            {
                device = GpuDevice.Create(request);
                renderer = new PassRenderer(device);
                a = renderer.CreateTarget(TimingWidth, TimingHeight);
                b = renderer.CreateTarget(TimingWidth, TimingHeight);
                display = renderer.CreateTarget(TimingWidth, TimingHeight, PassRenderer.DisplayFormat);
                return (device, renderer, a, b, display);
            }
            catch
            {
                display?.Dispose();
                b?.Dispose();
                a?.Dispose();
                renderer?.Dispose();
                device?.Dispose();
                throw;
            }
        });

        var gpu = new List<double>();
        var write = new List<double>();
        var total = new List<double>();
        try
        {
            for (var i = 0; i < Warmup + Iterations; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                thread.Invoke(() =>
                {
                    var (_, r, a, b, display) = state;
                    r.Fill(a, GpuSelfTest.SelfTestColor);
                    r.RgbAffine(a, b, new Vector3(0.5f), Vector3.Zero);
                    r.RgbAffine(b, a, Vector3.One, new Vector3(0.5f));
                    r.EncodeBgra8(a, display);
                    r.ReadbackBgra8(display, buffer, stride);
                });
                var t1 = Stopwatch.GetTimestamp();
                bitmap.WritePixels(rect, buffer, stride, 0);
                var t2 = Stopwatch.GetTimestamp();
                if (i < Warmup) continue;
                gpu.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                write.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
                total.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
            }
        }
        finally
        {
            thread.Invoke(() =>
            {
                state.display.Dispose();
                state.b.Dispose();
                state.a.Dispose();
                state.renderer.Dispose();
                state.device.Dispose();
            });
        }

        // 結果の中身も 1 画素だけ確かめる(0.75, 0.625, 1.5 → straight の sRGB 8 ビットで R225 G207 B255。根拠は TimingExpectedBgra)
        var px = $"B{buffer[0]} G{buffer[1]} R{buffer[2]} A{buffer[3]}";
        var matched = buffer.AsSpan(0, 4).SequenceEqual(TimingExpectedBgra);
        var expected = $"B{TimingExpectedBgra[0]} G{TimingExpectedBgra[1]} R{TimingExpectedBgra[2]} A{TimingExpectedBgra[3]}";
        var verdict = matched ? $"一致(期待 {expected})" : $"不一致(期待 {expected})";
        return ($"合計 中央値 {Median(total):F2} / p95 {P95(total):F2}  (GPU+読み戻し {Median(gpu):F2} / {P95(gpu):F2}、書き込み {Median(write):F2} / {P95(write):F2})  先頭の画素 {px} {verdict}", matched);
    }

    private static double Median(List<double> values)
    {
        var s = values.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    private static double P95(List<double> values)
    {
        var s = values.Order().ToArray();
        var index = (int)Math.Ceiling(0.95 * s.Length) - 1;
        return s[Math.Clamp(index, 0, s.Length - 1)];
    }

    private static string Level(GpuDevice device) => LevelName(device.FeatureLevel);

    private static string LevelName(Vortice.Direct3D.FeatureLevel level) => level.ToString().Replace("Level_", "");

    private static string Fmt(Vector4 v) => $"({v.X:0.####}, {v.Y:0.####}, {v.Z:0.####}, {v.W:0.####})";
}
