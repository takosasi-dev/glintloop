// 文書の描画を 1 本ずつ回し、最新の依頼だけを出す(連続する変更で描画を溜めない)。描いている間に次の依頼が来たら、終わった結果を捨てて最新を描き直す。
// 依頼は (文書, フレーム k, フレーム数 N) の組。再生中に間に合わないフレームは描かずに飛ばす(FR-60)のは、この「最新だけ」の作りによる。
// UI スレッドで使う(await のあとも UI スレッドに戻る)。描画そのもの(DocumentRenderer)は描画の専用スレッドで動く。失敗したときは最後の表示を残したまま Failed を呼ぶ。
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Viewing;

/// <summary>描画の依頼 1 つ。K・N は描くフレーム(t=K÷N。静止画は 0 と 1)。文書は参照で比べる。</summary>
public readonly record struct RenderRequest(SessionDocument Doc, int K, int N)
{
    public bool SameAs(RenderRequest other) => ReferenceEquals(Doc, other.Doc) && K == other.K && N == other.N;
}

public sealed class LatestRender
{
    private readonly Func<RenderRequest, Task<DocumentImage>> _render;
    private RenderRequest? _wanted;
    private RenderRequest? _presented;
    private int _generation;
    private bool _busy;
    // いまの _wanted を描き始めたか(同じものを出し済みで描かずに済ませたときも true)。false のまま次の依頼が来たら、置き換えた依頼として数える
    private bool _wantedTaken = true;

    public LatestRender(Func<RenderRequest, Task<DocumentImage>> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        _render = render;
    }

    /// <summary>フレームを持たない描画(いつも k=0・N=1 を描く関数)から作る。</summary>
    public LatestRender(Func<SessionDocument, Task<DocumentImage>> render)
        : this(r => render(r.Doc))
    {
        ArgumentNullException.ThrowIfNull(render);
    }

    /// <summary>描き終えた最新の結果(文書と画像)。</summary>
    public event Action<SessionDocument, DocumentImage>? Presented;

    /// <summary>最新の依頼の描画が失敗したとき。</summary>
    public event Action<Exception>? Failed;

    /// <summary>まだ描き始めていない依頼を、後から来た別の依頼で置き換えたとき(測定の記録の render_calls。§6.8)。置き換えた依頼は描画に届かない。</summary>
    public Action? Replaced { get; set; }

    /// <summary>描き始めた回数(テスト用)。</summary>
    public int Started { get; private set; }

    /// <summary>古くなって捨てた結果の数(テスト用)。</summary>
    public int Discarded { get; private set; }

    /// <summary>出した結果の数(テスト用)。</summary>
    public int PresentedCount { get; private set; }

    /// <summary>最後に出した文書。</summary>
    public SessionDocument? PresentedDocument => _presented?.Doc;

    /// <summary>最後に出した依頼(文書とフレーム)。</summary>
    public RenderRequest? PresentedRequest => _presented;

    /// <summary>最後に出した画像。</summary>
    public DocumentImage? PresentedImage { get; private set; }

    /// <summary>描いている途中か。</summary>
    public bool IsBusy => _busy;

    /// <summary>最新の依頼の文書(まだ出していなければ描く)。</summary>
    public SessionDocument? Wanted => _wanted?.Doc;

    /// <summary>最新の依頼(文書とフレーム)。</summary>
    public RenderRequest? WantedRequest => _wanted;

    /// <summary>doc の静止画(k=0・N=1)を描く依頼。</summary>
    public void Request(SessionDocument doc) => Request(doc, 0, 1);

    /// <summary>doc のフレーム k(N のうち)を描く依頼。描いている途中なら、終わったあとに最新の 1 つだけを描く。</summary>
    public void Request(SessionDocument doc, int k, int n)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var next = new RenderRequest(doc, k, Math.Max(1, n));
        if (!_wantedTaken && _wanted is { } old && !old.SameAs(next)) Replaced?.Invoke();
        _wanted = next;
        _wantedTaken = false;
        _generation++;
        if (!_busy) _ = RunAsync();
    }

    /// <summary>最新の依頼を描き直す(デバイスを作り直したあと。FR-93)。依頼が無ければ何もしない。結果が出るまで前の表示を残す。</summary>
    public void Redo()
    {
        if (_wanted is not { } w) return;
        _presented = null;
        Request(w.Doc, w.K, w.N);
    }

    /// <summary>依頼を取り消し、出した結果を忘れる(文書を閉じたとき)。描いている途中の結果は捨てる。</summary>
    public void Clear()
    {
        _wanted = null;
        _generation++;
        _presented = null;
        PresentedImage = null;
    }

    private async Task RunAsync()
    {
        _busy = true;
        try
        {
            while (_wanted is { } req)
            {
                var gen = _generation;
                _wantedTaken = true;
                if (_presented is { } p && p.SameAs(req)) break;
                Started++;
                DocumentImage image;
                try
                {
                    image = await _render(req);
                }
                catch (Exception e)
                {
                    if (gen != _generation) continue;
                    Failed?.Invoke(e);
                    break;
                }
                if (gen != _generation)
                {
                    Discarded++;
                    continue;
                }
                _presented = req;
                PresentedImage = image;
                PresentedCount++;
                Presented?.Invoke(req.Doc, image);
                break;
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
