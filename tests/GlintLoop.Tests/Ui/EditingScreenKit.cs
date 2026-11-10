// AC-21・AC-22 の仕上げの道具: 本物の部品がそろった編集画面(画像を取り込んだ文書)を、WARP と一時フォルダの画面の外のアプリで作る。
// 取り込みは偽のクリップボードと Ctrl+V(画面の配線を通す)。2 枚目に空のレイヤー(Ctrl+Shift+N)を足して選び、そのスタックにぼかしを入れる。
// layers を渡すと、空のレイヤーをその枚数になるまで足す(32 枚の文書の検査)。部品の取り出しと、修飾キーつきのキーもここに置く。
using System.Windows;
using System.Windows.Input;
using GlintLoop.App.Layers;
using GlintLoop.App.Looks;
using GlintLoop.App.Screens;
using GlintLoop.App.Stack;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

internal static class EditingScreenKit
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public static T M<T>(TestApp app) where T : class => app.Ui(() => app.Shell.Modules.OfType<T>().Single());

    /// <summary>修飾キーを入れて、フォーカスのある要素へキーを 1 回送る(UI スレッドで)。処理済みなら true。</summary>
    public static bool Key(TestApp app, Key key, ModifierKeys mods = ModifierKeys.None) => app.Ui(() =>
    {
        app.Modifiers.Current = mods;
        try
        {
            return UiInput.Key(app.Shell.Window, key);
        }
        finally
        {
            app.Modifiers.Current = ModifierKeys.None;
        }
    });

    /// <summary>取り込みが終わる(門を出る)まで待つ。</summary>
    public static void WaitImports(TestApp app)
    {
        var screens = M<ScreensModule>(app);
        Check.True(UiHost.WaitUntil(() => !screens.Imports.IsImporting && !screens.Imports.Gate.IsBusy, Wait), "取り込みが終わらない");
    }

    /// <summary>
    /// 編集画面を作る: 400×300 の画像を Ctrl+V で取り込み(土台)、Ctrl+Shift+N で空のレイヤーを足し(layers 枚まで)、選んだレイヤーにぼかしを入れて、
    /// 行の選択を外す(パラメータ欄はレイヤーの設定)。キャンバスに加工後の表示が載るまで待つ。
    /// </summary>
    public static TestApp Start(int layers = 2, bool gpu = true)
    {
        var app = TestApp.Start(gpu: gpu ? GpuAdapterRequest.Warp : null);
        try
        {
            app.Clipboard.Data = new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(400, 300, Img.Pattern(400, 300, alpha: false)));
            Check.True(Key(app, System.Windows.Input.Key.V, ModifierKeys.Control), "Ctrl+V が処理されない");
            WaitImports(app);
            Check.True(app.Ui(() => app.Shell.Session.HasDocument), "Ctrl+V で文書ができない");
            for (var i = 1; i < layers; i++)
                Check.True(Key(app, System.Windows.Input.Key.N, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+N が処理されない");
            var stack = M<StackModule>(app);
            app.Ui(() =>
            {
                var s = app.Shell.Session;
                s.Select(Target.OfLayer(Math.Min(1, s.Document!.Layers.Count - 1)));
                Check.True(stack.AddEffect("blur").Changed, "ぼかしを足せない");
                stack.ClearRowSelection();
            });
            UiHost.Idle();
            if (gpu)
            {
                var canvas = M<CanvasModule>(app);
                Check.True(UiHost.WaitUntil(() => canvas.Picture.Bitmap is not null && ReferenceEquals(canvas.Processed.PresentedDocument, app.Shell.Session.Document), Wait),
                    "加工後の表示が載らない");
            }
            UiHost.Idle();
            return app;
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    /// <summary>左ペインのタブを選ぶ。</summary>
    public static void SelectTab(TestApp app, LeftPaneTab tab)
    {
        var looks = M<LooksModule>(app);
        app.Ui(() => Check.True(looks.SelectTab(tab), "タブを選べない: " + tab));
        UiHost.Idle();
    }

    /// <summary>ウィンドウのクライアント領域の大きさ(DIP)を変えて、レイアウトが済むまで待つ(枠とタイトルバーの分は今の大きさから測って足す)。</summary>
    public static void Resize(TestApp app, double w, double h)
    {
        app.Ui(() =>
        {
            var win = app.Shell.Window;
            win.Width = w + (win.ActualWidth - win.RootLayer.ActualWidth);
            win.Height = h + (win.ActualHeight - win.RootLayer.ActualHeight);
        });
        UiHost.WaitUntil(() =>
        {
            var c = app.Shell.Window.ClientSize;
            return Math.Abs(c.Width - w) < 1 && Math.Abs(c.Height - h) < 1;
        }, Wait);
        UiHost.Idle();
    }

    /// <summary>空のレイヤーの名前を UI スレッドで読む(テストの説明用)。</summary>
    public static LayersModule Layers(TestApp app) => M<LayersModule>(app);
}
