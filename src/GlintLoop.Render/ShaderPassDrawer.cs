// 埋め込みのピクセルシェーダーを名前で選んで、全画面の三角形で 1 パス描く汎用の道具(エフェクトのパス・取り込み・最後の変換が使う)。
// 入力は t0〜t4、サンプラーは s0 が点・s1 が双線形(どちらも端を延ばす)、定数バッファは b0 に任意の長さ(16 の倍数に詰める)。
// GpuDevice の持ち主のスレッドだけで使う(INV-5)。ブレンドは使わない(描いた値で上書きする)。
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace GlintLoop.Render;

internal sealed class ShaderPassDrawer : IDisposable
{
    /// <summary>結べる入力の数(t0〜t4。エフェクトのパスは t0〜t3 まで、t4 は最後の変換の青色雑音の表)。</summary>
    public const int MaxInputs = 5;

    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11BlendState _opaque;
    private readonly ID3D11SamplerState _point;
    private readonly ID3D11SamplerState _linear;
    private readonly Dictionary<string, ID3D11PixelShader> _shaders = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ID3D11Buffer> _constantBuffers = new();
    private bool _disposed;

    public ShaderPassDrawer(GpuDevice gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;
        _vs = device.CreateVertexShader(ShaderLibrary.Load("fullscreen.vs"));
        _rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
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
        _point = device.CreateSamplerState(SamplerDescription.PointClamp);
        _linear = device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    /// <summary>名前("exposure.ps" の形)のピクセルシェーダー。初めて使うときに埋め込みリソースから作る。</summary>
    private ID3D11PixelShader Shader(string name)
    {
        if (_shaders.TryGetValue(name, out var ps)) return ps;
        ps = _gpu.Device.CreatePixelShader(ShaderLibrary.Load(name));
        _shaders[name] = ps;
        return ps;
    }

    /// <summary>
    /// shaderName のシェーダーで target を描く。inputs[i] を t{i} に結ぶ(null は結ばない)。
    /// constants が空でなければ b0 に置く(長さは 16 の倍数にすること)。
    /// </summary>
    public void Draw(string shaderName, GpuTexture target, ReadOnlySpan<GpuTexture?> inputs, ReadOnlySpan<byte> constants)
    {
        if (inputs.Length > MaxInputs) throw new ArgumentException($"入力は {MaxInputs} 個まで", nameof(inputs));
        if (!ReferenceEquals(target.Owner, _gpu)) throw new InvalidOperationException("別のデバイスのテクスチャは描き先にできない");
        foreach (var input in inputs)
        {
            if (ReferenceEquals(input, target)) throw new ArgumentException("読み元と描き先は別のテクスチャにする");
            if (input is not null && !ReferenceEquals(input.Owner, _gpu)) throw new InvalidOperationException("別のデバイスのテクスチャは読み元にできない");
        }
        if (constants.Length % 16 != 0) throw new ArgumentException("定数バッファの長さは 16 の倍数", nameof(constants));

        var ps = Shader(shaderName);
        var ctx = _gpu.Context;
        for (uint i = 0; i < MaxInputs; i++) ctx.PSSetShaderResource(i, null!);
        ctx.OMSetRenderTargets(target.Rtv);
        ctx.OMSetBlendState(_opaque);
        ctx.RSSetState(_rasterizer);
        ctx.RSSetViewport(0f, 0f, target.Width, target.Height);
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(ps);
        if (constants.Length > 0)
        {
            var buffer = ConstantBuffer(constants.Length);
            ctx.UpdateSubresource(constants, buffer, 0, 0, 0, null);
            ctx.PSSetConstantBuffer(0, buffer);
        }
        else
        {
            ctx.PSSetConstantBuffer(0, null!);
        }
        ctx.PSSetSampler(0, _point);
        ctx.PSSetSampler(1, _linear);
        for (var i = 0; i < inputs.Length; i++)
        {
            if (inputs[i] is { } t) ctx.PSSetShaderResource((uint)i, t.Srv);
        }
        ctx.Draw(3, 0);
        for (uint i = 0; i < MaxInputs; i++) ctx.PSSetShaderResource(i, null!);
    }

    private ID3D11Buffer ConstantBuffer(int bytes)
    {
        if (_constantBuffers.TryGetValue(bytes, out var buffer)) return buffer;
        buffer = _gpu.Device.CreateBuffer((uint)bytes, BindFlags.ConstantBuffer, ResourceUsage.Default);
        _constantBuffers[bytes] = buffer;
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gpu.EnsureOwnerThread();
        _disposed = true;
        foreach (var b in _constantBuffers.Values) b.Dispose();
        _constantBuffers.Clear();
        foreach (var s in _shaders.Values) s.Dispose();
        _shaders.Clear();
        _linear.Dispose();
        _point.Dispose();
        _opaque.Dispose();
        _rasterizer.Dispose();
        _vs.Dispose();
    }
}
