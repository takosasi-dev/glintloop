// 取り込みと置き換えの確認ダイアログを同時に 1 つにする門(仕様書 E-20)。先に入った方を続け、後から来た操作は無視する。
// 取り込みは、読み込みを始めてから最初の表示が終わるまでを 1 つと数える(複数のファイルも全体で 1 つ)。
// 確認ダイアログ(Phase 3)も同じ門を使う。
namespace GlintLoop.Codec.Import;

public sealed class ImportGate
{
    private int _busy;

    /// <summary>今、取り込みかダイアログが進行中か。</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>門に入る。入れたら、出るときに Dispose する札を返す。ほかが進行中なら null(その操作は無視する)。</summary>
    public IDisposable? TryEnter() =>
        Interlocked.CompareExchange(ref _busy, 1, 0) == 0 ? new Ticket(this) : null;

    private sealed class Ticket(ImportGate gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Volatile.Write(ref gate._busy, 0);
        }
    }
}
