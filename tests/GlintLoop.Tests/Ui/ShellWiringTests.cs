// AppShell の共通の口の検査: 取り込みの行き先(ImportTarget)の画素を、共有の文書の描画(RendererReady)が読めること。
// 画面の部品(IShellModule)は、このあと各担当が足す。ここでは描画と取り込みのつなぎだけを WARP で確かめる。
using GlintLoop.Codec;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Ui;

public sealed class ShellWiringTests
{
    [Test]
    public void 取り込んだ画像を共有の文書の描画で描ける()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        var png = SkiaBytes.Png(40, 30, Img.Solid(40, 30, 250, 10, 10));
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        var o = importer.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", png)));
        Check.Equal(1, o.Added, "取り込めた枚数");
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var image = renderer.RenderProxyAsync(app.Shell.Session.Document!, DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
        Check.Equal(40, image.Width, "幅");
        Check.Equal(30, image.Height, "高さ");
        Check.Equal(250, (int)image.Pixels[0], "左上の R");
        Check.Equal(255, (int)image.Pixels[3], "左上の A");
    }
}
