// AC-26 の画面の検査(FR-33〜FR-36・§9.3): 左ペインの 3 つのタブ、ギャラリーのタイルの並び・名前・192 px のサムネイル(エンジンの結果とバイト列で一致)、
// 最後の変更から 300 ms 後の作り直し(偽の時計)と見えているタイルからの順、Enter での適用、強さのスライダー、名前の入力と上書きの確認、名前の変更と削除。
// 画面の外に出したアプリ(TestApp)を UIA と直接発生させるキーで操作する(INV-18)。サムネイルを描くテストだけ WARP を使う。
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Looks;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Looks;

public sealed class LooksUiAc26Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static LooksModule Module(TestApp app) => app.Shell.Modules.OfType<LooksModule>().Single();

    private static void Import(TestApp app, TempDir dir, string name, CompositeImg img)
    {
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        var o = importer.Drop(new MemoryImportData().WithFiles(dir.Write(name, SkiaBytes.Png(img.W, img.H, img.Px))));
        Check.Equal(1, o.Added, name + " を取り込めない");
        UiHost.Idle();
    }

    /// <summary>192 px より小さい画像(サムネイルを元より大きくしない確かめ)。</summary>
    private static CompositeImg Tiny(byte r, byte g, byte b) => CompositeImg.Make(120, 80, (x, y) => (r, (g + 2 * x) % 256, (b + 3 * y) % 256, 255));

    private static CompositeImg Small(byte r, byte g, byte b) => CompositeImg.Make(240, 160, (x, y) => (r, (g + x) % 256, (b + y) % 256, 255));

    [Test]
    public void AC26_1_文書が無い間はタブを出さず取り込むとルックのタブを選ぶ()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        var m = Module(app);
        app.Ui(() =>
        {
            Check.True(ReferenceEquals(app.Shell.Window.LeftPaneHost.Content, m.LeftPane), "左ペインの枠が LeftPaneHost に入っている");
            Check.Equal(Visibility.Collapsed, m.LeftPane.Tabs.Visibility, "文書が無い間のタブ");
            Check.Equal(Visibility.Visible, m.LeftPane.DisabledText.Visibility, "画像を取り込むと使えます");
        });
        Import(app, dir, "a.png", Small(200, 30, 30));
        app.Ui(() =>
        {
            Check.Equal(Visibility.Visible, m.LeftPane.Tabs.Visibility, "文書があるときのタブ");
            Check.SequenceEqual(["ルック", "エフェクト", "モーション"], m.LeftPane.Tabs.Items.OfType<TabItem>().Select(t => (string)t.Header).ToList(), "3 つのタブ");
            Check.Equal(LeftPaneTab.Looks, m.LeftPane.SelectedTab, "最初の取り込みのあと");
            m.LeftPane.SelectTab(LeftPaneTab.Effects);
            // エフェクトのタブの差し込み口
            var probe = new TextBlock { Text = "probe" };
            m.LeftPane.EffectsContent = probe;
            Check.True(ReferenceEquals(m.LeftPane.EffectsTab.Content, probe), "EffectsContent の差し込み口");
        });
        Import(app, dir, "b.png", Small(30, 200, 30));
        app.Ui(() => Check.Equal(LeftPaneTab.Looks, m.LeftPane.SelectedTab, "2 枚目の取り込みのあともルックのタブ"));
        // 空のレイヤーは取り込みではない
        app.Ui(() =>
        {
            m.LeftPane.SelectTab(LeftPaneTab.Motion);
            app.Shell.Session.AddEmptyLayer();
        });
        UiHost.Idle();
        app.Ui(() => Check.Equal(LeftPaneTab.Motion, m.LeftPane.SelectedTab, "空のレイヤーではタブを変えない"));
    }

    [Test]
    public void AC26_1_Alt1から3で左ペインのタブを切り替える()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        Import(app, dir, "a.png", Small(200, 30, 30));
        var m = Module(app);
        app.Ui(() =>
        {
            app.Modifiers.Current = ModifierKeys.Alt;
            foreach (var (key, tab) in new[] { (Key.D2, LeftPaneTab.Effects), (Key.D3, LeftPaneTab.Motion), (Key.D1, LeftPaneTab.Looks) })
            {
                Check.True(app.Shell.Router.HandleKeyDown(key, m.Gallery.Strength), $"Alt+{key} を処理しない");
                Check.Equal(tab, m.LeftPane.SelectedTab, $"Alt+{key}");
            }
            app.Modifiers.Current = ModifierKeys.None;
        });
    }

    [Test]
    public void AC26_1_タイルはルックなしと組み込みの24個とユーザーのルックの順で名前を持つ()
    {
        using var app = TestApp.Start(prepare: p =>
        {
            var store = new UserLookStore(p.LooksDir, new DictionaryEffectCatalog(Array.Empty<EffectDef>()));
            store.Save("わたしのルック", []);
            store.Save("Aのルック", []);
        });
        using var dir = new TempDir();
        Import(app, dir, "a.png", Small(200, 30, 30));
        var m = Module(app);
        var expected = new List<string> { "ルックなし" };
        expected.AddRange(LookCatalogAc13Tests.SpecOrder.Select(s => s.Name));
        expected.Add("Aのルック");
        expected.Add("わたしのルック");
        app.Ui(() =>
        {
            var tiles = m.Gallery.Tiles.ToList();
            Check.SequenceEqual(expected, tiles.Select(t => t.DisplayName).ToList(), "タイルの並び");
            Check.SequenceEqual(expected, tiles.Select(AutomationProperties.GetName).ToList(), "UIA の Name");
            Check.Equal(2, m.Gallery.Columns, "既定の幅 312 では 2 列");
            Check.True(tiles.All(t => Math.Abs(t.ActualWidth - 132) < 0.5), "タイルの幅 132");
        });
        // UIA で名前が見える(操作できる部品の名前)
        var root = Uia.Root(app.Hwnd);
        Check.True(Uia.TryFind(root, "ビビッド") is not null, "UIA でタイルが見えない");
    }

    /// <summary>WARP で起動して 1 枚取り込み、300 ms 進めて作り直しを終えるまで待つ。</summary>
    private static (TestApp App, TempDir Dir, LooksModule M) StartWarp(CompositeImg img)
    {
        var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var dir = new TempDir();
        Import(app, dir, "a.png", img);
        var m = Module(app);
        Rebuild(app, m);
        return (app, dir, m);
    }

    private static void Rebuild(TestApp app, LooksModule m)
    {
        app.Ui(() => app.Clock.Advance(LooksModule.RebuildDelay));
        Check.True(UiHost.WaitUntil(() => !m.IsBuilding && m.Gallery.Tiles.All(t => t.Thumbnail is not null), Wait), "サムネイルが揃わない");
    }

    /// <summary>適用してからエンジンで描いた、タイルと同じ設定の 192 px(Pbgra32)。適用は Undo で戻す。</summary>
    private static byte[] Expected(TestApp app, LookTile tile, Target target)
    {
        var session = app.Shell.Session;
        var renderer = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var doc = app.Ui(() =>
        {
            var applied = session.ApplyLook(target, tile.Effects).Changed;
            var d = session.Document!;
            if (applied) session.Undo();
            return d;
        });
        SessionDocument single;
        if (target.IsAll)
        {
            single = doc with { All = doc.All with { LookStrength = 100 } };
        }
        else
        {
            var one = SingleLayerView.Of(doc, target.LayerIndex);
            single = one with { Layers = [one.Layers[0] with { LookStrength = 100 }] };
        }
        var lout = Math.Min(192, renderer.ThumbnailLongSide(single));
        return renderer.RenderAsync(single, lout, DocumentPixelFormat.Pbgra32).GetAwaiter().GetResult().Pixels;
    }

    [Test(Slow = true)]
    public void AC26_1_レイヤーのタイルのサムネイルはエンジンの192pxとバイト列が一致する()
    {
        var (app, dir, m) = StartWarp(LookTestImages.Gradient());
        using (app)
        using (dir)
        {
            var tiles = app.Ui(() => m.Gallery.Tiles.ToList());
            foreach (var t in tiles)
            {
                var th = t.Thumbnail!;
                Check.Equal(192, Math.Max(th.Width, th.Height), t.DisplayName + " の長辺");
                Check.True(th.Pixels.AsSpan().SequenceEqual(Expected(app, t, Target.OfLayer(0))), t.DisplayName + " のサムネイルがエンジンの結果と違う");
            }
            Check.True(app.Ui(() => tiles.All(t => t.ThumbnailSource is not null)), "画像が出ていない");
        }
    }

    [Test(Slow = true)]
    public void AC26_1_全体と小さいレイヤーのサムネイルもエンジンの結果と一致する()
    {
        var (app, dir, m) = StartWarp(LookTestImages.Gradient());
        using (app)
        using (dir)
        {
            // レイヤー A(小さい画像。元より大きくしない)に形ステージ(flip)を足し、配置と不透明度とブレンドを変える
            Import(app, dir, "b.png", Tiny(40, 180, 90));
            app.Ui(() =>
            {
                var s = app.Shell.Session;
                s.AddEffect(Target.OfLayer(1), "flip");
                s.SetEffectParam(Target.OfLayer(1), 0, "h", new BoolValue(true));
                s.SetLayerPlacement(1, 80, -40, 60);
                s.SetLayerOpacity(1, 70);
                s.SetLayerBlend(1, BlendMode.Screen);
            });
            Rebuild(app, m);
            foreach (var key in new[] { "none", "vivid", "film", "sticker" })
            {
                var t = app.Ui(() => m.Gallery.Tiles.Single(x => x.Key == key));
                Check.Equal(120, t.Thumbnail!.Width, key + " の幅(元の 120 より大きくしない)");
                Check.True(t.Thumbnail.Pixels.AsSpan().SequenceEqual(Expected(app, t, Target.OfLayer(1))), key + " のレイヤー A のサムネイル");
            }
            app.Ui(() => app.Shell.Session.Select(Target.All));
            Rebuild(app, m);
            foreach (var key in new[] { "none", "vivid", "noir", "comic" })
            {
                var t = app.Ui(() => m.Gallery.Tiles.Single(x => x.Key == key));
                Check.True(t.Thumbnail!.Pixels.AsSpan().SequenceEqual(Expected(app, t, Target.All)), key + " の全体のサムネイル");
            }
        }
    }

    [Test(Slow = true)]
    public void AC26_1_作り直しは最後の変更から300ms後に1回だけで見えているタイルが先で作成中は無地()
    {
        var (app, dir, m) = StartWarp(Small(200, 30, 30));
        using (app)
        using (dir)
        {
            var count = m.RebuildCount;
            app.Ui(() =>
            {
                var s = app.Shell.Session;
                for (var i = 0; i < 5; i++)
                {
                    s.SetLayerOpacity(0, 90 - i * 10);
                    app.Clock.Advance(TimeSpan.FromMilliseconds(100));
                }
                Check.Equal(count, m.RebuildCount, "100 ms 間隔の 5 回の変更の途中では作り直さない");
                Check.True(m.IsRebuildPending, "作り直しを待っていない");
                app.Clock.Advance(TimeSpan.FromMilliseconds(199));
                Check.Equal(count, m.RebuildCount, "最後の変更から 299 ms では作り直さない");
                app.Clock.Advance(TimeSpan.FromMilliseconds(1));
                Check.Equal(count + 1, m.RebuildCount, "最後の変更から 300 ms で 1 回");
                // 作成中のタイルは無地(画像なし)
                Check.True(m.Gallery.Tiles.All(t => t.Thumbnail is null && t.ThumbnailSource is null), "作り始めに画像が残っている");
                app.Clock.Advance(TimeSpan.FromSeconds(2));
                Check.Equal(count + 1, m.RebuildCount, "1 回だけ");
            });
            Check.True(UiHost.WaitUntil(() => !m.IsBuilding, Wait), "作り終えない");

            // ルックの適用・強さ・ルックの行の変更では作り直さない
            app.Ui(() =>
            {
                var s = app.Shell.Session;
                s.ApplyLook(s.Selected, m.BuiltIns[0].Effects);
                s.SetLookStrength(s.Selected, 40);
                Check.True(!m.IsRebuildPending, "ルックの適用と強さで作り直しを待っている");
            });

            // いちばん下までスクロールしてから作り直すと、下の(見えている)タイルが先
            app.Ui(() =>
            {
                m.Gallery.Scroll.ScrollToEnd();
                m.Gallery.Scroll.UpdateLayout();
                app.Shell.Session.Select(Target.All);
                app.Clock.Advance(LooksModule.RebuildDelay);
                var tiles = m.Gallery.Tiles.ToList();
                var view = new Rect(0, 0, m.Gallery.Scroll.ViewportWidth, m.Gallery.Scroll.ViewportHeight);
                var visible = tiles.Where(t => t.TransformToAncestor(m.Gallery.Scroll).TransformBounds(new Rect(t.RenderSize)).IntersectsWith(view))
                    .Select(t => t.Key).ToList();
                Check.True(visible.Count > 0 && visible.Count < tiles.Count, $"見えているタイルの数 {visible.Count}");
                Check.True(!visible.Contains("none"), "いちばん下までスクロールしても先頭が見えている");
                Check.SequenceEqual(visible, m.LastPlan.Take(visible.Count).ToList(), "見えているタイルから作る");
                Check.Equal(tiles.Count, m.LastPlan.Count, "全部のタイルを作る");
            });
            Check.True(UiHost.WaitUntil(() => !m.IsBuilding, Wait), "作り終えない");
            Check.SequenceEqual(m.LastPlan, m.LastBuildOrder, "決めた順に作った");
        }
    }

    [Test]
    public void AC26_2_タイルのEnterで選んでいる対象に適用しUndoで戻る()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        Import(app, dir, "a.png", Small(200, 30, 30));
        Import(app, dir, "b.png", Small(30, 200, 30));
        var m = Module(app);
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.Equal(Target.OfLayer(1), s.Selected, "最後に取り込んだレイヤーを選んでいる");
            var before = s.Json;
            var tile = m.Gallery.Tiles.Single(t => t.Key == "neon");
            Keyboard.Focus(tile);
            Check.True(UiInput.Key(tile, Key.Enter), "Enter を処理しない");
            var stack = s.Document!.Layers[1].Stack;
            Check.SequenceEqual(m.BuiltIns.Single(l => l.Key == "neon").Effects.Select(e => e.Id).ToList(), stack.Select(e => e.Id).ToList(), "neon の行");
            Check.True(stack.All(e => e.Origin == EffectOrigin.Look), "origin=look");
            Check.Equal(0, s.Document!.Layers[0].Stack.Count, "ほかのレイヤーは変わらない");
            Check.True(ReferenceEquals(tile, m.Gallery.List.SelectedItem), "適用したタイルを選択中にする");
            // ルックなし
            var none = m.Gallery.Tiles.First();
            Keyboard.Focus(none);
            UiInput.Key(none, Key.Enter);
            Check.Equal(0, s.Document!.Layers[1].Stack.Count, "ルックなしで取り除く");
            s.Undo();
            s.Undo();
            Check.Equal(before, s.Json, "Undo で適用前");
        });
    }

    [Test]
    public void AC26_3_強さのスライダーは対象ごとの値を出し動かすと対象の強さが変わる()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        Import(app, dir, "a.png", Small(200, 30, 30));
        Import(app, dir, "b.png", Small(30, 200, 30));
        var m = Module(app);
        var root = Uia.Root(app.Hwnd);
        var slider = Uia.FindById(root, "LookStrength");
        Check.Equal("ルックの強さ", slider.Current.Name, "スライダーの名前");
        Uia.SetValue(slider, 50);
        UiHost.Idle();
        app.Ui(() =>
        {
            var s = app.Shell.Session;
            Check.Equal(50, s.LookStrengthOf(Target.OfLayer(1)), "レイヤー A の強さ");
            Check.Equal(100, s.LookStrengthOf(Target.OfLayer(0)), "土台の強さ");
            Check.Equal(100, s.LookStrengthOf(Target.All), "全体の強さ");
            Check.Equal("50%", m.Gallery.StrengthValue.Text, "値の表示");
            s.Select(Target.All);
        });
        UiHost.Idle();
        app.Ui(() => Check.Equal(100.0, m.Gallery.Strength.Value, "全体を選ぶと全体の値"));
        app.Ui(() => app.Shell.Session.Select(Target.OfLayer(1)));
        UiHost.Idle();
        app.Ui(() => Check.Equal(50.0, m.Gallery.Strength.Value, "レイヤー A に戻すと 50"));
    }

    [Test]
    public void AC26_4_名前を付けて保存し同名は上書きの確認を出しキャンセルではファイルが変わらない()
    {
        using var app = TestApp.Start();
        using var dir = new TempDir();
        Import(app, dir, "a.png", Small(200, 30, 30));
        var m = Module(app);
        app.Ui(() => app.Shell.Session.ApplyLook(Target.OfLayer(0), m.BuiltIns.Single(l => l.Key == "vivid").Effects));
        var root = Uia.Root(app.Hwnd);

        // 1 回目の保存
        Uia.Invoke(Uia.Find(root, "ルックを保存"));
        Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Dialogs.Top is NameInputDialog), Wait), "名前の入力が出ない");
        app.Ui(() =>
        {
            var d = (NameInputDialog)app.Shell.Dialogs.Top!;
            Check.Equal("名前を付けて保存", d.Title.Text, "見出し");
            Check.Equal("保存", (string)d.Confirm.Content, "決定のボタン");
            Check.True(d.Confirm.IsDefault, "決定は既定のボタン");
            Check.True(!d.Confirm.IsEnabled, "空の間は保存のボタンが無効");
            d.Input.Text = "   ";
            Check.True(!d.Confirm.IsEnabled, "空白だけの間も無効");
            d.Input.Text = new string('あ', 45) + "\n";
            Check.Equal(40, d.Input.Text.Length, "入力欄は 40 文字で止まる");
            d.Input.Text = "わたしの\r\nルック";
            Check.Equal("わたしのルック", d.Input.Text, "改行を取り除く");
            Check.True(d.Confirm.IsEnabled, "名前があれば有効");
            d.Confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Check.True(UiHost.WaitUntil(() => app.Ui(() => !app.Shell.Dialogs.IsOpen && m.Store.Looks.Count == 1), Wait), "保存されない");
        var path = app.Ui(() => m.Store.Looks[0].Path);
        Check.Equal(Path.Combine(app.Paths.LooksDir, "look-" + LookNames.Hash8("わたしのルック") + ".json"), path, "保存先");
        var bytes = File.ReadAllBytes(path);
        app.Ui(() => Check.Equal("わたしのルック", m.Gallery.Tiles.Last().DisplayName, "ユーザーのルックのタイルが最後に足される"));

        // 同名: 上書きの確認でキャンセル → ファイルが変わらない
        app.Ui(() => app.Shell.Session.ApplyLook(Target.OfLayer(0), m.BuiltIns.Single(l => l.Key == "mono").Effects));
        void SaveAs(string name)
        {
            Uia.Invoke(Uia.Find(root, "ルックを保存"));
            Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Dialogs.Top is NameInputDialog), Wait), "名前の入力が出ない");
            app.Ui(() =>
            {
                var d = (NameInputDialog)app.Shell.Dialogs.Top!;
                d.Input.Text = name;
                d.Confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            });
            Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Dialogs.Top is OverwriteConfirmDialog), Wait), "上書きの確認が出ない");
        }
        SaveAs(" わたしのルック ");
        app.Ui(() =>
        {
            var c = (OverwriteConfirmDialog)app.Shell.Dialogs.Top!;
            Check.Equal("同じ名前のものがあります。上書きしますか", c.Message.Text, "name.overwrite の文言");
            Check.Equal("上書き", (string)c.Overwrite.Content, "上書きのボタン");
            Check.Equal("キャンセル", (string)c.Cancel.Content, "キャンセルのボタン");
            Check.True(c.Cancel.IsDefault && !c.Overwrite.IsDefault, "既定はキャンセル");
        });
        UiHost.Idle();
        app.Ui(() => Check.True(FocusCheck.IsFocused(((OverwriteConfirmDialog)app.Shell.Dialogs.Top!).Cancel), "最初のフォーカスはキャンセル"));
        app.Ui(() =>
        {
            // Enter は既定のボタン(キャンセル)
            var c = (OverwriteConfirmDialog)app.Shell.Dialogs.Top!;
            c.Cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Check.True(UiHost.WaitUntil(() => app.Ui(() => !app.Shell.Dialogs.IsOpen), Wait), "閉じない");
        Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "キャンセルでファイルが変わった");

        // 上書きを選ぶと中身が置き換わる
        SaveAs("わたしのルック");
        app.Ui(() => ((OverwriteConfirmDialog)app.Shell.Dialogs.Top!).Overwrite.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
        Check.True(UiHost.WaitUntil(() => app.Ui(() => !app.Shell.Dialogs.IsOpen), Wait), "閉じない");
        app.Ui(() =>
        {
            Check.Equal(1, m.Store.Looks.Count, "同名は 1 つ");
            Check.SequenceEqual(["mono", "contrast"], m.Store.Looks[0].Effects.Select(e => e.Id).ToList(), "置き換わった中身");
        });
        Check.True(!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "上書きでファイルが変わらない");

        // 保存したルックを別の対象に適用すると origin=look で入る
        app.Ui(() =>
        {
            var tile = m.Gallery.Tiles.Last();
            app.Shell.Session.Select(Target.All);
            Keyboard.Focus(tile);
            UiInput.Key(tile, Key.Enter);
            var all = app.Shell.Session.Document!.All.Stack;
            Check.SequenceEqual(["mono", "contrast"], all.Select(e => e.Id).ToList(), "全体に適用");
            Check.True(all.All(e => e.Origin == EffectOrigin.Look), "origin=look");
        });
    }

    [Test]
    public void AC26_4_コンテキストメニューで名前の変更と削除ができる()
    {
        using var app = TestApp.Start(prepare: p =>
        {
            var store = new UserLookStore(p.LooksDir, new DictionaryEffectCatalog(Array.Empty<EffectDef>()));
            store.Save("いつもの", []);
            store.Save("ほかの", []);
        });
        using var dir = new TempDir();
        Import(app, dir, "a.png", Small(200, 30, 30));
        var m = Module(app);
        LookTile TileNamed(string n) => m.Gallery.Tiles.Single(t => t.DisplayName == n);
        app.Ui(() =>
        {
            Check.True(m.Gallery.Tiles.First().ContextMenu is null && m.Gallery.Tiles.Single(t => t.Key == "vivid").ContextMenu is null, "組み込みにはメニューが無い");
            var tile = TileNamed("いつもの");
            // メニューは開かない(ポップアップは実際の画面に出る。INV-18)。Shift+F10 の対応は Shortcuts.Resolve の表のテストが調べる
            var items = tile.ContextMenu!.Items.OfType<MenuItem>().ToList();
            Check.SequenceEqual(["名前を変更", "削除"], items.Select(i => (string)i.Header).ToList(), "メニューの項目");
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        });
        Check.True(UiHost.WaitUntil(() => app.Ui(() => app.Shell.Dialogs.Top is NameInputDialog), Wait), "名前の入力が出ない");
        app.Ui(() =>
        {
            var d = (NameInputDialog)app.Shell.Dialogs.Top!;
            Check.Equal("名前を変更", d.Title.Text, "見出し");
            Check.Equal("変更", (string)d.Confirm.Content, "決定のボタン");
            Check.Equal("いつもの", d.Input.Text, "今の名前が入っている");
            d.Input.Text = "新しい名前";
            d.Confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Check.True(UiHost.WaitUntil(() => app.Ui(() => !app.Shell.Dialogs.IsOpen && m.Store.Find("新しい名前") is not null), Wait), "名前が変わらない");
        Check.True(File.Exists(Path.Combine(app.Paths.LooksDir, "look-" + LookNames.Hash8("新しい名前") + ".json")), "新しい名前のファイル");
        Check.True(!File.Exists(Path.Combine(app.Paths.LooksDir, "look-" + LookNames.Hash8("いつもの") + ".json")), "古いファイルが残っている");
        app.Ui(() =>
        {
            var tile = TileNamed("ほかの");
            tile.ContextMenu!.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check.True(!app.Shell.Dialogs.IsOpen, "削除に確認は出さない");
            Check.True(m.Store.Find("ほかの") is null, "削除されない");
            Check.True(m.Gallery.Tiles.All(t => t.DisplayName != "ほかの"), "タイルが残っている");
        });
    }
}
