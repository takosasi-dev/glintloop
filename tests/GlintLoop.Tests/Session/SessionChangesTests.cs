// EditorSession.Changed の検査: 文書を変える操作のあとに 1 回だけ上がり、フラグで何が変わったかを区別できる。
// 変わらなかった操作(EditOutcome.Changed=false・Undo できないときなど)では上がらない。連続する変更の途中と終わり、Undo で連続を閉じたときも 1 回。
// 画面と GPU は使わない。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Session.SessionTestKit;

namespace GlintLoop.Tests.Session;

public sealed class SessionChangesTests
{
    /// <summary>Changed を数える。</summary>
    private sealed class Recorder
    {
        public Recorder(EditorSession s) => s.Changed += List.Add;

        public List<SessionChange> List { get; } = new();

        /// <summary>action のあいだに上がった通知がちょうど 1 回であることを確かめ、そのフラグを返す。</summary>
        public SessionChange Once(Action action, string what)
        {
            List.Clear();
            action();
            Check.Equal(1, List.Count, what + " の通知の回数");
            return List[0];
        }

        /// <summary>action のあいだに通知が上がらないことを確かめる。</summary>
        public void None(Action action, string what)
        {
            List.Clear();
            action();
            Check.Equal(0, List.Count, what + " の通知の回数");
        }

        /// <summary>編集の操作が Changed=true なら 1 回、false なら 0 回であることを確かめ、上がったときのフラグを返す。</summary>
        public SessionChange Edit(Func<EditOutcome> op, string what)
        {
            List.Clear();
            var r = op();
            Check.Equal(r.Changed ? 1 : 0, List.Count, what + $" の通知の回数(Changed={r.Changed})");
            return r.Changed ? List[0] : SessionChange.None;
        }
    }

    private static void Has(SessionChange expected, SessionChange actual, string what) =>
        Check.True((actual & expected) == expected, $"{what}: {expected} が立っていない(実際は {actual})");

    private static void Lacks(SessionChange unexpected, SessionChange actual, string what) =>
        Check.True((actual & unexpected) == 0, $"{what}: {unexpected} が立っている(実際は {actual})");

    private const SessionChange LayerContent = SessionChange.Layers | SessionChange.Placement | SessionChange.Stack | SessionChange.Params;

    [Test]
    public void Changed_新しい文書と閉じるは1回でDocumentとすべての状態が立つ()
    {
        var s = NewSession();
        var rec = new Recorder(s);
        var c = rec.Once(() => s.AddImportedImages([Image(1, 100, 80)]), "最初の取り込み");
        Check.Equal(SessionChange.Document | SessionChange.AllState, c, "最初の取り込みのフラグ");
        c = rec.Once(() => s.NewEmptyCanvas(64, 64), "空のキャンバス");
        Has(SessionChange.Document, c, "空のキャンバス");
        var doc = s.Document!;
        c = rec.Once(() => s.OpenDocument(doc, Array.Empty<SourceInfo>()), "開く");
        Has(SessionChange.Document, c, "開く");
        c = rec.Once(() => s.CloseDocument(), "閉じる");
        Has(SessionChange.Document, c, "閉じる");
        Check.True(!s.HasDocument, "閉じたのに文書がある");
        rec.None(() => s.CloseDocument(), "文書が無いときの閉じる");

        s.AddImportedImages([Image(1, 100, 80)]);
        s.SetLayerName(0, "変えた");
        c = rec.Once(() => s.RequestNew(new FakeConfirm(ReplaceChoice.ReplaceWithoutSaving)), "新規(保存せず置き換え)");
        Has(SessionChange.Document, c, "新規");
        rec.None(() => s.RequestNew(new FakeConfirm(ReplaceChoice.Cancel)), "文書が無いときの新規");
        s.AddImportedImages([Image(1, 100, 80)]);
        s.SetLayerName(0, "変えた");
        rec.None(() => s.RequestNew(new FakeConfirm(ReplaceChoice.Cancel)), "新規のキャンセル");
        c = rec.Once(() => s.RequestOpen(doc, Array.Empty<SourceInfo>(), new FakeConfirm(ReplaceChoice.ReplaceWithoutSaving)), "開く(置き換え)");
        Has(SessionChange.Document, c, "開く(置き換え)");
    }

    [Test]
    public void Changed_レイヤーの操作は1回で種類ごとのフラグが立つ()
    {
        var s = WithBase();
        var rec = new Recorder(s);
        var c = rec.Once(() => s.AddImportedImages([Image(2, 50, 50), Image(3, 40, 40)]), "2 枚目からの取り込み");
        Has(LayerContent | SessionChange.Selection | SessionChange.History | SessionChange.Undelivered, c, "取り込み");
        Lacks(SessionChange.Document | SessionChange.CompositeSize, c, "取り込み");

        c = rec.Edit(() => s.SetLayerOpacity(1, 50), "不透明度");
        Has(SessionChange.Placement | SessionChange.History, c, "不透明度");
        Lacks(SessionChange.Layers | SessionChange.Stack | SessionChange.Params | SessionChange.Selection | SessionChange.Undelivered, c, "不透明度");
        Has(SessionChange.Placement, rec.Edit(() => s.SetLayerBlend(1, BlendMode.Screen), "ブレンド"), "ブレンド");
        Has(SessionChange.Placement, rec.Edit(() => s.SetLayerPlacement(1, 10, 20, 50), "配置"), "配置");
        c = rec.Edit(() => s.SetLayerVisible(1, false), "表示");
        Has(SessionChange.Layers, c, "表示");
        Lacks(SessionChange.Placement, c, "表示");
        Has(SessionChange.Layers, rec.Edit(() => s.SetLayerName(2, "名前"), "名前"), "名前");
        Has(SessionChange.Params, rec.Edit(() => s.SetLookStrength(Target.OfLayer(1), 40), "ルックの強さ"), "ルックの強さ");
        Has(SessionChange.Params, rec.Edit(() => s.SetLookStrength(Target.All, 40), "全体のルックの強さ"), "全体のルックの強さ");
        Has(LayerContent | SessionChange.Selection, rec.Edit(() => s.AddEmptyLayer(), "空のレイヤー"), "空のレイヤー");
        Has(SessionChange.Layers, rec.Edit(() => s.SetLayerFill(3, "#FF000080"), "塗り"), "塗り");
        Has(LayerContent, rec.Edit(() => s.DuplicateLayer(1), "複製"), "複製");
        Has(SessionChange.Layers | SessionChange.Selection, rec.Edit(() => s.MoveLayer(1, 3), "並べ替え"), "並べ替え");
        Has(LayerContent, rec.Edit(() => s.DeleteLayer(2), "削除"), "削除");

        // 選択だけの変更は Selection だけ(履歴に積まず、未渡しにもしない)
        s.MarkDelivered();
        c = rec.Once(() => s.Select(Target.All), "選択");
        Check.Equal(SessionChange.Selection, c, "選択のフラグ");
        Check.True(!s.IsUndelivered, "選択で未渡しになった");
        rec.None(() => s.MarkDelivered(), "選択のあとの渡した(未渡しは変わらない)");
    }

    [Test]
    public void Changed_渡したことの知らせは未渡しが変わるときだけ1回()
    {
        var s = WithBase();
        var rec = new Recorder(s);
        rec.None(() => s.MarkDelivered(), "新しい文書(未渡しでない)で渡した");
        s.SetLayerName(0, "x");
        var c = rec.Once(() => s.MarkDelivered(), "渡した");
        Check.Equal(SessionChange.Undelivered, c, "渡したのフラグ");
        rec.None(() => s.MarkDelivered(), "2 回目の渡した");
    }

    [Test]
    public void Changed_スタックの操作は1回でStackとParamsを区別する()
    {
        var s = WithBase();
        var rec = new Recorder(s);
        var t = Target.OfLayer(0);
        var c = rec.Edit(() => s.AddEffect(t, "exposure"), "追加");
        Has(SessionChange.Stack, c, "追加");
        Lacks(SessionChange.Layers | SessionChange.Placement | SessionChange.CompositeSize, c, "追加");
        c = rec.Edit(() => s.SetEffectParam(t, 0, "ev", new NumberValue(1)), "パラメータ");
        Has(SessionChange.Params, c, "パラメータ");
        Lacks(SessionChange.Stack | SessionChange.Layers, c, "パラメータ");
        Has(SessionChange.Params, rec.Edit(() => s.SetEffectAmount(t, 0, 50), "強さ"), "強さ");
        c = rec.Edit(() => s.SetEffectEnabled(t, 0, false), "無効");
        Has(SessionChange.Stack, c, "無効");
        Lacks(SessionChange.Params, c, "無効");
        Has(SessionChange.Stack, rec.Edit(() => s.DuplicateEffect(t, 0), "複製"), "複製");
        Has(SessionChange.Params, rec.Edit(() => s.ResetEffect(t, 0), "リセット"), "リセット");
        Has(SessionChange.Stack, rec.Edit(() => s.MoveEffect(t, 1, 0), "並べ替え"), "並べ替え");
        Has(SessionChange.Stack, rec.Edit(() => s.RemoveEffect(t, 0), "削除"), "削除");
        Has(SessionChange.Stack, rec.Edit(() => s.AddEffect(Target.All, "invert"), "全体への追加"), "全体への追加");

        // 土台の切り抜きは合成の大きさを変える
        s.AddEffect(t, "crop");
        c = rec.Edit(() => s.SetEffectParam(t, 0, "rect", new RectValue(100, 0, 100, 0)), "切り抜き");
        Has(SessionChange.Params | SessionChange.CompositeSize, c, "切り抜き");
        Has(SessionChange.Stack | SessionChange.Params, rec.Edit(() => s.ResetAll(), "すべてリセット"), "すべてリセット");
    }

    [Test]
    public void Changed_変わらなかった操作では上がらない()
    {
        var s = WithBase();
        s.AddImportedImages([Image(2, 50, 50)]);
        s.AddEffect(Target.OfLayer(0), "exposure");
        var rec = new Recorder(s);
        var t = Target.OfLayer(0);
        rec.None(() => Check.True(!s.Select(s.Selected), "同じ対象の選択が変わった"), "同じ対象の選択");
        rec.Edit(() => s.MoveLayer(1, 1), "同じ位置への並べ替え");
        rec.Edit(() => s.SetLayerBlend(0, BlendMode.Screen), "土台のブレンド");
        rec.Edit(() => s.SetLayerOpacity(1, double.NaN), "NaN の不透明度");
        rec.Edit(() => s.SetLayerOpacity(1, 100), "同じ値の不透明度");
        rec.Edit(() => s.SetLayerName(1, "  "), "空白の名前");
        rec.Edit(() => s.SetEffectParam(t, 0, "ev", new NumberValue(double.NaN)), "NaN のパラメータ");
        rec.Edit(() => s.SetEffectParam(t, 0, "ev", new BoolValue(true)), "型違いのパラメータ");
        rec.Edit(() => s.AddEffect(t, "no-such-effect"), "無いエフェクト");
        rec.Edit(() => s.AddEffect(Target.All, "crop"), "全体の切り抜き");
        rec.Edit(() => s.SetEffectAmount(t, 0, 100), "同じ値の強さ");
        rec.None(() => s.AddImportedImages(Array.Empty<ImportedSource>()), "空の取り込み");
        rec.None(() => Check.True(!s.Redo(), "Redo できた"), "末尾の Redo");
        while (s.CanUndo) s.Undo();
        rec.None(() => Check.True(!s.Undo(), "Undo できた"), "先頭の Undo");
        rec.None(() => Check.Throws<ArgumentOutOfRangeException>(() => s.SetLayerOpacity(9, 50), "範囲外"), "範囲外のレイヤー");

        // 32 枚でいっぱいのときの空のレイヤー(Rejected)
        var full = WithBase();
        full.AddImportedImages(Enumerable.Range(2, DocumentRules.MaxLayers - 1).Select(n => Image(n, 10, 10)).ToList());
        var rf = new Recorder(full);
        var r = rf.Edit(() => full.AddEmptyLayer(), "いっぱいのときの空のレイヤー");
        Check.Equal(SessionChange.None, r, "いっぱいのときのフラグ");
        rf.None(() => full.AddImportedImages([Image(99, 10, 10)]), "いっぱいのときの取り込み");
    }

    [Test]
    public void Changed_UndoとRedoは1回でHistoryと戻った中身が立つ()
    {
        var s = WithBase();
        s.AddImportedImages([Image(2, 50, 50)]);
        s.SetLayerOpacity(1, 30);
        var rec = new Recorder(s);
        var c = rec.Once(() => s.Undo(), "Undo");
        Has(SessionChange.History | SessionChange.Placement, c, "Undo");
        Lacks(SessionChange.Document | SessionChange.Layers, c, "Undo");
        c = rec.Once(() => s.Undo(), "2 回目の Undo");
        Has(SessionChange.History | LayerContent, c, "取り込みの Undo");
        c = rec.Once(() => s.Redo(), "Redo");
        Has(SessionChange.History | LayerContent, c, "Redo");
    }

    [Test]
    public void Changed_連続する変更は途中ごとに1回で終わりに1回()
    {
        var s = WithBase();
        s.AddImportedImages([Image(2, 50, 50)]);
        var rec = new Recorder(s);
        rec.None(() => s.BeginContinuous(), "連続の始め");
        for (var v = 90; v >= 70; v -= 10)
        {
            var c = rec.Edit(() => s.SetLayerOpacity(1, v), $"途中の不透明度 {v}");
            Has(SessionChange.Placement | SessionChange.Continuous, c, "途中");
            Lacks(SessionChange.ContinuousEnd, c, "途中");
        }
        var end = rec.Once(() => s.EndContinuous(), "連続の終わり");
        Check.Equal(SessionChange.ContinuousEnd, end, "残したときの終わりのフラグ");
        rec.None(() => s.EndContinuous(), "閉じたあとの終わり");

        // 何も変えなかった連続は、終わりでも上がらない
        s.BeginContinuous();
        rec.None(() => s.EndContinuous(), "変えなかった連続の終わり");

        // 元に戻った連続は 1 件を捨てるので History も立つ
        var undo = s.UndoCount;
        s.BeginContinuous();
        s.SetLayerOpacity(1, 10);
        s.SetLayerOpacity(1, 70);
        end = rec.Once(() => s.EndContinuous(), "元に戻った連続の終わり");
        Check.Equal(SessionChange.History | SessionChange.ContinuousEnd, end, "元に戻ったときの終わりのフラグ");
        Check.Equal(undo, s.UndoCount, "履歴の件数");

        // 連続の途中の Undo は、連続を閉じることと戻すことを 1 回にまとめる
        s.BeginContinuous();
        s.SetLayerOpacity(1, 20);
        var c2 = rec.Once(() => s.Undo(), "連続の途中の Undo");
        Has(SessionChange.History | SessionChange.Placement | SessionChange.ContinuousEnd, c2, "連続の途中の Undo");
        Lacks(SessionChange.Continuous, c2, "連続の途中の Undo");
    }

    [Test]
    public void Changed_受け手が操作を呼んでも二重に上がらない()
    {
        var s = WithBase();
        var calls = 0;
        var inner = SessionChange.None;
        s.Changed += c =>
        {
            calls++;
            if (calls == 1) s.Select(Target.All);
            else inner = c;
        };
        s.SetLayerName(0, "a");
        Check.Equal(2, calls, "外の操作と、受け手の中の選択の通知");
        Check.Equal(SessionChange.Selection, inner, "受け手の中の選択のフラグ");
    }
}
