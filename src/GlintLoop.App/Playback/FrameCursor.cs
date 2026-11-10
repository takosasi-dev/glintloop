// いま表示しているフレームの位置(k・N・再生中か)。表示の状態なので履歴に入れない(FR-30)。UI スレッドだけで使い、Changed も UI スレッドで上がる。
// N は AppShell が文書の変更のたびに MotionInfo.FrameCount で入れる(モーションが無ければ 1)。k は担当 timeline(再生・スクラブ)が Set で入れる。
// キャンバス(表示するフレームの描画)・スポイト・書き出し(静止形式の k)・タイムラインの表示が読む。
using GlintLoop.Core.Session;

namespace GlintLoop.App.Playback;

public sealed class FrameCursor
{
    /// <summary>いまのフレームの番号 k(0〜N−1)。</summary>
    public int K { get; private set; }

    /// <summary>いまの文書のフレーム数 N(モーションが無ければ 1。FR-67)。</summary>
    public int N { get; private set; } = 1;

    /// <summary>再生中か(担当 timeline が SetPlaying で入れる)。</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>位相 t=k÷N。</summary>
    public double T => TimeModel.Phase(K, N);

    /// <summary>K・N・IsPlaying のどれかが変わったとき(UI スレッド)。</summary>
    public event Action? Changed;

    /// <summary>表示するフレームを k にする(0〜N−1 に切る)。</summary>
    public void Set(int k)
    {
        var v = Math.Clamp(k, 0, N - 1);
        if (v == K) return;
        K = v;
        Changed?.Invoke();
    }

    /// <summary>再生中かを入れる。</summary>
    public void SetPlaying(bool playing)
    {
        if (playing == IsPlaying) return;
        IsPlaying = playing;
        Changed?.Invoke();
    }

    /// <summary>フレーム数を入れる(1 未満は 1)。k が N−1 を超えたら N−1 に切る(仮の決め: 位相は保たず番号を切る)。</summary>
    public void SetFrameCount(int n)
    {
        var v = Math.Max(1, n);
        var k = Math.Min(K, v - 1);
        if (v == N && k == K) return;
        N = v;
        K = k;
        Changed?.Invoke();
    }

    /// <summary>文書が入れ替わったとき: k=0、再生を止める(N はそのあと SetFrameCount で入る)。</summary>
    public void Reset()
    {
        if (K == 0 && !IsPlaying) return;
        K = 0;
        IsPlaying = false;
        Changed?.Invoke();
    }
}
