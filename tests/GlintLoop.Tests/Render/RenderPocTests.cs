// 描画の PoC の検査(WARP だけを使う。D-39・INV-18): AC-1 の 3 パスと over、FR-94 の自己検査、sRGB 8 ビットへの変換。
// INV-5(GPU は専用の 1 スレッドだけ)と INV-3(シェーダーは埋め込みリソースだけ、D3DCompiler を参照しない)も確かめる。
// 実 GPU での同じ検査は probe-gpu が行う(§8.4 の測定 1)。
using System.IO;
using System.Numerics;
using GlintLoop.Core.Numerics;
using GlintLoop.Render;
using GlintLoop.Tests.Framework;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlintLoop.Tests.Render;

public sealed class RenderPocTests
{
    /// <summary>WARP のデバイスと PassRenderer を専用スレッドの上に作り、body をそのスレッドで実行する。</summary>
    private static T OnWarp<T>(Func<GpuDevice, PassRenderer, T> body)
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        return thread.Invoke(() =>
        {
            using var device = GpuDevice.Create(GpuAdapterRequest.Warp);
            using var renderer = new PassRenderer(device);
            return body(device, renderer);
        });
    }

    // 仕様書の数値をそのまま書く(実装の定数から取らない)。FR-94 の塗る色、AC-1 の 3 パスの結果と over の下・上・結果、許容差
    private static readonly Vector4 SpecFr94Fill = new(0.5f, 0.25f, 2.0f, 1.0f);
    private static readonly Vector4 SpecAc1ThreePass = new(0.75f, 0.625f, 1.5f, 1.0f);
    private static readonly Vector4 SpecAc1OverBase = new(0.2f, 0.2f, 0.2f, 0.5f);
    private static readonly Vector4 SpecAc1OverTop = new(0.3f, 0.0f, 0.0f, 0.3f);
    private static readonly Vector4 SpecAc1OverResult = new(0.44f, 0.14f, 0.14f, 0.65f);
    private const float SpecTolerance = 0.002f;
    private const int SpecSize = 4;

    /// <summary>読み戻した 4×4 の全画素を、テストに書いた仕様の値 expected と比べる。</summary>
    private static void CheckAllPixels(Vector4 expected, GpuCheckResult r)
    {
        Check.Equal(SpecSize * SpecSize * 4, r.Pixels.Length, $"{r.Name}: 読み戻した成分の数");
        for (var i = 0; i < SpecSize * SpecSize; i++)
        {
            var p = r.Pixel(i);
            Check.Near(expected.X, p.X, SpecTolerance, $"{r.Name}: 画素 {i} の R");
            Check.Near(expected.Y, p.Y, SpecTolerance, $"{r.Name}: 画素 {i} の G");
            Check.Near(expected.Z, p.Z, SpecTolerance, $"{r.Name}: 画素 {i} の B");
            Check.Near(expected.W, p.W, SpecTolerance, $"{r.Name}: 画素 {i} の A");
        }
        Check.Equal(expected, r.Expected, $"{r.Name}: 結果に入っている期待値");
        Check.True(r.Passed, $"{r.Name}: Passed が false(最大の差 {r.MaxError})");
    }

    [Test]
    public void AC1_FR94_GpuSelfTestの色と期待値と許容差が仕様の数値と同じ()
    {
        Check.Equal(SpecFr94Fill, GpuSelfTest.SelfTestColor, "FR-94 の塗る色");
        Check.Equal(SpecAc1ThreePass, GpuSelfTest.ThreePassExpected, "AC-1 の 3 パスの結果");
        Check.Equal(SpecAc1OverBase, GpuSelfTest.OverBase, "AC-1 の over の下");
        Check.Equal(SpecAc1OverTop, GpuSelfTest.OverTop, "AC-1 の over の上");
        Check.Equal(SpecAc1OverResult, GpuSelfTest.OverExpected, "AC-1 の over の結果");
        Check.Equal(SpecTolerance, GpuCheckResult.Tolerance, "許容差");
        Check.Equal(SpecSize, GpuSelfTest.Size, "レンダーターゲットの辺");
    }

    [Test]
    public void AC1_WARPのデバイスでRGBA16Fを描ける()
    {
        var (isWarp, level, f16) = OnWarp((d, _) => (d.IsWarp, d.FeatureLevel, d.CheckFormatSupport(Format.R16G16B16A16_Float)));
        Check.True(isWarp, "WARP のデバイスになっていない");
        Check.True(level >= FeatureLevel.Level_11_0, $"機能レベルが 11_0 未満: {level}(NFR-11)");
        Check.True((f16 & FormatSupport.RenderTarget) != 0, $"R16G16B16A16_FLOAT がレンダーターゲットに使えない: {f16}");
        Check.True((f16 & FormatSupport.Blendable) != 0, $"R16G16B16A16_FLOAT がブレンドできない: {f16}");
    }

    [Test]
    public void NFR11_要求する機能レベルは11_1と11_0だけで下限は11_0()
    {
        Check.SequenceEqual(new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, GpuDevice.FeatureLevels, "要求する機能レベル");
        Check.Equal(FeatureLevel.Level_11_0, GpuDevice.MinimumFeatureLevel, "機能レベルの下限");
    }

    [Test]
    public void NFR11_WARPを機能レベル10_xに絞って作ると理由付きで拒む()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var e = thread.Invoke(() => Check.Throws<GpuFeatureLevelTooLowException>(
            () => GpuDevice.Create(GpuAdapterRequest.Warp, [FeatureLevel.Level_10_1, FeatureLevel.Level_10_0]).Dispose(),
            "10_1・10_0 だけを要求した WARP"));
        Check.Equal(FeatureLevel.Level_10_1, e.Level, "拒んだときの機能レベル");
        Check.Equal(GpuAdapterRequest.Warp, e.Request, "拒んだときの要求");
        Check.True(e.Message.Contains("11_0 未満", StringComparison.Ordinal), $"理由に 11_0 未満が無い: {e.Message}");
        Check.True(e.Message.Contains("10_1", StringComparison.Ordinal), $"理由に機能レベルが無い: {e.Message}");
    }

    [Test]
    public void NFR11_FR94_機能レベルが11_0未満のアダプターは理由を分けてWARPにする()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var (usedWarp, isWarp, level, reason) = thread.Invoke(() =>
        {
            // 高性能の指定の代わりに、10_x に絞った WARP を作らせる(10_x しか無いアダプターの代わり)
            var (device, renderer, warp, why) = GpuSelfTest.CreateWithFallback(
                GpuAdapterRequest.HighPerformance,
                _ => GpuDevice.Create(GpuAdapterRequest.Warp, [FeatureLevel.Level_10_1, FeatureLevel.Level_10_0]));
            using (device)
            using (renderer)
            {
                return (warp, device.IsWarp, device.FeatureLevel, why);
            }
        });
        Check.True(usedWarp && isWarp, "WARP に切り替わっていない");
        Check.True(level >= FeatureLevel.Level_11_0, $"切り替えた WARP の機能レベルが 11_0 未満: {level}");
        Check.True(reason is not null && reason.StartsWith("機能レベルが 11_0 未満(10_1)", StringComparison.Ordinal),
            $"理由がほかの失敗と区別できない: {reason}");
    }

    // 実機の 10_x アダプターの代わり: 11_0 以上を含む要求は DXGI_ERROR_UNSUPPORTED で失敗させ、10_x だけの要求は本物で作る(WARP は 10_1 になる)
    private static readonly Result DxgiErrorUnsupported = new(unchecked((int)0x887A0004));

    /// <summary>11_0 以上を要求されたら失敗し、それ以外は本物の作成関数に回す偽の作成関数(lowAlsoFails なら 10_x も失敗)。受けた要求を calls に足す。</summary>
    private static GpuDevice.CreateDeviceFunc TooLowAdapter(List<FeatureLevel[]> calls, bool lowAlsoFails = false) =>
        (GpuAdapterHandle adapter, DriverType driverType, FeatureLevel[] levels,
         out ID3D11Device? device, out FeatureLevel level, out ID3D11DeviceContext? context) =>
        {
            calls.Add(levels.ToArray());
            if (lowAlsoFails || levels.Any(l => l >= FeatureLevel.Level_11_0))
            {
                device = null;
                level = default;
                context = null;
                return DxgiErrorUnsupported;
            }
            return GpuDevice.CreateD3D11Device(adapter, driverType, levels, out device, out level, out context);
        };

    [Test]
    public void NFR11_11_0以上で作れないアダプターは10_xで作り直して理由付きで拒む()
    {
        var calls = new List<FeatureLevel[]>();
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var e = thread.Invoke(() => Check.Throws<GpuFeatureLevelTooLowException>(
            () => GpuDevice.Create(GpuAdapterRequest.Warp, TooLowAdapter(calls)).Dispose(),
            "11_x で作れず 10_x で作れるアダプター"));
        Check.Equal(FeatureLevel.Level_10_1, e.Level, "拒んだときの機能レベル");
        Check.True(e.Message.Contains("11_0 未満", StringComparison.Ordinal), $"理由に 11_0 未満が無い: {e.Message}");
        // 1 回目は仕様の 11_1・11_0、2 回目は原因を分けるための 10_1・10_0(NFR-11)
        Check.Equal(2, calls.Count, "作成関数を呼んだ回数");
        Check.SequenceEqual(new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, calls[0], "1 回目に要求した機能レベル");
        Check.SequenceEqual(new[] { FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }, calls[1], "2 回目に要求した機能レベル");
    }

    [Test]
    public void NFR11_10_xでも作れないアダプターは元のHRESULTの失敗にする()
    {
        var calls = new List<FeatureLevel[]>();
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var e = thread.Invoke(() => Check.Throws<InvalidOperationException>(
            () => GpuDevice.Create(GpuAdapterRequest.Warp, TooLowAdapter(calls, lowAlsoFails: true)).Dispose(),
            "11_x でも 10_x でも作れないアダプター"));
        Check.True(e is not GpuFeatureLevelTooLowException, $"11_0 未満の理由になっている: {e.Message}");
        Check.True(e.Message.Contains("887A0004", StringComparison.Ordinal), $"元の HRESULT が無い: {e.Message}");
        Check.Equal(2, calls.Count, "作成関数を呼んだ回数");
    }

    [Test]
    public void NFR11_10_xでの作り直しは元のアダプターとDriverTypeで行う()
    {
        // 番号で指定したアダプター(DriverType.Unknown で渡す)の要求で、11_x の失敗のあとの 10_x の作り直しも同じアダプター・同じ DriverType になること。
        // DXGI を呼ばないよう、アダプターを開く関数は偽(LUID だけを持つ GpuAdapterHandle を返す)にする。番号はどの PC にも無い大きさにし、本物の DXGI に回ったら別の例外で落ちる。
        // 偽の作成関数は 11_x を失敗にし、10_x は WARP で作る(lowAlsoFails なら 10_x も失敗)。渡されたアダプターではデバイスを作らない。
        const long fakeLuid = 0x0000_7E57_0000_1234;
        var fakeAdapter = new GpuAdapterHandle(null, fakeLuid);
        var request = GpuAdapterRequest.ByIndex(9999);
        foreach (var lowAlsoFails in new[] { false, true })
        {
            var opened = new List<GpuAdapterRequest>();
            var calls = new List<(GpuAdapterHandle Adapter, DriverType DriverType, FeatureLevel[] Levels)>();
            GpuAdapterHandle open(GpuAdapterRequest r)
            {
                opened.Add(r);
                return fakeAdapter;
            }
            GpuDevice.CreateDeviceFunc fake = (GpuAdapterHandle adapter, DriverType driverType, FeatureLevel[] levels,
                out ID3D11Device? device, out FeatureLevel level, out ID3D11DeviceContext? context) =>
            {
                calls.Add((adapter, driverType, levels.ToArray()));
                if (lowAlsoFails || levels.Any(l => l >= FeatureLevel.Level_11_0))
                {
                    device = null;
                    level = default;
                    context = null;
                    return DxgiErrorUnsupported;
                }
                return GpuDevice.CreateD3D11Device(GpuAdapterHandle.None, DriverType.Warp, levels, out device, out level, out context);
            };
            using var thread = new RenderThread("GlintLoop.Render.Test");
            var e = thread.Invoke(() => Check.Throws<InvalidOperationException>(
                () => GpuDevice.Create(request, fake, open).Dispose(),
                $"番号 {request.Index} の要求・lowAlsoFails={lowAlsoFails}"));
            var what = $"lowAlsoFails={lowAlsoFails}";
            if (lowAlsoFails)
            {
                Check.True(e is not GpuFeatureLevelTooLowException, $"{what}: 11_0 未満の理由になっている: {e.Message}");
                Check.True(e.Message.Contains("887A0004", StringComparison.Ordinal), $"{what}: 元の HRESULT が無い: {e.Message}");
            }
            else
            {
                Check.True(e is GpuFeatureLevelTooLowException, $"{what}: 11_0 未満の理由で拒んでいない: {e.GetType().Name}: {e.Message}");
                var low = (GpuFeatureLevelTooLowException)e;
                Check.Equal(request, low.Request, $"{what}: 拒んだときの要求");
                Check.Equal(FeatureLevel.Level_10_1, low.Level, $"{what}: 拒んだときの機能レベル");
            }
            Check.SequenceEqual(new[] { request, request }, opened, $"{what}: アダプターを開いた要求");
            Check.Equal(2, calls.Count, $"{what}: 作成関数を呼んだ回数");
            Check.SequenceEqual(new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, calls[0].Levels, $"{what}: 1 回目に要求した機能レベル");
            Check.SequenceEqual(new[] { FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }, calls[1].Levels, $"{what}: 2 回目に要求した機能レベル");
            for (var i = 0; i < 2; i++)
            {
                Check.Equal(fakeAdapter, calls[i].Adapter, $"{what}: {i + 1} 回目に渡したアダプター");
                Check.Equal<long?>(fakeLuid, calls[i].Adapter.Luid, $"{what}: {i + 1} 回目に渡したアダプターの LUID");
                Check.Equal(DriverType.Unknown, calls[i].DriverType, $"{what}: {i + 1} 回目の DriverType");
            }
        }
    }

    [Test]
    public void NFR11_AC1_11_xで作れず10_xのアダプターは機能レベルの理由でWARPにする()
    {
        var calls = new List<FeatureLevel[]>();
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var (usedWarp, isWarp, level, reason) = thread.Invoke(() =>
        {
            var (device, renderer, warp, why) = GpuSelfTest.CreateWithFallback(
                GpuAdapterRequest.HighPerformance,
                _ => GpuDevice.Create(GpuAdapterRequest.Warp, TooLowAdapter(calls)));
            using (device)
            using (renderer)
            {
                return (warp, device.IsWarp, device.FeatureLevel, why);
            }
        });
        Check.True(usedWarp && isWarp, "WARP に切り替わっていない");
        Check.True(level >= FeatureLevel.Level_11_0, $"切り替えた WARP の機能レベルが 11_0 未満: {level}");
        Check.True(reason is not null && reason.StartsWith("機能レベルが 11_0 未満(10_1)", StringComparison.Ordinal),
            $"理由がほかの失敗と区別できない: {reason}");
    }

    /// <summary>4×4 の全画素が c の読み戻し(RGBA の並び)。</summary>
    private static float[] UniformPixels(Vector4 c) =>
        Enumerable.Range(0, SpecSize * SpecSize).SelectMany(_ => new[] { c.X, c.Y, c.Z, c.W }).ToArray();

    [Test]
    public void AC1_FR94_自己検査の判定は0_002を超える差やNaNで落ちる()
    {
        // 許容差 0.002 の内側(0.0015)は通る
        var inside = UniformPixels(SpecFr94Fill);
        for (var i = 0; i < inside.Length; i++) inside[i] += i % 2 == 0 ? 0.0015f : -0.0015f;
        var ok = GpuSelfTest.Evaluate("内側", SpecFr94Fill, inside);
        Check.True(ok.Passed, $"差 0.0015 で落ちた(最大の差 {ok.MaxError})");
        Check.Near(0.0015, ok.MaxError, 0.0001, "差 0.0015 の最大の差");

        // 1 画素の 1 成分だけが 0.003 外れる(上にも下にも)と落ちる
        foreach (var (component, delta) in new[] { (5 * 4 + 1, 0.003f), (11 * 4 + 2, -0.003f), (15 * 4 + 3, 0.003f) })
        {
            var pixels = UniformPixels(SpecFr94Fill);
            pixels[component] += delta;
            var r = GpuSelfTest.Evaluate("外れ", SpecFr94Fill, pixels);
            Check.True(!r.Passed, $"成分 {component} が {delta} 外れても通った");
            Check.Near(0.003, r.MaxError, 0.0001, $"成分 {component} の最大の差");
        }

        // NaN の画素は差を数えられないので落ちる
        var nan = UniformPixels(SpecFr94Fill);
        nan[7] = float.NaN;
        Check.True(!GpuSelfTest.Evaluate("NaN", SpecFr94Fill, nan).Passed, "NaN があっても通った");
    }

    [Test]
    public void AC1_FR94_自己検査に通るアダプターはそのまま使う()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var (usedWarp, same, reason) = thread.Invoke(() =>
        {
            GpuDevice? preferred = null;
            var (device, renderer, warp, why) = GpuSelfTest.CreateWithFallback(
                GpuAdapterRequest.HighPerformance,
                _ => preferred = GpuDevice.Create(GpuAdapterRequest.Warp));
            using (device)
            using (renderer)
            {
                return (warp, ReferenceEquals(preferred, device), why);
            }
        });
        Check.True(!usedWarp, "自己検査に通ったのに WARP に切り替えた");
        Check.True(same, "要求のアダプターのデバイスを返していない");
        Check.True(reason is null, $"理由が入っている: {reason}");
    }

    [Test]
    public void AC1_FR94_自己検査に落ちたアダプターは理由を付けてWARPにする()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var (usedWarp, isWarp, same, preferredDisposed, reason) = thread.Invoke(() =>
        {
            GpuDevice? preferred = null;
            var (device, renderer, warp, why) = GpuSelfTest.CreateWithFallback(
                GpuAdapterRequest.HighPerformance,
                _ => preferred = GpuDevice.Create(GpuAdapterRequest.Warp),
                r =>
                {
                    // 本物の検査で読み戻した画素の 1 成分を 0.01 ずらす(R16G16B16A16_FLOAT を正しく描けないアダプターの代わり)
                    var real = GpuSelfTest.StartupCheck(r);
                    var pixels = real.Pixels.ToArray();
                    pixels[6] += 0.01f;
                    return GpuSelfTest.Evaluate(real.Name, SpecFr94Fill, pixels);
                });
            using (device)
            using (renderer)
            {
                var disposed = false;
                try { preferred!.EnsureOwnerThread(); }
                catch (ObjectDisposedException) { disposed = true; }
                return (warp, device.IsWarp, ReferenceEquals(preferred, device), disposed, why);
            }
        });
        Check.True(usedWarp && isWarp, "自己検査に落ちたのに WARP に切り替わっていない");
        Check.True(!same, "落ちたデバイスをそのまま返した");
        Check.True(preferredDisposed, "落ちたデバイスを破棄していない");
        Check.True(reason is not null && reason.StartsWith("自己検査に落ちた(最大の差 ", StringComparison.Ordinal),
            $"理由が自己検査の失敗になっていない: {reason}");
    }

    [Test]
    public void AC1_WARPで3パスの結果が一致し1を超える値が切られない()
    {
        var r = OnWarp((_, renderer) => GpuSelfTest.ThreePass(renderer));
        CheckAllPixels(SpecAc1ThreePass, r);
        for (var i = 0; i < SpecSize * SpecSize; i++)
            Check.True(r.Pixel(i).Z > 1.4f, $"画素 {i} の B が 1.0 に切られている: {r.Pixel(i).Z}");
    }

    [Test]
    public void AC1_WARPでプリマルチプライのoverのブレンドが一致する()
    {
        CheckAllPixels(SpecAc1OverResult, OnWarp((_, renderer) => GpuSelfTest.Over(renderer)));
    }

    [Test]
    public void AC1_FR94_起動時の自己検査がWARPで通る()
    {
        CheckAllPixels(SpecFr94Fill, OnWarp((_, renderer) => GpuSelfTest.StartupCheck(renderer)));
    }

    [Test]
    public void AC1_FR94_WARPを要求したときはWARPのデバイスを返す()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var (usedWarp, isWarp, reason) = thread.Invoke(() =>
        {
            var (device, renderer, warp, why) = GpuSelfTest.CreateWithFallback(GpuAdapterRequest.Warp);
            using (device)
            using (renderer)
            {
                return (warp, device.IsWarp, why);
            }
        });
        Check.True(usedWarp && isWarp, "WARP になっていない");
        Check.True(reason is null, $"理由が入っている: {reason}");
    }

    [Test]
    public void AC1_sRGB8ビットへの変換はBGRAの並びでstraightになる()
    {
        // 画素の値は線形光・プリマルチプライ。straight の sRGB (200, 100, 50)、α 0.4 を作る
        var c = new Vector3((float)Srgb.Lin(200 / 255.0), (float)Srgb.Lin(100 / 255.0), (float)Srgb.Lin(50 / 255.0));
        const float alpha = 0.4f;
        var bytes = OnWarp((_, renderer) =>
        {
            using var src = renderer.CreateTarget(4, 2);
            using var dst = renderer.CreateTarget(4, 2, PassRenderer.DisplayFormat);
            renderer.Fill(src, new Vector4(c * alpha, alpha));
            renderer.EncodeBgra8(src, dst);
            var buffer = new byte[4 * 4 * 2];
            renderer.ReadbackBgra8(dst, buffer, 16);
            return buffer;
        });
        for (var i = 0; i < 8; i++)
        {
            Check.InRange(49, 51, bytes[i * 4 + 0], $"画素 {i} の B");
            Check.InRange(99, 101, bytes[i * 4 + 1], $"画素 {i} の G");
            Check.InRange(199, 201, bytes[i * 4 + 2], $"画素 {i} の R");
            Check.InRange(101, 103, bytes[i * 4 + 3], $"画素 {i} の A");
        }
    }

    [Test]
    public void 読み戻しのステージングは大きさが変わり続けても上限の数までしか持たない()
    {
        var (count, before, after) = OnWarp((_, renderer) =>
        {
            float[] Read(int w, int h, Vector4 color)
            {
                using var t = renderer.CreateTarget(w, h);
                renderer.Fill(t, color);
                return renderer.ReadbackRgba16F(t);
            }
            var first = Read(4, 4, new Vector4(0.25f, 0.5f, 0.75f, 1f));
            for (var w = 5; w < 5 + PassRenderer.StagingCapacity + 2; w++) Read(w, 1, Vector4.One);
            // 放した大きさをもう一度読む(作り直したステージングで正しく読めること)
            return (renderer.StagingCount, first, Read(4, 4, new Vector4(1f, 0.5f, 0.25f, 0.5f)));
        });
        Check.Equal(PassRenderer.StagingCapacity, count, "持っているステージングの数");
        Check.Near(0.25f, before[0], 0.001f, "最初の読み戻しの R");
        Check.Near(1f, after[0], 0.001f, "放したあとの読み戻しの R");
        Check.Near(0.25f, after[2], 0.001f, "放したあとの読み戻しの B");
        Check.Near(0.5f, after[^1], 0.001f, "放したあとの読み戻しの最後の画素の A");
    }

    [Test]
    public void INV5_別のスレッドからGPUを呼ぶと例外になる()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var (device, renderer) = thread.Invoke(() =>
        {
            var d = GpuDevice.Create(GpuAdapterRequest.Warp);
            return (d, new PassRenderer(d));
        });
        try
        {
            Check.True(!thread.IsCurrent, "テストのスレッドが専用スレッドと同じ");
            Check.Throws<InvalidOperationException>(() => device.CheckFormatSupport(Format.R16G16B16A16_Float), "別スレッドから CheckFormatSupport");
            Check.Throws<InvalidOperationException>(() => renderer.CreateTarget(4, 4), "別スレッドから CreateTarget");
            Check.Throws<InvalidOperationException>(() => renderer.Dispose(), "別スレッドから Dispose");
            // 持ち主のスレッドからは使える
            var ok = thread.Invoke(() => GpuSelfTest.StartupCheck(renderer).Passed);
            Check.True(ok, "持ち主のスレッドで自己検査が通らない");
        }
        finally
        {
            thread.Invoke(() =>
            {
                renderer.Dispose();
                device.Dispose();
            });
        }
    }

    [Test]
    public void INV5_RenderThreadは1本のスレッドで依頼の順に実行する()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        var order = new List<int>();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => thread.InvokeAsync(() =>
            {
                order.Add(i);
                return Environment.CurrentManagedThreadId;
            }))
            .ToArray();
        Task.WaitAll(tasks);
        var ids = tasks.Select(t => t.Result).Distinct().ToArray();
        Check.Equal(1, ids.Length, "実行したスレッドの数");
        Check.Equal(thread.ManagedThreadId, ids[0], "実行したスレッド");
        Check.True(ids[0] != Environment.CurrentManagedThreadId, "テストのスレッドで実行された");
        Check.SequenceEqual(Enumerable.Range(0, 50).ToArray(), order, "実行の順");
        // 専用スレッドで投げた例外は Task に入る
        var faulted = thread.InvokeAsync<int>(() => throw new FormatException("試験"));
        Check.Throws<FormatException>(() => faulted.GetAwaiter().GetResult(), "例外の受け渡し");
    }

    [Test]
    public void INV3_シェーダーは埋め込みリソースのDXBCだけから読む()
    {
        // Phase 1 の 4 つと Phase 2 の描画の土台の 4 つが必ずある(エフェクトのシェーダーは後から増えるので、一覧の一致は求めない)
        foreach (var required in new[]
                 {
                     "encode_bgra8.ps", "fill.ps", "fullscreen.vs", "rgb_affine.ps",
                     "ingest.ps", "effect_finish.ps", "final_encode.ps", "exposure.ps",
                 })
            Check.True(ShaderLibrary.Names.Contains(required), $"埋め込みのシェーダーに {required} が無い");
        foreach (var name in ShaderLibrary.Names)
        {
            var bytes = ShaderLibrary.Load(name);
            Check.True(bytes.Length > 32, $"{name} が短い");
            Check.SequenceEqual("DXBC"u8.ToArray(), bytes[..4], $"{name} の先頭が DXBC でない");
        }
        Check.Throws<KeyNotFoundException>(() => ShaderLibrary.Load("missing.ps"), "無い名前");
        // 埋め込みリソースの名前に .hlsl(ソース)が入っていない
        var resources = typeof(ShaderLibrary).Assembly.GetManifestResourceNames();
        Check.True(resources.All(n => !n.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase)), "HLSL のソースが埋め込まれている");
    }

    [Test]
    public void INV3_RenderはD3DCompilerを参照しない()
    {
        var refs = typeof(ShaderLibrary).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Check.True(refs.All(n => !n.Contains("D3DCompiler", StringComparison.OrdinalIgnoreCase)),
            $"GlintLoop.Render が D3DCompiler を参照している: {string.Join(", ", refs)}");
        // テストの出力(Render の依存を含む)に Vortice.D3DCompiler.dll が無い
        var dll = Path.Combine(AppContext.BaseDirectory, "Vortice.D3DCompiler.dll");
        Check.True(!File.Exists(dll), "出力に Vortice.D3DCompiler.dll がある");
        var deps = Path.Combine(AppContext.BaseDirectory, "GlintLoop.Tests.deps.json");
        if (File.Exists(deps))
            Check.True(!File.ReadAllText(deps).Contains("D3DCompiler", StringComparison.OrdinalIgnoreCase), "deps.json に D3DCompiler がある");
    }
}
