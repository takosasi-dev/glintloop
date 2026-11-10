// AC-23(スタックの編集。§6.3 の FR-22〜FR-32)を、画面の外のアプリの上で UIA と、ウィンドウの中へ直接発生させるキーと、座標を引数に取る関数で調べる。
// 文書は特に書かない限り土台 1 枚で、対象は土台。画素を比べる項目((2) の目のトグル・(7) の強さ 0%)は WARP で描く。
// スポイト((10))は AC23_10 と、レイヤーが複数のときの AC-29 の (8) を EyedropperAc29Tests で調べる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlintLoop.App.Stack;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Stack.StackTestKit;

namespace GlintLoop.Tests.Stack;

public sealed class StackAc23Tests
{
    private static (TestApp App, StackModule M) Start(int w = 64, int h = 48, GpuAdapterRequest? gpu = null)
    {
        var app = TestApp.Start(gpu: gpu);
        var m = Module(app);
        Import(app, w, h, Img.Pattern(w, h, alpha: false));
        return (app, m);
    }

    private static SessionDocument Doc(TestApp app) => app.Ui(() => app.Shell.Session.Document!);

    private static IReadOnlyList<StackEntry> StackOf(TestApp app, Target t) => app.Ui(() => app.Shell.Session.Document!.StackOf(t));

    private static void Add(TestApp app, StackModule m, params string[] ids)
    {
        foreach (var id in ids) app.Ui(() => Check.True(m.AddEffect(id).Changed, id + " を足せない"));
        UiHost.Idle();
    }

    /// <summary>キーを押す(修飾キーは偽物に入れる)。フォーカスは focus(null なら今のまま)に置いてから。</summary>
    private static bool Press(TestApp app, Key key, ModifierKeys mods = ModifierKeys.None, Func<UIElement?>? focus = null)
    {
        app.Modifiers.Current = mods;
        try
        {
            return app.Ui(() =>
            {
                if (focus?.Invoke() is { } f) Keyboard.Focus(f);
                return UiInput.Key(app.Shell.Window, key);
            });
        }
        finally
        {
            app.Modifiers.Current = ModifierKeys.None;
            UiHost.Idle();
        }
    }

    private static UIElement Row(StackModule m, int row) => m.StackView.RowItems[row];

    private static AutomationElement? TryFindById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static T? FindById<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T t && AutomationProperties.GetAutomationId(root) == id) return t;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindById<T>(VisualTreeHelper.GetChild(root, i), id) is { } hit) return hit;
        }
        return null;
    }

    private static T Param<T>(TestApp app, StackModule m, string id) where T : DependencyObject =>
        app.Ui(() => FindById<T>(m.ParamsView, id)) ?? throw new CheckFailedException("パラメータ欄に無い: " + id);

    // ------------------------------------------------ (1) 構成

    [Test]
    public void AC23_1_右ペインに4つのステージが形ルック仕上げモーションの順に並ぶ()
    {
        var (app, _) = Start();
        using (app)
        {
            var root = Uia.Root(app.Hwnd);
            var ys = new[] { "shape", "look", "finish", "motion" }.Select(s => Uia.RectDip(Uia.FindById(root, "stack.stage." + s), 1).Y).ToList();
            for (var i = 1; i < ys.Count; i++) Check.True(ys[i] > ys[i - 1], $"ステージの順: {string.Join(", ", ys)}");
            var names = new[] { "shape", "look", "finish", "motion" }.Select(s => Uia.FindById(root, "stack.stage." + s).Current.Name).ToList();
            Check.SequenceEqual(["形", "ルック", "仕上げ", "モーション"], names, "ステージの名前");
        }
    }

    [Test]
    public void AC23_1_64個まで追加でき65個目はstack_fullで複製でも同じ()
    {
        var (app, m) = Start();
        using (app)
        {
            app.Ui(() =>
            {
                for (var i = 0; i < 63; i++) Check.True(app.Shell.Session.AddEffect(Target.OfLayer(0), "exposure").Changed, $"{i + 1} 個目を足せない");
                Check.True(m.AddEffect("exposure").Changed, "64 個目を足せない");
            });
            Check.Equal(64, StackOf(app, Target.OfLayer(0)).Count, "64 個");
            var o = app.Ui(() => m.AddEffect("invert"));
            Check.True(!o.Changed, "65 個目が入った");
            Check.Equal(NoticeKeys.StackFull, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "通知");
            Check.Equal("エフェクトは 64 個までです", app.Ui(() => app.Shell.NoticeBar.Text), "通知の文");
            app.Ui(() => app.Shell.NoticeBar.Close());
            // Ctrl+D の複製でも同じ
            app.Ui(() => m.SelectRow(10));
            Check.True(Press(app, Key.D, ModifierKeys.Control, () => Row(m, 10)), "Ctrl+D が処理されない");
            Check.Equal(64, StackOf(app, Target.OfLayer(0)).Count, "複製のあとも 64 個");
            Check.Equal(NoticeKeys.StackFull, app.Ui(() => app.Shell.NoticeBar.Current?.Key), "複製の通知");
            // 上限は対象ごと: レイヤー B と全体にはそれぞれ 64 個まで
            Import(app, 32, 32, Img.Solid(32, 32, 1, 2, 3), "b");
            var s = app.Shell.Session;
            app.Ui(() =>
            {
                for (var i = 0; i < 64; i++) Check.True(s.AddEffect(Target.OfLayer(1), "invert").Changed, $"レイヤー B の {i + 1} 個目");
                for (var i = 0; i < 64; i++) Check.True(s.AddEffect(Target.All, "invert").Changed, $"全体の {i + 1} 個目");
                Check.True(!s.AddEffect(Target.OfLayer(1), "invert").Changed, "レイヤー B の 65 個目");
                Check.True(!s.AddEffect(Target.All, "invert").Changed, "全体の 65 個目");
            });
        }
    }

    // ------------------------------------------------ (2) 行

    [Test]
    public void AC23_2_目をオフにした結果は取り除いた結果と一致する()
    {
        var (app, m) = Start(48, 32, GpuAdapterRequest.Warp);
        using (app)
        {
            Add(app, m, "invert", "exposure", "vignette");
            app.Ui(() => app.Shell.Session.SetEffectParam(Target.OfLayer(0), 1, "ev", new NumberValue(1.5)));
            var root = Uia.Root(app.Hwnd);
            var eye = Uia.FindById(root, "stack.row.1.visible");
            Check.Equal(ToggleState.On, Uia.ToggleStateOf(eye), "目の初期の状態");
            Uia.Toggle(eye);
            UiHost.Idle();
            var off = Doc(app);
            Check.True(!off.Layers[0].Stack[1].Enabled, "目のトグルで無効にならない");
            var removed = off with { Layers = [off.Layers[0] with { Stack = off.Layers[0].Stack.Where((_, i) => i != 1).ToArray() }] };
            var r = app.Shell.RendererReady!.GetAwaiter().GetResult();
            var a = r.RenderProxyAsync(off, DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            var b = r.RenderProxyAsync(removed, DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            Check.True(a.Pixels.AsSpan().SequenceEqual(b.Pixels), "表示が一致しない");
            var ta = r.RenderThumbnailAsync(off, DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            var tb = r.RenderThumbnailAsync(removed, DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            Check.True(ta.Pixels.AsSpan().SequenceEqual(tb.Pixels), "サムネイルが一致しない");
            // 目がオンのときは違う(検査が働いている)
            var on = r.RenderProxyAsync(off with { Layers = [off.Layers[0] with { Stack = off.Layers[0].Stack.Select(e => e with { Enabled = true }).ToArray() }] },
                DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            Check.True(!on.Pixels.AsSpan().SequenceEqual(b.Pixels), "目がオンでも同じ");
        }
    }

    [Test]
    public void AC23_2_複製は直後に同じパラメータとuserと新しいsaltで入り削除はUndoで戻る()
    {
        var (app, m) = Start();
        using (app)
        {
            Add(app, m, "exposure", "contrast");
            var s = app.Shell.Session;
            app.Ui(() =>
            {
                s.SetEffectParam(Target.OfLayer(0), 0, "ev", new NumberValue(-1.25));
                s.SetEffectAmount(Target.OfLayer(0), 0, 40);
            });
            var before = StackOf(app, Target.OfLayer(0));
            var counter = Doc(app).SaltCounter;
            var root = Uia.Root(app.Hwnd);
            Uia.Invoke(Uia.FindById(root, "stack.row.0.duplicate"));
            UiHost.Idle();
            var after = StackOf(app, Target.OfLayer(0));
            Check.Equal(3, after.Count, "行の数");
            Check.Equal("exposure", after[1].Id, "直後の行");
            Check.Equal("contrast", after[2].Id, "その次");
            Check.SequenceEqual(before[0].Params.ToList(), after[1].Params.ToList(), "同じパラメータ");
            Check.Equal(before[0].Amount, after[1].Amount, "同じ強さ");
            Check.Equal(EffectOrigin.User, after[1].Origin, "origin");
            Check.True(after[1].Salt != before[0].Salt && after[1].Salt > counter, "新しい salt");
            Check.Equal(1, m.SelectedRow?.Row ?? -1, "複製した行が選ばれる");
            // 削除して Undo
            var json = app.Ui(() => s.Json);
            Uia.Invoke(Uia.FindById(root, "stack.row.0.delete"));
            UiHost.Idle();
            Check.Equal(2, StackOf(app, Target.OfLayer(0)).Count, "削除のあと");
            Check.True(Press(app, Key.Z, ModifierKeys.Control), "Ctrl+Z");
            Check.Equal(json, app.Ui(() => s.Json), "Undo で元の位置と値に戻る");
            // Delete のキー(スタックにフォーカス)
            app.Ui(() => m.SelectRow(2));
            Check.True(Press(app, Key.Delete, ModifierKeys.None, () => Row(m, 2)), "Delete が処理されない");
            Check.Equal(2, StackOf(app, Target.OfLayer(0)).Count, "Delete で消えない");
        }
    }

    // ------------------------------------------------ (3) 選択

    [Test]
    public void AC23_3_選べる行は1つでパラメータ欄とハンドルが出て対象を切り替えると外れる()
    {
        var (app, m) = Start();
        using (app)
        {
            Add(app, m, "crop", "exposure", "blur");
            var root = Uia.Root(app.Hwnd);
            Uia.Select(Uia.FindById(root, "stack.row.1"));
            UiHost.Idle();
            Check.Equal(1, m.SelectedRow?.Row ?? -1, "露出を選んだ");
            Check.True(TryFindById(root, "param.ev.slider") is not null, "露出のパラメータが出ない");
            Check.Equal(0, app.Ui(() => m.Handles.Current.Count), "露出にハンドルが出た");
            Check.True(!m.SelectedRowHasHandles, "露出はハンドルを持たない");
            Uia.Select(Uia.FindById(root, "stack.row.0"));
            UiHost.Idle();
            Check.Equal(0, m.SelectedRow?.Row ?? -1, "切り抜きを選んだ");
            var selected = app.Ui(() => m.StackView.RowItems.Count(i => ((ListBoxItem)i).IsSelected));
            Check.Equal(1, selected, "選ばれている行の数");
            Check.Equal(8, app.Ui(() => m.Handles.Current.Count), "切り抜きの 8 つのハンドル");
            Check.True(m.SelectedRowHasHandles, "切り抜きはハンドルを持つ");
            Check.True(TryFindById(root, "param.rect.l") is not null, "切り抜きのパラメータが出ない");
            // 行を選んでいないとき: レイヤーならレイヤーの設定、全体なら案内
            app.Ui(() =>
            {
                m.LayerSettingsFactory = t => new TextBlock { Text = "設定" + t.LayerIndex };
                m.ClearRowSelection();
            });
            UiHost.Idle();
            Check.True(app.Ui(() => FindById<ContentControl>(m.ParamsView, "params.layerSettings") is not null), "レイヤーの設定の場所が無い");
            Check.True(Uia.TryFind(root, "設定0") is not null, "レイヤーの設定が出ない");
            Check.Equal(0, app.Ui(() => m.Handles.Current.Count), "選んでいないのにハンドルが出た");
            app.Ui(() => m.SelectRow(2));
            app.Ui(() => app.Shell.Session.Select(Target.All));
            UiHost.Idle();
            Check.True(m.SelectedRow is null, "対象を切り替えても行の選択が外れない");
            Check.Equal(StackTexts.ParamsEmptyAll, Uia.FindById(root, "params.message").Current.Name, "全体の案内");
            // Esc(スタックにフォーカス)で選択を外す
            app.Ui(() => app.Shell.Session.Select(Target.OfLayer(0)));
            app.Ui(() => m.SelectRow(1));
            Check.True(Press(app, Key.Escape, ModifierKeys.None, () => Row(m, 1)), "Esc が処理されない");
            Check.True(m.SelectedRow is null, "Esc で選択が外れない");
        }
    }

    // ------------------------------------------------ (4) 並べ替え

    [Test]
    public void AC23_4_AltとUpDownで同じステージの中を1つずつ動き端と固定の3つでは動かない()
    {
        var (app, m) = Start();
        using (app)
        {
            Add(app, m, "rotate", "crop", "pad", "flip", "chroma", "exposure", "contrast", "blur");
            string Ids() => string.Join(",", StackOf(app, Target.OfLayer(0)).Select(e => e.Id));
            Check.Equal("rotate,crop,pad,flip,chroma,exposure,contrast,blur", Ids(), "初めの並び");
            void Move(int row, Key key, string expect, string what)
            {
                app.Ui(() => m.SelectRow(row));
                Press(app, key, ModifierKeys.Alt, () => Row(m, row));
                Check.Equal(expect, Ids(), what);
            }
            Move(5, Key.Up, "rotate,crop,pad,flip,chroma,exposure,contrast,blur", "ルックの先頭から上(形へは行かない)");
            Move(5, Key.Down, "rotate,crop,pad,flip,chroma,contrast,exposure,blur", "露出を下へ");
            Check.Equal(6, m.SelectedRow?.Row ?? -1, "動かした行が選ばれたまま");
            Move(6, Key.Down, "rotate,crop,pad,flip,chroma,contrast,exposure,blur", "ルックの末尾から下(仕上げへは行かない)");
            Move(4, Key.Up, "rotate,crop,pad,chroma,flip,contrast,exposure,blur", "色抜きを上へ");
            Move(3, Key.Up, "rotate,crop,pad,chroma,flip,contrast,exposure,blur", "固定の 3 つの上へは行かない");
            Move(1, Key.Down, "rotate,crop,pad,chroma,flip,contrast,exposure,blur", "切り抜きは動かない");
            Move(0, Key.Down, "rotate,crop,pad,chroma,flip,contrast,exposure,blur", "回転は動かない");
            Move(7, Key.Up, "rotate,crop,pad,chroma,flip,contrast,exposure,blur", "仕上げの先頭から上");
            // 行の中のボタンにフォーカスがあっても(スタックの中)動く
            app.Ui(() => m.SelectRow(6));
            Press(app, Key.Up, ModifierKeys.Alt, () => FindById<Button>(m.StackView, "stack.row.6.delete"));
            Check.Equal("rotate,crop,pad,chroma,flip,exposure,contrast,blur", Ids(), "削除ボタンにフォーカス");
            // 全体のスタックの形ステージには、固定の 3 つが無い
            app.Ui(() =>
            {
                foreach (var id in new[] { "rotate", "crop", "pad" }) Check.True(!app.Shell.Session.AddEffect(Target.All, id).Changed, "全体に " + id);
            });
        }
    }

    // ------------------------------------------------ (5) 追加

    // 左ペインのタブの枠は担当 looks のもの(統合で EffectsContent に入れた)。エフェクトのタブを選んでおく
    private static void PlaceTab(TestApp app, StackModule m) => app.Ui(() =>
    {
        var looks = app.Shell.Modules.OfType<GlintLoop.App.Looks.LooksModule>().Single();
        Check.True(ReferenceEquals(looks.LeftPane.EffectsContent, m.EffectsTab), "エフェクトのタブの中身が stack の一覧でない");
        Check.True(looks.SelectTab(GlintLoop.App.Looks.LeftPaneTab.Effects), "エフェクトのタブを選べない");
    });

    [Test]
    public void AC23_5_エフェクトのタブはカタログの全部を並べEnterで末尾に足して選ぶ()
    {
        var (app, m) = Start();
        using (app)
        {
            PlaceTab(app, m);
            UiHost.Idle();
            var catalog = app.Shell.Catalog;
            Check.Equal(catalog.Effects.Count, app.Ui(() => m.EffectsTab.Items.Count), "項目の数");
            Check.SequenceEqual(catalog.Effects.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                app.Ui(() => m.EffectsTab.Items.Select(i => i.Def.Id).OrderBy(x => x, StringComparer.Ordinal).ToList()), "項目の id");
            // ステージごとのグループ(並びはステージの順)
            var stages = app.Ui(() => m.EffectsTab.Items.Select(i => EffectSchema.StageOrder(i.Def.Stage)).ToList());
            for (var i = 1; i < stages.Count; i++) Check.True(stages[i] >= stages[i - 1], "ステージの順に並ばない");
            Add(app, m, "contrast", "blur");
            ListBoxItem ItemOf(string id) => app.Ui(() => m.EffectsTab.Items.Single(x => x.Def.Id == id).Item);
            var item = ItemOf("exposure");
            Check.True(Press(app, Key.Enter, ModifierKeys.None, () => item), "Enter が処理されない");
            var stack = StackOf(app, Target.OfLayer(0));
            Check.Equal("contrast,exposure,blur", string.Join(",", stack.Select(e => e.Id)), "ルックの末尾に入る");
            Check.Equal(EffectOrigin.User, stack[1].Origin, "origin");
            Check.SequenceEqual(EffectSchema.DefaultParams(catalog.Find("exposure")!).ToList(), stack[1].Params.ToList(), "既定値");
            Check.Equal(1, m.SelectedRow?.Row ?? -1, "足した行が選ばれる");
            // UIA の項目でも(クリックの代わりに選択の後の Enter は上で確かめた)、名前で見つかる
            var root = Uia.Root(app.Hwnd);
            Check.True(TryFindById(root, "effects.item.blur") is not null, "UIA で項目が見つからない");
            // 対象が全体: 回転・切り抜き・余白は押せない表示とツールチップ、Enter でも足さない
            app.Ui(() => app.Shell.Session.Select(Target.All));
            UiHost.Idle();
            foreach (var id in new[] { "rotate", "crop", "pad" })
            {
                var it = ItemOf(id);
                Check.True(!app.Ui(() => it.IsEnabled), id + " が押せる");
                Check.Equal(StackTexts.FixedShapeNotForAll, app.Ui(() => it.ToolTip as string), id + " のツールチップ");
                Check.Equal("全体には使えません(合成の大きさは、いちばん下のレイヤーで決まります)", StackTexts.FixedShapeNotForAll, "文言");
                app.Ui(() =>
                {
                    var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(app.Shell.Window)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                    it.RaiseEvent(e);
                });
                Check.Equal(0, StackOf(app, Target.All).Count, id + " が全体に入った");
            }
            Check.True(app.Ui(() => ItemOf("flip").IsEnabled), "反転は全体でも押せる");
            app.Ui(() => app.Shell.Session.Select(Target.OfLayer(0)));
            UiHost.Idle();
            Check.True(app.Ui(() => ItemOf("crop").IsEnabled), "レイヤーでは切り抜きを押せる");
        }
    }

    [Test]
    public void AC23_5_検索は名前とidとキーワードの部分一致で期待の集合と一致する()
    {
        var (app, m) = Start();
        using (app)
        {
            PlaceTab(app, m);
            var defs = app.Shell.Catalog.Effects;
            foreach (var q in new[] { "BLUR", "ぼかし", "ev", "" })
            {
                var expected = defs.Where(d => q.Length == 0 || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Keywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    .Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                app.Ui(() => m.EffectsTab.SearchBox.Text = q);
                var got = app.Ui(() => m.EffectsTab.VisibleIds.OrderBy(x => x, StringComparer.Ordinal).ToList());
                Check.SequenceEqual(expected, got, $"検索語「{q}」");
                Check.True(q.Length == 0 || expected.Count < defs.Count, $"検索語「{q}」で絞れていない");
            }
            // Ctrl+F で検索欄にフォーカス
            Check.True(Press(app, Key.F, ModifierKeys.Control), "Ctrl+F");
            Check.True(app.Ui(() => m.EffectsTab.SearchBox.IsKeyboardFocused), "検索欄にフォーカスが無い");
        }
    }

    // ------------------------------------------------ (6) パラメータ欄

    [Test]
    public void AC23_6_実装済みのすべてのエフェクトのすべてのパラメータが型ごとの部品で作られる()
    {
        var (app, m) = Start();
        using (app)
        {
            var defs = app.Shell.Catalog.Effects;
            var s = app.Shell.Session;
            var count = 0;
            foreach (var def in defs)
            {
                app.Ui(() =>
                {
                    s.ResetAll();
                    Check.True(m.AddEffect(def.Id).Changed, def.Id + " を足せない");
                });
                UiHost.Idle();
                app.Ui(() =>
                {
                    var ps = EffectSchema.AllParams(def);
                    var keys = m.ParamsView.Rows.Select(r => r.Key).Where(k => k != "_amount").ToList();
                    Check.SequenceEqual(ps.Select(p => p.Key).ToList(), keys, def.Id + " のパラメータの並び");
                    foreach (var p in ps)
                    {
                        string[] ids = p.Type switch
                        {
                            ParamType.Float or ParamType.Int or ParamType.Angle => [".slider", ".number", ".reset"],
                            ParamType.Bool => [".toggle", ".reset"],
                            ParamType.Enum => [".choice", ".reset"],
                            ParamType.Color => [".swatch", ".hex", ".eyedropper", ".reset"],
                            ParamType.Point => [".x", ".y", ".reset"],
                            ParamType.Rect => [".l", ".t", ".r", ".b", ".reset"],
                            _ => [".reset"],
                        };
                        foreach (var suffix in ids)
                            Check.True(FindById<DependencyObject>(m.ParamsView, "param." + p.Key + suffix) is not null, $"{def.Id}.{p.Key} に {suffix} が無い");
                        if (p.Type == ParamType.Enum)
                        {
                            var choice = FindById<Selector>(m.ParamsView, "param." + p.Key + ".choice")!;
                            Check.Equal(p.Options!.Count <= 4, choice is ListBox, $"{def.Id}.{p.Key} の切替ボタンと一覧の使い分け");
                        }
                        if (p.Advanced) Check.True(m.ParamsView.AdvancedHost is not null, $"{def.Id}.{p.Key} の「詳細」が無い");
                        count++;
                    }
                    // advanced のパラメータがあるときだけ「詳細」(既定は閉じる)
                    Check.Equal(ps.Any(p => p.Advanced), m.ParamsView.AdvancedToggle is not null, def.Id + " の「詳細」の有無");
                    if (m.ParamsView.AdvancedToggle is { } t) Check.True(t.IsChecked == false && m.ParamsView.AdvancedHost!.Visibility != Visibility.Visible, "「詳細」が開いている");
                });
            }
            Check.True(count > 100, $"調べたパラメータの数 {count}");
        }
    }

    [Test]
    public void AC23_6_visibleWhenで表示が切り替わる()
    {
        var (app, m) = Start();
        using (app)
        {
            Add(app, m, "blur");
            var angle = Param<StackPanel>(app, m, "param.angle");
            Check.Equal(Visibility.Collapsed, app.Ui(() => angle.Visibility), "ガウスのとき角度は出ない");
            var root = Uia.Root(app.Hwnd);
            var choice = Uia.FindById(root, "param.kind.choice");
            var directional = choice.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition)[1];
            Uia.Select(directional);
            UiHost.Idle();
            Check.Equal("directional", ((StringValue)StackOf(app, Target.OfLayer(0))[0].Get("kind")!).Value, "種類が JSON に入る");
            Check.Equal(Visibility.Visible, app.Ui(() => angle.Visibility), "方向のとき角度が出る");
            Check.True(app.Ui(() => app.Shell.Session.Json!).Contains("\"directional\"", StringComparison.Ordinal), "EditorSession の JSON");
        }
    }

    [Test]
    public void AC23_6_数値入力はEnterとフォーカスを失ったときに反映し範囲外は端に読めない入力は戻す()
    {
        var (app, m) = Start();
        using (app)
        {
            Add(app, m, "exposure");
            var box = Param<GlintLoop.App.Controls.NumberBox>(app, m, "param.ev.number");
            double Ev() => ((NumberValue)StackOf(app, Target.OfLayer(0))[0].Get("ev")!).Value;
            void Type(string text, bool enter = true)
            {
                app.Ui(() =>
                {
                    Keyboard.Focus(box);
                    box.Text = text;
                });
                if (enter) Press(app, Key.Enter);
                else app.Ui(() => Keyboard.Focus(FindById<Button>(m.ParamsView, "params.reset")));
                UiHost.Idle();
            }
            Type("1.25");
            Check.Equal(1.25, Ev(), "Enter で反映");
            Type("-0.5", enter: false);
            Check.Equal(-0.5, Ev(), "フォーカスを失って反映");
            Type("9");
            Check.Equal(3.0, Ev(), "最大より大きい値は最大");
            Type("-9");
            Check.Equal(-3.0, Ev(), "最小より小さい値は最小");
            Type("abc");
            Check.Equal(-3.0, Ev(), "abc は反映しない");
            Check.Equal("-3.00", app.Ui(() => box.Text), "abc のあとは直前の値");
            Type("");
            Check.Equal(-3.0, Ev(), "空は反映しない");
            Check.Equal("-3.00", app.Ui(() => box.Text), "空のあとは直前の値");
            // 既定値に戻すボタンとスライダーのダブルクリック
            Uia.Invoke(Uia.FindById(Uia.Root(app.Hwnd), "param.ev.reset"));
            UiHost.Idle();
            Check.Equal(0.0, Ev(), "既定値に戻すボタン");
            Type("2");
            var slider = Param<Slider>(app, m, "param.ev.slider");
            app.Ui(() => slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent }));
            UiHost.Idle();
            Check.Equal(0.0, Ev(), "スライダーのダブルクリック");
            // スライダーの操作が JSON に入る
            app.Ui(() => slider.Value = 1.5);
            UiHost.Idle();
            Check.Equal(1.5, Ev(), "スライダー");
            Check.Equal("1.50", app.Ui(() => box.Text), "数値入力が追う");
        }
    }

    // ------------------------------------------------ (7) 強さ

    [Test]
    public void AC23_7_強さのスライダーはmixとscaleのエフェクトだけが先頭に持つ()
    {
        var (app, m) = Start();
        using (app)
        {
            foreach (var def in app.Shell.Catalog.Effects)
            {
                app.Ui(() =>
                {
                    app.Shell.Session.ResetAll();
                    m.AddEffect(def.Id);
                });
                UiHost.Idle();
                var rows = app.Ui(() => m.ParamsView.Rows.Select(r => r.Key).ToList());
                var has = rows.Count > 0 && rows[0] == "_amount";
                Check.Equal(def.AmountMode != AmountMode.None, has, def.Id + " の強さ");
                Check.Equal(has ? 1 : 0, rows.Count(k => k == "_amount"), def.Id + " の強さの数");
            }
        }
    }

    [Test]
    public void AC23_7_強さ0の出力は入力と1以内で一致する()
    {
        var (app, m) = Start(48, 32, GpuAdapterRequest.Warp);
        using (app)
        {
            var r = app.Shell.RendererReady!.GetAwaiter().GetResult();
            var plain = r.RenderProxyAsync(Doc(app), DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            Add(app, m, "exposure");
            app.Ui(() => app.Shell.Session.SetEffectParam(Target.OfLayer(0), 0, "ev", new NumberValue(2)));
            var box = Param<GlintLoop.App.Controls.NumberBox>(app, m, "param._amount.number");
            app.Ui(() =>
            {
                Keyboard.Focus(box);
                box.Text = "0";
            });
            Press(app, Key.Enter);
            Check.Equal(0.0, StackOf(app, Target.OfLayer(0))[0].Amount, "強さ 0");
            var zero = r.RenderProxyAsync(Doc(app), DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            Check.True(Img.MaxDiff(plain.Pixels, zero.Pixels) <= 1, $"強さ 0 の差 {Img.MaxDiff(plain.Pixels, zero.Pixels)}");
            app.Ui(() => app.Shell.Session.SetEffectAmount(Target.OfLayer(0), 0, 100));
            var full = r.RenderProxyAsync(Doc(app), DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            Check.True(Img.MaxDiff(plain.Pixels, full.Pixels) > 1, "強さ 100 で変わらない");
        }
    }

    // ------------------------------------------------ (8) 元に戻す

    [Test]
    public void AC23_8_CtrlZとCtrlYとCtrlShiftZで戻してやり直す()
    {
        var (app, m) = Start();
        using (app)
        {
            var s = app.Shell.Session;
            var j0 = app.Ui(() => s.Json);
            Add(app, m, "exposure");
            var j1 = app.Ui(() => s.Json);
            Check.True(Press(app, Key.Z, ModifierKeys.Control), "Ctrl+Z");
            Check.Equal(j0, app.Ui(() => s.Json), "Ctrl+Z で 1 つ前");
            Check.True(Press(app, Key.Y, ModifierKeys.Control), "Ctrl+Y");
            Check.Equal(j1, app.Ui(() => s.Json), "Ctrl+Y でやり直す");
            Press(app, Key.Z, ModifierKeys.Control);
            Check.True(Press(app, Key.Z, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+Z");
            Check.Equal(j1, app.Ui(() => s.Json), "Ctrl+Shift+Z でやり直す");
            // 文字入力欄にフォーカスがあるときは履歴が動かない(Undo で行が消えたので選び直す)
            app.Ui(() => m.SelectRow(0));
            var box = Param<GlintLoop.App.Controls.NumberBox>(app, m, "param.ev.number");
            Press(app, Key.Z, ModifierKeys.Control, () => box);
            Check.Equal(j1, app.Ui(() => s.Json), "数値入力の中の Ctrl+Z で履歴が動いた");
        }
    }

    [Test]
    public void AC23_8_履歴は100件で101回目のCtrlZは何も起きない()
    {
        var (app, m) = Start();
        using (app)
        {
            var s = app.Shell.Session;
            Add(app, m, "exposure");
            app.Ui(() =>
            {
                for (var i = 1; i <= 100; i++) s.SetEffectParam(Target.OfLayer(0), 0, "ev", new NumberValue(i % 2 == 0 ? 0.01 * i : -0.01 * i));
            });
            Check.Equal(100, app.Ui(() => s.UndoCount), "101 回の変更のあとの戻れる回数");
            app.Ui(() => Keyboard.Focus(app.Shell.Window.CommandBar.Button(GlintLoop.App.Theme.Icons.Undo)));
            for (var i = 0; i < 100; i++) Check.True(Press(app, Key.Z, ModifierKeys.Control), $"{i + 1} 回目の Ctrl+Z");
            var json = app.Ui(() => s.Json);
            Check.Equal(0, app.Ui(() => s.UndoCount), "100 回戻ったあと");
            Press(app, Key.Z, ModifierKeys.Control);
            Check.Equal(json, app.Ui(() => s.Json), "101 回目の Ctrl+Z で変わった");
        }
    }

    [Test]
    public void AC23_8_スライダーのドラッグは終わりの時点で1件になりズームと行の選択は履歴に入らない()
    {
        var (app, m) = Start();
        using (app)
        {
            var s = app.Shell.Session;
            Add(app, m, "exposure", "contrast");
            var before = app.Ui(() => s.UndoCount);
            var slider = Param<Slider>(app, m, "param._amount.slider");
            app.Ui(() =>
            {
                m.SelectRow(1);
                m.SelectRow(0);
            });
            slider = Param<Slider>(app, m, "param.ev.slider");
            app.Ui(() =>
            {
                slider.ApplyTemplate();
                var thumb = (Thumb)slider.Template.FindName("Thumb", slider);
                thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
                for (var i = 1; i <= 30; i++) slider.Value = i * 0.05;
                thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            });
            UiHost.Idle();
            Check.Equal(before + 1, app.Ui(() => s.UndoCount), "ドラッグ 1 回で 1 件");
            Check.Equal(1.5, ((NumberValue)StackOf(app, Target.OfLayer(0))[0].Get("ev")!).Value, "ドラッグの最後の値");
            // ズーム・表示位置・行の選択は履歴に入らない
            var n = app.Ui(() => s.UndoCount);
            app.Ui(() =>
            {
                app.CanvasTransform.Set(app.CanvasTransform.Scale * 2, new Vector(3, 4));
                m.SelectRow(1);
                m.ClearRowSelection();
            });
            Check.Equal(n, app.Ui(() => s.UndoCount), "ズームと選択で件数が変わった");
            // 新しい文書(空のキャンバス)で空になり、レイヤーを足す取り込みは 1 件を積む
            app.Ui(() =>
            {
                m.SelectRow(0);
                s.NewEmptyCanvas(100, 100);
            });
            UiHost.Idle();
            Check.Equal(0, app.Ui(() => s.UndoCount), "空のキャンバスで履歴が空にならない");
            Check.True(m.SelectedRow is null, "新しい文書で行の選択が残った");
            Import(app, 10, 10, Img.Solid(10, 10, 5, 5, 5), "c");
            Check.Equal(1, app.Ui(() => s.UndoCount), "レイヤーの取り込みで 1 件");
        }
    }

    // ------------------------------------------------ (9) リセット

    [Test]
    public void AC23_9_リセットとすべてリセット()
    {
        var (app, m) = Start();
        using (app)
        {
            var s = app.Shell.Session;
            Add(app, m, "exposure", "blur");
            app.Ui(() =>
            {
                s.SetEffectParam(Target.OfLayer(0), 0, "ev", new NumberValue(2));
                s.SetEffectAmount(Target.OfLayer(0), 0, 30);
                m.SelectRow(0);
            });
            UiHost.Idle();
            var root = Uia.Root(app.Hwnd);
            Uia.Invoke(Uia.FindById(root, "params.reset"));
            UiHost.Idle();
            var e = StackOf(app, Target.OfLayer(0))[0];
            Check.Equal(100.0, e.Amount, "強さが 100 に戻る");
            Check.SequenceEqual(EffectSchema.DefaultParams(StackTestKit.Def("exposure")).ToList(), e.Params.ToList(), "既定値に戻る");
            // すべてリセット: 2 枚目のレイヤーと全体も
            Import(app, 16, 16, Img.Solid(16, 16, 9, 9, 9), "b");
            app.Ui(() =>
            {
                s.AddEffect(Target.OfLayer(1), "invert");
                s.AddEffect(Target.All, "vignette");
                s.SetLookStrength(Target.OfLayer(1), 40);
                s.SetLookStrength(Target.All, 20);
                s.SetLayerPlacement(1, 5, 6, 70);
                s.SetLayerOpacity(1, 50);
                s.SetLayerName(1, "名前");
            });
            var json = app.Ui(() => s.Json);
            var before = Doc(app);
            Uia.Invoke(Uia.FindById(root, "stack.resetAll"));
            UiHost.Idle();
            var after = Doc(app);
            Check.True(after.Layers.All(l => l.Stack.Count == 0 && l.LookStrength == 100), "レイヤーのスタックとルックの強さ");
            Check.True(after.All.Stack.Count == 0 && after.All.LookStrength == 100, "全体");
            Check.Equal(TimeSettings.Default, after.Time, "時間");
            Check.Equal(before.Layers[1].Place, after.Layers[1].Place, "配置は変えない");
            Check.Equal(before.Layers[1].Opacity, after.Layers[1].Opacity, "不透明度は変えない");
            Check.Equal(before.Layers[1].Name, after.Layers[1].Name, "名前は変えない");
            Check.Equal(before.Output, after.Output, "出力設定は変えない");
            Press(app, Key.Z, ModifierKeys.Control);
            Check.Equal(json, app.Ui(() => s.Json), "Undo で元に戻る");
        }
    }

    // ------------------------------------------------ (10) スポイト

    [Test]
    public void AC23_10_スポイトは出力の座標の色のRGBを入れAAは変えない()
    {
        var px = Gradient(1000, 500, 128);
        SetPixel(px, 1000, 10, 10, 0, 255, 0, 255);
        SetPixel(px, 1000, 20, 10, 200, 100, 50, 0);
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var m = Module(app);
        Import(app, 1000, 500, px);
        var s = app.Shell.Session;
        Add(app, m, "chroma");
        app.Ui(() => s.SetEffectParam(Target.OfLayer(0), 0, "key", new StringValue("#11223340")));
        string Key0() => ((StringValue)StackOf(app, Target.OfLayer(0)).Single(e => e.Id == "chroma").Get("key")!).Value;
        int Row(string id) => StackOf(app, Target.OfLayer(0)).ToList().FindIndex(e => e.Id == id);
        bool Pick(double x, double y)
        {
            app.Ui(() =>
            {
                m.SelectRow(Row("chroma"));
                m.Eyedropper.Start(Target.OfLayer(0), StackOf(app, Target.OfLayer(0))[Row("chroma")].Salt, "key");
            });
            var task = app.Ui(() => m.Eyedropper.PickAtAsync(new Point(x, y)));
            var ok = task.GetAwaiter().GetResult();
            UiHost.Idle();
            return ok;
        }
        Check.True(Pick(30, 40), "(30, 40)");
        Check.Equal("#1E288040", Key0(), "(30, 40) の色");
        Check.True(!m.Eyedropper.IsActive, "色を取ったらスポイトは終わる");
        Pick(20, 10);
        Check.Equal("#00000040", Key0(), "α=0 の画素");
        // 切り抜き (250, 0, 250, 0): 出力 500×500 で (0, 0) は元の (250, 0)
        app.Ui(() =>
        {
            s.AddEffect(Target.OfLayer(0), "crop");
            s.SetEffectParam(Target.OfLayer(0), 0, "rect", new RectValue(250, 0, 250, 0));
        });
        Check.Equal(new SizeD(500, 500), app.Ui(() => s.CompositeNative()), "切り抜いた出力");
        Pick(0, 0);
        Check.Equal("#FA008040", Key0(), "切り抜きのあとの (0, 0)");
        // 切り抜きを外して反転(左右)
        app.Ui(() =>
        {
            s.RemoveEffect(Target.OfLayer(0), Row("crop"));
            s.AddEffect(Target.OfLayer(0), "flip");
            s.SetEffectParam(Target.OfLayer(0), Row("flip"), "h", new BoolValue(true));
        });
        Pick(0, 0);
        Check.Equal("#E7008040", Key0(), "反転のあとの (0, 0)");
        app.Ui(() => s.SetEffectEnabled(Target.OfLayer(0), Row("flip"), false));
        Pick(0, 0);
        Check.Equal("#00008040", Key0(), "反転の目をオフ");
        // エフェクトで消える色も取れる
        app.Ui(() => s.SetEffectParam(Target.OfLayer(0), Row("chroma"), "key", new StringValue("#00FF00FF")));
        var r = app.Shell.RendererReady!.GetAwaiter().GetResult();
        var shown = r.RenderProxyAsync(Doc(app), DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
        Check.True(shown.Pixels[(10 * 1000 + 10) * 4 + 3] <= 5, "(10, 10) の出力が透明にならない");
        app.Ui(() => s.SetEffectParam(Target.OfLayer(0), Row("chroma"), "key", new StringValue("#123456FF")));
        Pick(10, 10);
        Check.Equal("#00FF00FF", Key0(), "消える色を取る");
        // 画像の外では変わらず、スポイトは続く。Esc で取り消すと変わらない
        Check.True(!Pick(-5, 10), "画像の外で取った");
        Check.True(m.Eyedropper.IsActive, "画像の外でスポイトが終わった");
        Check.True(!Pick(1000, 10), "画像の右の外で取った");
        Check.Equal("#00FF00FF", Key0(), "画像の外で変わった");
        Check.True(Press(app, Key.Escape), "Esc");
        Check.True(!m.Eyedropper.IsActive, "Esc で取り消せない");
        Check.Equal("#00FF00FF", Key0(), "Esc で変わった");
    }
}
