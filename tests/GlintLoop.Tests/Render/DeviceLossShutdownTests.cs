// デバイスの消失の途中でアプリを閉じたときの後始末。作り直しを待っているサムネイルの依頼は、破棄で GpuDeviceLostException になる。閉じたあとにその例外が UI スレッドの外へ出ないことを確かめる。
using GlintLoop.App.Layers;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using Ac49 = GlintLoop.Tests.Render.GpuRecoveryAc49Tests;

namespace GlintLoop.Tests.Render;

public sealed class DeviceLossShutdownTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    [Test(Slow = true)]
    public void FR104_消失の途中で閉じても待っていたサムネイルの例外が処理されない例外にならない()
    {
        var factory = new FakeDeviceFactory();
        var app = Ac49.Start(factory);
        try
        {
            var layers = EditingScreenKit.M<LayersModule>(app);
            factory.FailNextCreates = 2;
            factory.FailRender(FakeDeviceFactory.DeviceRemoved, 1, RenderPriority.Display);
            app.Ui(() => app.Shell.Session.AddEffect(Target.OfLayer(0), "exposure"));
            Check.True(UiHost.WaitUntil(() => app.Shell.Recovery!.IsWaitingForRetry, Limit), "2 回目を待つ状態にならない");
            var before = app.Ui(() => layers.Thumbnails.RenderCount);
            // 消失の間にサムネイルの 300 ms が来て、依頼が作り直しを待つ(2 回目はまだ始まらない)
            app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(300)));
            Check.True(UiHost.WaitUntil(() => layers.Thumbnails.RenderCount > before, Limit), "サムネイルを頼まない");
            UiHost.Idle();
        }
        finally
        {
            app.Dispose();
        }
        UiHost.Idle();
        UiHost.Idle();
    }
}
