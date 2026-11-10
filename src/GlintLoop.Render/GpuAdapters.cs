// GPU アダプターの一覧と、デバイスを作るアダプターの指定(仕様書 §2「テストの差し込み口」)。
// 既定は高性能 GPU(DXGI の GpuPreference.HighPerformance の先頭)、自動テストは WARP、測定は番号で個別に指定する。
// ここでは DXGI だけを触り、Direct3D 11 のデバイスは作らない。
using Vortice.DXGI;

namespace GlintLoop.Render;

public enum GpuAdapterKind
{
    /// <summary>DXGI が高性能と並べた先頭のアダプター(製品の既定)。</summary>
    HighPerformance,

    /// <summary>ソフトウェア描画(WARP)。自動テストはこれだけを使う(D-39)。</summary>
    Warp,

    /// <summary>EnumAdapters1 の番号で指定する(測定用)。</summary>
    ByIndex,

    /// <summary>アダプターの識別子(LUID)で指定する(FR-93 の作り直し。その GPU が無ければ作れない)。</summary>
    ByLuid,
}

public readonly record struct GpuAdapterRequest(GpuAdapterKind Kind, int Index = -1, long Luid = 0)
{
    public static GpuAdapterRequest HighPerformance => new(GpuAdapterKind.HighPerformance);
    public static GpuAdapterRequest Warp => new(GpuAdapterKind.Warp);
    public static GpuAdapterRequest ByIndex(int index) => new(GpuAdapterKind.ByIndex, index);
    public static GpuAdapterRequest ByLuid(long luid) => new(GpuAdapterKind.ByLuid, Luid: luid);

    public override string ToString() => Kind switch
    {
        GpuAdapterKind.ByIndex => $"ByIndex({Index})",
        GpuAdapterKind.ByLuid => $"ByLuid({Luid})",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// 開いたアダプター。Native は D3D11CreateDevice に渡す本物(WARP では null)、Luid はアダプターの識別(WARP では null)。
/// テストは Native を持たない偽(Luid だけ)を差し込み、DXGI を呼ばずにデバイスの作り方を確かめる。Dispose で Native を放す。
/// </summary>
internal readonly record struct GpuAdapterHandle(IDXGIAdapter1? Native, long? Luid) : IDisposable
{
    public static GpuAdapterHandle None => default;

    public void Dispose() => Native?.Dispose();
}

public sealed record GpuAdapterInfo(
    int Index,
    string Description,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemory,
    ulong SharedSystemMemory,
    bool IsSoftware,
    long Luid);

public static class GpuAdapters
{
    /// <summary>EnumAdapters1 の順にアダプターを並べる(WARP の "Microsoft Basic Render Driver" も含まれうる)。</summary>
    public static IReadOnlyList<GpuAdapterInfo> Enumerate()
    {
        var list = new List<GpuAdapterInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                list.Add(ToInfo((int)i, adapter.Description1));
            }
        }
        return list;
    }

    /// <summary>GpuPreference.HighPerformance の先頭のアダプターの LUID。取れなければ null。</summary>
    public static long? HighPerformanceLuid()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        using var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (factory6 is null) return null;
        if (factory6.EnumAdapterByGpuPreference(0, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Failure || adapter is null)
            return null;
        using (adapter)
        {
            return LuidValue(adapter.Description1.Luid);
        }
    }

    /// <summary>要求に合うアダプターを開き、LUID を添えて返す(GpuDevice.Create の本物の開く関数)。WARP のときは Native も Luid も null。</summary>
    internal static GpuAdapterHandle OpenHandle(GpuAdapterRequest request)
    {
        var adapter = Open(request);
        if (adapter is null) return GpuAdapterHandle.None;
        try
        {
            return new GpuAdapterHandle(adapter, LuidValue(adapter.Description1.Luid));
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
    }

    /// <summary>要求に合うアダプターを開く。WARP のときは null(D3D11CreateDevice に DriverType.Warp で渡す)。</summary>
    internal static IDXGIAdapter1? Open(GpuAdapterRequest request)
    {
        if (request.Kind == GpuAdapterKind.Warp) return null;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (request.Kind == GpuAdapterKind.ByLuid) return FindByLuid(All(factory), a => LuidValue(a.Description1.Luid), request.Luid);
        if (request.Kind == GpuAdapterKind.HighPerformance)
        {
            using var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
            if (factory6 is not null &&
                factory6.EnumAdapterByGpuPreference(0, GpuPreference.HighPerformance, out IDXGIAdapter1? preferred).Success &&
                preferred is not null)
            {
                return preferred;
            }
            // IDXGIFactory6 が無い古い環境では、列挙の先頭(既定のアダプター)を使う
            return factory.EnumAdapters1(0, out var first).Success ? first : throw new InvalidOperationException("GPU アダプターが 1 つも無い");
        }

        if (request.Index < 0 || factory.EnumAdapters1((uint)request.Index, out var byIndex).Failure)
            throw new ArgumentOutOfRangeException(nameof(request), $"アダプター {request.Index} は無い");
        return byIndex;
    }

    private static IEnumerable<IDXGIAdapter1> All(IDXGIFactory1 factory)
    {
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++) yield return adapter;
    }

    /// <summary>列挙の中から LUID が合うものを返し、前にあったものは放す。無ければ例外(作り直しの失敗に数える。FR-93)。</summary>
    internal static TAdapter FindByLuid<TAdapter>(IEnumerable<TAdapter> adapters, Func<TAdapter, long> luidOf, long luid) where TAdapter : IDisposable
    {
        foreach (var adapter in adapters)
        {
            if (luidOf(adapter) == luid) return adapter;
            adapter.Dispose();
        }
        throw new InvalidOperationException($"アダプター LUID {luid} は無い");
    }

    internal static GpuAdapterInfo ToInfo(int index, AdapterDescription1 d) => new(
        index,
        d.Description,
        d.VendorId,
        d.DeviceId,
        (ulong)d.DedicatedVideoMemory,
        (ulong)d.SharedSystemMemory,
        (d.Flags & AdapterFlags.Software) != 0,
        LuidValue(d.Luid));

    internal static long LuidValue(Vortice.Luid luid) => ((long)luid.HighPart << 32) | luid.LowPart;
}
