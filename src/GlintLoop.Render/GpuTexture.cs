// パスの描き先と読み元になる 2D テクスチャ(レンダーターゲットビューとシェーダーリソースビューを持つ)。
// 内部の形式は R16G16B16A16_FLOAT(線形光・プリマルチプライ。D-3)、表示の読み戻しは B8G8R8A8_UNORM。
// 作るのも破棄するのも GpuDevice の持ち主のスレッドだけ(INV-5)。
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlintLoop.Render;

public sealed class GpuTexture : IDisposable
{
    private readonly GpuDevice _owner;
    private bool _disposed;

    internal GpuTexture(GpuDevice owner, int width, int height, Format format)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "大きさは 1 以上");
        _owner = owner;
        Width = width;
        Height = height;
        Format = format;
        var device = owner.Device;
        var desc = new Texture2DDescription(
            format,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None);
        Texture = device.CreateTexture2D(desc);
        Rtv = device.CreateRenderTargetView(Texture);
        Srv = device.CreateShaderResourceView(Texture);
    }

    /// <summary>中身を initialData(1 行は rowPitch バイト)で初めから入れて作る。取り込んだ画像と青色雑音の表を置くのに使う。</summary>
    internal unsafe GpuTexture(GpuDevice owner, int width, int height, Format format, ReadOnlySpan<byte> initialData, int rowPitch)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "大きさは 1 以上");
        if (rowPitch <= 0 || initialData.Length < (long)rowPitch * height)
            throw new ArgumentException("初めの中身が大きさに足りない", nameof(initialData));
        _owner = owner;
        Width = width;
        Height = height;
        Format = format;
        var device = owner.Device;
        var desc = new Texture2DDescription(
            format,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None);
        fixed (byte* p = initialData)
        {
            Texture = device.CreateTexture2D(desc, new SubresourceData(p, (uint)rowPitch, (uint)(rowPitch * height)));
        }
        Rtv = device.CreateRenderTargetView(Texture);
        Srv = device.CreateShaderResourceView(Texture);
    }

    /// <summary>作ったデバイス(作り直しのあとに古いデバイスのテクスチャを使っていないかの表明に使う)。</summary>
    internal GpuDevice Owner => _owner;

    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    internal ID3D11Texture2D Texture { get; }
    internal ID3D11RenderTargetView Rtv { get; }
    internal ID3D11ShaderResourceView Srv { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _owner.EnsureOwnerThread();
        _disposed = true;
        Srv.Dispose();
        Rtv.Dispose();
        Texture.Dispose();
        _owner.NoteFreed((long)Width * Height * (Format == Format.R16G16B16A16_Float ? 8 : 4));
    }
}
