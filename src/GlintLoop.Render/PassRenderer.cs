// 全画面の三角形で 1 パスずつ描く道具と、ステージングテクスチャ経由の CPU への読み戻し(仕様書 §2 の「パスグラフ」「読み戻し」)。
// Phase 1 は AC-1 と FR-94 と測定 1 に要るパス(定数の塗り、RGB のアフィン、sRGB 8 ビットへの変換)だけを持つ。
// ブレンドは「不透明(上書き)」と「プリマルチプライの over」の 2 つ。GpuDevice の持ち主のスレッドだけで使う(INV-5)。ステージングは RenderContext の読み戻しと共用。
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlintLoop.Render;

public enum PassBlend
{
    /// <summary>描いた値で上書きする。</summary>
    Opaque,

    /// <summary>出力 = 重ねる値 + 下の値 × (1 − 重ねる α)(色と α の両方)。</summary>
    PremultipliedOver,
}

[StructLayout(LayoutKind.Sequential)]
internal struct PassConstants
{
    public Vector4 A;
    public Vector4 B;
}

public sealed class PassRenderer : IDisposable
{
    /// <summary>内部の形式(D-3)。</summary>
    public const Format WorkingFormat = Format.R16G16B16A16_Float;

    /// <summary>表示の読み戻しの形式。WPF の PixelFormats.Bgra32 と同じ並び。</summary>
    public const Format DisplayFormat = Format.B8G8R8A8_UNorm;

    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _fullscreenVs;
    private readonly ID3D11PixelShader _fillPs;
    private readonly ID3D11PixelShader _rgbAffinePs;
    private readonly ID3D11PixelShader _encodeBgra8Ps;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11BlendState _opaque;
    private readonly ID3D11BlendState _premultipliedOver;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly Dictionary<(Format, int, int), (ID3D11Texture2D Texture, long Used)> _staging = new();
    private long _stagingUse;
    private bool _disposed;

    public PassRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;
        _fullscreenVs = device.CreateVertexShader(ShaderLibrary.Load("fullscreen.vs"));
        _fillPs = device.CreatePixelShader(ShaderLibrary.Load("fill.ps"));
        _rgbAffinePs = device.CreatePixelShader(ShaderLibrary.Load("rgb_affine.ps"));
        _encodeBgra8Ps = device.CreatePixelShader(ShaderLibrary.Load("encode_bgra8.ps"));
        _constants = device.CreateBuffer((uint)Marshal.SizeOf<PassConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default);

        var opaque = new BlendDescription();
        opaque.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = false,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.Zero,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _opaque = device.CreateBlendState(opaque);

        var over = new BlendDescription();
        over.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _premultipliedOver = device.CreateBlendState(over);

        // 巻き順に依らず全画面の三角形が描かれるよう、カリングを切る
        _rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    }

    public GpuDevice Gpu => _gpu;

    /// <summary>描き先と読み元に使えるテクスチャを作る(既定は RGBA16F)。</summary>
    public GpuTexture CreateTarget(int width, int height, Format format = WorkingFormat) => new(_gpu, width, height, format);

    /// <summary>全画素を色 rgba(線形光・プリマルチプライ)で塗る。blend が over なら下の値に重ねる。</summary>
    public void Fill(GpuTexture target, Vector4 rgba, PassBlend blend = PassBlend.Opaque)
    {
        Draw(_fillPs, target, source: null, new PassConstants { A = rgba }, blend);
    }

    /// <summary>target = (source.rgb × scale + offset, source.a)。値は切らない。</summary>
    public void RgbAffine(GpuTexture source, GpuTexture target, Vector3 scale, Vector3 offset)
    {
        if (ReferenceEquals(source, target)) throw new ArgumentException("読み元と描き先は別のテクスチャにする");
        Draw(_rgbAffinePs, target, source, new PassConstants { A = new Vector4(scale, 0f), B = new Vector4(offset, 0f) }, PassBlend.Opaque);
    }

    /// <summary>線形光・プリマルチプライの source を、straight の sRGB 8 ビット(B8G8R8A8_UNORM の target)へ変える。</summary>
    public void EncodeBgra8(GpuTexture source, GpuTexture target)
    {
        if (target.Format != DisplayFormat) throw new ArgumentException("描き先は B8G8R8A8_UNORM にする");
        if (source.Width != target.Width || source.Height != target.Height) throw new ArgumentException("大きさが違う");
        Draw(_encodeBgra8Ps, target, source, default, PassBlend.Opaque);
    }

    /// <summary>RGBA16F のテクスチャを読み戻し、行優先の RGBA の float(幅×高さ×4)で返す。</summary>
    public unsafe float[] ReadbackRgba16F(GpuTexture source)
    {
        if (source.Format != WorkingFormat) throw new ArgumentException("RGBA16F のテクスチャだけを読み戻せる");
        var result = new float[source.Width * source.Height * 4];
        var staging = MapForRead(source, out var mapped);
        try
        {
            for (var y = 0; y < source.Height; y++)
            {
                var row = (ushort*)((byte*)mapped.DataPointer + (long)y * mapped.RowPitch);
                var o = y * source.Width * 4;
                for (var i = 0; i < source.Width * 4; i++)
                    result[o + i] = (float)BitConverter.UInt16BitsToHalf(row[i]);
            }
        }
        finally
        {
            _gpu.Context.Unmap(staging, 0);
        }
        return result;
    }

    /// <summary>B8G8R8A8_UNORM のテクスチャを destination へ読み戻す(1 行は destinationStride バイト)。</summary>
    public unsafe void ReadbackBgra8(GpuTexture source, Span<byte> destination, int destinationStride)
    {
        if (source.Format != DisplayFormat) throw new ArgumentException("B8G8R8A8_UNORM のテクスチャだけを読み戻せる");
        var rowBytes = source.Width * 4;
        if (destinationStride < rowBytes || destination.Length < (long)destinationStride * (source.Height - 1) + rowBytes)
            throw new ArgumentException("読み戻し先が小さい");
        var staging = MapForRead(source, out var mapped);
        try
        {
            fixed (byte* dst = destination)
            {
                for (var y = 0; y < source.Height; y++)
                {
                    Buffer.MemoryCopy(
                        (byte*)mapped.DataPointer + (long)y * mapped.RowPitch,
                        dst + (long)y * destinationStride,
                        rowBytes,
                        rowBytes);
                }
            }
        }
        finally
        {
            _gpu.Context.Unmap(staging, 0);
        }
    }

    private void Draw(ID3D11PixelShader ps, GpuTexture target, GpuTexture? source, PassConstants constants, PassBlend blend)
    {
        var ctx = _gpu.Context;
        ctx.UpdateSubresource(in constants, _constants);
        // 先に描き先を差し替えてから読み元を結ぶ(同じテクスチャが RTV と SRV に同時に結ばれないように)
        ctx.OMSetRenderTargets(target.Rtv);
        ctx.OMSetBlendState(blend == PassBlend.Opaque ? _opaque : _premultipliedOver);
        ctx.RSSetState(_rasterizer);
        ctx.RSSetViewport(0f, 0f, target.Width, target.Height);
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_fullscreenVs);
        ctx.PSSetShader(ps);
        ctx.PSSetConstantBuffer(0, _constants);
        ctx.PSSetShaderResource(0, source?.Srv!);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null!);
    }

    /// <summary>source をステージングへ写して Map する。呼び手は返したテクスチャを Unmap(…, 0) すること。</summary>
    internal ID3D11Texture2D MapForRead(GpuTexture source, out MappedSubresource mapped)
    {
        var ctx = _gpu.Context;
        var staging = Staging(source.Format, source.Width, source.Height);
        ctx.CopyResource(staging, source.Texture);
        // Map は GPU の処理が終わるまで待つ(同期点)
        mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        return staging;
    }

    /// <summary>持っておくステージングの数。表示・サムネイル・書き出しの大きさが入れ替わりに来るので 1 つにはしない。</summary>
    internal const int StagingCapacity = 4;

    /// <summary>今持っているステージングの数(テスト用)。</summary>
    internal int StagingCount => _staging.Count;

    // 大きさが変わり続けても増え続けないよう、上限を超えたら最後に使ったのが最も古いものを放す
    private ID3D11Texture2D Staging(Format format, int width, int height)
    {
        var key = (format, width, height);
        if (_staging.TryGetValue(key, out var cached))
        {
            _staging[key] = (cached.Texture, ++_stagingUse);
            return cached.Texture;
        }
        if (_staging.Count >= StagingCapacity)
        {
            var oldest = _staging.MinBy(p => p.Value.Used);
            _staging.Remove(oldest.Key);
            oldest.Value.Texture.Dispose();
        }
        var desc = new Texture2DDescription(
            format,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging,
            cpuAccessFlags: CpuAccessFlags.Read);
        var staging = _gpu.Device.CreateTexture2D(desc);
        _staging[key] = (staging, ++_stagingUse);
        return staging;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gpu.EnsureOwnerThread();
        _disposed = true;
        foreach (var s in _staging.Values) s.Texture.Dispose();
        _staging.Clear();
        _rasterizer.Dispose();
        _premultipliedOver.Dispose();
        _opaque.Dispose();
        _constants.Dispose();
        _encodeBgra8Ps.Dispose();
        _rgbAffinePs.Dispose();
        _fillPs.Dispose();
        _fullscreenVs.Dispose();
    }
}
