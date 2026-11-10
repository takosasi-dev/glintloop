// Phase 4 の画面の受け口(P0-6)の検査: shell.Frames の N が文書のモーションで決まり、キャンバスが表示するフレーム k を描くこと、
// キャンバスの実出力の口(ShowActualOutput・HideActualOutput・IsActualOutputAvailable。編集と Esc で戻る)、タイムラインの行の高さの切り替え。
// WARP で画面の外にアプリを立てる。F5 の登録と実出力の中身は担当 band、再生とスクラブは担当 timeline が足す。
using System.Windows.Controls;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.App.Input;
using GlintLoop.App.Theme;
using GlintLoop.App.Viewing;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Ui;

public sealed class FramesShellTests
{
    private static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(20);

    private static (TestApp App, CanvasModule Canvas) StartWithImage()
    {
        var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        var png = SkiaBytes.Png(80, 60, Img.Pattern(80, 60, alpha: false));
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        Check.Equal(1, importer.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", png))).Added, "取り込めた枚数");
        var canvas = app.Ui(() => app.Shell.Modules.OfType<CanvasModule>().Single());
        Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Session.HasDocument && canvas.Picture.Bitmap is not null && !canvas.Processed.IsBusy), RenderWait),
            "最初の表示が出ない");
        return (app, canvas);
    }

    [Test]
    public void P0_6_FramesのNは文書のモーションで決まりキャンバスは表示するフレームを描く()
    {
        var (app, canvas) = StartWithImage();
        using var _ = app;
        var frames = app.Shell.Frames;
        Check.Equal(1, app.Ui(() => frames.N), "モーションが無い文書の N");
        var row = app.Ui(() => app.Shell.Session.AddEffect(Target.OfLayer(0), "spin").Row!.Value);
        Check.Equal(40, app.Ui(() => frames.N), "spin を入れた文書の N(L=2.0・F=20)");
        app.Ui(() => frames.Set(10));
        Check.True(UiHost.WaitUntil(() => app.Ui(() => canvas.Processed.PresentedRequest is { K: 10, N: 40 } && !canvas.Processed.IsBusy), RenderWait),
            "フレーム 10 の表示が出ない");
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var doc = app.Ui(() => app.Shell.Session.Document!);
        var expected = renderer.RenderProxyAsync(doc, DocumentPixelFormat.Pbgra32, 10, 40).GetAwaiter().GetResult();
        var still = renderer.RenderProxyAsync(doc, DocumentPixelFormat.Pbgra32, 0, 40).GetAwaiter().GetResult();
        Check.True(!expected.Pixels.AsSpan().SequenceEqual(still.Pixels), "フレーム 10 と 0 が同じ絵(spin が効いていない)");
        app.Ui(() => Check.True(canvas.Picture.CopyPixels()!.AsSpan().SequenceEqual(expected.Pixels), "表示がフレーム 10 の描画と一致しない"));

        app.Ui(() => frames.Set(99));
        Check.Equal(39, app.Ui(() => frames.K), "k は N−1 までに切る");
        app.Ui(() => app.Shell.Session.SetEffectEnabled(Target.OfLayer(0), row, false));
        Check.Equal(1, app.Ui(() => frames.N), "spin の目をオフにすると N=1");
        Check.Equal(0, app.Ui(() => frames.K), "N=1 で k=0");
    }

    [Test]
    public void P0_6_実出力の口は出せるときだけ入り編集とEscでプロキシに戻る()
    {
        var (app, canvas) = StartWithImage();
        using var _ = app;
        var button = app.Ui(() => app.Shell.CommandBar.Button(Icons.ActualOutput));
        Check.True(!app.Ui(() => button.IsEnabled), "出せない間の実出力のボタンは無効");
        Check.True(!app.Ui(() => canvas.ShowActualOutput(new Border())), "出せない間は入らない");
        app.Ui(() => canvas.IsActualOutputAvailable = true);
        Check.True(app.Ui(() => button.IsEnabled), "出せるときの実出力のボタンは有効");
        var changes = 0;
        app.Ui(() => canvas.ActualOutputChanged += () => changes++);
        var content = app.Ui(() => new Border());
        Check.True(app.Ui(() => canvas.ShowActualOutput(content)), "実出力に入る");
        app.Ui(() =>
        {
            Check.Equal(CanvasMode.ActualOutput, canvas.Mode, "実出力のモード");
            Check.True(ReferenceEquals(content, canvas.ActualOutputHost.Content), "中身が枠に入る");
            Check.True(canvas.Picture.Visibility != System.Windows.Visibility.Visible, "プロキシの表示を隠す");
            Check.True(app.Shell.Router.Escape.IsOpen(EscapeTarget.ActualOutput), "Esc の対象に開いている");
        });
        // 編集の操作が入ると、自動でプロキシの表示に戻る(FR-19)
        app.Ui(() => app.Shell.Session.AddEffect(Target.OfLayer(0), "invert"));
        app.Ui(() =>
        {
            Check.Equal(CanvasMode.Normal, canvas.Mode, "編集のあと");
            Check.True(canvas.ActualOutputHost.Content is null, "中身を外す");
        });
        // Esc でも戻る
        app.Ui(() => canvas.ShowActualOutput(new Border()));
        app.Ui(() => app.Shell.Router.Escape.Cancel(FocusKind.Other));
        Check.Equal(CanvasMode.Normal, app.Ui(() => canvas.Mode), "Esc のあと");
        Check.Equal(4, changes, "入る・出るの通知");
        // 出せなくなったら閉じる
        app.Ui(() => canvas.ShowActualOutput(new Border()));
        app.Ui(() => canvas.IsActualOutputAvailable = false);
        Check.Equal(CanvasMode.Normal, app.Ui(() => canvas.Mode), "出せなくなったとき");
        Check.True(!app.Ui(() => button.IsEnabled), "出せなくなったらボタンは無効");
    }

    [Test]
    public void P0_6_タイムラインの行の高さを1行と2行で切り替える()
    {
        using var app = TestApp.Start();
        var w = app.Shell.Window;
        var r = AppResources.LayoutTokens;
        app.Ui(() =>
        {
            Check.Equal((double)r["TimelineHeight"], w.TimelineRow.Height.Value, "既定は 1 行");
            w.SetTimelineWrapped(true);
            Check.True(w.IsTimelineWrapped, "折り返しの印");
            Check.Equal((double)r["TimelineWrappedHeight"], w.TimelineRow.Height.Value, "2 行");
            Check.Equal(96.0, w.TimelineRow.Height.Value, "2 行は 96");
            w.SetTimelineWrapped(false);
            Check.Equal(56.0, w.TimelineRow.Height.Value, "1 行は 56");
        });
    }
}
