// AC-52・AC-57・AC-58 のテストの道具: 本物の書き出しの中身(RenderExportPipeline)を、偽のエンコーダー・偽のデバイス工場・偽のファイルシステムと組み合わせて画面の外のアプリに入れる。
// 偽のエンコーダーは決まった大きさのバイト列を書き、呼ばれた設定を記録する(フレームを描かないので速い)。渡すエンコーダーを省くと本物のエンコーダーで動く。
// ログはログのファイルを読み直す(Dispose が app_exit(0) を足すので、Dispose の前に読む)。押す操作はボタンの Click を発生させる。
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlintLoop.App.Export;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Exports;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Ui.EditingScreenKit;

namespace GlintLoop.Tests.Edge;

/// <summary>決まった大きさのバイト列を書く偽のエンコーダー。上限を超えた時点で打ち切る約束は本物と同じ(ExportEncoding.Run)。</summary>
internal sealed class SizedEncoder(string format, long bytes) : IExportEncoder
{
    private readonly ConcurrentQueue<(ExportSettings Settings, long? Limit)> _calls = new();

    public string Format => format;

    public IReadOnlyList<(ExportSettings Settings, long? Limit)> Calls => _calls.ToArray();

    public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
    {
        _calls.Enqueue((settings, limitBytes));
        return ExportEncoding.Run(output, limitBytes, s =>
        {
            var chunk = new byte[4096];
            for (var written = 0L; written < bytes; written += chunk.Length)
            {
                s.Write(chunk, 0, (int)Math.Min(chunk.Length, bytes - written));
            }
        });
    }
}

/// <summary>最初の 1 回だけ例外を投げ、そのあとは中の本物に任せるエンコーダー(E-32 のやり直しの確かめ)。</summary>
internal sealed class ThrowOnceEncoder(IExportEncoder inner) : IExportEncoder
{
    private int _calls;

    public string Format => inner.Format;

    public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
    {
        if (Interlocked.Increment(ref _calls) == 1) throw new InvalidDataException("偽のエンコーダーの失敗");
        return inner.Encode(frames, settings, output, limitBytes, run);
    }
}

/// <summary>ログのファイルの読み取り。</summary>
internal static class EdgeLog
{
    public static List<JsonElement> Lines(TestApp app)
    {
        var lines = new List<JsonElement>();
        if (!Directory.Exists(app.Paths.LogsDir)) return lines; // まだ 1 行も書いていない
        foreach (var f in Directory.GetFiles(app.Paths.LogsDir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                if (line.Length > 0) lines.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
        }
        return lines;
    }

    public static string[] Events(TestApp app) => Lines(app).Select(l => l.GetProperty("event").GetString()!).ToArray();

    public static int Count(TestApp app, string name) => Events(app).Count(e => e == name);

    public static JsonElement Last(TestApp app, string name) => Lines(app).Last(l => l.GetProperty("event").GetString() == name);

    public static string Text(TestApp app)
    {
        var text = "";
        if (!Directory.Exists(app.Paths.LogsDir)) return text;
        foreach (var f in Directory.GetFiles(app.Paths.LogsDir, "*.jsonl"))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            text += r.ReadToEnd();
        }
        return text;
    }
}

internal sealed class ExportEdgeRig : IDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private ExportEdgeRig(TestApp app, ExportBandModule band, TempDir dir, string source)
    {
        App = app;
        Band = band;
        Dir = dir;
        Source = source;
    }

    public TestApp App { get; }

    public ExportBandModule Band { get; }

    public TempDir Dir { get; }

    /// <summary>取り込んだ PNG のパス。</summary>
    public string Source { get; }

    public EditorSession Session => App.Shell.Session;

    public ExportHandoff Handoff => Band.Handoff;

    public ExportBandView View => Band.View;

    public FakeFileSystem Fs => App.FileSystem;

    public string Status => App.Ui(() => View.StatusText);

    /// <summary>
    /// w×h の画像を取り込んだ文書と、本物の書き出しの中身。encoders を渡すとそのエンコーダーを使う。factory を渡すと高性能の要求で偽のデバイス工場を通す(既定は WARP)。
    /// configure で文書を整える(UI スレッド)。書き出しはまだ始まらない(StartExport の前)。
    /// </summary>
    public static ExportEdgeRig Start(int w, int h, IReadOnlyList<IExportEncoder>? encoders = null, Action<EditorSession>? configure = null,
        FakeDeviceFactory? factory = null, FakeFileSystem? fileSystem = null, byte[]? png = null)
    {
        var app = TestApp.Start(clock: FakeClock.ForPhase5(), gpu: factory is null ? GpuAdapterRequest.Warp : GpuAdapterRequest.HighPerformance, deviceFactory: factory, fileSystem: fileSystem);
        var dir = new TempDir();
        try
        {
            var source = dir.Write("a.png", png ?? SkiaBytes.Png(w, h, Img.Pattern(w, h, alpha: false)));
            var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
            Check.Equal(1, importer.Drop(new MemoryImportData().WithFiles(source)).Added, "取り込めた枚数");
            UiHost.Idle();
            var band = M<ExportBandModule>(app);
            Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, Limit), "本物の中身が入らない");
            var rig = new ExportEdgeRig(app, band, dir, source);
            app.Ui(() =>
            {
                app.Shell.Settings.CopyMode = CopyModeSetting.File;
                configure?.Invoke(app.Shell.Session);
            });
            if (encoders is not null) rig.UseEncoders(encoders);
            UiHost.Idle();
            return rig;
        }
        catch
        {
            app.Dispose();
            dir.Dispose();
            throw;
        }
    }

    /// <summary>本物の書き出しの中身のエンコーダーを差し替える(差し替えも編集として数え直す)。</summary>
    public void UseEncoders(IReadOnlyList<IExportEncoder> encoders)
    {
        var renderer = App.Shell.RendererReady!.GetAwaiter().GetResult();
        App.Ui(() => Band.Pipeline = new RenderExportPipeline(renderer, App.Shell.Sources, App.Shell.Catalog, App.Shell.Log, encoders));
    }

    /// <summary>モーションのある小さい文書の設定(spin 1 個、long 64、L=2.0、F=20)。</summary>
    public static void SmallMotion(EditorSession s)
    {
        Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
        s.SetLength10(20);
        s.SetFps(20);
        s.SetOutputFormat(ExportFormat.Gif);
        s.SetOutputSize("long", 64);
    }

    public void Dispose()
    {
        App.Dispose();
        Dir.Dispose();
    }

    public void Advance(double ms)
    {
        UiHost.Idle();
        App.Ui(() => App.Clock.Advance(TimeSpan.FromMilliseconds(ms)));
        UiHost.Idle();
    }

    public void StartExport() => Advance(AutoExport.Delay.TotalMilliseconds);

    public void WaitEnd(string what = "書き出し") =>
        Check.True(UiHost.WaitUntil(() => Band.Auto.State is ExportState.Done or ExportState.Failed, Limit), what + "が終わらない: " + App.Ui(() => Band.Auto.State));

    /// <summary>600 ms 進めて完了まで待つ。</summary>
    public void ExportDone(string what = "書き出し")
    {
        StartExport();
        WaitEnd(what);
        Check.Equal(ExportState.Done, App.Ui(() => Band.Auto.State), what + "の状態(" + App.Ui(() => Band.Auto.Error?.ToString()) + ")");
    }

    /// <summary>600 ms 進めて失敗まで待つ。</summary>
    public void ExportFailed(string what = "書き出し")
    {
        StartExport();
        WaitEnd(what);
        Check.Equal(ExportState.Failed, App.Ui(() => Band.Auto.State), what + "の状態");
    }

    public void Click(Button button) => App.Ui(() =>
    {
        Check.True(button.IsEnabled, button.Content + " が無効");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    });

    public void Copy() => Click(View.Copy);

    public void Save() => Click(View.Save);

    /// <summary>ドラッグ欄で押して、しきい値(4)を超えて動かす。</summary>
    public void Drag() => App.Ui(() =>
    {
        View.DragPress(new Point(10, 10));
        View.DragMove(new Point(15, 10), 4, 4);
    });

    public void Settle(string what = "渡す操作")
    {
        Check.True(UiHost.WaitUntil(() => Handoff.Busy is null && Band.Auto.Idle.IsCompleted, Limit), what + "が終わらない");
        UiHost.Idle();
    }

    public ExportConfirmOverView WaitConfirm()
    {
        Check.True(UiHost.WaitUntil(() => App.Shell.Dialogs.Top is ExportConfirmOverView, Limit), "上限超過の確認が出ない");
        return App.Ui(() => (ExportConfirmOverView)App.Shell.Dialogs.Top!);
    }

    public bool DialogOpen => App.Ui(() => App.Shell.Dialogs.IsOpen);

    /// <summary>確認のボタンを押して、閉じるまで待つ。</summary>
    public void Answer(ExportConfirmOverView view, Func<ExportConfirmOverView, Button> button)
    {
        App.Ui(() => button(view).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        Check.True(UiHost.WaitUntil(() => !App.Shell.Dialogs.IsOpen && Handoff.Busy is null, Limit), "ダイアログが閉じない");
        UiHost.Idle();
    }

    public string[] Tmp() => Directory.Exists(App.Paths.CacheExportsDir) ? Directory.GetFiles(App.Paths.CacheExportsDir, "*.tmp") : [];
}
