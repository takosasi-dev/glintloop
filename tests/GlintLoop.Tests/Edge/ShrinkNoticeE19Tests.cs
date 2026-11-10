// E-19 の layer.render_shrunk を、表示と書き出しの両方の経路で文書ごとに 1 回にすることを調べる(AC-57 の (7) の残り)。書き出しのキャッシュを使い回した完了と、載せ方の静止画の経路も見る。
// レイヤー 3000×2000 を scale 200 で置いた 4000×3000 の文書は、プロキシでは縮めず、長辺 4000 の書き出しだけ縮める(AC57_7_E19_… と同じ場面)。
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Edge;

public sealed class ShrinkNoticeE19Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private static ExportEdgeRig Start(string format)
    {
        var r = ExportEdgeRig.Start(4000, 3000, png: SkiaBytes.Png(4000, 3000, Img.Solid(4000, 3000, 128, 128, 128)));
        try
        {
            using var dir = new TempDir();
            var layer = dir.Write("b.png", SkiaBytes.Png(3000, 2000, Img.Solid(3000, 2000, 30, 140, 210)));
            Check.Equal(1, new ImageImporter(r.App.Shell.ImportTarget, new MemoryLogSink()).Drop(new MemoryImportData().WithFiles(layer)).Added, "レイヤーを足せない");
            UiHost.Idle();
            r.App.Ui(() =>
            {
                r.Session.SetOutputFormat(format);
                r.Session.SetOutputSize("long", 4000);
                Check.True(r.Session.SetLayerPlacement(1, 0, 0, 200).Changed, "配置");
                r.App.Shell.NoticeBar.Close();
            });
            var canvas = M<CanvasModule>(r.App);
            Check.True(UiHost.WaitUntil(() => !canvas.Processed.IsBusy && ReferenceEquals(canvas.Processed.PresentedDocument, r.Session.Document), Limit), "表示が更新されない");
            UiHost.Idle();
            Check.Equal<string?>(null, CurrentKey(r), "前提: 画面の描画では通知が出ない");
            return r;
        }
        catch
        {
            r.Dispose();
            throw;
        }
    }

    private static string? CurrentKey(ExportEdgeRig r) => r.App.Ui(() => r.App.Shell.NoticeBar.Current?.Key);

    [Test(Slow = true)]
    public void AC57_7_E19_書き出しで出した通知は同じ文書では表示でも次の書き出しでも出ず新しい文書で数え直す()
    {
        using var r = Start(ExportFormat.Png);
        r.ExportDone();
        Check.Equal(NoticeKeys.LayerRenderShrunk, CurrentKey(r), "最初の書き出し");
        r.App.Ui(() =>
        {
            r.App.Shell.NoticeBar.Close();
            r.Session.SetLayerOpacity(1, 99);
        });
        r.ExportDone("やり直し");
        Check.Equal<string?>(null, CurrentKey(r), "同じ文書の次の書き出しでは出ない");

        // 表示の経路でも縮める配置にする(scale 400 はプロキシでも上限を超える)
        var canvas = M<CanvasModule>(r.App);
        r.App.Ui(() => Check.True(r.Session.SetLayerPlacement(1, 0, 0, 400).Changed, "配置"));
        Check.True(UiHost.WaitUntil(() => !canvas.Processed.IsBusy && ReferenceEquals(canvas.Processed.PresentedDocument, r.Session.Document), Limit), "表示が更新されない");
        UiHost.Idle();
        var renderer = r.App.Shell.RendererReady!.GetAwaiter().GetResult();
        var doc = r.App.Ui(() => r.Session.Document!);
        Check.True(renderer.Layout(doc, renderer.ProxyLongSide(doc)).Layers[1].Shrink.N >= 1, "前提: 画面でも縮める");
        Check.Equal<string?>(null, CurrentKey(r), "書き出しで出したレイヤーは表示の経路でも出ない");

        // 新しい文書を始めると数え直す
        r.App.Ui(() => r.App.Shell.ShrinkNotices.Observe([1]));
        r.App.Ui(() => r.Session.CloseDocument());
        UiHost.Idle();
        Check.Equal(1, r.App.Ui(() => r.App.Shell.ShrinkNotices.Observe([1]).Count), "文書を閉じたら数え直す");
    }

    [Test(Slow = true)]
    public void AC57_7_E19_キャッシュを使い回した完了でも縮めたレイヤーの通知が出る()
    {
        using var r = Start(ExportFormat.Png);
        r.ExportDone();
        Check.Equal(NoticeKeys.LayerRenderShrunk, CurrentKey(r), "前提: 最初の書き出し");
        var calls = r.App.Ui(() => r.Band.Auto.PipelineCalls);
        r.App.Ui(() =>
        {
            r.App.Shell.NoticeBar.Close();
            r.App.Shell.ShrinkNotices.Reset(); // 起動し直して同じ文書を開いた場合と同じ(この文書ではまだ出していない)
            r.Band.Auto.Retry();
        });
        r.WaitEnd("やり直し");
        Check.True(UiHost.WaitUntil(() => r.Band.Auto.State == ExportState.Done, Limit), "完了しない");
        Check.Equal(calls, r.App.Ui(() => r.Band.Auto.PipelineCalls), "前提: エンコーダーを呼ばずにキャッシュを使う");
        Check.Equal(NoticeKeys.LayerRenderShrunk, CurrentKey(r), "キャッシュを使い回した完了");
    }

    [Test(Slow = true)]
    public void AC57_7_E19_載せ方の静止画の経路で縮めたレイヤーの通知が出る()
    {
        using var r = Start(ExportFormat.Webp);
        r.App.Ui(() => r.Band.Auto.RequestStill(0));
        Check.True(UiHost.WaitUntil(() => r.Band.Auto.StillFor(0) is not null, Limit), "静止画ができない");
        UiHost.Idle();
        Check.Equal(NoticeKeys.LayerRenderShrunk, CurrentKey(r), "静止画の完了");
    }

    [Test(Slow = true)]
    public void AC57_7_E19_書き出しの中身は選んだ段の縮めたレイヤーの番号だけを返す()
    {
        using var r = Start(ExportFormat.Png);
        r.ExportDone();
        var outcome = r.App.Ui(() => r.Band.Auto.Outcome!);
        Check.SequenceEqual([1], outcome.ShrunkLayers.ToList(), "土台は縮めず、レイヤー 1 だけ縮める");

        // 1 バイトの上限は、r_0(長辺 4000。縮める)を試して捨て、最後の段(縮めない)を使う。試して捨てた段は数えない
        r.App.Ui(() => Check.True(r.Session.SetOutputLimit(new OutputLimit("bytes", null, 1)).Changed, "上限"));
        UiHost.Idle();
        r.ExportDone("小さい段");
        var small = r.App.Ui(() => r.Band.Auto.Outcome!);
        Check.True(small.SizeFail, "前提: 上限に収まらず最後の段を使う");
        var renderer = r.App.Shell.RendererReady!.GetAwaiter().GetResult();
        var doc = r.App.Ui(() => r.Session.Document!);
        Check.Equal(0, renderer.Layout(doc, Math.Max(small.Width, small.Height)).Layers[1].Shrink.N, "前提: 最後の段では縮めない(長辺 " + Math.Max(small.Width, small.Height) + ")");
        Check.SequenceEqual([], small.ShrunkLayers.ToList(), "選んだ段の番号だけを返す");
    }
}
