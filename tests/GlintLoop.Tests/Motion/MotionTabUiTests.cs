// モーションのタブの画面の検査(FR-64・§9.5 の 1527 行・NFR-9): AC-35 の (7)(先頭が「モーションなし」で 28 個が並ぶ・Enter で適用・静止画はタイルの t・
// 「再生中」のタイル 1 個だけが L=2.0・F=12 の 24 フレームを長辺 192 で再生・再生中のタイルが無いとき 3 秒間の描画 0 回)と、AC-36 の (6)(タイルの絵がエンジンの描画とバイト列で一致)。
// 画面の外に出したアプリ(TestApp)を UIA と直接発生させるキーで操作し、「再生中」は入力から切り離した口(SetPointerOver・SetKeyboardFocus)で与える。中身の差し込みは統合の代わりにテストが行う。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.App.Looks;
using GlintLoop.App.MotionTab;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Export;
using GlintLoop.Core.Motion;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Motions;

public sealed class MotionTabUiTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static MotionTabModule Module(TestApp app) => app.Shell.Modules.OfType<MotionTabModule>().Single();

    private static LooksModule Looks(TestApp app) => app.Shell.Modules.OfType<LooksModule>().Single();

    private static void Import(TestApp app, TempDir dir, string name, CompositeImg img)
    {
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        var o = importer.Drop(new MemoryImportData().WithFiles(dir.Write(name, SkiaBytes.Png(img.W, img.H, img.Px))));
        Check.Equal(1, o.Added, name + " を取り込めない");
        UiHost.Idle();
    }

    /// <summary>左ペインのモーションのタブを選ぶ(中身は ModuleWiring が差し込んでいる)。</summary>
    private static void ShowTab(TestApp app)
    {
        var looks = Looks(app);
        app.Ui(() => looks.LeftPane.SelectTab(LeftPaneTab.Motion));
        UiHost.Idle();
    }

    /// <summary>編集の 600 ms 後に動く自動の書き出しを済ませる(書き出しは待機ではないので、終わってから描画の回数を数える)。</summary>
    private static void SettleExport(TestApp app)
    {
        var band = app.Ui(() => app.Shell.Modules.OfType<ExportBandModule>().Single());
        Check.True(UiHost.WaitUntil(() => band.Pipeline is not null && band.Auto.Environment is not null, Wait), "書き出しの中身が入らない");
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, Wait), "自動の書き出しが終わらない");
    }

    private static CompositeImg Gradient200() => CompositeImg.Make(200, 200, (x, y) => (x, y, (x + y) / 2, 255));

    /// <summary>100×50 の不透明な画像(左半分が赤、右半分が青)。</summary>
    private static CompositeImg LeftRight() => CompositeImg.Make(100, 50, (x, y) => x < 50 ? (255, y < 10 ? 120 : 0, 0, 255) : (0, y < 10 ? 120 : 0, 255, 255));

    /// <summary>土台 200×200 の階調・2 枚目 100×50 を (40, −30, 100) に置き、2 枚目に slide、土台に spin を入れ、2 枚目を選ぶ。</summary>
    private static void BuildDoc(TestApp app, TempDir dir)
    {
        Import(app, dir, "a.png", Gradient200());
        Import(app, dir, "b.png", LeftRight());
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.True(s.SetLayerPlacement(1, 40, -30, 100).Changed, "配置");
            Check.True(s.SetLayerOpacity(1, 60).Changed, "不透明度");
            Check.True(s.AddEffect(Target.OfLayer(1), "slide").Changed, "2 枚目の slide");
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "土台の spin");
            s.Select(Target.OfLayer(1));
        });
        UiHost.Idle();
    }

    private static bool AllStill(MotionTabModule m) => !m.IsBuilding && !m.IsRebuildPending && m.View.Tiles.All(t => t.Thumbnail is not null);

    [Test]
    public void AC35_7_モーションのタブの先頭はモーションなしで28個のタイルが表の順に並ぶ()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        Import(app, dir, "a.png", Gradient200());
        ShowTab(app);
        var m = Module(app);
        var expected = new List<string> { "モーションなし" };
        expected.AddRange(PresetsTests.Spec.Select(s => s.Name));
        app.Ui(() =>
        {
            Check.Equal(450, m.Order, "Order");
            var tiles = m.View.Tiles.ToList();
            Check.SequenceEqual(expected, tiles.Select(t => t.DisplayName).ToList(), "タイルの並び");
            Check.SequenceEqual(expected, tiles.Select(System.Windows.Automation.AutomationProperties.GetName).ToList(), "UIA の Name");
            Check.SequenceEqual(["none", .. PresetsTests.Spec.Select(s => s.Key)], tiles.Select(t => t.Key).ToList(), "キー");
            Check.Equal(2, m.View.Columns, "既定の幅では 2 列");
            Check.True(tiles.All(t => Math.Abs(t.ActualWidth - 132) < 0.5), "タイルの幅 132");
            // 静止画の t(AC-35 の (7) の例)
            (int, int) T(string key) => (tiles.Single(t => t.Key == key).StillK, tiles.Single(t => t.Key == key).StillN);
            Check.Equal((1, 4), T("spin"), "spin は 0.25");
            Check.Equal((1, 8), T("pulse"), "pulse は 0.125");
            Check.Equal((1, 10), T("heartbeat"), "heartbeat は 0.1");
            Check.Equal((1, 20), T("blink"), "blink は 0.05");
        });
        var root = Uia.Root(app.Hwnd);
        Check.True(Uia.TryFind(root, "モーションなし") is not null, "UIA で「モーションなし」が見えない");
        Check.True(Uia.TryFind(root, "くるくる") is not null, "UIA で「くるくる」が見えない");
        Check.True(Uia.TryFind(root, "フィルム") is not null, "UIA で「フィルム」が見えない");
    }

    [Test]
    public void AC35_7_タイルのEnterで選んでいる対象のモーションを置き換えUndoの1件で戻る()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        BuildDoc(app, dir);
        ShowTab(app);
        var m = Module(app);
        var s = app.Shell.Session;
        app.Ui(() =>
        {
            var before = s.Json;
            var undo = s.UndoCount;
            var baseStack = s.Document!.Layers[0].Stack;
            var tile = m.View.Tiles.Single(t => t.Key == "spin_ccw");
            Keyboard.Focus(tile);
            Check.True(UiInput.Key(tile, Key.Enter), "Enter を処理しない");
            var stack = s.Document!.Layers[1].Stack;
            Check.SequenceEqual(["spin"], stack.Select(e => e.Id).ToList(), "slide を取り除いて spin を入れる");
            Check.Equal(EffectOrigin.User, stack[0].Origin, "origin=user");
            Check.Equal(new StringValue("ccw"), stack[0].Get("dir"), "spin_ccw の dir");
            Check.SequenceEqual(baseStack, s.Document.Layers[0].Stack, "ほかのレイヤーのスタックが変わった");
            Check.Equal(undo + 1, s.UndoCount, "Undo の 1 件");
            Check.True(ReferenceEquals(tile, m.View.List.SelectedItem), "適用したタイルを選ぶ");
            // 「モーションなし」はモーションの行を取り除く
            var none = m.View.Tiles.First();
            Keyboard.Focus(none);
            Check.True(UiInput.Key(none, Key.Enter), "モーションなしの Enter");
            Check.Equal(0, s.Document!.Layers[1].Stack.Count, "モーションなし");
            s.Undo();
            s.Undo();
            Check.Equal(before, s.Json, "Undo で元に戻らない");
        });
    }

    // ================================================================ 絵(WARP)

    /// <summary>レイヤー i のタイルの絵の参照(規則 14 の言葉のとおりに組む): ほかのレイヤーの表示をオフ、対象は表示・不透明度 100・通常、全体は空、対象のモーションの行は適用した結果のもの。</summary>
    private static SessionDocument LayerReference(SessionDocument applied, int index, IEffectCatalog catalog)
    {
        var layers = applied.Layers.Select((l, i) => i == index
            ? l with { Visible = true, Opacity = 100, Blend = BlendMode.Normal }
            : l with { Visible = false }).ToArray();
        return applied with { Layers = layers, All = applied.All with { Stack = [] } };
    }

    /// <summary>全体のタイルの絵の参照: 各レイヤーのモーションの行を除き、全体のスタックは適用した結果のモーションの行だけ。</summary>
    private static SessionDocument AllReference(SessionDocument applied, IEffectCatalog catalog)
    {
        bool Motion(StackEntry e) => catalog.Find(e.Id)?.Stage == EffectStage.Motion;
        var layers = applied.Layers.Select(l => l with { Stack = l.Stack.Where(e => !Motion(e)).ToArray() }).ToArray();
        return applied with { Layers = layers, All = applied.All with { Stack = applied.All.Stack.Where(Motion).ToArray() } };
    }

    /// <summary>tile のプリセットを target に適用した文書(適用は Undo で戻す)から参照を組み、タイルの t でエンジンから描く(Pbgra32、長辺 min(192, 合成))。</summary>
    private static DocumentImage Expected(TestApp app, MotionTile tile, Target target)
    {
        var s = app.Shell.Session;
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var applied = app.Ui(() =>
        {
            var changed = s.ApplyMotionPreset(target, tile.Effects).Changed;
            var d = s.Document!;
            if (changed) s.Undo();
            return d;
        });
        var reference = target.IsAll ? AllReference(applied, app.Shell.Catalog) : LayerReference(applied, target.LayerIndex, app.Shell.Catalog);
        var lout = Math.Min(192, renderer.ThumbnailLongSide(reference));
        return renderer.RenderAsync(reference, lout, DocumentPixelFormat.Pbgra32, tile.StillK, tile.StillN).GetAwaiter().GetResult();
    }

    private static byte Alpha(DocumentImage im, int x, int y) => im.Pixels[(y * im.Width + x) * 4 + 3];

    [Test(Slow = true)]
    public void AC36_6_タイルの静止画はタイルのtでエンジンの描画とバイト列が一致する()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        BuildDoc(app, dir);
        var m = Module(app);
        ShowTab(app);
        Check.True(UiHost.WaitUntil(() => AllStill(m), Wait), "静止画が揃わない");
        var keys = new[] { "none", "spin", "spin_ccw", "marquee" };
        var tiles = app.Ui(() => m.View.Tiles.Where(t => keys.Contains(t.Key)).ToList());
        foreach (var t in tiles)
        {
            var th = t.Thumbnail!;
            Check.Equal(192, Math.Max(th.Width, th.Height), t.Key + " の長辺");
            Check.True(th.Pixels.AsSpan().SequenceEqual(Expected(app, t, Target.OfLayer(1)).Pixels), t.Key + " のレイヤーのタイルがエンジンの描画と違う");
        }
        // 下のレイヤーは重ねない(枠の外で回した像の外は透明)・配置どおり(枠の中心は不透明)。192÷200 で縮めた座標で見る
        var spin = tiles.Single(t => t.Key == "spin").Thumbnail!;
        Check.Equal(0, Alpha(spin, 5, 185), "spin: 土台を重ねていない");
        Check.Equal(255, Alpha(spin, (int)(140 * 0.96), (int)(70 * 0.96)), "spin: 配置の枠の中心");
        Check.True(app.Ui(() => tiles.All(t => t.ThumbnailSource is not null)), "画像が出ていない");

        // 全体が対象のとき(Q-L): 各レイヤーのモーションを通さない合成に、プリセットだけを通す
        app.Ui(() => app.Shell.Session.Select(Target.All));
        UiHost.Idle();
        Check.True(app.Ui(() => m.IsRebuildPending), "対象を変えても作り直しを待たない");
        app.Ui(() => app.Clock.Advance(MotionTabModule.RebuildDelay));
        Check.True(UiHost.WaitUntil(() => AllStill(m), Wait), "全体の静止画が揃わない");
        foreach (var t in app.Ui(() => m.View.Tiles.Where(x => keys.Contains(x.Key)).ToList()))
            Check.True(t.Thumbnail!.Pixels.AsSpan().SequenceEqual(Expected(app, t, Target.All).Pixels), t.Key + " の全体のタイルがエンジンの描画と違う");
    }

    [Test(Slow = true)]
    public void AC35_7_再生中のタイル1個だけがL2F12の24フレームを長辺192で再生しNFR9で止まると描かない()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        using var dir = new TempDir();
        BuildDoc(app, dir);
        var m = Module(app);
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();

        // タブが見えるまでは静止画も描かない
        app.Ui(() => app.Clock.Advance(TimeSpan.FromSeconds(1)));
        UiHost.Idle();
        Check.Equal(0, app.Ui(() => m.RenderCount), "タブが見えないのに描いた");
        ShowTab(app);
        Check.True(UiHost.WaitUntil(() => AllStill(m), Wait), "静止画が揃わない");
        Check.Equal(29, app.Ui(() => m.RenderCount), "静止画の描画は 29 枚");

        // NFR-9: 再生中のタイルが無いとき、3 秒間の描画の呼び出しが 0 回
        void Idle3s(string what)
        {
            SettleExport(app);
            var count = app.Ui(() => m.RenderCount);
            var spinRuns = renderer.EffectRuns("spin");
            var slideRuns = renderer.EffectRuns("slide");
            for (var i = 0; i < 30; i++) app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(100)));
            Thread.Sleep(300);
            UiHost.Idle();
            Check.Equal(count, app.Ui(() => m.RenderCount), what + ": モーションのタブが描いた");
            Check.Equal(spinRuns, renderer.EffectRuns("spin"), what + ": spin のノードを計算した");
            Check.Equal(slideRuns, renderer.EffectRuns("slide"), what + ": slide のノードを計算した");
            Check.True(!app.Ui(() => m.IsPlaybackTimerRunning), what + ": 再生のタイマーが動いている");
        }
        Idle3s("最初");

        // マウスが載った状態(入力から切り離して与える)の spin だけが再生する
        var spin = app.Ui(() => m.View.Tiles.Single(t => t.Key == "spin"));
        var marquee = app.Ui(() => m.View.Tiles.Single(t => t.Key == "marquee"));
        var start = app.Ui(() => m.RenderCount);
        app.Ui(() => m.View.SetPointerOver(spin, true));
        Check.True(ReferenceEquals(spin, app.Ui(() => m.PlayTile)), "spin が再生しない");
        Check.Equal(24, MotionTabModule.PlayFrames, "L=2.0・F=12 の 24 フレーム");
        var frames = new DocumentImage[24];
        long now = 0;
        for (var k = 0; k < 24; k++)
        {
            var sk = TimeModel.StartMs(k, 12);
            if (sk > now) app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(sk - now)));
            now = sk;
            var kk = k;
            Check.True(UiHost.WaitUntil(() => spin.ShownFrame == kk, Wait), $"フレーム {k} を出さない(経過 {sk} ms)");
            frames[k] = app.Ui(() => spin.Shown!);
            Check.Equal(192, Math.Max(frames[k].Width, frames[k].Height), $"フレーム {k} の長辺");
            Check.True(app.Ui(() => m.View.Tiles.Where(t => !ReferenceEquals(t, spin)).All(t => t.ShownFrame is null)), "ほかのタイルが再生している");
        }
        Check.Equal(24, app.Ui(() => m.RenderCount) - start, "1 周で 24 フレームを描く");
        // t=0.25 のフレーム 6 は静止画と同じ(同じ t=k÷N の式)。どのフレームも別の絵
        Check.True(frames[6].Pixels.AsSpan().SequenceEqual(app.Ui(() => spin.Thumbnail!).Pixels), "フレーム 6 が静止画(t=0.25)と違う");
        Check.True(!frames[0].Pixels.AsSpan().SequenceEqual(frames[1].Pixels), "フレームが動いていない");
        // エンジンの描画と同じ(フレーム 3)
        var reference = LayerReference(app.Ui(() =>
        {
            var s = app.Shell.Session;
            s.ApplyMotionPreset(Target.OfLayer(1), spin.Effects);
            var d = s.Document!;
            s.Undo();
            return d;
        }), 1, app.Shell.Catalog);
        Check.True(renderer.RenderAsync(reference, 192, DocumentPixelFormat.Pbgra32, 3, 24).GetAwaiter().GetResult().Pixels.AsSpan().SequenceEqual(frames[3].Pixels),
            "フレーム 3 がエンジンの描画と違う");
        UiHost.WaitUntil(() => !m.IsFrameRendering, Wait);

        // 2 周目(P=2000 ms)は描いたフレームを使い回す
        var afterLoop = app.Ui(() => m.RenderCount);
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(2000 - now)));
        Check.True(UiHost.WaitUntil(() => spin.ShownFrame == 0, Wait), "2000 ms でフレーム 0 に戻らない");
        app.Ui(() => app.Clock.Advance(TimeSpan.FromMilliseconds(500)));
        Check.Equal(TimeModel.FrameAt(2500, MotionTabModule.PlayTime), app.Ui(() => spin.ShownFrame), "2500 ms のフレーム");
        Check.Equal(afterLoop, app.Ui(() => m.RenderCount), "2 周目に描き直した");

        // キーボードのフォーカスが当たったタイルに移る(1 個だけ)
        app.Ui(() => m.View.SetKeyboardFocus(marquee, true));
        Check.True(ReferenceEquals(marquee, app.Ui(() => m.PlayTile)), "フォーカスの当たった marquee が再生しない");
        Check.True(app.Ui(() => spin.ShownFrame is null && spin.Shown is not null && ReferenceEquals(spin.Shown, spin.Thumbnail)), "spin が静止画に戻らない");
        Check.True(UiHost.WaitUntil(() => marquee.ShownFrame == 0, Wait), "marquee がフレーム 0 を出さない");
        app.Ui(() => m.View.SetPointerOver(spin, false));
        Check.True(ReferenceEquals(marquee, app.Ui(() => m.PlayTile)), "マウスが外れてもフォーカスのタイルは再生を続ける");
        app.Ui(() => m.View.SetKeyboardFocus(marquee, false));
        Check.True(app.Ui(() => m.PlayTile) is null, "再生中のタイルが残る");
        Check.True(app.Ui(() => marquee.ShownFrame is null), "marquee が静止画に戻らない");
        UiHost.WaitUntil(() => !m.IsFrameRendering, Wait);
        Idle3s("再生をやめたあと");

        // 「モーションなし」は載っても再生しない
        var none = app.Ui(() => m.View.Tiles.First());
        app.Ui(() => m.View.SetPointerOver(none, true));
        Check.True(app.Ui(() => m.PlayTile) is null, "モーションなしが再生する");
        Idle3s("モーションなしに載ったとき");
    }
}
