// Direct3D 11 のデバイスとイミディエイトコンテキストの持ち主(仕様書 D-2・D-37・INV-5)。機能レベルは 11_0 以上だけを使う(NFR-11)。
// 作ったスレッドだけが使ってよく、ほかのスレッドから触ると InvalidOperationException になる(常に有効な表明)。
// RenderThread の上で Create し、そのスレッドの上で使い、そのスレッドの上で Dispose する。
using Vortice.Direct3D;
using Vortice.Direct3D11;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace GlintLoop.Render;

public sealed class GpuDevice : IDisposable
{
    /// <summary>使う機能レベルの下限。埋め込みのシェーダーが vs_5_0/ps_5_0 で、11_0 未満では作れない(D-2・NFR-11)。</summary>
    public static FeatureLevel MinimumFeatureLevel => FeatureLevel.Level_11_0;

    /// <summary>デバイスを作るときに要求する機能レベル(11_1 と 11_0 だけ)。</summary>
    internal static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
    ];

    /// <summary>11_0 以上で作れなかったときに、拒む理由を分けるためだけに試す機能レベル。このデバイスは返さない。</summary>
    private static readonly FeatureLevel[] TooLowFeatureLevels =
    [
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private bool _disposed;

    private GpuDevice(ID3D11Device device, ID3D11DeviceContext context, FeatureLevel level, GpuAdapterRequest request, GpuAdapterInfo? adapter, long? adapterLuid)
    {
        _device = device;
        _context = context;
        FeatureLevel = level;
        Request = request;
        Adapter = adapter;
        AdapterLuid = adapterLuid;
        OwnerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>デバイスを作ったスレッド。GPU の操作はこのスレッドだけで行う(INV-5)。</summary>
    public int OwnerThreadId { get; }

    public FeatureLevel FeatureLevel { get; }

    public GpuAdapterRequest Request { get; }

    /// <summary>WARP のときは null。</summary>
    public GpuAdapterInfo? Adapter { get; }

    /// <summary>作ったアダプターの識別子(WARP のときは null)。FR-93 の作り直しはこの GPU に固定する。</summary>
    public long? AdapterLuid { get; }

    public bool IsWarp => Request.Kind == GpuAdapterKind.Warp;

    /// <summary>この量のテクスチャを放すたびに、WARP に Trim させる(AC-61・NFR-8)。</summary>
    internal const long TrimFreedBytes = 256_000_000;

    private long _freedSinceTrim;

    /// <summary>WARP のときに Trim した回数(テスト用)。</summary>
    internal int TrimCount { get; private set; }

    /// <summary>WARP は放したテクスチャのメモリを抱えたままにするので、Flush と Trim で返させる。実 GPU では何もしない。</summary>
    internal void TrimIfWarp()
    {
        if (!IsWarp) return;
        _freedSinceTrim = 0;
        Context.Flush();
        using var dxgi = Device.QueryInterface<IDXGIDevice3>();
        dxgi.Trim();
        TrimCount++;
    }

    /// <summary>テクスチャを放した量を数え、TrimFreedBytes を超えたら TrimIfWarp。持ち主のスレッドだけから呼ぶ。</summary>
    internal void NoteFreed(long bytes)
    {
        if (IsWarp && (_freedSinceTrim += bytes) >= TrimFreedBytes) TrimIfWarp();
    }

    /// <summary>表示用の名前(WARP は "WARP")。</summary>
    public string Name => Adapter?.Description ?? "WARP";

    internal ID3D11Device Device
    {
        get
        {
            EnsureOwnerThread();
            return _device;
        }
    }

    internal ID3D11DeviceContext Context
    {
        get
        {
            EnsureOwnerThread();
            return _context;
        }
    }

    /// <summary>
    /// 要求のアダプターでデバイスを作る。呼んだスレッドが持ち主になる。作れなければ例外(呼び手が WARP へ切り替える。FR-94)。
    /// 機能レベルが 10_x までのアダプターは GpuFeatureLevelTooLowException で拒む(NFR-11)。
    /// </summary>
    public static GpuDevice Create(GpuAdapterRequest request) => Create(request, CreateD3D11Device);

    /// <summary>D3D11CreateDevice の形(アダプターは開いたものを GpuAdapterHandle で受ける)。テストが偽の作成関数を差し込むためにある(実機の 10_x アダプターの代わり)。</summary>
    internal delegate Result CreateDeviceFunc(
        GpuAdapterHandle adapter,
        DriverType driverType,
        FeatureLevel[] levels,
        out ID3D11Device? device,
        out FeatureLevel level,
        out ID3D11DeviceContext? context);

    /// <summary>本物の作成関数(開いたアダプターの Native で D3D11CreateDevice をそのまま呼ぶ)。</summary>
    internal static Result CreateD3D11Device(
        GpuAdapterHandle adapter,
        DriverType driverType,
        FeatureLevel[] levels,
        out ID3D11Device? device,
        out FeatureLevel level,
        out ID3D11DeviceContext? context) =>
        D3D11.D3D11CreateDevice(adapter.Native, driverType, DeviceCreationFlags.None, levels, out device, out level, out context);

    /// <summary>作成関数を指定して、要求する機能レベル FeatureLevels で作る(テストの差し込み口。公開の Create と同じ道)。</summary>
    internal static GpuDevice Create(GpuAdapterRequest request, CreateDeviceFunc createDevice) =>
        Create(request, FeatureLevels, createDevice);

    /// <summary>作成関数とアダプターを開く関数を指定して作る(テストの差し込み口。DXGI を呼ばずに偽のアダプターを渡す)。</summary>
    internal static GpuDevice Create(GpuAdapterRequest request, CreateDeviceFunc createDevice, Func<GpuAdapterRequest, GpuAdapterHandle> openAdapter) =>
        Create(request, FeatureLevels, createDevice, openAdapter);

    /// <summary>要求する機能レベルを指定して作る(テストの差し込み口)。11_0 未満のデバイスになったら返さずに拒む。</summary>
    internal static GpuDevice Create(GpuAdapterRequest request, FeatureLevel[] levels) => Create(request, levels, CreateD3D11Device);

    /// <summary>要求する機能レベルと作成関数を指定して作る(アダプターは本物の DXGI で開く)。</summary>
    internal static GpuDevice Create(GpuAdapterRequest request, FeatureLevel[] levels, CreateDeviceFunc createDevice) =>
        Create(request, levels, createDevice, GpuAdapters.OpenHandle);

    /// <summary>
    /// 要求する機能レベル・作成関数・アダプターを開く関数を指定して作る。
    /// 11_0 以上で作れなければ、同じ作成関数と同じ開く関数で 10_x を試して拒む理由を分ける。
    /// </summary>
    internal static GpuDevice Create(
        GpuAdapterRequest request,
        FeatureLevel[] levels,
        CreateDeviceFunc createDevice,
        Func<GpuAdapterRequest, GpuAdapterHandle> openAdapter)
    {
        using var adapter = openAdapter(request);
        // アダプターを渡すときは DriverType.Unknown にする(Hardware だと E_INVALIDARG)。WARP だけはアダプターを渡さない。
        var driverType = request.Kind == GpuAdapterKind.Warp ? DriverType.Warp : DriverType.Unknown;
        var hr = createDevice(
            adapter,
            driverType,
            levels,
            out ID3D11Device? device,
            out FeatureLevel level,
            out ID3D11DeviceContext? context);
        if (hr.Failure || device is null || context is null)
        {
            device?.Dispose();
            context?.Dispose();
            if (levels.Any(l => l >= MinimumFeatureLevel))
            {
                // 11_0 以上で作れない。10_x でなら作れるかを同じ手順で調べ、作れれば下の検査が理由付きで拒む
                try
                {
                    using var low = Create(request, TooLowFeatureLevels, createDevice, openAdapter);
                }
                catch (GpuFeatureLevelTooLowException)
                {
                    throw;
                }
                catch (InvalidOperationException)
                {
                    // 10_x でも作れない。元の HRESULT で投げる
                }
            }
            throw new InvalidOperationException($"Direct3D 11 のデバイスを作れない({request}、HRESULT 0x{hr.Code:X8})");
        }

        if (level < MinimumFeatureLevel)
        {
            context.Dispose();
            device.Dispose();
            throw new GpuFeatureLevelTooLowException(request, level);
        }

        GpuAdapterInfo? info = null;
        if (adapter.Native is not null)
        {
            info = GpuAdapters.ToInfo(request.Index, adapter.Native.Description1);
            if (request.Kind is GpuAdapterKind.HighPerformance or GpuAdapterKind.ByLuid)
            {
                // 番号で指定していないときは、EnumAdapters1 の番号を LUID で引き当てて入れる
                var index = GpuAdapters.Enumerate().FirstOrDefault(a => a.Luid == info.Luid)?.Index ?? -1;
                info = info with { Index = index };
            }
        }
        return new GpuDevice(device, context, level, request, info, adapter.Luid);
    }

    /// <summary>デバイスが失われていれば失敗の HRESULT(ID3D11Device::GetDeviceRemovedReason)。FR-93 の検出に使う。</summary>
    internal Result RemovedReason => Device.DeviceRemovedReason;

    /// <summary>形式の対応フラグ(CheckFormatSupport)。</summary>
    public FormatSupport CheckFormatSupport(Format format) => Device.CheckFormatSupport(format);

    /// <summary>持ち主のスレッド以外から呼ばれたら InvalidOperationException(INV-5 の表明)。</summary>
    public void EnsureOwnerThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != OwnerThreadId)
        {
            throw new InvalidOperationException(
                $"GPU は持ち主のスレッド({OwnerThreadId})だけで使う。呼んだスレッドは {Environment.CurrentManagedThreadId}(INV-5)");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureOwnerThread();
        _disposed = true;
        _context.ClearState();
        _context.Flush();
        _context.Dispose();
        _device.Dispose();
    }
}

/// <summary>アダプターの機能レベルが 11_0 未満なので使わない(NFR-11)。FR-94 では WARP に切り替える理由になる。</summary>
public sealed class GpuFeatureLevelTooLowException(GpuAdapterRequest request, FeatureLevel level)
    : InvalidOperationException(
        $"機能レベル {level.ToString().Replace("Level_", "")} が 11_0 未満のため使わない({request}。シェーダーが ps_5_0 で 11_0 以上が要る。NFR-11)")
{
    public GpuAdapterRequest Request { get; } = request;

    /// <summary>そのアダプターで作れた最も高い機能レベル(11_0 未満)。</summary>
    public FeatureLevel Level { get; } = level;
}
