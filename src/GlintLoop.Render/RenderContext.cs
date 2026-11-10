// 描画の専用スレッドの上で使う道具一式(取り込み・エフェクトとスタックの実行・最後の変換と読み戻し)。RenderService が 1 つ持つ。
// どのメソッドも GpuDevice の持ち主のスレッドだけで呼べる(INV-5。ほかのスレッドからは InvalidOperationException)。
// 返した GpuTexture は呼び手のもので、同じスレッドの上で Dispose する。
using GlintLoop.Core.Dither;
using GlintLoop.Render.Effects;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlintLoop.Render;

public sealed class RenderContext : IDisposable
{
    /// <summary>取り込んだ画像を置く形式(§9.7 の規則 2)。読むときにハードウェアが lin を行う。</summary>
    public const Format SourceFormat = Format.R8G8B8A8_UNorm_SRgb;

    /// <summary>最後の変換の描き先の形式(8 ビットの整数を、そのまま書く)。</summary>
    internal const Format EncodedFormat = Format.R8G8B8A8_UInt;

    internal const string IngestShader = "ingest.ps";
    internal const string FinalEncodeShader = "final_encode.ps";

    private readonly ShaderPassDrawer _drawer;
    private readonly EffectRunner _runner;
    private GpuTexture? _blueNoise;
    private bool _disposed;

    internal RenderContext(GpuDevice gpu, PassRenderer passes)
    {
        Gpu = gpu;
        Passes = passes;
        _drawer = new ShaderPassDrawer(gpu);
        _runner = new EffectRunner(gpu, _drawer);
    }

    public GpuDevice Gpu { get; }

    /// <summary>Phase 1 のパスの道具(塗り・RGB のアフィン・RGBA16F の読み戻し)。</summary>
    public PassRenderer Passes { get; }

    /// <summary>アダプターのテクスチャの最大寸法(§9.7 の規則 2: 機能レベル 10_x は 8192、11_0 以上は 16384)。</summary>
    public int MaxTextureDimension => MaxTextureDimensionOf(Gpu.FeatureLevel);

    internal static int MaxTextureDimensionOf(Vortice.Direct3D.FeatureLevel level) =>
        level >= Vortice.Direct3D.FeatureLevel.Level_11_0 ? 16384 : 8192;

    /// <summary>描き先と読み元に使える RGBA16F(線形光・プリマルチプライ)のテクスチャを作る。中身は未定義。</summary>
    public GpuTexture CreateWorking(int width, int height)
    {
        Gpu.EnsureOwnerThread();
        return new GpuTexture(Gpu, width, height, PassRenderer.WorkingFormat);
    }

    /// <summary>
    /// sRGB 8 ビット・straight の RGBA(1 行 stride バイト)を R8G8B8A8_UNORM_SRGB のテクスチャとして置く(規則 2)。
    /// 辺が MaxTextureDimension を超えるときは ArgumentOutOfRangeException(縮小は取り込みが先に行う)。
    /// </summary>
    public GpuTexture UploadSrgb8(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        Gpu.EnsureOwnerThread();
        if (width > MaxTextureDimension || height > MaxTextureDimension)
            throw new ArgumentOutOfRangeException(nameof(width), $"辺がテクスチャの最大寸法 {MaxTextureDimension} を超える");
        if (stride < width * 4) throw new ArgumentException("1 行のバイト数が幅×4 より小さい", nameof(stride));
        return new GpuTexture(Gpu, width, height, SourceFormat, rgba, stride);
    }

    /// <summary>R8G8B8A8_UNORM_SRGB のソースを、同じ大きさの線形光・プリマルチプライの RGBA16F に直す。</summary>
    public GpuTexture Ingest(GpuTexture source)
    {
        Gpu.EnsureOwnerThread();
        if (source.Format != SourceFormat) throw new ArgumentException("ソースは R8G8B8A8_UNORM_SRGB にする", nameof(source));
        var target = CreateWorking(source.Width, source.Height);
        Draw(IngestShader, target, [source], []);
        return target;
    }

    /// <summary>UploadSrgb8 と Ingest をまとめて行う(sRGB のテクスチャは残さない)。</summary>
    public GpuTexture IngestSrgb8(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        using var source = UploadSrgb8(rgba, width, height, stride);
        return Ingest(source);
    }

    /// <summary>
    /// 1 つのエフェクトを input に掛けた結果(新しいテクスチャ)。orig は inputs の orig に渡すテクスチャ(null なら input)。
    /// 実効 amount と mix・出力の切り・scale モードの実効値は、ここで共通に扱う。
    /// </summary>
    public GpuTexture RunEffect(EffectInvocation effect, GpuTexture input, FrameContext frame, GpuTexture? orig = null)
    {
        Gpu.EnsureOwnerThread();
        return _runner.Run(effect, input, frame, orig);
    }

    /// <summary>
    /// エフェクトの列を input に順に掛けた結果(新しいテクスチャ。列が空なら input の写し)。input は変えない。
    /// orig は各エフェクトの inputs の orig(null なら input)。オフの行は呼び手が除いて渡す。
    /// </summary>
    public GpuTexture RunStack(IReadOnlyList<EffectInvocation> stack, GpuTexture input, FrameContext frame, GpuTexture? orig = null)
    {
        Gpu.EnsureOwnerThread();
        if (stack.Count == 0)
        {
            var copy = new GpuTexture(Gpu, input.Width, input.Height, input.Format);
            Gpu.Context.CopyResource(copy.Texture, input.Texture);
            return copy;
        }
        GpuTexture current = input;
        foreach (var effect in stack)
        {
            var next = _runner.Run(effect, current, frame, orig ?? input);
            if (!ReferenceEquals(current, input)) current.Dispose();
            current = next;
        }
        return current;
    }

    /// <summary>最後の変換(規則 7)の書き出し用: straight の RGBA 8 ビット(行優先、幅×4 バイトの行)。</summary>
    public byte[] ReadStraightRgba8(GpuTexture working) => Encode(working, premultipliedBgra: false);

    /// <summary>最後の変換(規則 7)の表示用: Pbgra32(B・G・R・A の並び、straight の結果の各色に α÷255 を掛けて round)。</summary>
    public byte[] ReadPbgra32(GpuTexture working) => Encode(working, premultipliedBgra: true);

    /// <summary>RGBA16F をそのまま読み戻す(行優先の RGBA の float。NaN と範囲の検査に使う)。</summary>
    public float[] ReadRgba16F(GpuTexture working)
    {
        Gpu.EnsureOwnerThread();
        return Passes.ReadbackRgba16F(working);
    }

    private byte[] Encode(GpuTexture working, bool premultipliedBgra)
    {
        Gpu.EnsureOwnerThread();
        if (working.Format != PassRenderer.WorkingFormat) throw new ArgumentException("RGBA16F のテクスチャだけを変換できる", nameof(working));
        using var target = new GpuTexture(Gpu, working.Width, working.Height, EncodedFormat);
        var constants = new byte[16];
        BitConverter.TryWriteBytes(constants.AsSpan(0, 4), premultipliedBgra ? 1f : 0f);
        Draw(FinalEncodeShader, target, [working, null, null, null, BlueNoiseTexture()], constants);
        return ReadBytes(target);
    }

    private GpuTexture BlueNoiseTexture()
    {
        if (_blueNoise is not null) return _blueNoise;
        var bytes = new byte[BlueNoise.Count * 2];
        var tile = BlueNoise.Tile;
        for (var k = 0; k < tile.Length; k++)
        {
            bytes[2 * k] = (byte)tile[k];
            bytes[2 * k + 1] = (byte)(tile[k] >> 8);
        }
        _blueNoise = new GpuTexture(Gpu, BlueNoise.Size, BlueNoise.Size, Format.R16_UInt, bytes, BlueNoise.Size * 2);
        return _blueNoise;
    }

    internal void Draw(string shader, GpuTexture target, ReadOnlySpan<GpuTexture?> inputs, ReadOnlySpan<byte> constants) =>
        _drawer.Draw(shader, target, inputs, constants);

    /// <summary>4 バイトの画素のテクスチャを、行を詰めたバイト列で読み戻す。</summary>
    private unsafe byte[] ReadBytes(GpuTexture source)
    {
        var staging = Passes.MapForRead(source, out var mapped);
        var rowBytes = source.Width * 4;
        var result = new byte[rowBytes * source.Height];
        try
        {
            fixed (byte* dst = result)
            {
                for (var y = 0; y < source.Height; y++)
                {
                    Buffer.MemoryCopy((byte*)mapped.DataPointer + (long)y * mapped.RowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            Gpu.Context.Unmap(staging, 0);
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Gpu.EnsureOwnerThread();
        _disposed = true;
        _blueNoise?.Dispose();
        _drawer.Dispose();
    }
}
