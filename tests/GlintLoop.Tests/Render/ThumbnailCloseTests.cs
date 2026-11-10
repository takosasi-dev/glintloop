// デバイスの消失が無くても、閉じたときに並んでいたサムネイルの依頼は RenderService の破棄で GpuDeviceLostException になる(破棄の仕事は優先度が高く、並んでいた依頼を追い越す)。
// その例外が閉じたあとの async void の外へ出ないことを、本物の WARP で確かめる(FR-104。出ると次のテストの UI の待ちで投げ直される)。
using GlintLoop.App.Layers;
using GlintLoop.App.Startup;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Render;

public sealed class ThumbnailCloseTests
{
    private sealed class CountingCrash : ICrashHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Handle(Exception exception, CrashSource source) => Interlocked.Increment(ref _calls);
    }

    // 描画の準備に失敗すると RendererReady は取り消しになる。ルックとモーションのタイルの async void の外へ TaskCanceledException を出さない
    [Test(Slow = true)]
    public void FR94_描画の準備に失敗してもタイルの待ちの取り消しが2回目の処理されない例外にならない()
    {
        var crash = new CountingCrash();
        using var app = TestApp.Start(gpu: GpuAdapterRequest.HighPerformance, deviceFactory: new FakeDeviceFactory { Fail = _ => true }, crashFactory: _ => crash);
        var limit = TimeSpan.FromSeconds(60);
        Check.True(UiHost.WaitUntil(() => crash.Calls > 0, limit), "前提: クラッシュの入口が呼ばれる");
        Check.True(UiHost.WaitUntil(() => app.Shell.RendererReady!.IsCompleted, limit), "前提: 描画の準備が終わる");
        Check.True(app.Shell.RendererReady!.IsCanceled, "前提: 描画の準備は取り消しになる");
        Check.Equal(1, new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink()).Paste(new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(32, 32, Img.Pattern(32, 32, alpha: false)))).Added, "取り込めた枚数");
        for (var i = 0; i < 10; i++)
        {
            UiHost.Idle();
            app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(1)));
        }
        UiHost.Idle();
        Check.Equal(1, crash.Calls, "クラッシュの入口は 1 回だけ");
    }

    [Test(Slow = true)]
    public void FR104_消失が無くても閉じたときに並んでいたサムネイルの例外が処理されない例外にならない()
    {
        var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        var layers = EditingScreenKit.M<LayersModule>(app);
        try
        {
            var files = Enumerable.Range(0, 8).Select(i => dir.Write($"{i}.png", SkiaBytes.Png(512, 512, Img.Solid(512, 512, (byte)(i * 30), 40, 50)))).ToArray();
            Check.Equal(8, new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink()).Drop(new MemoryImportData().WithFiles(files)).Added, "取り込めた枚数");
            UiHost.Idle();
            Check.True(app.Shell.RendererReady!.Wait(TimeSpan.FromSeconds(60)), "描画の準備ができない");
            UiHost.Idle();
            var before = app.Ui(() => layers.Thumbnails.RenderCount);
            app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(LayerThumbnails.DelayMs)));
            Check.True(app.Ui(() => layers.Thumbnails.RenderCount) >= before + 8, "前提: 8 行のサムネイルを頼む");
        }
        finally
        {
            app.Dispose();
        }
        // 破棄で失敗した依頼の続きが UI スレッドへ戻るまで回す(漏れた例外は UiHost の待ちが投げ直す)
        Check.True(UiHost.WaitUntil(() => layers.Thumbnails.Pending == 0, TimeSpan.FromSeconds(60)), "描きかけの依頼が終わらない");
        UiHost.Idle();
        UiHost.Idle();
    }
}
