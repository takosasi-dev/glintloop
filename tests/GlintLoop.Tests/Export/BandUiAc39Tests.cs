// AC-39 の (7)(実出力の表示)と (2)(状態欄)・(3)(画面の状態ではキーが変わらない)を、画面の外に立てたアプリと偽の書き出しの中身で調べる。
// 実出力は F5 と「実出力」のボタンで、完成したキャッシュのファイルを出す。GIF(既存の GifEncoder で作る)と APNG(テスト用の最小の書き手)はフレームを再生し、
// 最初のフレームが WPF のデコーダーで別に読んだ最初のフレームと一致すること、編集と Esc でプロキシに戻ること、完成前は切り替えず「書き出し中」を出すことを見る。
using System.IO;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Export;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Gif;
using GlintLoop.Tests.Session;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class BandUiAc39Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    internal sealed record Band(TestApp App, ExportBandModule Module, CanvasModule Canvas) : IDisposable
    {
        public void Dispose() => App.Dispose();

        /// <summary>待ちの仕事を済ませてから、偽の時計を ms 進める(UI スレッドで)。</summary>
        public void Advance(int ms)
        {
            UiHost.Idle();
            App.Ui(() => App.Clock.Advance(TimeSpan.FromMilliseconds(ms)));
            UiHost.Idle();
        }

        public void WaitDone(string what) =>
            Check.True(UiHost.WaitUntil(() => Module.Auto.State == ExportState.Done, Wait), what + ": 書き出しが完了しない(" + App.Ui(() => Module.Auto.State) + ")");
    }

    /// <summary>画像を 1 枚取り込み、帯に偽の環境と中身を入れたアプリ(GPU なし)。</summary>
    internal static Band Start(IExportPipeline pipeline, int w = 48, int h = 32)
    {
        var app = TestApp.Start();
        using var dir = new TempDir();
        var png = SkiaBytes.Png(w, h, Img.Pattern(w, h, alpha: false));
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        Check.Equal(1, importer.Drop(new MemoryImportData().WithFiles(dir.Write("a.png", png))).Added, "取り込めた枚数");
        UiHost.Idle();
        var module = app.Ui(() => app.Shell.Modules.OfType<ExportBandModule>().Single());
        var canvas = app.Ui(() => app.Shell.Modules.OfType<CanvasModule>().Single());
        app.Ui(() =>
        {
            module.Auto.Environment = BandTestKit.Env();
            module.Pipeline = pipeline;
        });
        return new Band(app, module, canvas);
    }

    private static byte[] Rgba(int w, int h, Func<int, int, (byte R, byte G, byte B)> f)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = f(x, y);
                var i = (y * w + x) * 4;
                px[i] = r;
                px[i + 1] = g;
                px[i + 2] = b;
                px[i + 3] = 255;
            }
        }
        return px;
    }

    private static byte[] Pixels(BitmapSource b)
    {
        var src = b.Format == PixelFormats.Bgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var px = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return px;
    }

    private static ActualOutputView Shown(Band b)
    {
        Check.True(UiHost.WaitUntil(() => b.Module.PendingShow is null && b.Canvas.IsActualOutput, Wait), "実出力に切り替わらない");
        return b.App.Ui(() =>
        {
            Check.Equal(CanvasMode.ActualOutput, b.Canvas.Mode, "キャンバスのモード");
            var view = (ActualOutputView)b.Canvas.ActualOutputHost.Content;
            Check.True(ReferenceEquals(view, b.Module.Showing), "出している実出力");
            return view;
        });
    }

    [Test]
    public void AC39_7_F5でGIFの完成ファイルを出しフレームを再生し最初のフレームがデコードと一致し編集とEscで戻る()
    {
        const int w = 48, h = 32;
        var frames = new[]
        {
            Rgba(w, h, (x, y) => ((byte)(x * 5), (byte)(y * 7), (byte)90)),
            Rgba(w, h, (x, y) => x < 20 ? ((byte)(x * 5), (byte)(y * 7), (byte)90) : ((byte)200, (byte)30, (byte)30)),
            Rgba(w, h, (_, _) => ((byte)10, (byte)220, (byte)40)),
        };
        var gif = GifEncoder.EncodeToArray(new GifFrameList(w, h, frames), new GifEncoderOptions { Fps = 10, Parallelism = 1 });
        using var b = Start(new FakePipeline { FrameCount = 3, Content = _ => gif, Result = new ExportPipelineResult(w, h, 3, 10, null, false, null) });
        var button = b.App.Ui(() => b.App.Shell.CommandBar.Button(GlintLoop.App.Theme.Icons.ActualOutput));
        Check.True(!b.App.Ui(() => button.IsEnabled), "完成前は実出力のボタンが無効");

        // 完成前の F5: 切り替えず「書き出し中」を出す
        b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));
        Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "完成前に切り替わった");
        Check.True(b.App.Ui(() => b.Module.View.StatusText).StartsWith("書き出し中", StringComparison.Ordinal), "完成前の F5 で「書き出し中」が出ない: " + b.App.Ui(() => b.Module.View.StatusText));

        b.Advance(600);
        b.WaitDone("1 回目");
        Check.True(b.App.Ui(() => button.IsEnabled), "完成したら実出力のボタンが有効");
        Check.True(b.App.Ui(() => b.Module.View.StatusText).StartsWith("完了", StringComparison.Ordinal), "完成のあとの状態欄");
        Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "完成しても自分では切り替えない");

        b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));
        var view = Shown(b);
        var expected = Pixels(new GifBitmapDecoder(new MemoryStream(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
        b.App.Ui(() =>
        {
            Check.True(button is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true }, "ボタンが入");
            Check.Equal(0, view.Index, "最初のフレーム");
            Check.Equal((w, h), (view.Frames.Width, view.Frames.Height), "画素の大きさ");
            Check.Equal(3, view.Frames.Frames.Count, "フレーム数");
            Check.True(Pixels(view.CurrentFrame).AsSpan().SequenceEqual(expected), "表示の最初のフレームがデコードした最初のフレームと一致しない");
        });
        // ファイルの遅延のとおりに進む(10 fps → 100 ms)
        var delays = GifEncoder.FrameDelays(3, 10, 0);
        b.Advance(delays[0] * 10 - 1);
        Check.Equal(0, b.App.Ui(() => view.Index), "遅延の前");
        b.Advance(1);
        Check.Equal(1, b.App.Ui(() => view.Index), "1 枚目の遅延のあと");
        // 差分の最適化で部分だけのフレームも、前の画面に重ねた絵になる(WPF で読んで重ねたものと一致)
        var composite = GifTestKit.Composite(GifTestKit.ReadWithWpf(gif), w, h);
        b.App.Ui(() =>
        {
            for (var i = 0; i < 3; i++)
                Check.True(Pixels(view.Frames.Frames[i]).AsSpan().SequenceEqual(composite[i]), $"フレーム {i} が WPF で読んで重ねた画面と一致しない");
        });
        b.Advance(delays[1] * 10);
        b.Advance(delays[2] * 10);
        Check.Equal(0, b.App.Ui(() => view.Index), "無限のループで先頭に戻る");

        // 実出力の表示中に編集が入ると、プロキシの表示に戻る
        b.App.Ui(() => b.App.Shell.Session.SetLayerOpacity(0, 50));
        UiHost.Idle();
        b.App.Ui(() =>
        {
            Check.True(!b.Canvas.IsActualOutput, "編集のあとも実出力のまま");
            Check.True(button is System.Windows.Controls.Primitives.ToggleButton { IsChecked: false }, "編集のあとのボタン");
            Check.True(!button.IsEnabled, "編集のあとは完成していないのでボタンが無効");
            Check.True(b.Module.Showing is null, "外した実出力を持ったまま");
        });

        // Esc でも戻る
        b.Advance(600);
        b.WaitDone("2 回目");
        b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));
        Shown(b);
        b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.Escape));
        Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "Esc のあとも実出力のまま");
        // F5 は入・切のトグル
        b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));
        Shown(b);
        b.App.Ui(() => UiInput.Key(b.App.Shell.Window, Key.F5));
        Check.True(!b.App.Ui(() => b.Canvas.IsActualOutput), "2 回目の F5 で戻らない");
    }

    [Test]
    public void AC39_7_実出力のボタンのトグルでAPNGを出し重ねた絵を再生する()
    {
        const int w = 20, h = 12;
        var f0 = Rgba(w, h, (x, y) => ((byte)(x * 12), (byte)(y * 20), (byte)77));
        var sub = BandTestKit.Solid(6, 4, 0, 0, 255);
        var apng = BandTestKit.Apng(w, h,
        [
            new BandTestKit.ApngFrame(0, 0, w, h, f0, 1, 20),
            new BandTestKit.ApngFrame(5, 3, 6, 4, sub, 3, 20, Blend: 1),
        ], plays: 1);
        var pipe = new FakePipeline { FrameCount = 2, Content = _ => apng, Result = new ExportPipelineResult(w, h, 2, 20, null, false, null) };
        using var b = Start(pipe, w, h);
        b.App.Ui(() => b.App.Shell.Session.SetOutputFormat(ExportFormat.Apng));
        b.Advance(600);
        b.WaitDone("APNG");
        Check.Equal(".png", b.App.Ui(() => System.IO.Path.GetExtension(b.Module.Auto.Entry!.Path)), "APNG の拡張子");

        var root = Uia.Root(b.App.Hwnd);
        var toggle = Uia.Find(root, "実出力");
        Uia.Toggle(toggle);
        var view = Shown(b);
        var expected0 = Pixels(new PngBitmapDecoder(new MemoryStream(apng), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
        b.App.Ui(() =>
        {
            Check.Equal(2, view.Frames.Frames.Count, "APNG のフレーム数");
            Check.True(Pixels(view.CurrentFrame).AsSpan().SequenceEqual(expected0), "表示の最初のフレームがデコードした最初のフレームと一致しない");
            Check.True(Pixels(view.CurrentFrame).AsSpan().SequenceEqual(BandTestKit.ToBgra(f0)), "最初のフレームが元の画素と一致しない");
        });
        b.Advance(50);
        var px = Pixels(b.App.Ui(() => view.CurrentFrame));
        var want = (byte[])f0.Clone();
        for (var y = 3; y < 7; y++)
        {
            for (var x = 5; x < 11; x++) Buffer.BlockCopy(sub, ((y - 3) * 6 + (x - 5)) * 4, want, (y * w + x) * 4, 4);
        }
        Check.True(px.AsSpan().SequenceEqual(BandTestKit.ToBgra(want)), "2 枚目は 1 枚目の上に枠の中を重ねた絵");
        b.Advance(150);
        Check.True(b.App.Ui(() => view.Ended && view.Index == 1), "1 回の再生で最後のフレームで止まる");

        Uia.Toggle(toggle);
        Check.True(UiHost.WaitUntil(() => !b.Canvas.IsActualOutput, Wait), "ボタンをもう一度押しても戻らない");
        Check.Equal(ToggleState.Off, Uia.ToggleStateOf(toggle), "ボタンの状態");
    }

    [Test]
    public void AC39_3_ズーム背景選択とレイヤーの名前ではキーが変わらずエンコーダーを呼び直さない()
    {
        var pipe = new FakePipeline();
        using var b = Start(pipe);
        b.Advance(600);
        b.WaitDone("1 回目");
        var key = b.App.Ui(() => b.Module.Auto.Key);
        b.App.Ui(() =>
        {
            b.Canvas.Viewport.StepZoom(1);
            b.App.Shell.Settings.BgPreview = b.App.Shell.Settings.BgPreview with { Mode = GlintLoop.Core.Settings.BgPreviewMode.Black };
            b.Canvas.SetSplit(true);
            b.App.Shell.Session.Select(Target.All);
        });
        b.Advance(600);
        Check.Equal(ExportState.Done, b.App.Ui(() => b.Module.Auto.State), "画面の状態だけの変更のあと");
        b.App.Ui(() => b.App.Shell.Session.SetLayerName(0, "別の名前"));
        b.Advance(600);
        b.WaitDone("名前の変更のあと");
        Check.Equal(key, b.App.Ui(() => b.Module.Auto.Key), "名前の変更でキーが変わった");
        Check.Equal(1, pipe.Calls, "エンコーダーを呼び直した");
    }

    [Test]
    public void AC39_2_状態欄にWARPのときソフトウェア描画を省略せずに出し全文をUIAの名前に入れる()
    {
        using var b = Start(new FakePipeline { Result = new ExportPipelineResult(48, 32, 1, 20, "色数 256→64", false, null) });
        b.Advance(600);
        b.WaitDone("完了");
        var text = b.App.Ui(() => b.Module.View.StatusText);
        var bytes = b.App.Ui(() => b.Module.Auto.Outcome!.Bytes);
        var kb = (bytes / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Check.Equal($"完了 {kb} KB・48×32・1 フレーム・20 fps ソフトウェア描画 色数 256→64", text, "状態欄の全文");
        b.App.Ui(() =>
        {
            Check.True(b.Module.View.Software.IsVisible, "WARP のとき「ソフトウェア描画」");
            Check.Equal(System.Windows.TextTrimming.None, b.Module.View.Software.TextTrimming, "「ソフトウェア描画」は省略しない");
            Check.Equal(System.Windows.TextTrimming.CharacterEllipsis, b.Module.View.StatusLine1.TextTrimming, "ほかの文は「…」で省略する");
            Check.Equal(text, (string)b.Module.View.Status.ToolTip, "ToolTip に全文");
            b.Module.Auto.Environment = BandTestKit.Env(warp: false);
        });
        Check.True(!b.App.Ui(() => b.Module.View.Software.IsVisible), "GPU のときは出さない");
        var line = Uia.Find(Uia.Root(b.App.Hwnd), b.App.Ui(() => b.Module.View.StatusText));
        Check.Equal("待機", line.Current.Name, "UIA の名前に全文(環境を変えたので待機)");
    }
}
