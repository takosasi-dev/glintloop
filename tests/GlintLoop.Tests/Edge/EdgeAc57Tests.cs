// AC-57: 書き出しと渡す操作のエッジケース(E-19・E-30〜E-38)。本物の RenderExportPipeline に偽のエンコーダーを入れて、上限・大きすぎる・失敗・条件・パス・大きなレイヤーを調べる。
// (5) の残りは AC-43・AC-44 と HandoffAc44Tests.AC57_5_ が、(3) の 3 つの理由の単独の検査は GpuRecoveryAc49Tests.AC57_3_ が調べる。ここでは「やり直しが 1 回だけ」をそろえて調べる。
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using GlintLoop.App.Export;
using GlintLoop.App.Screens;
using GlintLoop.App.Text;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Export;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Static;
using GlintLoop.Tests.Ui;
using SkiaSharp;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc57Tests
{
    private static readonly TimeSpan Limit = ExportEdgeRig.Limit;

    private static readonly IReadOnlyList<IExportEncoder> RealEncoders =
        [new PngExportEncoder(), new JpegExportEncoder(), new GifExportEncoder(), new WebpExportEncoder(), new ApngEncoder()];

    private static void Preset(EditorSession s, string key)
    {
        var p = BandLimitPresets.Builtin.Single(x => x.Key == key);
        Check.True(s.ApplyLimitPreset(p.Key, p.Format, p.LongSide, p.Loop).Changed, key + " を選べない");
    }

    // ================================================================ (1) 上限に収まらない(E-30)

    private const long OverBytes = 300_000;

    private static ExportEdgeRig StartOver()
    {
        var r = ExportEdgeRig.Start(64, 64, [new SizedEncoder(ExportFormat.Gif, OverBytes)], s =>
        {
            ExportEdgeRig.SmallMotion(s);
            Preset(s, "discord_emoji");
        });
        r.ExportDone();
        return r;
    }

    [Test(Slow = true)]
    public void AC57_1_どの段でも収まらないと最後の段の結果ができて完了しsize_failが状態欄に出る()
    {
        var encoder = new SizedEncoder(ExportFormat.Gif, OverBytes);
        using var r = ExportEdgeRig.Start(64, 64, [encoder], s =>
        {
            ExportEdgeRig.SmallMotion(s);
            Preset(s, "discord_emoji");
        });
        r.ExportDone();
        var outcome = r.App.Ui(() => r.Band.Auto.Outcome!);
        Check.True(outcome.SizeFail, "SizeFail");
        Check.Equal(OverBytes, outcome.Bytes, "バイト数");
        Check.Equal(OverBytes, new FileInfo(outcome.Path).Length, "キャッシュのファイルが最後の段の結果");
        Check.True(encoder.Calls.Count >= 2, "はしごを下りて試す: " + encoder.Calls.Count);
        Check.Equal(255_000L, encoder.Calls[0].Limit, "上限は discord_emoji の 255,000");
        var text = NoticeTexts.Format(NoticeKeys.ExportSizeFail, ("limit", "255000"), ("size", "300000"));
        Check.True(r.Status.Contains(text, StringComparison.Ordinal), "状態欄: " + r.Status);
        Check.Equal(0, EdgeLog.Count(r.App, "export_fail"), "失敗ではない");
    }

    private static void CheckStopDefault(ExportEdgeRig r, ExportConfirmOverView v, string what) => r.App.Ui(() =>
    {
        Check.True(v.Stop.IsDefault && !v.Pass.IsDefault, what + ": 既定のボタンは「やめる」");
    });

    [Test(Slow = true)]
    public void AC57_1_コピーは確認のやめるで呼ばれず渡すで最後の段の結果が渡る()
    {
        using var r = StartOver();
        r.Copy();
        var v = r.WaitConfirm();
        CheckStopDefault(r, v, "コピー");
        r.Answer(v, x => x.Stop);
        Check.Equal(0, r.App.Clipboard.PlaceCalls, "やめるではクリップボードが呼ばれない");
        Check.Equal(0, EdgeLog.Count(r.App, "export_handoff"), "やめるではログなし");

        r.Copy();
        r.Answer(r.WaitConfirm(), x => x.Pass);
        Check.Equal(1, r.App.Clipboard.PlaceCalls, "渡すでクリップボードに置く");
        var path = r.App.Ui(() => ((DataObject)r.App.Clipboard.Placed[0].Data).GetFileDropList()[0]!);
        Check.Equal(OverBytes, new FileInfo(path).Length, "最後の段の結果");
        Check.Equal(1, EdgeLog.Count(r.App, "export_handoff"), "ログ");
    }

    [Test(Slow = true)]
    public void AC57_1_保存は確認のやめるで呼ばれず渡すで最後の段の結果が渡る()
    {
        using var r = StartOver();
        var target = Path.Combine(r.Dir.Path, "over.gif");
        r.App.Ui(() => r.App.FileDialogs.SaveAnswers.Enqueue(target));
        r.Save();
        var v = r.WaitConfirm();
        CheckStopDefault(r, v, "保存");
        r.Answer(v, x => x.Stop);
        Check.Equal(0, r.App.FileDialogs.Calls, "やめるでは保存ダイアログが呼ばれない");
        Check.True(!File.Exists(target), "やめるでは保存先に書かない");

        r.Save();
        r.Answer(r.WaitConfirm(), x => x.Pass);
        Check.True(UiHost.WaitUntil(() => File.Exists(target) && r.Handoff.Busy is null, Limit), "渡すで保存される");
        Check.Equal(OverBytes, new FileInfo(target).Length, "最後の段の結果");
    }

    [Test(Slow = true)]
    public void AC57_1_ドラッグは確認のやめるで呼ばれず渡すのあとのもう一度のドラッグで確認なしに渡る()
    {
        using var r = StartOver();
        r.Drag();
        var v = r.WaitConfirm();
        CheckStopDefault(r, v, "ドラッグ");
        r.Answer(v, x => x.Stop);
        Check.Equal(0, r.App.DragRunner.Runs.Count, "やめるでは実行器が呼ばれない");

        // 2026-11-05 の判断: 確認のあいだにマウスのボタンが離れるので、「渡す」のあとは案内を出し、同じファイルのもう一度のドラッグで渡す
        r.Drag();
        r.Answer(r.WaitConfirm(), x => x.Pass);
        r.Settle("渡す");
        Check.Equal(0, r.App.DragRunner.Runs.Count, "「渡す」の直後は実行器を呼ばない(案内を出す)");

        r.Drag();
        Check.True(UiHost.WaitUntil(() => r.App.DragRunner.Runs.Count == 1 && r.Handoff.Busy is null, Limit), "もう一度のドラッグで実行器が呼ばれる");
        Check.True(!r.DialogOpen, "もう一度のドラッグでは確認を出さない");
        var path = r.App.Ui(() => ((DataObject)r.App.DragRunner.Runs[0].Data).GetFileDropList()[0]!);
        Check.Equal(OverBytes, new FileInfo(path).Length, "最後の段の結果");
        Check.Equal(1, EdgeLog.Count(r.App, "export_handoff"), "ログ");
    }

    // ================================================================ (2) 大きすぎる(E-31)

    private static IReadOnlyList<SizedEncoder> AllSized() =>
        ExportFormat.All.Select(f => new SizedEncoder(f, 1000)).ToArray();

    private static void CheckTooBig(string format, string fmt)
    {
        var encoders = AllSized();
        using var r = ExportEdgeRig.Start(64, 64, encoders, s =>
        {
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
            s.SetOutputFormat(format);
            s.SetOutputSize("long", 4096);
            s.SetLength10(10);
            s.SetFps(10);
        });
        r.ExportFailed(fmt);
        Check.Equal(0, encoders.Sum(e => e.Calls.Count), fmt + ": エンコーダーを呼ばない");
        var text = NoticeTexts.Format(NoticeKeys.ExportGifTooBig, ("fmt", fmt));
        Check.True(r.Status.Contains(text, StringComparison.Ordinal), fmt + ": 状態欄: " + r.Status);
        Check.Equal("too_big_gif", EdgeLog.Last(r.App, "export_fail").GetProperty("reason").GetString(), fmt + ": reason");
        Check.Equal(0, r.Tmp().Length, fmt + ": .tmp が残らない");
    }

    [Test(Slow = true)]
    public void AC57_2_GIFで4096四方のN10は書き出さずgif_too_bigとtoo_big_gifになる() => CheckTooBig(ExportFormat.Gif, "GIF");

    [Test(Slow = true)]
    public void AC57_2_APNGも同じでfmtがAPNGになる() => CheckTooBig(ExportFormat.Apng, "APNG");

    [Test(Slow = true)]
    public void AC57_2_縦横比100対1を長辺480にすると480x5で書き出す()
    {
        var encoders = AllSized();
        using var r = ExportEdgeRig.Start(1000, 10, encoders, s =>
        {
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
            s.SetOutputFormat(ExportFormat.Gif);
            s.SetOutputSize("long", 480);
        });
        r.ExportDone();
        var settings = encoders.Single(e => e.Format == ExportFormat.Gif).Calls.Single().Settings;
        Check.Equal((480, 5), (settings.Width, settings.Height), "出力の大きさ");
        Check.Equal(0, EdgeLog.Count(r.App, "export_fail"), "失敗にならない");
    }

    [Test(Slow = true)]
    public void AC57_2_PNGとJPEGでは同じ大きさでも失敗にならない()
    {
        foreach (var format in new[] { ExportFormat.Png, ExportFormat.Jpeg })
        {
            var encoders = AllSized();
            using var r = ExportEdgeRig.Start(64, 64, encoders, s =>
            {
                Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
                s.SetOutputFormat(format);
                s.SetOutputSize("long", 4096);
                s.SetLength10(10);
                s.SetFps(10);
            });
            r.ExportDone(format);
            Check.Equal(1, encoders.Single(e => e.Format == format).Calls.Count, format + ": エンコーダーを呼ぶ");
            Check.Equal(0, EdgeLog.Count(r.App, "export_fail"), format + ": 失敗にならない");
        }
    }

    // ================================================================ (3) 書き出しの失敗のあとのやり直し(E-32)

    private enum Reason { Encode, Write, Gpu }

    private enum Trigger { Slider, Copy, Save }

    private static void CheckRetryOnce(Reason reason, Trigger trigger)
    {
        var what = reason + "・" + trigger;
        var factory = new FakeDeviceFactory();
        var fs = new FakeFileSystem();
        using var r = ExportEdgeRig.Start(64, 64, null, ExportEdgeRig.SmallMotion, factory, fs);
        switch (reason)
        {
            case Reason.Encode:
                r.UseEncoders([.. RealEncoders.Select(e => e.Format == ExportFormat.Gif ? new ThrowOnceEncoder(e) : e)]);
                break;
            case Reason.Write:
                fs.FailOn(FileOps.Create, p => p.Contains(Path.Combine("cache", "exports"), StringComparison.OrdinalIgnoreCase) && p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
                break;
            default:
                factory.FailRender(FakeDeviceFactory.EFail, 3, RenderPriority.Export);
                break;
        }
        r.ExportFailed(what);
        var expected = reason switch { Reason.Encode => "encode_error", Reason.Write => "write_error", _ => "gpu_error" };
        Check.Equal(expected, EdgeLog.Last(r.App, "export_fail").GetProperty("reason").GetString(), what + ": reason");
        Check.Equal(1, EdgeLog.Count(r.App, "export_fail"), what + ": export_fail は 1 行");
        Check.Equal(0, EdgeLog.Count(r.App, "render_fail"), what + ": render_fail は書かない");
        Check.Equal(0, r.Tmp().Length, what + ": .tmp が消える");
        Check.True(r.Status.Contains(NoticeTexts.Format(NoticeKeys.ExportFailed), StringComparison.Ordinal), what + ": 状態欄: " + r.Status);

        var calls = r.App.Ui(() => r.Band.Auto.PipelineCalls);
        r.Advance(60_000);
        Check.Equal(calls, r.App.Ui(() => r.Band.Auto.PipelineCalls), what + ": 60 秒進めても始まらない");
        Check.Equal(ExportState.Failed, r.App.Ui(() => r.Band.Auto.State), what + ": 失敗のまま");

        fs.ClearFailures();
        var target = Path.Combine(r.Dir.Path, "retry.gif");
        switch (trigger)
        {
            case Trigger.Slider:
                r.App.Ui(() => r.Session.SetLayerOpacity(0, 70));
                r.ExportDone(what);
                break;
            case Trigger.Copy:
                r.Copy();
                Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), what + ": コピーされない");
                break;
            default:
                r.App.Ui(() => r.App.FileDialogs.SaveAnswers.Enqueue(target));
                r.Save();
                Check.True(UiHost.WaitUntil(() => File.Exists(target) && r.Handoff.Busy is null, Limit), what + ": 保存されない");
                break;
        }
        r.Settle(what);
        Check.Equal(calls + 1, r.App.Ui(() => r.Band.Auto.PipelineCalls), what + ": 書き出しが 1 回だけ始まる");
        r.Advance(60_000);
        Check.Equal(calls + 1, r.App.Ui(() => r.Band.Auto.PipelineCalls), what + ": そのあとも増えない");
    }

    [Test(Slow = true)]
    public void AC57_3_失敗のあとスライダーの変更で書き出しが1回だけ始まる()
    {
        foreach (var reason in Enum.GetValues<Reason>()) CheckRetryOnce(reason, Trigger.Slider);
    }

    [Test(Slow = true)]
    public void AC57_3_失敗のあとコピーで書き出しが1回だけ始まる()
    {
        foreach (var reason in Enum.GetValues<Reason>()) CheckRetryOnce(reason, Trigger.Copy);
    }

    [Test(Slow = true)]
    public void AC57_3_失敗のあと保存で書き出しが1回だけ始まる()
    {
        foreach (var reason in Enum.GetValues<Reason>()) CheckRetryOnce(reason, Trigger.Save);
    }

    // ================================================================ (4) 条件(E-33)

    [Test(Slow = true)]
    public void AC57_4_line_stampの条件の文が状態欄に出て値は変わらず確認なしで渡せる()
    {
        var encoders = AllSized();
        using var r = ExportEdgeRig.Start(640, 480, encoders, s =>
        {
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
            Preset(s, "line_stamp");
            s.SetFps(20);
            s.SetLength10(20);
        });
        Check.Equal(40, r.App.Ui(() => MotionInfo.FrameCount(r.Session.Document!, r.App.Shell.Catalog)), "N");
        r.ExportDone();
        Check.True(r.Status.Contains("条件:フレーム数 5〜20を満たしていません", StringComparison.Ordinal), "状態欄(1 回ループ): " + r.Status);
        Check.Equal(ExportFormat.Apng, r.App.Ui(() => r.Session.Document!.Output.Format), "形式");

        r.App.Ui(() => r.Session.SetLoop(LoopMode.Infinite));
        r.ExportDone("ループを変えた書き出し");
        Check.True(r.Status.Contains("条件:フレーム数 5〜20、ループ 1 回を満たしていません", StringComparison.Ordinal), "状態欄(無限): " + r.Status);

        var time = r.App.Ui(() => r.Session.Document!.Time);
        Check.Equal((20, 20, LoopMode.Infinite), (time.Fps, time.Length10, time.Loop), "F・L・ループは自動で変わらない");

        r.Copy();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "コピーされない");
        Check.True(!r.DialogOpen, "コピーで確認を出さない");
        r.Drag();
        Check.True(UiHost.WaitUntil(() => r.App.DragRunner.Runs.Count == 1 && r.Handoff.Busy is null, Limit), "ドラッグされない");
        Check.True(!r.DialogOpen, "ドラッグで確認を出さない");
        var target = Path.Combine(r.Dir.Path, "ok.png");
        r.App.Ui(() => r.App.FileDialogs.SaveAnswers.Enqueue(target));
        r.Save();
        Check.True(UiHost.WaitUntil(() => File.Exists(target) && r.Handoff.Busy is null, Limit), "保存されない");
        Check.True(!r.DialogOpen, "保存で確認を出さない");
    }

    // ================================================================ (6) パス(E-37)

    private static string DeepFolder(TempDir dir)
    {
        var path = dir.Path;
        while (path.Length <= 300) path = Path.Combine(path, "日本語 \U0001F600 のフォルダ名を長くする");
        Directory.CreateDirectory(path);
        Check.True(path.Length > 300, "前提: 300 文字を超える");
        return path;
    }

    [Test(Slow = true)]
    public void AC57_6_300文字を超えるパスで起動引数と保存とプロジェクトの保存と開くが成功し実体の名前はASCIIのまま()
    {
        using var dir = new TempDir();
        var folder = DeepFolder(dir);
        var source = Path.Combine(folder, "元の 画像 \U0001F600.png");
        File.WriteAllBytes(source, SkiaBytes.Png(64, 64, Img.Pattern(64, 64, alpha: false)));

        using var app = TestApp.Start(clock: FakeClock.ForPhase5(), gpu: GpuAdapterRequest.Warp);
        var screens = M<ScreensModule>(app);
        var band = M<ExportBandModule>(app);
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, Limit), "本物の中身が入らない");

        // 起動引数での取り込み
        app.Ui(() => { _ = screens.ImportStartupArgs([source]); });
        WaitImports(app);
        Check.True(app.Ui(() => app.Shell.Session.HasDocument), "起動引数で取り込めない: " + app.Ui(() => app.Shell.NoticeBar.Current?.Key));
        app.Ui(() =>
        {
            ExportEdgeRig.SmallMotion(app.Shell.Session);
            app.Shell.Settings.CopyMode = GlintLoop.Core.Settings.CopyModeSetting.File;
        });
        UiHost.Idle();
        app.Ui(() => app.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, Limit), "書き出しが終わらない");
        Check.Equal(ExportState.Done, app.Ui(() => band.Auto.State), "書き出しの状態");

        // 同じフォルダへの保存
        var saved = Path.Combine(folder, "保存 \U0001F600.gif");
        app.Ui(() => app.FileDialogs.SaveAnswers.Enqueue(saved));
        app.Ui(() => band.Handoff.PressSave());
        Check.True(UiHost.WaitUntil(() => File.Exists(saved) && band.Handoff.Busy is null, Limit), "保存されない: " + app.Ui(() => app.Shell.NoticeBar.Current?.Key));
        Check.Equal(new FileInfo(app.Ui(() => band.Auto.Entry!.Path)).Length, new FileInfo(saved).Length, "保存したファイルの大きさ");

        // プロジェクトの保存と開く
        var project = Path.Combine(folder, "プロジェクト \U0001F600.glint");
        var json = app.Ui(() => app.Shell.Session.Json);
        app.FileDialogs.SaveAnswers.Enqueue(project);
        Check.True(Key(app, System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift), "Ctrl+Shift+S が処理されない");
        Check.True(UiHost.WaitUntil(() => !screens.Imports.Projects.IsSaving && File.Exists(project), Limit), "プロジェクトを保存できない: " + app.Ui(() => app.Shell.NoticeBar.Current?.Key));
        // 文書の無い別のアプリで開いて、JSON を比べる
        using (var second = TestApp.Start(clock: FakeClock.ForPhase5()))
        {
            second.FileDialogs.OpenAnswers.Enqueue([project]);
            Check.True(Key(second, System.Windows.Input.Key.O, System.Windows.Input.ModifierKeys.Control), "Ctrl+O が処理されない");
            WaitImports(second);
            Check.True(second.Ui(() => second.Shell.Session.HasDocument), "プロジェクトを開けない: " + second.Ui(() => second.Shell.NoticeBar.Current?.Key));
            Check.Equal(json, second.Ui(() => second.Shell.Session.Json), "開いたプロジェクトの JSON");
        }

        // コピーで作る実体の名前
        app.Ui(() => band.Handoff.PressCopy());
        Check.True(UiHost.WaitUntil(() => app.Clipboard.PlaceCalls == 1 && band.Handoff.Busy is null, Limit), "コピーされない");
        var name = Path.GetFileName(app.Ui(() => ((DataObject)app.Clipboard.Placed[0].Data).GetFileDropList()[0]!));
        Check.True(Regex.IsMatch(name, @"^glint-[\x21-\x7E]+$"), "実体の名前: " + name);
    }

    [Test]
    public void AC57_6_アプリケーションマニフェストにlongPathAwareがtrueと書かれている()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoFiles.Root, "src", "GlintLoop.App", "app.manifest"));
        Check.True(Regex.IsMatch(manifest, @"<longPathAware\b[^>]*>\s*true\s*</longPathAware>"), "app.manifest に longPathAware が無い");
        var project = File.ReadAllText(Path.Combine(RepoFiles.Root, "src", "GlintLoop.App", "GlintLoop.App.csproj"));
        Check.True(project.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", StringComparison.Ordinal), "csproj が app.manifest を使わない");
    }

    // ================================================================ (7) 大きなレイヤー(E-19)

    [Test(Slow = true)]
    public void AC57_7_最大寸法8192の書き出しは完了しlayer_render_shrunkを1回出して単色の4000x3000になる()
    {
        var baseImg = SkiaBytes.Png(4000, 3000, Img.Solid(4000, 3000, 128, 128, 128));
        using var r = ExportEdgeRig.Start(4000, 3000, png: baseImg);
        using var dir = new TempDir();
        var layer = dir.Write("b.png", SkiaBytes.Png(3000, 2000, Img.Solid(3000, 2000, 30, 140, 210)));
        Check.Equal(1, new ImageImporter(r.App.Shell.ImportTarget, new GlintLoop.Core.Diagnostics.MemoryLogSink()).Drop(new MemoryImportData().WithFiles(layer)).Added, "レイヤーを足せない");
        UiHost.Idle();
        r.App.Ui(() =>
        {
            // 出力設定だけの変更では表示を描き直さないので、配置を最後に変える
            r.Session.SetOutputFormat(ExportFormat.Png);
            r.Session.SetOutputSize("long", 4000);
            Check.True(r.Session.SetLayerPlacement(1, 0, 0, 400).Changed, "配置");
            r.App.Shell.NoticeBar.Close();
        });
        // 偽のアダプターの最大寸法の代わりに、描画の上限の判定に使う値を 8192 にした描き手で書き出す(製品の入口はそのまま)
        var service = r.App.Shell.RenderReady!.GetAwaiter().GetResult();
        using var renderer = new DocumentRenderer(service, r.App.Shell.Sources, new DocumentRendererOptions { MaxTextureDimension = 8192 }, r.App.Shell.Catalog);
        r.App.Ui(() => r.Band.Pipeline = new RenderExportPipeline(renderer, r.App.Shell.Sources, r.App.Shell.Catalog, r.App.Shell.Log));
        UiHost.Idle();
        // 表示の描画が落ち着いてから調べる
        var canvas = M<CanvasModule>(r.App);
        Check.True(UiHost.WaitUntil(() => !canvas.Processed.IsBusy && ReferenceEquals(canvas.Processed.PresentedDocument, r.Session.Document), Limit), "表示が更新されない");
        UiHost.Idle();
        var json = r.App.Ui(() => r.Session.Json);
        var undo = r.App.Ui(() => r.Session.UndoCount);

        r.ExportDone();
        Check.Equal(0, EdgeLog.Count(r.App, "export_fail") + EdgeLog.Count(r.App, "render_fail"), "export_fail と render_fail が無い");
        Check.Equal(1, EdgeLog.Count(r.App, "export_done"), "export_done が 1 行");
        Check.True(!r.Status.Contains(NoticeTexts.Format(NoticeKeys.ExportFailed), StringComparison.Ordinal), "export.failed が出ない: " + r.Status);
        using (var bitmap = SKBitmap.Decode(r.App.Ui(() => r.Band.Auto.Outcome!.Path)))
        {
            Check.Equal((4000, 3000), (bitmap.Width, bitmap.Height), "出力の大きさ");
            var bad = 0;
            for (var y = 0; y < bitmap.Height; y += 7)
            {
                for (var x = 0; x < bitmap.Width; x += 7)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (Math.Abs(c.Red - 30) > 1 || Math.Abs(c.Green - 140) > 1 || Math.Abs(c.Blue - 210) > 1 || c.Alpha != 255) bad++;
                }
            }
            Check.Equal(0, bad, "全画素がレイヤーの単色");
        }
        Check.Equal(json, r.App.Ui(() => r.Session.Json), "通知の前後で JSON が変わらない");
        Check.Equal(undo, r.App.Ui(() => r.Session.UndoCount), "通知の前後で履歴の件数が変わらない");

        var notice = r.App.Ui(() => r.App.Shell.NoticeBar.Current);
        Check.Equal(NoticeKeys.LayerRenderShrunk, notice?.Key, "layer.render_shrunk が出る");
        Check.Equal(NoticeLevel.Info, notice?.Level, "info");

        // スライダーを動かしてやり直しても通知は出ない
        r.App.Ui(() =>
        {
            r.App.Shell.NoticeBar.Close();
            r.Session.SetLayerOpacity(1, 99);
        });
        r.ExportDone("やり直し");
        Check.Equal<string?>(null, r.App.Ui(() => r.App.Shell.NoticeBar.Current?.Key), "やり直しでは通知が出ない");
    }

    [Test(Slow = true)]
    public void AC57_7_E19_画面では縮めず書き出しだけ縮めるレイヤーでもlayer_render_shrunkが出る()
    {
        var baseImg = SkiaBytes.Png(4000, 3000, Img.Solid(4000, 3000, 128, 128, 128));
        using var r = ExportEdgeRig.Start(4000, 3000, png: baseImg);
        using var dir = new TempDir();
        var layer = dir.Write("b.png", SkiaBytes.Png(3000, 2000, Img.Solid(3000, 2000, 30, 140, 210)));
        Check.Equal(1, new ImageImporter(r.App.Shell.ImportTarget, new GlintLoop.Core.Diagnostics.MemoryLogSink()).Drop(new MemoryImportData().WithFiles(layer)).Added, "レイヤーを足せない");
        UiHost.Idle();
        r.App.Ui(() =>
        {
            // 出力設定だけの変更では表示を描き直さないので、配置を最後に変える
            r.Session.SetOutputFormat(ExportFormat.Png);
            r.Session.SetOutputSize("long", 4000);
            Check.True(r.Session.SetLayerPlacement(1, 0, 0, 200).Changed, "配置");
            r.App.Shell.NoticeBar.Close();
        });
        var canvas = M<CanvasModule>(r.App);
        Check.True(UiHost.WaitUntil(() => !canvas.Processed.IsBusy && ReferenceEquals(canvas.Processed.PresentedDocument, r.Session.Document), Limit), "表示が更新されない");
        UiHost.Idle();
        // 前提: プロキシ(長辺 2048)の 3072×2048 は上限の内で、書き出し(長辺 4000)の 6000×4000 は 16,777,216 画素を超える
        var renderer = r.App.Shell.RendererReady!.GetAwaiter().GetResult();
        var doc = r.App.Ui(() => r.Session.Document!);
        Check.Equal(0, renderer.Layout(doc, renderer.ProxyLongSide(doc)).Layers[1].Shrink.N, "前提: 画面では縮めない");
        Check.True(renderer.Layout(doc, 4000).Layers[1].Shrink.N >= 1, "前提: 書き出しでは縮める");
        Check.Equal<string?>(null, r.App.Ui(() => r.App.Shell.NoticeBar.Current?.Key), "前提: 画面の描画では通知が出ない");

        r.ExportDone();
        Check.Equal(NoticeKeys.LayerRenderShrunk, r.App.Ui(() => r.App.Shell.NoticeBar.Current?.Key), "書き出しで縮めたときに layer.render_shrunk が出る");
    }
}
