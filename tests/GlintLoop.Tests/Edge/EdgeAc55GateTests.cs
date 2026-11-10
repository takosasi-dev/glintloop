// AC-55 の (13)(14): 確認ダイアログの表示中と取り込みの処理中の操作(E-20)、書き出しの実行中と待機中の取り込みと新規(E-21)。
// (13) は取り込みの途中でデコードの呼び出しを止める。(14) は 10 フレーム目で止まる偽のエンコーダーを本物の中身(RenderExportPipeline。export_cancel を書く)に入れて WARP で動かす(Slow)。
// 待機の取り消しの細かい検査は HandoffAc43Tests にもある。
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.App.Layers;
using GlintLoop.App.Screens;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc55GateTests
{
    private static readonly TimeSpan Limit = EdgeRig.Limit;

    private static DataObject FileDrop(string path) => new(DataFormats.FileDrop, new[] { path });

    private static void Click(EdgeRig r, System.Windows.Controls.Button b) => r.App.Ui(() =>
    {
        Check.True(b.IsEnabled, b.Content + " が無効");
        b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    });

    private static ReplaceConfirmView WaitConfirm(EdgeRig r)
    {
        Check.True(UiHost.WaitUntil(() => r.App.Shell.Dialogs.Top is ReplaceConfirmView, Limit), "確認ダイアログが出ない");
        UiHost.Idle();
        return r.App.Ui(() => (ReplaceConfirmView)r.App.Shell.Dialogs.Top!);
    }

    // ================================================================ (13) 確認ダイアログの表示中(E-20)

    [Test]
    public void AC55_13_E20_確認ダイアログの表示中は新規と貼り付けとドロップと開くが増えない()
    {
        using var r = EdgeRig.Start();
        var png = EdgeBytes.Solid(30, 40, 50);
        var path = r.Dir.Write("other.png", EdgeBytes.Solid(60, 70, 80));
        Check.Equal(1, r.PastePng(png)?.Added, "土台");
        r.App.Ui(() => Check.True(r.Session.AddEffect(Target.OfLayer(0), "invert").Changed, "エフェクトを足せない"));
        var json = r.App.Ui(() => r.Session.Json);
        var undo = r.UndoCount;

        Check.True(r.Key(Key.N, ModifierKeys.Control), "Ctrl+N が処理されない");
        var view = WaitConfirm(r);

        var reads = r.App.Ui(() => r.App.Clipboard.ReadCalls);
        var opens = r.App.Ui(() => r.App.FileDialogs.Calls);
        var decodes = r.Decodes;
        r.App.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, EdgeBytes.Solid(90, 91, 92));
        r.App.FileDialogs.OpenAnswers.Enqueue([path]);

        r.Key(Key.N, ModifierKeys.Control);
        r.Key(Key.V, ModifierKeys.Control);
        r.RaiseDrop(FileDrop(path));
        r.Key(Key.O, ModifierKeys.Control);
        // 画面の入口を直接呼んでも同じ(キーの処理の順に頼らない)
        r.App.Ui(() =>
        {
            _ = r.Screens.RequestNewAsync();
            _ = r.Imports.PasteAsync();
            _ = r.Imports.DropAsync(FileDrop(path));
            _ = r.Imports.OpenAsync();
        });
        UiHost.Idle();

        Check.Equal(1, r.App.Ui(() => r.App.Shell.Dialogs.Count), "確認ダイアログは 1 つだけ");
        Check.True(r.App.Ui(() => ReferenceEquals(r.App.Shell.Dialogs.Top, view)), "確認ダイアログが同じ");
        Check.Equal(reads, r.App.Ui(() => r.App.Clipboard.ReadCalls), "クリップボードの読み取り");
        Check.Equal(opens, r.App.Ui(() => r.App.FileDialogs.Calls), "開くダイアログの呼び出し");
        Check.Equal(decodes, r.Decodes, "デコードの呼び出し");

        // キャンセル: 文書の JSON と履歴の件数が変わらない
        Click(r, r.App.Ui(() => view.Buttons[ReplaceChoice.Cancel]));
        Check.True(UiHost.WaitUntil(() => !r.App.Shell.Dialogs.IsOpen, Limit), "キャンセルで閉じない");
        Check.Equal(json, r.App.Ui(() => r.Session.Json), "キャンセルのあとの文書の JSON");
        Check.Equal(undo, r.UndoCount, "キャンセルのあとの履歴の件数");

        // 保存せず置き換え: ウェルカム画面になる
        Check.True(r.Key(Key.N, ModifierKeys.Control), "2 回目の Ctrl+N");
        view = WaitConfirm(r);
        Click(r, r.App.Ui(() => view.Buttons[ReplaceChoice.ReplaceWithoutSaving]));
        Check.True(UiHost.WaitUntil(() => !r.Session.HasDocument, Limit), "保存せず置き換えで文書が閉じない");
        Check.True(UiHost.WaitUntil(() => r.App.Shell.Window.WelcomeHost.Visibility == Visibility.Visible, Limit), "ウェルカム画面にならない");
    }

    // ================================================================ (13) 取り込みの処理中(E-20)

    [Test]
    public void AC55_13_E20_取り込みの処理中は取り込みと新規を無視しレイヤーの操作は受け付ける()
    {
        using var r = EdgeRig.Start();
        var other = r.Dir.Write("other.png", EdgeBytes.Solid(60, 70, 80));
        r.LoadTwoLayers();
        // 新規の確認ダイアログの出る状態にしておく(新規が通れば確認が開くので、無視されたことが分かる)
        r.App.Ui(() => Check.True(r.Session.SetLayerOpacity(0, 90).Changed, "不透明度を変えられない"));

        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        r.OnDecode = () =>
        {
            entered.Set();
            gate.Wait();
        };
        try
        {
            r.App.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, EdgeBytes.Solid(1, 200, 3));
            Check.True(r.Key(Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
            Check.True(entered.Wait(Limit), "デコードまで進まない");
            Check.True(r.App.Ui(() => r.Imports.IsImporting), "取り込みの処理中でない");

            var reads = r.App.Ui(() => r.App.Clipboard.ReadCalls);
            var decodes = r.Decodes;
            r.Key(Key.V, ModifierKeys.Control);
            r.RaiseDrop(FileDrop(other));
            r.Key(Key.N, ModifierKeys.Control);
            r.Key(Key.O, ModifierKeys.Control);
            UiHost.Idle();
            Check.Equal(reads, r.App.Ui(() => r.App.Clipboard.ReadCalls), "クリップボードの読み取り");
            Check.Equal(decodes, r.Decodes, "デコードの呼び出し");
            Check.True(r.App.Ui(() => !r.App.Shell.Dialogs.IsOpen), "確認ダイアログが開いた");
            Check.Equal(0, r.App.Ui(() => r.App.FileDialogs.Calls), "開くダイアログが開いた");

            // レイヤーの不透明度の変更・複製・並べ替え・削除は受け付けて、履歴に積む
            var list = EditingScreenKit.M<LayersModule>(r.App).List;
            var undo = r.UndoCount;
            r.App.Ui(() =>
            {
                r.Session.Select(Target.OfLayer(1));
                Check.True(r.Session.SetLayerOpacity(1, 50).Changed, "不透明度の変更を受け付けない");
                list.FocusSelected();
            });
            Check.Equal(undo + 1, r.UndoCount, "不透明度の変更が履歴に積まれる");
            r.Key(Key.D, ModifierKeys.Control);
            Check.Equal(3, r.LayerNames.Length, "複製を受け付けない");
            Check.Equal(undo + 2, r.UndoCount, "複製が履歴に積まれる");
            var names = r.LayerNames;
            r.Key(Key.Down, ModifierKeys.Alt);
            Check.True(!names.SequenceEqual(r.LayerNames), "並べ替えを受け付けない");
            Check.Equal(undo + 3, r.UndoCount, "並べ替えが履歴に積まれる");
            r.Key(Key.Delete);
            Check.Equal(2, r.LayerNames.Length, "削除を受け付けない");
            Check.Equal(undo + 4, r.UndoCount, "削除が履歴に積まれる");
            Check.True(r.App.Ui(() => r.Imports.IsImporting), "まだ取り込みの処理中");

            // 再開すると、最初の取り込みの 1 枚だけが最前面に足されて選ばれる
            var before = r.LayerNames;
            r.ClearNotice();
            gate.Set();
            r.WaitImports();
            UiHost.Idle();
            Check.SequenceEqual([.. before, "貼り付け"], r.LayerNames, "最初の取り込みの 1 枚だけが足される");
            Check.Equal(Target.OfLayer(2), r.App.Ui(() => r.Session.Selected), "足した画像が選ばれる");
            Check.Equal(undo + 5, r.UndoCount, "取り込みが履歴に積まれる");
            Check.Equal(decodes, r.Decodes, "デコードの呼び出しは最初の 1 回だけ");
            r.ExpectNotice(NoticeKeys.LayerAdded, NoticeLevel.Info, "再開のあと");
        }
        finally
        {
            gate.Set();
            r.OnDecode = null;
        }
    }

    // ================================================================ (14) 取り込みの割り込み(E-21)

    /// <summary>
    /// 1 回目の Encode だけ、10 フレームを読んだところで止まる偽のエンコーダー。2 回目からは本物の GIF のエンコーダーに任せる。
    /// honorCancel なら止まっている間に取り消しで抜ける(本物がフレームの切れ目で止まるのと同じ結果)。false なら再開されるまで抜けず、再開のあと次のフレームで取り消しを見る。
    /// </summary>
    private sealed class StallingEncoder(bool honorCancel) : IExportEncoder
    {
        private int _calls;

        public SemaphoreSlim Gate { get; } = new(0);

        public ManualResetEventSlim Stopped { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public string Format => ExportFormat.Gif;

        public EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                for (var i = 0; i < 10; i++)
                {
                    frames.GetFrame(i, run.Cancel);
                    output.WriteByte((byte)i);
                }
                output.Flush();
                Stopped.Set();
                if (honorCancel) Gate.Wait(run.Cancel);
                else Gate.Wait();
                frames.GetFrame(10, run.Cancel);
            }
            return new GifExportEncoder().Encode(frames, settings, output, limitBytes, run);
        }
    }

    /// <summary>GIF・spin(N=20)の文書で、10 フレーム目で書き出しが止まり、コピーが待機になっている状態を作る。keepHistory が false なら履歴を空にする。</summary>
    private static (ExportBandModule Band, StallingEncoder Encoder, string Key) Stalled(EdgeRig r, bool honorCancel, bool keepHistory)
    {
        var band = r.WaitBand();
        Check.Equal(1, r.PastePng(SkiaBytes.Png(48, 32, Img.Pattern(48, 32, alpha: false)))?.Added, "土台");
        var encoder = new StallingEncoder(honorCancel);
        var renderer = r.App.Shell.RendererReady!.GetAwaiter().GetResult();
        r.App.Ui(() =>
        {
            r.App.Shell.Settings.CopyMode = CopyModeSetting.File;
            var s = r.Session;
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
            s.SetLength10(10);
            s.SetFps(20);
            s.SetOutputFormat(ExportFormat.Gif);
            if (!keepHistory)
            {
                var target = r.App.Shell.ImportTarget;
                target.OpenDocument(s.Document!, target.Images.Values.ToArray());
            }
            band.Pipeline = new RenderExportPipeline(renderer, r.App.Shell.Sources, r.App.Shell.Catalog, r.App.Shell.Log, [encoder]);
        });
        UiHost.Idle();
        r.App.Ui(() => r.App.Clock.Advance(AutoExport.Delay));
        Check.True(encoder.Stopped.Wait(Limit), "偽のエンコーダーが 10 フレーム目まで進まない");
        UiHost.Idle();
        Check.Equal(ExportState.Running, r.App.Ui(() => band.Auto.State), "書き出しの実行中");
        Check.Equal(1, r.TmpFiles().Length, ".tmp が書かれている");
        var key = r.App.Ui(() => band.Auto.Key)!;

        Click(r, r.App.Ui(() => band.View.Copy));
        UiHost.Idle();
        Check.Equal("完成したらコピー", r.App.Ui(() => (string)band.View.Copy.Content), "コピーの待機");
        Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), "待機の間はクリップボードに置かない");
        return (band, encoder, key);
    }

    private static string[] CacheFilesOf(EdgeRig r, string key) => Directory.GetFiles(r.App.Paths.CacheExportsDir, key + "*");

    private static void ExpectCancelled(EdgeRig r, ExportBandModule band, string key, string what)
    {
        Check.True(UiHost.WaitUntil(() => r.TmpFiles().Length == 0, Limit), what + ": .tmp が消えない");
        Check.True(UiHost.WaitUntil(() => r.LogCount("export_cancel") >= 1, Limit), what + ": export_cancel が書かれない");
        Check.Equal(1, r.LogCount("export_cancel"), what + ": export_cancel は 1 行");
        Check.Equal("コピー", r.App.Ui(() => (string)band.View.Copy.Content), what + ": ボタンの表示が戻る");
        Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), what + ": クリップボードの呼び出しが増えない");
        Check.Equal(0, r.App.Ui(() => r.App.FileDialogs.Calls), what + ": 保存ダイアログの呼び出しが増えない");
        Check.Equal(0, CacheFilesOf(r, key).Length, what + ": 取り消した書き出しがキャッシュに残らない");
    }

    [Test(Slow = true)]
    public void AC55_14_E21_取り込みで書き出しと待機が取り消され履歴は1件で足したあとの内容で完成する()
    {
        using var r = EdgeRig.Start(gpu: true);
        var (band, encoder, key) = Stalled(r, honorCancel: true, keepHistory: false);
        Check.Equal(0, r.UndoCount, "足す前の履歴は空");
        try
        {
            r.ClearNotice();
            Check.Equal(1, r.PastePng(EdgeBytes.Solid(200, 100, 50))?.Added, "2 枚目のレイヤーが足される");
            Check.True(r.App.Ui(() => !r.App.Shell.Dialogs.IsOpen), "確認ダイアログは出ない");
            Check.Equal(2, r.LayerNames.Length, "2 枚");
            ExpectCancelled(r, band, key, "取り込み");
            Check.Equal(1, r.UndoCount, "Undo の履歴が 1 件");

            // 止めていたエンコーダーを再開しても、キャッシュにもコピーにも影響しない
            encoder.Gate.Release();
            UiHost.Idle();
            ExpectCancelled(r, band, key, "再開のあと");

            // 足したあとの内容での書き出しが新しく始まって完成する
            var o = r.ExportDone(band);
            Check.Equal(2, encoder.Calls, "新しい書き出しが 1 回");
            Check.Equal(2, r.App.Ui(() => band.Auto.Job!.Document.Layers.Count), "足したあとの内容");
            Check.True(File.Exists(o.Path) && new FileInfo(o.Path).Length > 0, "完成したファイル");
            Check.Equal(0, r.TmpFiles().Length, ".tmp が残らない");
            Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), "取り消したコピーは行われない");

            // 元に戻すボタンが有効で、1 回押すと足したレイヤーが消える
            var undoButton = r.App.Ui(() => r.App.Shell.CommandBar.Buttons.First(b => (string)b.ToolTip == "元に戻す(Ctrl+Z)"));
            Check.True(r.App.Ui(() => undoButton.IsEnabled), "元に戻すボタンが有効");
            r.App.Ui(() => undoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
            UiHost.Idle();
            Check.Equal(1, r.LayerNames.Length, "1 回押すと足したレイヤーが消える");
        }
        finally
        {
            encoder.Gate.Release();
        }
    }

    private static void ConfirmNewWithoutSaving(EdgeRig r)
    {
        Check.True(r.Key(Key.N, ModifierKeys.Control), "Ctrl+N が処理されない");
        var view = WaitConfirm(r);
        Click(r, r.App.Ui(() => view.Buttons[ReplaceChoice.ReplaceWithoutSaving]));
        Check.True(UiHost.WaitUntil(() => !r.Session.HasDocument, Limit), "文書が閉じない");
        Check.True(UiHost.WaitUntil(() => r.App.Shell.Window.WelcomeHost.Visibility == Visibility.Visible, Limit), "ウェルカム画面にならない");
    }

    [Test(Slow = true)]
    public void AC55_14_E21_新規で書き出しと待機が取り消されウェルカム画面になり履歴が空で新しい書き出しは始まらない()
    {
        using var r = EdgeRig.Start(gpu: true);
        var (band, encoder, key) = Stalled(r, honorCancel: true, keepHistory: true);
        try
        {
            ConfirmNewWithoutSaving(r);
            ExpectCancelled(r, band, key, "新規");
            Check.Equal(0, r.UndoCount, "履歴が空");

            encoder.Gate.Release();
            UiHost.Idle();
            r.App.Ui(() => r.App.Clock.Advance(AutoExport.Delay));
            UiHost.Idle();
            ExpectCancelled(r, band, key, "再開のあと");
            Check.Equal(1, encoder.Calls, "新しい書き出しは始まらない");
            Check.Equal(ExportState.Idle, r.App.Ui(() => band.Auto.State), "書き出しは待機のまま");
        }
        finally
        {
            encoder.Gate.Release();
        }
    }

    [Test(Slow = true)]
    public void AC55_14_E21_止まったまま抜けないエンコーダーでも再開のあとキャッシュとコピーに影響しない()
    {
        foreach (var replace in new[] { false, true })
        {
            var what = replace ? "新規" : "取り込み";
            using var r = EdgeRig.Start(gpu: true);
            var (band, encoder, key) = Stalled(r, honorCancel: false, keepHistory: replace);
            try
            {
                if (replace) ConfirmNewWithoutSaving(r);
                else Check.Equal(1, r.PastePng(EdgeBytes.Solid(200, 100, 50))?.Added, "2 枚目のレイヤーが足される");
                UiHost.Idle();
                // 取り消しの合図は立っている。エンコーダーはまだ抜けていない
                Check.Equal("コピー", r.App.Ui(() => (string)band.View.Copy.Content), what + ": 再開の前にボタンの表示が戻る");
                Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), what + ": 再開の前のクリップボード");

                encoder.Gate.Release();
                ExpectCancelled(r, band, key, what);
                if (replace)
                {
                    r.App.Ui(() => r.App.Clock.Advance(AutoExport.Delay));
                    UiHost.Idle();
                    Check.Equal(1, encoder.Calls, what + ": 新しい書き出しは始まらない");
                }
                else
                {
                    var o = r.ExportDone(band);
                    Check.Equal(2, encoder.Calls, what + ": 新しい書き出しが 1 回");
                    Check.True(File.Exists(o.Path), what + ": 新しい書き出しが完成する");
                    Check.Equal(0, r.App.Ui(() => r.App.Clipboard.PlaceCalls), what + ": 完成しても取り消したコピーは行われない");
                }
            }
            finally
            {
                encoder.Gate.Release();
            }
        }
    }
}
