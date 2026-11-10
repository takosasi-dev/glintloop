// 描画の自己検査: FR-94 の起動時の検査(4×4 の RGBA16F を塗って読み戻す)と、AC-1 の 3 パスと over のブレンドの検査。
// 自動テスト(WARP)と probe-gpu(実 GPU)が同じ関数を呼ぶ。許容差はどれも各成分 0.002。
// どの関数も GpuDevice の持ち主のスレッドの上で呼ぶ(INV-5)。
using System.Numerics;

namespace GlintLoop.Render;

/// <summary>検査 1 つの結果。Pixels は読み戻した RGBA(行優先)。</summary>
public sealed record GpuCheckResult(string Name, bool Passed, Vector4 Expected, float MaxError, float[] Pixels)
{
    public const float Tolerance = 0.002f;

    public Vector4 Pixel(int index) => new(Pixels[index * 4], Pixels[index * 4 + 1], Pixels[index * 4 + 2], Pixels[index * 4 + 3]);
}

/// <summary>起動時に WARP へ切り替えた理由(E-40。ログの warp_fallback の no_adapter と float_rt_unsupported)。</summary>
public enum GpuFallbackReason
{
    /// <summary>要求のアダプターでデバイスを作れない(機能レベルが 11_0 未満を含む)。</summary>
    NoAdapter,

    /// <summary>デバイスは作れたが、R16G16B16A16_FLOAT のレンダーターゲットへの描画を確認できない。</summary>
    FloatRtUnsupported,
}

/// <summary>FR-94 のデバイス選びの結果。Reason は人が読む説明(probe-gpu とテスト用)、Fallback は切り替えの理由の列挙(切り替えていなければ null)。</summary>
public sealed record GpuSelection(GpuDevice Device, PassRenderer Renderer, bool UsedWarp, string? Reason, GpuFallbackReason? Fallback)
{
    public void Deconstruct(out GpuDevice device, out PassRenderer renderer, out bool usedWarp, out string? reason)
    {
        device = Device;
        renderer = Renderer;
        usedWarp = UsedWarp;
        reason = Reason;
    }
}

public static class GpuSelfTest
{
    public const int Size = 4;

    public static readonly Vector4 SelfTestColor = new(0.5f, 0.25f, 2.0f, 1.0f);
    public static readonly Vector4 ThreePassExpected = new(0.75f, 0.625f, 1.5f, 1.0f);
    public static readonly Vector4 OverBase = new(0.2f, 0.2f, 0.2f, 0.5f);
    public static readonly Vector4 OverTop = new(0.3f, 0.0f, 0.0f, 0.3f);
    public static readonly Vector4 OverExpected = new(0.44f, 0.14f, 0.14f, 0.65f);

    /// <summary>FR-94: 4×4 の RGBA16F を (0.5, 0.25, 2.0, 1.0) で塗って読み戻し、全成分が 0.002 以内か。</summary>
    public static GpuCheckResult StartupCheck(PassRenderer renderer)
    {
        using var target = renderer.CreateTarget(Size, Size);
        renderer.Fill(target, SelfTestColor);
        return Evaluate("FR-94 起動時の自己検査", SelfTestColor, renderer.ReadbackRgba16F(target));
    }

    /// <summary>
    /// AC-1 の前半: パス 1 で (0.5, 0.25, 2.0, 1.0) を塗り、パス 2 で RGB を 0.5 倍、パス 3 で RGB に 0.5 を足す。
    /// 期待は (0.75, 0.625, 1.5, 1.0)。1.5 が 1.0 に切られていないことも、この一致で確かめる。
    /// </summary>
    public static GpuCheckResult ThreePass(PassRenderer renderer)
    {
        using var a = renderer.CreateTarget(Size, Size);
        using var b = renderer.CreateTarget(Size, Size);
        renderer.Fill(a, SelfTestColor);
        renderer.RgbAffine(a, b, new Vector3(0.5f), Vector3.Zero);
        renderer.RgbAffine(b, a, Vector3.One, new Vector3(0.5f));
        return Evaluate("AC-1 3 パス", ThreePassExpected, renderer.ReadbackRgba16F(a));
    }

    /// <summary>AC-1 の後半: (0.2, 0.2, 0.2, 0.5) の上にプリマルチプライの over で (0.3, 0, 0, 0.3) を重ねると (0.44, 0.14, 0.14, 0.65)。</summary>
    public static GpuCheckResult Over(PassRenderer renderer)
    {
        using var target = renderer.CreateTarget(Size, Size);
        renderer.Fill(target, OverBase);
        renderer.Fill(target, OverTop, PassBlend.PremultipliedOver);
        return Evaluate("AC-1 over のブレンド", OverExpected, renderer.ReadbackRgba16F(target));
    }

    /// <summary>
    /// FR-94 のデバイス選び: 要求のアダプター(既定は高性能 GPU)で作って自己検査し、作れないか検査に落ちたら WARP にする。
    /// WARP も作れなければ例外をそのまま投げる(FR-104 の「処理されない例外」)。呼んだスレッドが持ち主になる。
    /// 機能レベルが 11_0 未満のアダプターは、ほかの失敗と区別できる理由を付けて WARP にする(NFR-11)。
    /// </summary>
    public static GpuSelection CreateWithFallback(GpuAdapterRequest preferred) =>
        CreateWithFallback(preferred, GpuDevice.Create);

    /// <summary>同じ手順で、要求のアダプターのデバイスも、WARP のデバイスも、自己検査も factory を通す(AC-49 の偽のデバイス工場)。</summary>
    public static GpuSelection CreateWithFallback(GpuAdapterRequest preferred, IDeviceFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return CreateWithFallback(preferred, factory.Create, factory.StartupCheck, factory.Create);
    }

    /// <summary>要求のアダプターのデバイスを作る関数を差し替えられる版(テストの差し込み口)。WARP は常に GpuDevice.Create で作る。</summary>
    internal static GpuSelection CreateWithFallback(GpuAdapterRequest preferred, Func<GpuAdapterRequest, GpuDevice> createPreferred) =>
        CreateWithFallback(preferred, createPreferred, StartupCheck);

    /// <summary>要求のアダプターでの自己検査も差し替えられる版(テストの差し込み口。検査に落ちるデバイスの代わり)。</summary>
    internal static GpuSelection CreateWithFallback(
        GpuAdapterRequest preferred, Func<GpuAdapterRequest, GpuDevice> createPreferred, Func<PassRenderer, GpuCheckResult> startupCheck) =>
        CreateWithFallback(preferred, createPreferred, startupCheck, GpuDevice.Create);

    private static GpuSelection CreateWithFallback(
        GpuAdapterRequest preferred, Func<GpuAdapterRequest, GpuDevice> createPreferred, Func<PassRenderer, GpuCheckResult> startupCheck,
        Func<GpuAdapterRequest, GpuDevice> createWarp)
    {
        if (preferred.Kind != GpuAdapterKind.Warp)
        {
            string reason;
            GpuDevice? device = null;
            PassRenderer? renderer = null;
            try
            {
                device = createPreferred(preferred);
                renderer = new PassRenderer(device);
                var check = startupCheck(renderer);
                if (check.Passed) return new GpuSelection(device, renderer, false, null, null);
                reason = $"自己検査に落ちた(最大の差 {check.MaxError})";
            }
            catch (GpuFeatureLevelTooLowException e)
            {
                reason = $"機能レベルが 11_0 未満({e.Level.ToString().Replace("Level_", "")}): {e.Message}";
            }
            catch (Exception e)
            {
                reason = $"{e.GetType().Name}: {e.Message}";
            }
            // デバイスまで作れたのに先へ進めなかったときは、描画を確認できなかった(E-40)
            var fallback = device is null ? GpuFallbackReason.NoAdapter : GpuFallbackReason.FloatRtUnsupported;
            renderer?.Dispose();
            device?.Dispose();
            var warp = createWarp(GpuAdapterRequest.Warp);
            return new GpuSelection(warp, new PassRenderer(warp), true, reason, fallback);
        }

        var only = createWarp(GpuAdapterRequest.Warp);
        return new GpuSelection(only, new PassRenderer(only), true, null, null);
    }

    /// <summary>読み戻した画素 pixels(RGBA の並び)の全成分が expected から許容差以内か。NaN があれば落ちる(テストからも呼ぶ)。</summary>
    internal static GpuCheckResult Evaluate(string name, Vector4 expected, float[] pixels)
    {
        var e = new[] { expected.X, expected.Y, expected.Z, expected.W };
        var max = 0f;
        var ok = true;
        for (var i = 0; i < pixels.Length; i++)
        {
            var d = MathF.Abs(pixels[i] - e[i % 4]);
            if (float.IsNaN(d)) ok = false;
            else max = MathF.Max(max, d);
        }
        return new GpuCheckResult(name, ok && max <= GpuCheckResult.Tolerance, expected, max, pixels);
    }
}
