// EditorSession の変更の通知(Changed)。文書を変える公開の操作のあとに 1 回だけ、何が変わったかのフラグ(SessionChange)を付けて上げる。
// 操作の始めに Track() で前の状態を控え、いちばん外の Track が閉じるときに前後を比べてフラグを決める(入れ子の操作も 1 回にまとまる)。
// 何も変わらなかった操作(EditOutcome.Changed=false など)では上げない。通知は操作を呼んだスレッドで上がる(取り込みは UI スレッドの外)。
using GlintLoop.Core.Effects;

namespace GlintLoop.Core.Session;

/// <summary>
/// Changed の引数。複数が同時に立つ。Document が立つときは、ほかのすべての状態のフラグ(History〜Other)も立てる(全部を読み直す合図)。
/// レイヤーの数が変わったときは番号がずれるので Layers・Placement・Stack・Params を全部立て、スタックの行の数が変わったときは Stack・Params を立てる。
/// </summary>
[Flags]
public enum SessionChange
{
    None = 0,

    /// <summary>文書ができた・入れ替わった・閉じた(新しい文書・最初の取り込み・空のキャンバス・開く・新規)。HasDocument が変わったときも。</summary>
    Document = 1 << 0,

    /// <summary>Undo・Redo できる件数(CanUndo・CanRedo・UndoCount・RedoCount)が変わった。</summary>
    History = 1 << 1,

    /// <summary>未渡し(IsUndelivered)が変わった。</summary>
    Undelivered = 1 << 2,

    /// <summary>選んでいる対象(Selected)が変わった。</summary>
    Selection = 1 << 3,

    /// <summary>レイヤーの構造(数・並び・種類・画像・空のレイヤーの大きさと塗り・表示・名前)が変わった。</summary>
    Layers = 1 << 4,

    /// <summary>レイヤーの配置・ブレンド・不透明度が変わった。</summary>
    Placement = 1 << 5,

    /// <summary>スタック(レイヤーか全体)の行の増減・並び・有効と無効・origin・salt が変わった。</summary>
    Stack = 1 << 6,

    /// <summary>エフェクトのパラメータ・強さ(amount)・ルックの強さが変わった。</summary>
    Params = 1 << 7,

    /// <summary>合成のネイティブ寸法(CompositeNative。土台の形ステージで変わる)が変わった。</summary>
    CompositeSize = 1 << 8,

    /// <summary>上のどれにも入らない文書の変化(seed など。時間は Time、出力設定は Output)。</summary>
    Other = 1 << 9,

    /// <summary>連続する変更(BeginContinuous〜EndContinuous)の途中の変更。</summary>
    Continuous = 1 << 10,

    /// <summary>連続する変更が終わった(途中で 1 回でも通知したとき。元に戻って 1 件を捨てたときは History も立つ)。</summary>
    ContinuousEnd = 1 << 11,

    /// <summary>時間の設定(L・F・ループ・停止時間。FR-58〜FR-61)が変わった(Phase 4)。</summary>
    Time = 1 << 12,

    /// <summary>出力設定(形式・サイズ・形式ごとのオプション・容量上限。§9.11 の 3 の output)が変わった(Phase 4)。</summary>
    Output = 1 << 13,

    /// <summary>状態のフラグの全部(Document が立つときに一緒に立てるもの)。</summary>
    AllState = History | Undelivered | Selection | Layers | Placement | Stack | Params | CompositeSize | Other | Time | Output,
}

public sealed partial class EditorSession
{
    /// <summary>
    /// 文書を変える操作(Undo・Redo・取り込み・新しい文書・閉じる・選択・未渡しの変化を含む)のあとに 1 回だけ上がる。
    /// 変わらなかった操作では上がらない。操作を呼んだスレッドで上がるので、画面の受け手は必要なら Dispatcher に回す。
    /// </summary>
    public event Action<SessionChange>? Changed;

    private readonly record struct ChangeSnapshot(
        SessionDocument? Doc, int Generation, int UndoCount, int RedoCount, bool Undelivered, SizeD? Size, int BatchDepth);

    private int _trackDepth;
    private int _docGeneration;
    private bool _batchNotified;
    private ChangeSnapshot _before;

    /// <summary>公開の操作の始めに <c>using var _ = Track();</c> で呼ぶ。いちばん外が閉じたときに前後を比べて Changed を上げる。</summary>
    private ChangeScope Track()
    {
        if (_trackDepth++ == 0) _before = TakeSnapshot();
        return new ChangeScope(this);
    }

    private readonly struct ChangeScope(EditorSession session) : IDisposable
    {
        public void Dispose() => session.EndTrack();
    }

    private void EndTrack()
    {
        if (--_trackDepth > 0) return;
        var after = TakeSnapshot();
        var what = Compare(_before, after);
        var endedBatch = _before.BatchDepth > 0 && _batchDepth == 0;
        if (what != SessionChange.None && _batchDepth > 0)
        {
            what |= SessionChange.Continuous;
            _batchNotified = true;
        }
        if (endedBatch)
        {
            if (_batchNotified) what |= SessionChange.ContinuousEnd;
            _batchNotified = false;
        }
        _before = default;
        if (what != SessionChange.None) Changed?.Invoke(what);
    }

    private ChangeSnapshot TakeSnapshot() =>
        new(Document, _docGeneration, UndoCount, RedoCount, IsUndelivered, HasDocument ? CompositeNative() : null, _batchDepth);

    private static SessionChange Compare(ChangeSnapshot a, ChangeSnapshot b)
    {
        var f = SessionChange.None;
        if (a.UndoCount != b.UndoCount || a.RedoCount != b.RedoCount) f |= SessionChange.History;
        if (a.Undelivered != b.Undelivered) f |= SessionChange.Undelivered;
        if (a.Doc is null && b.Doc is null) return f;
        if (a.Doc is null || b.Doc is null || a.Generation != b.Generation) return SessionChange.Document | SessionChange.AllState;
        if (ReferenceEquals(a.Doc, b.Doc)) return f;
        var content = DiffDocuments(a.Doc, b.Doc);
        if (a.Size != b.Size) content |= SessionChange.CompositeSize;
        // 比べた項目のどれも違わないのに文書が別のものになったときは、取りこぼさないよう Other にする。
        if (content == SessionChange.None && DocumentJson.Write(a.Doc) != DocumentJson.Write(b.Doc)) content = SessionChange.Other;
        return f | content;
    }

    private static SessionChange DiffDocuments(SessionDocument a, SessionDocument b)
    {
        var f = SessionChange.None;
        if (a.Selected != b.Selected) f |= SessionChange.Selection;
        if (a.Layers.Count != b.Layers.Count)
        {
            f |= SessionChange.Layers | SessionChange.Placement | SessionChange.Stack | SessionChange.Params;
        }
        else
        {
            for (var i = 0; i < a.Layers.Count; i++)
            {
                var x = a.Layers[i];
                var y = b.Layers[i];
                if (ReferenceEquals(x, y)) continue;
                if (x.Name != y.Name || x.Kind != y.Kind || x.Asset != y.Asset || x.Size != y.Size || x.Fill != y.Fill || x.Visible != y.Visible)
                    f |= SessionChange.Layers;
                if (x.Place != y.Place || x.Blend != y.Blend || x.Opacity != y.Opacity) f |= SessionChange.Placement;
                if (x.LookStrength != y.LookStrength) f |= SessionChange.Params;
                f |= DiffStack(x.Stack, y.Stack);
            }
        }
        if (a.All.LookStrength != b.All.LookStrength) f |= SessionChange.Params;
        f |= DiffStack(a.All.Stack, b.All.Stack);
        if (a.Seed != b.Seed) f |= SessionChange.Other;
        if (a.Time != b.Time) f |= SessionChange.Time;
        if (a.Output != b.Output) f |= SessionChange.Output;
        return f;
    }

    private static SessionChange DiffStack(IReadOnlyList<StackEntry> a, IReadOnlyList<StackEntry> b)
    {
        if (ReferenceEquals(a, b)) return SessionChange.None;
        if (a.Count != b.Count) return SessionChange.Stack | SessionChange.Params;
        var f = SessionChange.None;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (ReferenceEquals(x, y)) continue;
            if (x.Id != y.Id || x.Enabled != y.Enabled || x.Origin != y.Origin || x.Salt != y.Salt) f |= SessionChange.Stack;
            if (x.Amount != y.Amount || !SameParams(x.Params, y.Params)) f |= SessionChange.Params;
        }
        return f;
    }

    private static bool SameParams(IReadOnlyList<KeyValuePair<string, ParamValue>> a, IReadOnlyList<KeyValuePair<string, ParamValue>> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Key != b[i].Key || !Equals(a[i].Value, b[i].Value)) return false;
        }
        return true;
    }
}
