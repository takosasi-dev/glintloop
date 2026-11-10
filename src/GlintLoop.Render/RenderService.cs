// 描画の窓口(仕様書 §2 の RenderService、D-37・INV-5)。GPU 専用の RenderThread の上に GpuDevice と RenderContext を持ち、外からは Task を返す依頼(InvokeAsync)だけで使う。GpuTexture をスレッドの外へ渡さない。
// アダプターは作るときに指定する(テストは WARP)。起動時に WARP へ切り替えた理由(FR-94)は FallbackReason に入れる。
// デバイスが失われたら(FR-93)中身のデバイスだけを放して依頼を待たせ、DeviceRecovery の指示で作り直してやり直す。呼び手はこの参照を持ち続けてよい。書き出しの依頼は待たせず失敗させる(E-39)。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Render.Effects;
using SharpGen.Runtime;
using Vortice.Direct3D;

namespace GlintLoop.Render;

public sealed class RenderService : IDisposable
{
    /// <summary>デバイスの状態。Lost は作り直しを待っている間、Abandoned は作り直しをあきらめたあとと破棄のあと。</summary>
    private enum DeviceState { Ready, Lost, Abandoned }

    /// <summary>いまのデバイスの情報(どのスレッドからも読める)。</summary>
    private sealed record DeviceInfo(GpuAdapterRequest Request, long? AdapterLuid, bool IsWarp, string AdapterName, FeatureLevel FeatureLevel, int MaxTextureDimension)
    {
        public static DeviceInfo Of(RenderContext c) => new(c.Gpu.Request, c.Gpu.AdapterLuid, c.Gpu.IsWarp, c.Gpu.Name, c.Gpu.FeatureLevel, c.MaxTextureDimension);
    }

    /// <summary>依頼 1 つ。消失のあとでやり直せるように、仕事と結果の受け口を持ち歩く。</summary>
    private abstract class Job(RenderPriority priority)
    {
        public RenderPriority Priority { get; } = priority;

        public abstract void Execute(RenderContext context);

        public abstract void Fail(Exception e);
    }

    private sealed class Job<T>(Func<RenderContext, T> work, RenderPriority priority) : Job(priority)
    {
        private readonly TaskCompletionSource<T> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _result.Task;

        public override void Execute(RenderContext context) => _result.TrySetResult(work(context));

        public override void Fail(Exception e) => _result.TrySetException(e);
    }

    private readonly RenderThread _thread;
    private readonly IDeviceFactory _factory;

    // 以下の 3 つは描画の専用スレッドの上だけで触る
    private RenderContext? _context;
    private readonly List<Job> _parked = [];
    private Action? _onLost;

    private volatile DeviceInfo _info;
    private volatile DeviceState _state = DeviceState.Ready;
    private volatile TaskCompletionSource _settled = Settled();
    private bool _disposed;

    private RenderService(RenderThread thread, RenderContext context, EffectCatalog effects, GpuFallbackReason? fallbackReason, IDeviceFactory factory)
    {
        _thread = thread;
        _factory = factory;
        _context = context;
        _info = DeviceInfo.Of(context);
        Effects = effects;
        FallbackReason = fallbackReason;
    }

    /// <summary>要求のアダプターで作る(呼んだスレッドは終わるまで待つ。UI スレッドからは CreateAsync を使う)。</summary>
    public static RenderService Create(GpuAdapterRequest request, ILogSink? log = null, IDeviceFactory? deviceFactory = null) =>
        CreateAsync(request, log, deviceFactory).GetAwaiter().GetResult();

    /// <summary>要求のアダプターで、専用スレッドの上にデバイスを作る。EffectDef の読み込み(埋め込みリソースだけ)もそこで行う。deviceFactory を渡すと、デバイスの作成と起動時の自己検査はそれを通す。</summary>
    public static async Task<RenderService> CreateAsync(GpuAdapterRequest request, ILogSink? log = null, IDeviceFactory? deviceFactory = null)
    {
        var factory = deviceFactory ?? DefaultDeviceFactory.Instance;
        var thread = new RenderThread();
        try
        {
            var (context, effects, reason) = await thread.InvokeAsync(() =>
            {
                var pick = GpuSelfTest.CreateWithFallback(request, factory);
                var effects = BuiltInEffects.Load(log ?? NullLogSink.Instance);
                return (new RenderContext(pick.Device, pick.Renderer), effects, pick.Fallback);
            }).ConfigureAwait(false);
            return new RenderService(thread, context, effects, reason, factory);
        }
        catch
        {
            thread.Dispose();
            throw;
        }
    }

    private static TaskCompletionSource Settled()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    /// <summary>読み込んだ EffectDef(埋め込みリソースから、検査に通ったものだけ)。</summary>
    public EffectCatalog Effects { get; }

    /// <summary>起動時に要求のアダプターを使えず WARP に切り替えたときの理由(FR-94)。切り替えていなければ null。</summary>
    public GpuFallbackReason? FallbackReason { get; }

    /// <summary>いまのデバイスが WARP か(消失で WARP に切り替えると true になる。FR-93)。</summary>
    public bool IsWarp => _info.IsWarp;

    /// <summary>アダプターの名前(WARP は "WARP")。</summary>
    public string AdapterName => _info.AdapterName;

    public FeatureLevel FeatureLevel => _info.FeatureLevel;

    /// <summary>アダプターのテクスチャの最大寸法(§9.7 の規則 2)。取り込みの縮小の判定に使う。</summary>
    public int MaxTextureDimension => _info.MaxTextureDimension;

    /// <summary>いまのデバイスを作った要求。</summary>
    public GpuAdapterRequest Request => _info.Request;

    /// <summary>いまのデバイスのアダプターの識別子(WARP のときは null)。</summary>
    public long? AdapterLuid => _info.AdapterLuid;

    /// <summary>いまのデバイスと同じアダプターで作り直す要求(FR-93)。WARP はアダプターが無いので WARP のまま。</summary>
    internal GpuAdapterRequest SameAdapterRequest => _info.AdapterLuid is { } luid ? GpuAdapterRequest.ByLuid(luid) : _info.Request;

    /// <summary>デバイスが失われたまま、まだ作り直せていないか。</summary>
    public bool IsDeviceLost => _state != DeviceState.Ready;

    /// <summary>デバイスが失われている間は、作り直して使えるようになるか、あきらめるまで終わらない Task(失われていなければ終わっている)。結果は終わったあとの IsDeviceLost で見る。</summary>
    public Task DeviceSettled => _settled.Task;

    /// <summary>デバイスを放す直前に専用スレッドの上で鳴る。受け手(DocumentRenderer)はそのデバイスのテクスチャをここで全部放す。</summary>
    internal event Action? DeviceReleasing;

    /// <summary>測定の記録(§6.8 の render_calls)。受け付けた依頼を 1 つずつ数える。記録しないときは null。</summary>
    public PerfRecorder? Perf { get; set; }

    /// <summary>専用スレッドで work を実行する。work の中でだけ RenderContext と GpuTexture を使ってよい。priority は待ち行列の優先度(§9.9 の 8。既定は表示)。消失のあとでもう一度呼ばれることがある。</summary>
    public Task<T> InvokeAsync<T>(Func<RenderContext, T> work, RenderPriority priority = RenderPriority.Display)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Perf?.CountRenderCall();
        var job = new Job<T>(work, priority);
        Post(job);
        return job.Task;
    }

    public Task InvokeAsync(Action<RenderContext> work, RenderPriority priority = RenderPriority.Display)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(c =>
        {
            work(c);
            return true;
        }, priority);
    }

    /// <summary>デバイスの状態に関わらず専用スレッドで動かす後始末(テクスチャの解放など。描画の依頼に数えない)。</summary>
    internal Task InvokeCleanupAsync(Action work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _thread.InvokeAsync(work);
    }

    private void Post(Job job) => _thread.InvokeAsync(() => Run(job), job.Priority);

    private void Run(Job job)
    {
        if (_state == DeviceState.Abandoned)
        {
            job.Fail(new GpuDeviceLostException());
            return;
        }
        if (_state == DeviceState.Lost)
        {
            Park(job);
            return;
        }
        try
        {
            _factory.BeforeRender(job.Priority);
            job.Execute(_context!);
        }
        catch (Exception e) when (_onLost is not null && IsLost(e))
        {
            EnterLost();
            Park(job);
        }
        catch (Exception e)
        {
            job.Fail(e);
        }
    }

    // 呼び出しが消失の HRESULT を返したとき。ほかの HRESULT でも、デバイス自身が失われたと答えるなら消失として扱う
    private bool IsLost(Exception e) => GpuErrors.IsDeviceLost(e) || (e is SharpGenException && _context is { } c && c.Gpu.RemovedReason.Failure);

    private void Park(Job job)
    {
        // 書き出しは .tmp を消してやり直すので待たせない(E-39)。表示とサムネイルは作り直したあとで同じ依頼を実行する
        if (job.Priority == RenderPriority.Export) job.Fail(new GpuDeviceLostException());
        else _parked.Add(job);
    }

    private void EnterLost()
    {
        _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _state = DeviceState.Lost;
        ReleaseDevice();
        _onLost?.Invoke();
    }

    /// <summary>デバイスと、それに結びついたものをすべて放す(失われたデバイスでも COM の解放はできる)。</summary>
    private void ReleaseDevice()
    {
        DeviceReleasing?.Invoke();
        if (_context is not { } c) return;
        _context = null;
        var gpu = c.Gpu;
        c.Dispose();
        c.Passes.Dispose();
        gpu.Dispose();
    }

    // ================================================================ DeviceRecovery が使う口

    /// <summary>消失を検出したら onLost を呼ぶようにする(専用スレッドで呼ばれる)。呼ばないうちは、消失の例外をそのまま依頼の結果にする。</summary>
    internal void SetLostHandler(Action onLost) => _thread.InvokeAsync(() => _onLost = onLost);

    /// <summary>request でデバイスを作り直す。結果は失敗の例外で、成功は null。依頼はまだ再開しない(ResumeAsync)。</summary>
    internal Task<Exception?> RecreateAsync(GpuAdapterRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _thread.InvokeAsync<Exception?>(() =>
        {
            GpuDevice? device = null;
            PassRenderer? passes = null;
            try
            {
                device = _factory.Create(request);
                passes = new PassRenderer(device);
                var context = new RenderContext(device, passes);
                _context = context;
                _info = DeviceInfo.Of(context);
                return null;
            }
            catch (Exception e)
            {
                passes?.Dispose();
                device?.Dispose();
                return e;
            }
        });
    }

    /// <summary>作り直しが済んだ。待たせていた依頼を積み直す。</summary>
    internal Task ResumeAsync() => InvokeCleanupAsync(() =>
    {
        if (_state != DeviceState.Lost) return;
        _state = DeviceState.Ready;
        foreach (var job in _parked) Post(job);
        _parked.Clear();
        _settled.TrySetResult();
    });

    /// <summary>作り直しをあきらめた。待たせていた依頼を失敗にして、これからの依頼も失敗にする。</summary>
    internal Task AbandonAsync() => InvokeCleanupAsync(Abandon);

    private void Abandon()
    {
        _state = DeviceState.Abandoned;
        foreach (var job in _parked) job.Fail(new GpuDeviceLostException());
        _parked.Clear();
        _settled.TrySetResult();
    }

    /// <summary>
    /// sRGB 8 ビット・straight の RGBA(幅×4 バイトの行)を取り込み、stack を掛けて、書き出し用の straight RGBA 8 ビットで返す(規則 7)。
    /// </summary>
    public Task<byte[]> RenderStraightRgba8Async(byte[] srgbRgba, int width, int height, IReadOnlyList<EffectInvocation> stack, FrameContext frame) =>
        Render(srgbRgba, width, height, stack, frame, static (c, t) => c.ReadStraightRgba8(t));

    /// <summary>同じ処理で、表示用の Pbgra32 で返す(規則 7)。</summary>
    public Task<byte[]> RenderPbgra32Async(byte[] srgbRgba, int width, int height, IReadOnlyList<EffectInvocation> stack, FrameContext frame) =>
        Render(srgbRgba, width, height, stack, frame, static (c, t) => c.ReadPbgra32(t));

    /// <summary>同じ処理で、RGBA16F のまま(行優先の RGBA の float)返す。</summary>
    public Task<float[]> RenderRgba16FAsync(byte[] srgbRgba, int width, int height, IReadOnlyList<EffectInvocation> stack, FrameContext frame) =>
        Render(srgbRgba, width, height, stack, frame, static (c, t) => c.ReadRgba16F(t));

    private Task<T> Render<T>(byte[] srgbRgba, int width, int height, IReadOnlyList<EffectInvocation> stack, FrameContext frame,
        Func<RenderContext, GpuTexture, T> read)
    {
        ArgumentNullException.ThrowIfNull(srgbRgba);
        ArgumentNullException.ThrowIfNull(stack);
        return InvokeAsync(c =>
        {
            using var input = c.IngestSrgb8(srgbRgba, width, height, width * 4);
            using var output = c.RunStack(stack, input, frame);
            return read(c, output);
        });
    }

    /// <summary>待たせている依頼を失敗にし、専用スレッドの上で GPU を放して、スレッドを止める。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thread.Invoke(() =>
        {
            Abandon();
            ReleaseDevice();
        });
        _thread.Dispose();
    }
}
