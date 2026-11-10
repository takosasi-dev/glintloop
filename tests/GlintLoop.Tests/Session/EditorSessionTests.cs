// EditorSession の編集の操作の検査。取り込み(FR-42・AC-11 (4))、レイヤーの追加・削除・複製・並べ替え(FR-45・FR-52・FR-53)、
// レイヤーの設定(FR-47〜FR-49・FR-54)、スタックの操作(FR-22〜FR-32)、全体の形ステージの拒否(AC-17 (6))、
// 未渡しと置き換えの確認(FR-13・AC-11 (6))を確かめる。画面と GPU は使わない。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Session.SessionTestKit;

namespace GlintLoop.Tests.Session;

public sealed class EditorSessionTests
{
    // ------------------------------------------------------------ 取り込み

    [Test]
    public void FR42_最初の取り込みは新しい文書で土台になり履歴が空()
    {
        var s = NewSession(seed: 1234);
        var r = s.AddImportedImages([Image(1, 640, 480, name: "写真")]);
        Check.Equal(1, r.AddedCount, "足した数");
        Check.True(r.StartedNewDocument, "新しい文書になっていない");
        Check.Equal(null, r.Notice, "1 枚だけの新しい文書では layer.added を出さない");
        var d = s.Document!;
        Check.Equal(1, d.Layers.Count, "枚数");
        Check.Equal("写真", d.Base.Name, "名前");
        Check.Equal(1234, d.Seed, "seed");
        Check.Equal(Target.OfLayer(0), d.Selected, "選択");
        Check.True(!s.CanUndo, "履歴が空でない");
        Check.True(!s.IsUndelivered, "新しい文書が未渡し");
        Check.Equal(1, d.Assets.Count, "assets");
    }

    [Test]
    public void FR42_scaleの式は合成に収まる大きさ()
    {
        Check.Equal(100.0, DocumentRules.ImportScale(400, 300, 400, 300), "同じ大きさ");
        Check.Equal(100.0, DocumentRules.ImportScale(400, 300, 20, 10), "小さい");
        Check.Equal(30.0, DocumentRules.ImportScale(400, 300, 1000, 1000), "r=0.3");
        Check.Equal(33.3, DocumentRules.ImportScale(100, 100, 300, 300), "r=1/3 は floor で 33.3");
        Check.Equal(5.0, DocumentRules.ImportScale(100, 100, 10000, 100), "下限 5");
        Check.Equal(66.6, DocumentRules.ImportScale(200, 1000, 300, 300), "r=2/3");

        var s = NewSession();
        s.AddImportedImages([Image(1, 400, 300), Image(2, 1000, 1000), Image(3, 20, 10)]);
        var d = s.Document!;
        Check.SequenceEqual([100.0, 30.0, 100.0], d.Layers.Select(l => l.Place.Scale).ToList(), "複数で新しい文書");
        Check.Equal(Target.OfLayer(2), d.Selected, "最後に足したもの");
        s.AddEffect(Target.OfLayer(0), "crop");
        s.SetEffectParam(Target.OfLayer(0), 0, "rect", new RectValue(250, 0, 250, 0));
        s.AddImportedImages([Image(4, 400, 300)]);
        Check.Equal(50.0, s.Document!.Layers[3].Place.Scale, "切り抜いた合成(200×300)に収める");
    }

    [Test]
    public void FR42_名前は40文字に切り空は画像()
    {
        var s = NewSession();
        s.AddImportedImages([Image(1, 10, 10, name: new string('x', 45)), Image(2, 10, 10, name: ""), Image(3, 10, 10, name: DocumentRules.PasteName)]);
        var names = s.Document!.Layers.Select(l => l.Name).ToList();
        Check.SequenceEqual([new string('x', 40), "画像", "貼り付け"], names, "名前");
        Check.Equal("photo.v2", DocumentRules.NameFromFileName("photo.v2.png"), "拡張子を除く");
        Check.Equal("画像", DocumentRules.NameFromFileName(".png"), "空になる");
    }

    [Test]
    public void AC11_4_複数の取り込みはCtrlZ1回で全部消える()
    {
        var s = WithBase();
        var before = s.Json;
        var r = s.AddImportedImages([Image(2, 10, 10), Image(3, 20, 20), Image(4, 30, 30)]);
        Check.Equal(3, r.AddedCount, "足した数");
        Check.Equal(NoticeKeys.LayerAdded, r.Notice?.Key, "通知");
        Check.Equal(4, s.Document!.Layers.Count, "枚数");
        Check.Equal(Target.OfLayer(3), s.Selected, "最後に足したもの");
        Check.Equal(1, s.UndoCount, "履歴の件数");
        Check.True(s.Undo(), "Undo できない");
        Check.Equal(before, s.Json, "Undo のあとの JSON");
        Check.Equal(1, s.Document!.Layers.Count, "枚数");
        Check.True(s.Redo(), "Redo できない");
        Check.Equal(4, s.Document!.Layers.Count, "Redo の枚数");
    }

    [Test]
    public void FR57_E12_32枚を超える取り込みとレイヤーは足さない()
    {
        var s = NewSession();
        var r = s.AddImportedImages(Enumerable.Range(1, 40).Select(i => Image(i, 10, 10)).ToList());
        Check.Equal(32, r.AddedCount, "新しい文書は 32 枚まで");
        var json = s.Json;
        var full = s.AddImportedImages([Image(99, 10, 10)]);
        Check.Equal(0, full.AddedCount, "33 枚目");
        Check.Equal(NoticeKeys.LayerFull, full.Notice?.Key, "通知");
        Check.Equal(NoticeKeys.LayerFull, s.AddEmptyLayer().Notice?.Key, "空のレイヤー");
        Check.Equal(NoticeKeys.LayerFull, s.DuplicateLayer(0).Notice?.Key, "複製");
        Check.Equal(json, s.Json, "文書が変わった");
    }

    [Test]
    public void FR57_取り込みの前の判定は枚数と合計を順に見る()
    {
        var s = WithBase();
        var budget = s.BeginImportBudget();
        Check.Equal(1, budget.Layers, "いまの枚数");
        Check.Equal(ImportVerdict.Ok, budget.TryAdmit(Sha(1), 1000, 10000), "同じ画像");
        Check.Equal(1000L, budget.Bytes, "同じ画像は合計を増やさない");
        Check.Equal(ImportVerdict.OverBudget, budget.TryAdmit(Sha(2), 299_999_001, 1), "バイト数の合計");
        Check.Equal(ImportVerdict.Ok, budget.TryAdmit(Sha(2), 299_999_000, 1), "ちょうど 300,000,000");
        Check.Equal(ImportVerdict.OverBudget, budget.TryAdmit(Sha(3), 1, 100_000_000), "画素数の合計");
        for (var i = 0; i < 29; i++) Check.Equal(ImportVerdict.Ok, budget.TryAdmit(Sha(1), 1000, 10000), "32 枚まで");
        Check.Equal(ImportVerdict.LayerFull, budget.TryAdmit(Sha(4), 1, 1), "33 枚目は合計より先に枚数");
    }

    // ------------------------------------------------------------ 空のキャンバスと空のレイヤー

    [Test]
    public void FR44_空のキャンバスは範囲に直した大きさのemptyのレイヤー1枚()
    {
        var s = NewSession();
        s.NewEmptyCanvas(8, 5000);
        var b = s.Document!.Base;
        Check.Equal(LayerKind.Empty, b.Kind, "種類");
        Check.Equal(new EmptySize(16, 4096), b.Size, "大きさ");
        Check.Equal(ColorText.OpaqueWhite, b.Fill, "塗りの既定");
        Check.Equal("キャンバス", b.Name, "名前");
        Check.True(!s.CanUndo && !s.IsUndelivered, "新しい文書の状態");
        Check.Equal(0, s.Document!.Assets.Count, "assets に入れない");
    }

    [Test]
    public void FR45_空のレイヤーは合成と同じ大きさで透明()
    {
        var s = WithBase(301, 200);
        s.AddEffect(Target.OfLayer(0), "crop");
        s.SetEffectParam(Target.OfLayer(0), 0, "rect", new RectValue(1, 0, 0, 0));
        var r = s.AddEmptyLayer();
        Check.True(r.Changed, "足せない");
        var l = s.Document!.Layers[1];
        Check.Equal(new EmptySize(301, 200), l.Size, "round(300.699)");
        Check.Equal(ColorText.Transparent, l.Fill, "塗り");
        Check.Equal("空のレイヤー", l.Name, "名前");
        Check.Equal(Target.OfLayer(1), s.Selected, "選択");
    }

    // ------------------------------------------------------------ 削除・複製・並べ替え

    [Test]
    public void FR53_複製は直後に入りsaltを振り直し名前にのコピーを付ける()
    {
        var s = WithBase();
        s.AddImportedImages([Image(2, 10, 10, name: new string('n', 38))]);
        s.AddEffect(Target.OfLayer(1), "exposure");
        s.AddEffect(Target.OfLayer(1), "invert");
        s.SetLayerPlacement(1, 3, 4, 50);
        var before = s.Document!;
        var r = s.DuplicateLayer(1);
        Check.True(r.Changed, "複製できない");
        var d = s.Document!;
        Check.Equal(3, d.Layers.Count, "枚数");
        Check.Equal(Target.OfLayer(2), d.Selected, "複製を選ぶ");
        Check.Equal(new string('n', 38) + "のコ", d.Layers[2].Name, "40 文字に切る");
        Check.Equal(before.Layers[1].Place, d.Layers[2].Place, "同じ設定");
        Check.Equal(d.Layers[1].Asset, d.Layers[2].Asset, "同じソース");
        var oldSalts = d.Layers[1].Stack.Select(e => e.Salt).ToList();
        var newSalts = d.Layers[2].Stack.Select(e => e.Salt).ToList();
        Check.SequenceEqual([before.SaltCounter + 1, before.SaltCounter + 2], newSalts, "新しい salt");
        Check.True(!oldSalts.Intersect(newSalts).Any(), "salt が重なった");
        Check.Equal(before.SaltCounter + 2, d.SaltCounter, "saltCounter");
        Check.Equal(before.Assets.Count, d.Assets.Count, "ソースを共有する(assets が増えない)");
        Check.Equal("aのコピー", DocumentRules.CopyName("a"), "短い名前");
    }

    [Test]
    public void FR53_削除は最後の1枚を消せず土台を消すと上が土台になる()
    {
        var s = WithBase();
        Check.True(!s.DeleteLayer(0).Changed, "最後の 1 枚を消せた");
        s.AddImportedImages([Image(2, 10, 10), Image(3, 10, 10), Image(4, 10, 10)]);
        s.SetLayerPlacement(1, 5, 6, 50);
        s.SetLayerBlend(1, BlendMode.Screen);
        s.DeleteLayer(0);
        var d = s.Document!;
        Check.Equal(Sha(2), d.Base.Asset, "新しい土台");
        Check.Equal(Placement.Identity, d.Base.Place, "土台の配置");
        Check.Equal(BlendMode.Normal, d.Base.Blend, "土台のブレンド");
        Check.Equal(Target.OfLayer(0), d.Selected, "削除した位置のレイヤーを選ぶ");
        s.DeleteLayer(2);
        Check.Equal(Target.OfLayer(1), s.Selected, "最前面を消したときは新しい最前面");
        Check.Equal(2, s.Document!.Layers.Count, "枚数");
    }

    [Test]
    public void FR52_並べ替えで番号0に来たレイヤーは配置とブレンドが既定になる()
    {
        var s = WithBase();
        s.AddImportedImages([Image(2, 50, 50)]);
        s.SetLayerPlacement(1, 12.346, -7, 60.06);
        s.SetLayerBlend(1, BlendMode.Multiply);
        Check.Equal(new Placement(12.35, -7, 60.1), s.Document!.Layers[1].Place, "round");
        var undoBefore = s.UndoCount;
        s.MoveLayer(1, 0);
        var d = s.Document!;
        Check.Equal(Sha(2), d.Base.Asset, "新しい土台");
        Check.Equal(Placement.Identity, d.Base.Place, "新しい土台の配置");
        Check.Equal(BlendMode.Normal, d.Base.Blend, "新しい土台のブレンド");
        Check.Equal(Placement.Identity, d.Layers[1].Place, "土台だった行は (0, 0, 100) のまま");
        Check.Equal(Target.OfLayer(0), d.Selected, "動かしたレイヤーを選ぶ");
        Check.Equal(undoBefore + 1, s.UndoCount, "1 件");
        Check.Equal(new SizeD(50, 50), s.CompositeNative(), "合成の大きさが決まり直る");
        Check.True(!s.MoveLayer(1, 1).Changed, "同じ位置");
    }

    // ------------------------------------------------------------ レイヤーの設定

    [Test]
    public void FR48_設定はroundして範囲に直し土台の配置とブレンドは変えない()
    {
        var s = WithBase();
        s.AddImportedImages([Image(2, 10, 10)]);
        s.SetLayerOpacity(1, 49.5);
        Check.Equal(50, s.Document!.Layers[1].Opacity, "opacity");
        s.SetLayerOpacity(1, 150);
        Check.Equal(100, s.Document!.Layers[1].Opacity, "opacity の上限");
        s.SetLayerPlacement(1, 200000, -1.005, 1);
        Check.Equal(new Placement(100000, -1, 5), s.Document!.Layers[1].Place, "配置の範囲");
        var json = s.Json;
        Check.True(!s.SetLayerPlacement(0, 1, 1, 50).Changed, "土台の配置");
        Check.True(!s.SetLayerBlend(0, BlendMode.Add).Changed, "土台のブレンド");
        Check.True(!s.SetLayerName(1, "   ").Changed, "空白の名前");
        Check.True(!s.SetLayerOpacity(1, double.NaN).Changed, "NaN");
        Check.True(!s.SetLayerFill(1, "#FF0000FF").Changed, "image の塗り");
        Check.Equal(json, s.Json, "JSON が変わった");
        s.SetLayerName(1, new string('z', 50));
        Check.Equal(40, s.Document!.Layers[1].Name.Length, "名前の長さ");
        s.SetLayerVisible(0, false);
        Check.True(!s.Document!.Base.Visible, "表示");
        Check.Equal(new SizeD(100, 100), s.CompositeNative(), "土台をオフにしても合成の大きさは変わらない");
        s.SetLookStrength(Target.All, 33.3);
        Check.Equal(33, s.Document!.All.LookStrength, "全体のルックの強さ");
        s.AddEmptyLayer();
        Check.True(s.SetLayerFill(2, "#ff000080").Changed, "empty の塗り");
        Check.Equal("#FF000080", s.Document!.Layers[2].Fill, "塗り");
    }

    // ------------------------------------------------------------ スタック

    [Test]
    public void FR26_追加はステージの末尾で固定の3つは先頭()
    {
        var s = WithBase();
        var t = Target.OfLayer(0);
        s.AddEffect(t, "spin");
        s.AddEffect(t, "outline");
        s.AddEffect(t, "invert");
        s.AddEffect(t, "flip");
        s.AddEffect(t, "exposure");
        var r = s.AddEffect(t, "pad");
        Check.Equal(0, r.Row, "pad の位置");
        s.AddEffect(t, "rotate");
        s.AddEffect(t, "crop");
        var st = s.Document!.Layers[0].Stack;
        Check.SequenceEqual(["rotate", "crop", "pad", "flip", "invert", "exposure", "outline", "spin"], st.Select(e => e.Id).ToList(), "並び");
        Check.True(st.All(e => e.Origin == EffectOrigin.User && e.Enabled && e.Amount == 100), "既定");
        Check.Equal(8, st.Select(e => e.Salt).Distinct().Count(), "salt");
        Check.Equal(new NumberValue(0), st.Single(e => e.Id == "exposure").Get("ev"), "既定値");
        Check.True(!s.AddEffect(t, "rotate").Changed, "回転の 2 個目");
        Check.True(!s.AddEffect(t, "nosuch").Changed, "カタログに無い id");
    }

    [Test]
    public void AC17_6_全体の形ステージに回転切り抜き余白を足す操作は拒否されてJSONが変わらない()
    {
        var s = WithBase();
        var json = s.Json;
        var undo = s.UndoCount;
        foreach (var id in new[] { "rotate", "crop", "pad" })
        {
            var r = s.AddEffect(Target.All, id);
            Check.True(!r.Changed, $"{id} を全体に足せた");
            Check.Equal(json, s.Json, $"{id} で JSON が変わった");
        }
        Check.Equal(undo, s.UndoCount, "履歴に積まれた");
        Check.True(!s.IsUndelivered, "未渡しになった");
        Check.True(s.AddEffect(Target.All, "flip").Changed, "全体の反転は足せる");
    }

    [Test]
    public void FR22_E22_65個目の追加と複製はstack_full()
    {
        var s = WithBase();
        for (var i = 0; i < 64; i++) Check.True(s.AddEffect(Target.All, "invert").Changed, $"{i + 1} 個目");
        var json = s.Json;
        Check.Equal(NoticeKeys.StackFull, s.AddEffect(Target.All, "exposure").Notice?.Key, "追加");
        Check.Equal(NoticeKeys.StackFull, s.DuplicateEffect(Target.All, 0).Notice?.Key, "複製");
        Check.Equal(json, s.Json, "JSON が変わった");
    }

    [Test]
    public void FR23_複製は直後にorigin_userと新しいsaltで入る()
    {
        var s = Load(DocumentJsonTests.SpecExample);
        var t = Target.OfLayer(0);
        var before = s.Document!;
        var r = s.DuplicateEffect(t, 0);
        Check.Equal(1, r.Row, "位置");
        var st = s.Document!.Layers[0].Stack;
        Check.Equal(EffectOrigin.Look, st[0].Origin, "元");
        Check.Equal(EffectOrigin.User, st[1].Origin, "複製");
        Check.Equal(before.SaltCounter + 1, st[1].Salt, "salt");
        Check.Equal(new NumberValue(0.2), st[1].Get("ev"), "同じパラメータ");
        s.AddEffect(t, "crop");
        Check.True(!s.DuplicateEffect(t, 0).Changed, "固定の行の複製");
    }

    [Test]
    public void FR25_並べ替えはステージの中だけで固定の3つは動かない()
    {
        var s = WithBase();
        var t = Target.OfLayer(0);
        foreach (var id in new[] { "rotate", "flip", "edge", "exposure", "invert", "levels" }) s.AddEffect(t, id);
        // rotate, flip, edge, exposure, invert, levels
        Check.True(s.MoveEffect(t, 5, 3).Changed, "ルックの中");
        Check.SequenceEqual(["rotate", "flip", "edge", "levels", "exposure", "invert"], Ids(s, t), "並び");
        var json = s.Json;
        Check.True(!s.MoveEffect(t, 3, 2).Changed, "ルックから形へ");
        Check.True(!s.MoveEffect(t, 1, 0).Changed, "回転の上へ");
        Check.True(!s.MoveEffect(t, 0, 1).Changed, "回転を動かす");
        Check.Equal(json, s.Json, "JSON が変わった");
        Check.True(s.MoveEffect(t, 2, 1).Changed, "形の中");
        Check.SequenceEqual(["rotate", "edge", "flip", "levels", "exposure", "invert"], Ids(s, t), "並び");
    }

    [Test]
    public void FR27_パラメータの変更は範囲の端に直し読めない値は反映しない()
    {
        var s = WithBase();
        var t = Target.OfLayer(0);
        s.AddEffect(t, "exposure");
        s.SetEffectParam(t, 0, "ev", new NumberValue(7));
        Check.Equal(new NumberValue(3), s.Document!.Layers[0].Stack[0].Get("ev"), "上限");
        var json = s.Json;
        Check.True(!s.SetEffectParam(t, 0, "ev", new NumberValue(double.NaN)).Changed, "NaN");
        Check.True(!s.SetEffectParam(t, 0, "ev", new StringValue("1")).Changed, "型違い");
        Check.True(!s.SetEffectParam(t, 0, "zz", new NumberValue(1)).Changed, "未知のキー");
        Check.True(!s.SetEffectAmount(t, 0, double.NaN).Changed, "amount の NaN");
        Check.Equal(json, s.Json, "JSON が変わった");
        s.SetEffectAmount(t, 0, -3);
        Check.Equal(0.0, s.Document!.Layers[0].Stack[0].Amount, "amount の下限");
        s.ResetEffect(t, 0);
        Check.Equal(new NumberValue(0), s.Document!.Layers[0].Stack[0].Get("ev"), "リセット");
        Check.Equal(100.0, s.Document!.Layers[0].Stack[0].Amount, "リセットの amount");
        s.SetEffectEnabled(t, 0, false);
        Check.True(!s.Document!.Layers[0].Stack[0].Enabled, "表示の切り替え");
        s.RemoveEffect(t, 0);
        Check.Equal(0, s.Document!.Layers[0].Stack.Count, "削除");
    }

    [Test]
    public void 表_切り抜きの辺を動かしたときは動かした辺を止めlevelsは動かした側を直す()
    {
        var s = WithBase();
        var t = Target.OfLayer(0);
        s.AddEffect(t, "crop");
        s.SetEffectParam(t, 0, "rect", new RectValue(500, 0, 400, 0));
        s.SetEffectParam(t, 0, "rect", new RectValue(700, 0, 400, 0));
        Check.Equal(new RectValue(580, 0, 400, 0), s.Document!.Layers[0].Stack[0].Get("rect"), "l を動かした");
        s.SetEffectParam(t, 0, "rect", new RectValue(580, 0, 900, 0));
        Check.Equal(new RectValue(580, 0, 400, 0), s.Document!.Layers[0].Stack[0].Get("rect"), "r を動かした");
        s.SetEffectParam(t, 0, "rect", new RectValue(100, 0, 100, 0));
        s.SetEffectParam(t, 0, "rect", new RectValue(900, 0, 900, 0));
        Check.Equal(new RectValue(900, 0, 80, 0), s.Document!.Layers[0].Stack[0].Get("rect"), "両方を動かした");

        s.AddEffect(t, "levels");
        s.SetEffectParam(t, 1, "white", new NumberValue(40));
        s.SetEffectParam(t, 1, "black", new NumberValue(38));
        Check.Equal(new NumberValue(35), s.Document!.Layers[0].Stack[1].Get("black"), "black を動かした");
        s.SetEffectParam(t, 1, "white", new NumberValue(20));
        Check.Equal(new NumberValue(40), s.Document!.Layers[0].Stack[1].Get("white"), "white を動かした(black 35 + 5)");
    }

    [Test]
    public void FR32_すべてリセットはスタックとルックの強さと時間だけを戻す()
    {
        var s = Load(DocumentJsonTests.SpecExample);
        var before = s.Document!;
        s.ResetAll();
        var d = s.Document!;
        Check.True(d.Layers.All(l => l.Stack.Count == 0 && l.LookStrength == 100), "レイヤーのスタック");
        Check.Equal(0, d.All.Stack.Count, "全体のスタック");
        Check.Equal(TimeSettings.Default, d.Time, "時間");
        Check.Equal(before.Output, d.Output, "出力設定");
        Check.Equal(before.Layers[1].Place, d.Layers[1].Place, "配置");
        Check.Equal(before.Layers[1].Blend, d.Layers[1].Blend, "ブレンド");
        Check.Equal(before.Layers[1].Opacity, d.Layers[1].Opacity, "不透明度");
        Check.True(s.Undo(), "Undo できない");
        Check.Equal(DocumentJson.Write(before), s.Json, "Undo で戻る");
    }

    // ------------------------------------------------------------ 未渡しと置き換え

    [Test]
    public void AC11_6_未渡しの判定と確認の要否()
    {
        var s = NewSession();
        var dlg = new FakeConfirm(ReplaceChoice.Cancel);
        Check.Equal(ReplaceResult.Ignored, s.RequestNew(dlg), "文書が無いときの新規");
        s.AddImportedImages([Image(1, 10, 10)]);
        Check.True(!s.NeedsReplaceConfirmation, "始めた直後に確認が要る");
        Check.Equal(ReplaceResult.Replaced, s.RequestNew(dlg), "編集していない新規");
        Check.Equal(0, dlg.Calls, "確認ダイアログが出た");
        Check.True(!s.HasDocument, "ウェルカム画面に戻らない");

        s.AddImportedImages([Image(1, 10, 10)]);
        s.AddImportedImages([Image(2, 10, 10)]);
        Check.True(s.IsUndelivered, "取り込みのあとが未渡しでない");
        s.MarkDelivered();
        Check.True(!s.NeedsReplaceConfirmation, "渡したあと");
        s.Undo();
        Check.True(s.NeedsReplaceConfirmation, "Undo のあと");
        s.MarkDelivered();
        s.Redo();
        Check.True(s.NeedsReplaceConfirmation, "Redo のあと");
        s.MarkDelivered();
        s.Select(Target.All);
        Check.True(!s.IsUndelivered, "選択だけで未渡しになった");
        s.SetLookStrength(Target.All, 50);
        Check.True(s.IsUndelivered, "変更のあと");
        Check.True(!s.AddEffect(Target.All, "rotate").Changed && s.IsUndelivered, "拒否された操作");
        var added = s.AddImportedImages([Image(3, 10, 10)]);
        Check.Equal(1, added.AddedCount, "未渡しでも取り込みは確認せずに足す");
        Check.Equal(0, dlg.Calls, "取り込みで確認ダイアログが出た");
    }

    [Test]
    public void AC11_6_確認の2つのボタンの動き()
    {
        var s = WithBase();
        s.AddEffect(Target.All, "invert");
        var json = s.Json;

        var cancel = new FakeConfirm(ReplaceChoice.Cancel);
        Check.Equal(ReplaceResult.Cancelled, s.RequestNew(cancel), "キャンセル");
        Check.Equal(1, cancel.Calls, "確認の回数");
        Check.SequenceEqual([ReplaceChoice.SaveAndReplace, ReplaceChoice.ReplaceWithoutSaving, ReplaceChoice.Cancel], cancel.Buttons!.ToList(), "ボタンは 2 つ");
        Check.Equal(ReplaceChoice.Cancel, cancel.DefaultButton, "既定のボタン");
        Check.Equal(json, s.Json, "キャンセルで変わった");
        Check.True(s.CanUndo && s.IsUndelivered, "キャンセルで履歴か未渡しが変わった");

        // セッションは保存できないので、保存して置き換えが返ってもここでは置き換えない(画面の経路は ProjectCoordinator)。
        Check.Equal(ReplaceResult.Cancelled, s.RequestNew(new FakeConfirm(ReplaceChoice.SaveAndReplace)), "保存して置き換え");
        Check.True(s.HasDocument, "文書が消えた");

        // ダイアログの表示中の取り込みは無視する(E-20)。
        ImportAddResult? during = null;
        var replace = new FakeConfirm(ReplaceChoice.ReplaceWithoutSaving) { DuringAsk = () => during = s.AddImportedImages([Image(9, 5, 5)]) };
        Check.Equal(ReplaceResult.Replaced, s.RequestNew(replace), "保存せず置き換え");
        Check.Equal(0, during!.AddedCount, "表示中の取り込みが足された");
        Check.True(!s.HasDocument, "ウェルカム画面に戻らない");
        Check.True(!s.CanUndo && !s.IsUndelivered, "履歴か未渡しが残った");
        Check.Equal(0, s.Sources.Count, "ソースが残った");

        // .glint を開く操作の置き換え(Phase 5 で使う)。
        s.AddImportedImages([Image(1, 10, 10)]);
        s.AddEffect(Target.All, "invert");
        var loaded = DocumentJson.Read(DocumentJsonTests.SpecExample, Catalog).Document!;
        var sources = loaded.Assets.Select(a => new SourceInfo(a.Sha256, a.Ext, a.Bytes, 100, 100)).ToList();
        var open = new FakeConfirm(ReplaceChoice.ReplaceWithoutSaving);
        Check.Equal(ReplaceResult.Replaced, s.RequestOpen(loaded, sources, open), "開く");
        Check.Equal(1, open.Calls, "開くときの確認");
        Check.Equal(DocumentJson.Write(loaded), s.Json, "開いた文書");
        Check.Equal(Target.OfLayer(1), s.Selected, "保存した選択");
        Check.True(!s.CanUndo && !s.IsUndelivered, "開いたあとの履歴と未渡し");
    }

    // ------------------------------------------------------------ 道具

    private static List<string> Ids(EditorSession s, Target t) => s.Document!.StackOf(t).Select(e => e.Id).ToList();

    /// <summary>§9.11 の例の文書で始めたセッション(ソースの大きさは 100×100 とする)。</summary>
    internal static EditorSession Load(string json)
    {
        var doc = DocumentJson.Read(json, Catalog).Document!;
        var s = NewSession();
        s.OpenDocument(doc, doc.Assets.Select(a => new SourceInfo(a.Sha256, a.Ext, a.Bytes, 100, 100)).ToList());
        return s;
    }
}
