// テストの偽の実装: 時計とタイマー(FakeClock)・OS の情報(FakeOsInfo)・修飾キー・ファイルダイアログ・クリップボード・ドラッグの実行・デバイスの作り方(INV-18 の約束)。偽のファイルシステムは FakeFileSystem.cs。
// FakeClock.Advance は、進めた時刻までに期限の来たタイマーを、呼んだスレッドで順に鳴らす。UI の部品のタイマーは UiHost.Invoke の中で Advance する。
// FakeOsInfo は値を変えると Changed を出す(製品の WM_SETTINGCHANGE の代わり)。FakeFileDialogs は次に返す答えを積んでおく。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using GlintLoop.App.Platform;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Platform;
using GlintLoop.Render;

namespace GlintLoop.Tests.Ui;

public sealed class FakeClock : IClock, ITimerFactory
{
    private readonly List<FakeTimer> _timers = new();

    /// <summary>Phase 5 のテストの開始時刻(既定のタイムゾーン +9 のローカル時刻で 2026-10-14 20:38:05。§11 Phase 5 の前書き)。</summary>
    public static readonly DateTimeOffset Phase5Start = new DateTimeOffset(2026, 10, 14, 20, 38, 5, TimeSpan.FromHours(9)).ToUniversalTime();

    /// <summary>Phase 5 の開始時刻から始める偽の時計。</summary>
    public static FakeClock ForPhase5() => new(Phase5Start);

    public FakeClock(DateTimeOffset? start = null, TimeZoneInfo? zone = null)
    {
        UtcNow = start ?? new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
        LocalZone = zone ?? TimeZoneInfo.CreateCustomTimeZone("Test+9", TimeSpan.FromHours(9), "Test+9", "Test+9");
    }

    public DateTimeOffset UtcNow { get; set; }

    public TimeZoneInfo LocalZone { get; set; }

    public IOneShotTimer Create()
    {
        var t = new FakeTimer(this);
        _timers.Add(t);
        return t;
    }

    /// <summary>時刻を進め、期限の来たタイマーを期限の順に鳴らす。</summary>
    public void Advance(TimeSpan by)
    {
        var end = UtcNow + by;
        while (true)
        {
            var next = _timers.Where(t => t.Due is { } d && d <= end).OrderBy(t => t.Due).FirstOrDefault();
            if (next is null) break;
            UtcNow = next.Due!.Value;
            next.Fire();
        }
        UtcNow = end;
    }

    private sealed class FakeTimer(FakeClock clock) : IOneShotTimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool IsRunning => Due is not null;

        public event Action? Elapsed;

        public void Start(TimeSpan due) => Due = clock.UtcNow + due;

        public void Stop() => Due = null;

        public void Fire()
        {
            Due = null;
            Elapsed?.Invoke();
        }

        public void Dispose()
        {
            Due = null;
            clock._timers.Remove(this);
        }
    }
}

public sealed class FakeOsInfo : IOsInfo
{
    private bool _light;
    private bool _animations = true;
    private bool _transparency = true;
    private bool _highContrast;
    private int _build = 26100;
    private IReadOnlyList<MonitorInfo> _monitors = [new MonitorInfo(new Rect(0, 0, 1920, 1040), true)];

    public bool AppsUseLightTheme { get => _light; set => Set(ref _light, value); }

    public bool AnimationsEnabled { get => _animations; set => Set(ref _animations, value); }

    public bool TransparencyEnabled { get => _transparency; set => Set(ref _transparency, value); }

    public bool HighContrast { get => _highContrast; set => Set(ref _highContrast, value); }

    public int BuildNumber { get => _build; set => Set(ref _build, value); }

    public IReadOnlyList<MonitorInfo> Monitors { get => _monitors; set => Set(ref _monitors, value); }

    public event Action? Changed;

    /// <summary>Refresh が呼ばれた回数(製品は WM_SETTINGCHANGE で呼ぶ)。</summary>
    public int RefreshCount { get; private set; }

    public void Refresh() => RefreshCount++;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke();
    }
}

public sealed class FakeModifierKeys : IModifierKeys
{
    public ModifierKeys Current { get; set; }
}

public sealed class FakeFileDialogs : IFileDialogs
{
    public Queue<IReadOnlyList<string>?> OpenAnswers { get; } = new();

    public Queue<string?> SaveAnswers { get; } = new();

    public Queue<string?> FolderAnswers { get; } = new();

    public int Calls { get; private set; }

    /// <summary>保存のダイアログに渡った引数(古い順)。</summary>
    public List<(string? InitialDir, string FileName, IReadOnlyList<FileDialogFilter> Filters)> SaveCalls { get; } = new();
    /// <summary>最後の開くダイアログの絞り込み。</summary>
    public IReadOnlyList<FileDialogFilter>? LastOpenFilters { get; private set; }

    /// <summary>最後の保存のダイアログの引数。</summary>
    public (string? InitialDir, string FileName, IReadOnlyList<FileDialogFilter> Filters)? LastSave { get; private set; }

    public IReadOnlyList<string>? OpenFiles(string? initialDir, IReadOnlyList<FileDialogFilter> filters, bool multiselect)
    {
        Calls++;
        LastOpenFilters = filters;
        return OpenAnswers.Count > 0 ? OpenAnswers.Dequeue() : null;
    }

    public string? SaveFile(string? initialDir, string fileName, IReadOnlyList<FileDialogFilter> filters)
    {
        Calls++;
        SaveCalls.Add((initialDir, fileName, filters));
        LastSave = (initialDir, fileName, filters);
        return SaveAnswers.Count > 0 ? SaveAnswers.Dequeue() : null;
    }

    public string? PickFolder(string? initialDir)
    {
        Calls++;
        return FolderAnswers.Count > 0 ? FolderAnswers.Dequeue() : null;
    }
}

/// <summary>
/// 偽のクリップボード。Data に入れたものを Read が返す。CantOpen を true にすると開けない(E-2)。
/// Place は置かれたデータと第 2 引数を Placed に記録する。PlaceHResult を入れると、その HRESULT の COMException を投げ続け、何も置かない(PlaceCalls は数える)。
/// </summary>
public sealed class FakeClipboard : IClipboardService
{
    private readonly List<(IDataObject Data, bool KeepAfterExit)> _placed = new();

    public IImportData Data { get; set; } = new MemoryImportData();

    public bool CantOpen { get; set; }

    /// <summary>Read が呼ばれた回数(投げた回も数える)。</summary>
    public int ReadCalls { get; private set; }

    /// <summary>入れると、読み取りをこれに任せる(WpfClipboard の例外の変換を通すテスト用)。</summary>
    public IClipboardService? ReadThrough { get; set; }

    public IImportData Read()
    {
        ReadCalls++;
        if (ReadThrough is { } through) return through.Read();
        return CantOpen ? throw new ImportDataUnavailableException(0x800401D0) : Data;
    }

    public int? PlaceHResult { get; set; }

    /// <summary>Place が呼ばれた回数(投げた回も数える)。</summary>
    public int PlaceCalls { get; private set; }

    /// <summary>置かれたデータと第 2 引数(古い順)。</summary>
    public IReadOnlyList<(IDataObject Data, bool KeepAfterExit)> Placed => _placed;

    public void Place(IDataObject data, bool keepAfterExit)
    {
        PlaceCalls++;
        if (PlaceHResult is { } hr) throw new COMException("偽のクリップボードの失敗", hr);
        _placed.Add((data, keepAfterExit));
    }
}

/// <summary>偽のドラッグの実行。受け取った引数を Runs に記録し、Result を返す。</summary>
public sealed class FakeDragRunner : IDragRunner
{
    private readonly List<(DependencyObject Source, IDataObject Data, DragDropEffects Allowed)> _runs = new();

    public DragDropEffects Result { get; set; } = DragDropEffects.Copy;

    /// <summary>呼ばれている間に動かす処理(ドラッグ中の検査に使う)。</summary>
    public Action? During { get; set; }

    public IReadOnlyList<(DependencyObject Source, IDataObject Data, DragDropEffects Allowed)> Runs => _runs;

    public DragDropEffects Run(DependencyObject source, IDataObject data, DragDropEffects allowed)
    {
        _runs.Add((source, data, allowed));
        During?.Invoke();
        return Result;
    }
}

/// <summary>
/// 偽のデバイス工場(AC-49)。どの要求にも WARP の実体を返す(INV-18)が、デバイスの Request は要求のままなので、高性能の要求では IsWarp が false になる。要求を Requests に記録する。
/// Adapters はいまある偽の GPU の識別子(LUID)で、先頭が高性能。ByLuid の要求は、その識別子が Adapters に無ければ作成に失敗する。作ったデバイスの識別子を CreatedLuids に記録する。
/// Fail が真を返す要求と、FailNextCreates の回数の分は作成に失敗する。FailStartupCheck は起動時の自己検査を落とす。FailRender は指定した回の描画の依頼で HRESULT の例外を投げる。
/// 呼ばれるのは描画のスレッド。
/// </summary>
public sealed class FakeDeviceFactory : IDeviceFactory
{
    public const int DeviceRemoved = unchecked((int)0x887A0005);
    public const int DeviceReset = unchecked((int)0x887A0007);
    public const int OutOfMemory = unchecked((int)0x8007000E);
    public const int EFail = unchecked((int)0x80004005);

    /// <summary>既定の偽の GPU の識別子(どの PC にも無い大きさにする)。</summary>
    public const long DefaultLuid = 0x7A00_0000_0000_0001;

    private sealed record RenderRule(int HResult, RenderPriority? Priority)
    {
        public int Remaining;
    }

    private readonly object _gate = new();
    private readonly List<GpuAdapterRequest> _requests = new();
    private readonly List<long?> _createdLuids = new();
    private long[] _adapters = [DefaultLuid];
    private readonly List<RenderRule> _renderRules = new();
    private readonly int[] _renderCounts = new int[3];
    private int _failNextCreates;

    public Func<GpuAdapterRequest, bool>? Fail { get; set; }

    /// <summary>作成の呼び出しのたびに、失敗させるかを決める前に呼ぶ(作成の時点の様子を調べるテスト用)。</summary>
    public Action<GpuAdapterRequest>? OnCreate { get; set; }

    /// <summary>起動時の自己検査に落とす(R16G16B16A16_FLOAT の確認の失敗の代わり)。</summary>
    public bool FailStartupCheck { get; set; }

    /// <summary>これからの n 回の作成を失敗させる。</summary>
    public int FailNextCreates
    {
        get
        {
            lock (_gate) return _failNextCreates;
        }
        set
        {
            lock (_gate) _failNextCreates = value;
        }
    }

    /// <summary>いまある偽の GPU(先頭が高性能)。</summary>
    public long[] Adapters
    {
        get
        {
            lock (_gate) return _adapters;
        }
        set
        {
            lock (_gate) _adapters = value;
        }
    }

    /// <summary>作成に成功したデバイスのアダプターの識別子(古い順。WARP は null)。</summary>
    public IReadOnlyList<long?> CreatedLuids
    {
        get
        {
            lock (_gate) return _createdLuids.ToArray();
        }
    }

    public IReadOnlyList<GpuAdapterRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToArray();
        }
    }

    /// <summary>いまから数えて nth 回目の描画の依頼(priority を渡すとその優先度の依頼だけを数える)で、hresult の例外を投げる。</summary>
    public void FailRender(int hresult, int nth, RenderPriority? priority = null)
    {
        lock (_gate) _renderRules.Add(new RenderRule(hresult, priority) { Remaining = nth });
    }

    /// <summary>これまでの描画の依頼の回数(priority を渡すとその優先度だけ)。</summary>
    public int RenderRequests(RenderPriority? priority = null)
    {
        lock (_gate) return priority is { } p ? _renderCounts[(int)p] : _renderCounts.Sum();
    }

    public GpuDevice Create(GpuAdapterRequest request)
    {
        OnCreate?.Invoke(request);
        lock (_gate)
        {
            _requests.Add(request);
            if (_failNextCreates > 0)
            {
                _failNextCreates--;
                throw new InvalidOperationException("偽のデバイスの作成の失敗");
            }
        }
        if (Fail?.Invoke(request) == true) throw new InvalidOperationException("偽のデバイスの作成の失敗");
        var adapters = Adapters;
        long? luid = request.Kind switch
        {
            GpuAdapterKind.Warp => null,
            GpuAdapterKind.ByLuid => adapters.Contains(request.Luid) ? request.Luid : throw new InvalidOperationException("偽のアダプターが無い"),
            _ => adapters[0],
        };
        var device = GpuDevice.Create(request,
            static (GpuAdapterHandle _, Vortice.Direct3D.DriverType _, Vortice.Direct3D.FeatureLevel[] levels,
                out Vortice.Direct3D11.ID3D11Device? device, out Vortice.Direct3D.FeatureLevel level, out Vortice.Direct3D11.ID3D11DeviceContext? context) =>
                GpuDevice.CreateD3D11Device(GpuAdapterHandle.None, Vortice.Direct3D.DriverType.Warp, levels, out device, out level, out context),
            _ => new GpuAdapterHandle(null, luid));
        lock (_gate) _createdLuids.Add(device.AdapterLuid);
        return device;
    }

    public GpuCheckResult StartupCheck(PassRenderer renderer)
    {
        var real = GpuSelfTest.StartupCheck(renderer);
        return FailStartupCheck ? real with { Passed = false } : real;
    }

    public void BeforeRender(RenderPriority priority)
    {
        lock (_gate)
        {
            _renderCounts[(int)priority]++;
            foreach (var rule in _renderRules)
            {
                if (rule.Priority is { } p && p != priority) continue;
                if (--rule.Remaining != 0) continue;
                _renderRules.Remove(rule);
                throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(rule.HResult));
            }
        }
    }
}
