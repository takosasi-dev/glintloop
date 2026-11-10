// FR-93・AC-49 の (1) の「そのアダプターの識別子が最初のデバイスと同じ」: 作り直しは最初のデバイスのアダプターの識別子(LUID)に固定する。その GPU が無ければ作り直しの失敗で、2 回続けば WARP。
// 偽の工場の Adapters で、高性能の先頭が別の GPU に変わった場合と、元の GPU が外れた場合を作る。実際の GPU は使わない(INV-18)。
using GlintLoop.Core.Notices;
using GlintLoop.Render;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using Ac49 = GlintLoop.Tests.Render.GpuRecoveryAc49Tests;

namespace GlintLoop.Tests.Render;

public sealed class SameAdapterAc49Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    private const long First = FakeDeviceFactory.DefaultLuid;
    private const long Other = FakeDeviceFactory.DefaultLuid + 1;
    private const int Removed = FakeDeviceFactory.DeviceRemoved;

    private static RenderService Service(TestApp app) => app.Ui(() => app.Shell.RenderReady!.Result);

    /// <summary>待たせていた描画の依頼が全部終わるまで待つ(閉じるときに残さない)。書き出しの優先度の依頼は、ほかの依頼のあとに動く。</summary>
    private static void Drain(TestApp app)
    {
        var service = Service(app);
        Check.True(UiHost.WaitUntil(() => !service.IsDeviceLost, Limit), "作り直しが終わらない");
        service.InvokeAsync(_ => 0, RenderPriority.Export).GetAwaiter().GetResult();
        UiHost.Idle();
    }

    private static void Lose(TestApp app, FakeDeviceFactory factory, int edit)
    {
        factory.FailRender(Removed, 1, RenderPriority.Display);
        Ac49.Edit(app, edit);
    }

    [Test(Slow = true)]
    public void AC49_1_高性能の先頭が別のGPUになっても最初のデバイスと同じ識別子で作り直す()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory);
        Check.SequenceEqual<long?>([First], factory.CreatedLuids, "最初のデバイス");
        Check.True(Service(app).AdapterLuid == First, "最初のデバイスの識別子");
        factory.Adapters = [Other, First];
        Lose(app, factory, 0);
        Ac49.WaitEvent(app, "device_recreated");
        Check.SequenceEqual<long?>([First, First], factory.CreatedLuids, "作り直したデバイスの識別子が最初と同じ");
        Check.Equal(GpuAdapterRequest.ByLuid(First), factory.Requests[^1], "識別子で頼む");
        Check.True(Service(app).AdapterLuid == First, "RenderService の識別子");
        Check.True(!Service(app).IsWarp, "WARP にしない");
        Check.Equal("hardware", Ac49.Last(app, "device_recreated").GetProperty("adapter").GetString(), "adapter");
        Drain(app);
    }

    [Test(Slow = true)]
    public void FR93_2回目の消失でも最初のデバイスと同じ識別子で作り直す()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory);
        var canvas = EditingScreenKitModule(app);
        factory.Adapters = [Other, First];
        Lose(app, factory, 0);
        Ac49.WaitEvent(app, "device_recreated");
        Ac49.WaitShown(app, canvas);
        Lose(app, factory, 1);
        Ac49.WaitEvent(app, "device_recreated", 2);
        Check.SequenceEqual<long?>([First, First, First], factory.CreatedLuids, "3 個とも同じ識別子");
        Drain(app);
    }

    private static GlintLoop.App.Viewing.CanvasModule EditingScreenKitModule(TestApp app) => EditingScreenKit.M<GlintLoop.App.Viewing.CanvasModule>(app);

    [Test(Slow = true)]
    public void FR93_元のGPUが無ければ別のGPUを選ばず作り直しの失敗に数えて2回でWARPにする()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory);
        var created = factory.Requests.Count;
        factory.Adapters = [Other];
        Lose(app, factory, 0);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        app.Ui(() => app.Clock.Advance(DeviceRecovery.RetryDelay));
        Ac49.WaitEvent(app, "device_recreated");
        Check.SequenceEqual([GpuAdapterRequest.ByLuid(First), GpuAdapterRequest.ByLuid(First), GpuAdapterRequest.Warp], factory.Requests.Skip(created).ToArray(), "作り直しの要求");
        Check.SequenceEqual<long?>([First, null], factory.CreatedLuids, "別の GPU のデバイスを作らない");
        Check.True(Service(app).IsWarp, "WARP に切り替わる");
        Check.True(Service(app).AdapterLuid is null, "WARP に識別子は無い");
        Check.SequenceEqual(["device_lost", "warp_fallback", "device_recreated"], Ac49.Events(app, "device_lost", "warp_fallback", "device_recreated"), "ログの順");
        Check.Equal("warp", Ac49.Last(app, "device_recreated").GetProperty("adapter").GetString(), "adapter");
        Check.Equal(NoticeKeys.GpuSoftware, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "gpu.software");
        Drain(app);
    }

    [Test(Slow = true)]
    public void FR93_元のGPUが2回目までに戻れば同じ識別子で作り直しWARPにしない()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory);
        factory.Adapters = [Other];
        Lose(app, factory, 0);
        Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
        factory.Adapters = [Other, First];
        app.Ui(() => app.Clock.Advance(DeviceRecovery.RetryDelay));
        Ac49.WaitEvent(app, "device_recreated");
        Check.SequenceEqual<long?>([First, First], factory.CreatedLuids, "同じ識別子");
        Check.Equal(0, Ac49.Count(app, "warp_fallback"), "warp_fallback が無い");
        Check.Equal(1000L, Ac49.Last(app, "device_recreated").GetProperty("ms").GetInt64(), "ms");
        Drain(app);
    }

    [Test(Slow = true)]
    public void FR93_WARPで動いているときの作り直しはWARPのまま()
    {
        var factory = new FakeDeviceFactory();
        using var app = Ac49.Start(factory, GpuAdapterRequest.Warp);
        Lose(app, factory, 0);
        Ac49.WaitEvent(app, "device_recreated");
        Check.SequenceEqual([GpuAdapterRequest.Warp, GpuAdapterRequest.Warp], factory.Requests.ToArray(), "WARP を頼む");
        Check.True(Service(app).AdapterLuid is null, "識別子は無い");
        Drain(app);
    }

    private sealed class FakeAdapter(long luid) : IDisposable
    {
        public long Luid { get; } = luid;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Test]
    public void FR93_識別子で探すと合うものを返し前のものを放し後ろを開かない()
    {
        var a = new FakeAdapter(1);
        var b = new FakeAdapter(2);
        var c = new FakeAdapter(3);
        var opened = new List<long>();
        IEnumerable<FakeAdapter> All()
        {
            foreach (var x in new[] { a, b, c })
            {
                opened.Add(x.Luid);
                yield return x;
            }
        }
        Check.True(ReferenceEquals(b, GpuAdapters.FindByLuid(All(), x => x.Luid, 2)), "合うもの");
        Check.True(a.Disposed, "合わなかったものは放す");
        Check.True(!b.Disposed, "返すものは放さない");
        Check.SequenceEqual([1L, 2L], opened, "見つけたら先を開かない");
    }

    [Test]
    public void FR93_識別子で探して無ければ例外で全部放す()
    {
        var a = new FakeAdapter(1);
        var b = new FakeAdapter(2);
        Check.Throws<InvalidOperationException>(() => GpuAdapters.FindByLuid([a, b], x => x.Luid, 3), "無い");
        Check.True(a.Disposed && b.Disposed, "全部放す");
    }

    [Test]
    public void FR93_要求の値は識別子で区別する()
    {
        Check.True(GpuAdapterRequest.ByLuid(1) != GpuAdapterRequest.ByLuid(2), "違う識別子は別の要求");
        Check.Equal("ByLuid(5)", GpuAdapterRequest.ByLuid(5).ToString(), "ログ用の文字");
        Check.Equal("HighPerformance", GpuAdapterRequest.HighPerformance.ToString(), "ほかは今のまま");
        Check.Equal("ByIndex(2)", GpuAdapterRequest.ByIndex(2).ToString(), "番号");
    }
}
