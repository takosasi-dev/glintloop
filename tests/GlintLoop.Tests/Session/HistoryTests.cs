// EditorSession の履歴(FR-30・D-36)の検査。100 件の上限(AC-23 (8))、選択だけの変更と選び直し(AC-28 (11))、
// 履歴だけが参照するソースのバイト数の上限(AC-28 (11)・Q-23)、連続する変更のまとめ、新しい文書で空になること(E-28)を確かめる。
// ソースのバイト数は ImportedSource の値で与えるだけで、大きな配列は確保しない。
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Session.SessionTestKit;

namespace GlintLoop.Tests.Session;

public sealed class HistoryTests
{
    private static EditorSession WithLimit(long limit) => new(Catalog, new FixedSeedSource(7), null, EditorSession.DefaultMaxUndo, limit);

    // ------------------------------------------------------------ 100 件の上限

    [Test]
    public void FR30_AC23_8_履歴は100件で101回の変更のあと100回戻れて101回目は何もしない()
    {
        Check.Equal(100, EditorSession.DefaultMaxUndo, "既定の件数");
        var s = WithBase();
        var initial = s.Json;
        for (var i = 1; i <= 101; i++) Check.True(s.SetLayerName(0, "n" + i).Changed, "変更 " + i);
        Check.Equal(100, s.UndoCount, "Undo できる件数");
        for (var i = 1; i <= 100; i++) Check.True(s.Undo(), "Undo " + i);
        // 最も古い件(取り込んだ直後)が捨てられ、戻りきった先は 1 回目の変更のあと。
        Check.Equal("n1", s.Document!.Base.Name, "戻りきった先");
        Check.True(s.Json != initial, "最も古い件が残っている");
        var json = s.Json;
        Check.True(!s.CanUndo, "101 回目が有効表示");
        Check.True(!s.Undo(), "101 回目の Undo が動いた");
        Check.Equal(json, s.Json, "101 回目の Undo で変わった");
        for (var i = 1; i <= 100; i++) Check.True(s.Redo(), "Redo " + i);
        Check.Equal("n101", s.Document!.Base.Name, "やり直しきった先");
        Check.True(!s.Redo(), "末尾の Redo が動いた(E-28)");
    }

    [Test]
    public void FR30_E28_履歴の先頭のUndoと末尾のRedoは何もしない()
    {
        var s = NewSession();
        Check.True(!s.Undo() && !s.Redo(), "文書が無いときに動いた");
        s.AddImportedImages([Image(1, 10, 10)]);
        var json = s.Json;
        Check.True(!s.CanUndo && !s.CanRedo, "始めた直後に有効表示");
        Check.True(!s.Undo() && !s.Redo(), "始めた直後に動いた");
        Check.Equal(json, s.Json, "文書が変わった");
        Check.True(!s.IsUndelivered, "何もしない Undo で未渡しになった");
    }

    // ------------------------------------------------------------ 選択

    [Test]
    public void FR30_AC28_11_選択だけの変更は積まずUndoとRedoは対象を選び直す()
    {
        var s = WithBase();                                   // A(土台)
        s.AddImportedImages([Image(2, 10, 10)]);              // B が選ばれる
        Check.Equal(Target.OfLayer(1), s.Selected, "足した B が選ばれていない");
        var count = s.UndoCount;
        Check.True(s.Select(Target.OfLayer(0)), "A を選べない");
        Check.Equal(count, s.UndoCount, "選択で件が増えた");
        Check.True(!s.CanRedo, "選択で Redo の件ができた");
        Check.True(!s.Select(Target.OfLayer(0)), "同じ対象を選んで変わったと返した");
        s.SetLayerOpacity(0, 50);
        Check.True(s.Select(Target.OfLayer(1)), "B を選べない");
        s.SetLayerOpacity(1, 30);
        Check.Equal(count + 2, s.UndoCount, "変更 2 回で 2 件");

        Check.True(s.Undo(), "Undo 1");
        Check.Equal(100, s.Document!.Layers[1].Opacity, "1 回目で B の不透明度");
        Check.Equal(50, s.Document!.Layers[0].Opacity, "1 回目で A の不透明度");
        Check.Equal(Target.OfLayer(1), s.Selected, "1 回目で B が選ばれる");
        Check.True(s.Undo(), "Undo 2");
        Check.Equal(100, s.Document!.Layers[0].Opacity, "2 回目で A の不透明度");
        Check.Equal(Target.OfLayer(0), s.Selected, "2 回目で A が選ばれる");
        Check.True(s.Redo(), "Redo");
        Check.Equal(50, s.Document!.Layers[0].Opacity, "Redo で A の不透明度");
        Check.Equal(Target.OfLayer(1), s.Selected, "Redo の先はその件に最後に記録した B");
    }

    [Test]
    public void FR30_Undoで戻った件での選択はその件だけを書き換えRedoの件を消さない()
    {
        var s = WithBase();
        s.AddImportedImages([Image(2, 10, 10)]);
        s.SetLayerOpacity(1, 40);
        s.Undo();
        Check.True(s.Select(Target.All), "全体を選べない");
        Check.Equal(1, s.RedoCount, "選択で Redo の件が消えた");
        Check.Equal(1, s.UndoCount, "選択で Undo の件が変わった");
        s.Redo();
        Check.Equal(40, s.Document!.Layers[1].Opacity, "Redo の先");
        s.Undo();
        Check.Equal(Target.All, s.Selected, "戻った件に記録した選択");
    }

    // ------------------------------------------------------------ ソースの保持の上限

    [Test]
    public void FR30_AC28_11_履歴だけが参照するソースの合計が上限を超えたら古い件から捨てる()
    {
        var s = WithLimit(10_000);
        var released = new List<string>();
        s.SourceReleased += released.Add;
        s.AddImportedImages([Image(1, 30, 30, bytes: 3_000)]);    // S
        s.AddImportedImages([Image(2, 40, 40, bytes: 4_000)]);    // L1
        s.AddImportedImages([Image(3, 40, 40, bytes: 4_000)]);    // L2
        s.DeleteLayer(1);
        s.DeleteLayer(1);
        Check.Equal(1, s.Document!.Layers.Count, "削除のあとの枚数");
        Check.Equal(8_000L, s.HistoryOnlySourceBytes, "履歴だけが参照する合計");
        Check.True(s.Sources.ContainsKey(Sha(2)) && s.Sources.ContainsKey(Sha(3)), "削除したレイヤーのソースが残っていない");
        Check.Equal(0, released.Count, "参照されているソースを解放した");
        Check.Equal(4, s.UndoCount, "8,000 で件が捨てられた");

        // 4 回戻れて、そのたびにレイヤーとソースが元に戻る。
        int[] counts = [2, 3, 2, 1];
        for (var i = 0; i < 4; i++)
        {
            Check.True(s.Undo(), "Undo " + (i + 1));
            Check.Equal(counts[i], s.Document!.Layers.Count, "Undo " + (i + 1) + " の枚数");
            foreach (var l in s.Document!.Layers)
                Check.True(s.Sources.ContainsKey(l.Asset!), "Undo " + (i + 1) + " のソース");
        }
        Check.True(!s.Undo(), "5 回目の Undo");
        for (var i = 0; i < 4; i++) Check.True(s.Redo(), "Redo " + (i + 1));

        s.AddImportedImages([Image(4, 50, 50, bytes: 6_000)]);    // L3
        Check.Equal(8_000L, s.HistoryOnlySourceBytes, "L3 を足したとき(現在の文書が使うものは数えない)");
        Check.Equal(5, s.UndoCount, "L3 を足したとき");
        s.DeleteLayer(1);                                          // 合計 14,000 → 古い件から 3 件
        Check.Equal(10_000L, s.HistoryOnlySourceBytes, "ちょうど 10,000 は捨てない");
        Check.Equal(3, s.UndoCount, "捨てた件の数");
        Check.True(!s.Sources.ContainsKey(Sha(2)), "L1 のソースが残っている");
        Check.True(s.Sources.ContainsKey(Sha(3)) && s.Sources.ContainsKey(Sha(4)), "L2・L3 のソースが無い");
        Check.SequenceEqual([Sha(2)], released, "解放の知らせ");

        for (var i = 0; i < 3; i++) Check.True(s.Undo(), "Undo " + (i + 1));
        var d = s.Document!;
        Check.SequenceEqual([Sha(1), Sha(3)], d.Layers.Select(l => l.Asset!).ToList(), "3 回目で S と L2 の文書");
        Check.True(!s.Undo(), "4 回目の Undo");
    }

    [Test]
    public void FR30_Q23_ソースの保持の上限の既定は300000000で現在の文書が使うものは数えない()
    {
        Check.Equal(300_000_000L, EditorSession.DefaultHistorySourceBytesLimit, "既定の上限の定数");
        var s = new EditorSession(Catalog, new FixedSeedSource(7));
        var released = new List<string>();
        s.SourceReleased += released.Add;
        s.AddImportedImages([Image(1, 10, 10, bytes: 1_000)]);
        s.AddImportedImages([Image(2, 10, 10, bytes: 300_000_000)]);
        s.AddImportedImages([Image(5, 10, 10, bytes: 900_000_000)]);
        Check.Equal(0L, s.HistoryOnlySourceBytes, "現在の文書が使うソースを数えた");
        Check.Equal(2, s.UndoCount, "現在の文書が使うソースで件が捨てられた");
        // 画像 5 を消すと、履歴だけが参照する合計が 900,000,000 になり、超えなくなるまで(画像 5 を参照する件が無くなるまで)捨てる。
        s.DeleteLayer(2);
        Check.Equal(0L, s.HistoryOnlySourceBytes, "超えたまま残った");
        Check.Equal(0, s.UndoCount, "画像 5 を参照する件が残った");
        Check.True(!s.Sources.ContainsKey(Sha(5)), "画像 5 のソースが残っている");
        Check.True(s.Sources.ContainsKey(Sha(1)) && s.Sources.ContainsKey(Sha(2)), "現在の文書のソースを捨てた");
        Check.SequenceEqual([Sha(5)], released, "解放の知らせ");
    }

    [Test]
    public void FR30_Q23_既定の上限はちょうど300000000を捨てず1バイト超えで捨てる()
    {
        var s = new EditorSession(Catalog, new FixedSeedSource(7));
        var released = new List<string>();
        s.SourceReleased += released.Add;
        s.AddImportedImages([Image(1, 10, 10, bytes: 1_000)]);
        s.AddImportedImages([Image(2, 10, 10, bytes: 300_000_000)]);
        s.DeleteLayer(1);
        Check.Equal(300_000_000L, s.HistoryOnlySourceBytes, "ちょうど 300,000,000");
        Check.Equal(2, s.UndoCount, "ちょうど 300,000,000 で捨てた");
        s.AddImportedImages([Image(3, 10, 10, bytes: 1)]);
        s.DeleteLayer(1);
        Check.Equal(1L, s.HistoryOnlySourceBytes, "1 バイト超えのあと");
        Check.Equal(2, s.UndoCount, "古い 2 件を捨てて残る件");
        Check.True(!s.Sources.ContainsKey(Sha(2)), "捨てた件のソースが残っている");
        Check.SequenceEqual([Sha(2)], released, "解放の知らせ");
    }

    [Test]
    public void FR30_新しい分岐で消えたRedoの件だけが参照するソースは解放する()
    {
        var s = WithBase();
        var released = new List<string>();
        s.SourceReleased += released.Add;
        s.AddImportedImages([Image(2, 10, 10)]);
        s.Undo();
        Check.True(s.Sources.ContainsKey(Sha(2)), "Redo の件が参照するソースを捨てた");
        s.SetLayerOpacity(0, 10);
        Check.True(!s.CanRedo, "新しい変更で Redo の件が残った");
        Check.True(!s.Sources.ContainsKey(Sha(2)), "どの件も参照しないソースが残った");
        Check.SequenceEqual([Sha(2)], released, "解放の知らせ");
    }

    // ------------------------------------------------------------ 連続する変更

    [Test]
    public void FR30_AC23_8_連続する変更は終わりの時点で1件になる()
    {
        var s = WithBase();
        var before = s.Json;
        var count = s.UndoCount;
        s.BeginContinuous();
        for (var i = 1; i <= 30; i++) s.SetLayerOpacity(0, 100 - i);
        s.EndContinuous();
        Check.Equal(count + 1, s.UndoCount, "ドラッグが 1 件になっていない");
        Check.Equal(70, s.Document!.Layers[0].Opacity, "終わりの値");
        Check.True(s.Undo(), "Undo できない");
        Check.Equal(before, s.Json, "1 回の Undo でドラッグの前に戻らない");
        Check.True(s.Redo(), "Redo できない");
        Check.Equal(70, s.Document!.Layers[0].Opacity, "Redo の値");
    }

    [Test]
    public void FR30_連続する変更が元の値に戻ったときは件を積まない()
    {
        var s = WithBase();
        s.SetLayerOpacity(0, 80);
        var json = s.Json;
        var count = s.UndoCount;
        s.BeginContinuous();
        s.SetLayerOpacity(0, 60);
        s.SetLayerOpacity(0, 80);
        s.EndContinuous();
        Check.Equal(count, s.UndoCount, "元に戻ったドラッグが件を積んだ");
        Check.Equal(json, s.Json, "文書");
        Check.True(s.Undo(), "Undo できない");
        Check.Equal(100, s.Document!.Layers[0].Opacity, "ドラッグの前の件が消えた");
    }

    [Test]
    public void FR30_連続する変更の途中のUndoはまとめを閉じる()
    {
        var s = WithBase();
        var before = s.Json;
        s.BeginContinuous();
        s.SetLayerOpacity(0, 50);
        s.SetLayerOpacity(0, 40);
        Check.True(s.Undo(), "途中の Undo");
        Check.Equal(before, s.Json, "途中の Undo の先");
        s.SetLayerOpacity(0, 30);
        s.SetLayerOpacity(0, 20);
        Check.Equal(2, s.UndoCount, "閉じたあとの変更がまとまった");
        s.EndContinuous();                                    // 閉じたあとの End は何もしない
        Check.Equal(2, s.UndoCount, "余分な End で件が変わった");
    }

    // ------------------------------------------------------------ 新しい文書

    [Test]
    public void FR30_AC23_8_新しい文書を始める操作で履歴が空になり取り込みの追加では空にならない()
    {
        var s = WithBase();
        var released = new List<string>();
        s.SourceReleased += released.Add;
        s.AddImportedImages([Image(2, 10, 10)]);
        Check.Equal(1, s.UndoCount, "レイヤーを足す取り込みは 1 件を積む");
        s.SetLayerOpacity(1, 50);
        s.Undo();
        Check.True(s.CanUndo && s.CanRedo, "前提");

        // 空のキャンバス
        s.NewEmptyCanvas(100, 100);
        Check.True(!s.CanUndo && !s.CanRedo, "空のキャンバスで履歴が残った");
        Check.Equal(0, s.Sources.Count, "空のキャンバスでソースが残った");
        Check.SequenceEqual([Sha(1), Sha(2)], released.OrderBy(x => x, StringComparer.Ordinal).ToList(), "解放の知らせ");

        // 新規 → 最初の取り込み
        s.AddEmptyLayer();
        Check.True(s.CanUndo, "前提 2");
        Check.Equal(ReplaceResult.Replaced, s.RequestNew(new FakeConfirm(ReplaceChoice.ReplaceWithoutSaving)), "新規");
        Check.True(!s.CanUndo && !s.CanRedo, "新規で履歴が残った");
        var r = s.AddImportedImages([Image(3, 10, 10)]);
        Check.True(r.StartedNewDocument, "最初の取り込みが新しい文書にならない");
        Check.True(!s.CanUndo && !s.CanRedo, "最初の取り込みで履歴が残った");

        // .glint を開く
        s.SetLayerOpacity(0, 10);
        Check.True(s.CanUndo, "前提 3");
        var loaded = DocumentJson.Read(DocumentJsonTests.SpecExample, Catalog).Document!;
        var sources = loaded.Assets.Select(a => new SourceInfo(a.Sha256, a.Ext, a.Bytes, 100, 100)).ToList();
        s.OpenDocument(loaded, sources);
        Check.True(!s.CanUndo && !s.CanRedo, "開いたあとに履歴が残った");
        Check.True(!s.Sources.ContainsKey(Sha(3)), "開く前のソースが残った");
        Check.True(released.Contains(Sha(3)), "開く前のソースの解放の知らせ");
    }
}
