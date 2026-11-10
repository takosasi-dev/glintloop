// WARP のときだけ、放したテクスチャが 256 MB を超えるたびに Trim することの検査(AC-61・NFR-8 の対策)。
// 実 GPU では Trim しないが、それは probe でしか確かめられないので、ここでは WARP の側だけを見る。
using GlintLoop.Render;
using GlintLoop.Tests.Framework;
using Vortice.DXGI;

namespace GlintLoop.Tests.Render;

public sealed class WarpTrimTests
{
    [Test]
    public void WARP_では放したテクスチャが256MBを超えるたびに1回Trimする()
    {
        using var thread = new RenderThread("GlintLoop.Render.Test");
        thread.Invoke(() =>
        {
            using var device = GpuDevice.Create(GpuAdapterRequest.Warp);
            // 2048×2048 の RGBA16F は 1 枚 33,554,432 バイトなので、8 枚で 256 MB を超える
            for (var i = 0; i < 7; i++) new GpuTexture(device, 2048, 2048, Format.R16G16B16A16_Float).Dispose();
            Check.Equal(0, device.TrimCount, "7 枚(約 235 MB)では Trim しない");
            new GpuTexture(device, 2048, 2048, Format.R16G16B16A16_Float).Dispose();
            Check.Equal(1, device.TrimCount, "8 枚目で 1 回 Trim する");
            for (var i = 0; i < 7; i++) new GpuTexture(device, 2048, 2048, Format.R16G16B16A16_Float).Dispose();
            Check.Equal(1, device.TrimCount, "Trim のあとは数え直す");
            return 0;
        });
    }
}
