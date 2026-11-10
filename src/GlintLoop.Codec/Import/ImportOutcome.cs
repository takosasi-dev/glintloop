// 1 回の取り込み(貼り付け・ドロップ・開く・起動引数・サンプル)の結果。通知バーに出す通知は FR-4 の規則で 1 つだけ。
// 足せた画像があるときは、画面が最初の表示を終えた時点で CompleteDisplay を呼ぶ。そこでログ import_ok を書き、取り込みの門を出る(E-20)。
// 足せなかったファイルの import_fail は、取り込みの処理の中で書き終えている。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;

namespace GlintLoop.Codec.Import;

public enum ImportStatus
{
    /// <summary>ほかの取り込みかダイアログの進行中だったので無視した(E-20)。何も変えず、通知もログも無い。</summary>
    Ignored,

    /// <summary>取り込むものが無かった(起動引数が 0 個、何も持たないドロップなど)。</summary>
    NoInput,

    /// <summary>取り込みを行った(足せた枚数が 0 のときも含む。そのときは Notice が理由の通知)。</summary>
    Completed,

    /// <summary>.glint(先頭バイトが ZIP のファイル)が 1 個だけ渡された。開く処理(FR-6・FR-13)は呼び出し側が行い、終わったら CompleteDisplay で門を出る。ProjectPath にパスが入る。</summary>
    ProjectRequested,
}

/// <summary>import_ok の ms の測り方: 時計と、取り込みの操作を受け取った時刻(Monotonic)。</summary>
internal sealed record ImportClock(IClock Clock, TimeSpan ReceivedAt)
{
    /// <summary>受け取ってからの経過ミリ秒(切り捨て。負にしない)。</summary>
    public long ElapsedMilliseconds => Math.Max(0, (Clock.Monotonic - ReceivedAt).Ticks / TimeSpan.TicksPerMillisecond);
}

public sealed class ImportOutcome
{
    private readonly ILogSink _log;
    private readonly ImportClock? _clock;
    private IDisposable? _ticket;

    internal ImportOutcome(ImportStatus status, ImportVia via, Notice? notice, int requested,
        IReadOnlyList<ImportedLayer> added, IReadOnlyList<ImportFailure> failures, int layersAfter,
        string? projectPath, ILogSink log, ImportClock? clock, IDisposable? ticket)
    {
        Status = status;
        Via = via;
        Notice = notice;
        Requested = requested;
        AddedLayers = added;
        Failures = failures;
        LayersAfter = layersAfter;
        ProjectPath = projectPath;
        _log = log;
        _clock = clock;
        _ticket = ticket;
    }

    internal static ImportOutcome Simple(ImportStatus status, ImportVia via, Notice? notice, ILogSink log, string? projectPath = null) =>
        new(status, via, notice, 0, [], [], 0, projectPath, log, null, null);

    public ImportStatus Status { get; }

    public ImportVia Via { get; }

    /// <summary>通知バーに出す通知(出さないときは null)。</summary>
    public Notice? Notice { get; }

    /// <summary>数えたファイル(またはデータ)の個数 N。</summary>
    public int Requested { get; }

    /// <summary>足せた枚数 M。</summary>
    public int Added => AddedLayers.Count;

    /// <summary>足したレイヤー(渡した順)。IImportTarget.AddImages に渡したものと同じ。</summary>
    public IReadOnlyList<ImportedLayer> AddedLayers { get; }

    /// <summary>足せなかったもの(数えた順)。</summary>
    public IReadOnlyList<ImportFailure> Failures { get; }

    /// <summary>足したあとの文書のレイヤーの枚数(AddImages の戻り値。足さなかったときは 0)。</summary>
    public int LayersAfter { get; }

    /// <summary>Status が ProjectRequested のときの .glint のパス。</summary>
    public string? ProjectPath { get; }

    /// <summary>CompleteDisplay で import_ok を書いたときの ms(書かなかったら null。測定の記録の paste に同じ値を使う。§6.8)。</summary>
    public long? DisplayMs { get; private set; }

    /// <summary>取り込みの操作を受け取った時刻(IClock.Monotonic)。門に入らなかったときは null。</summary>
    public TimeSpan? ReceivedAt => _clock?.ReceivedAt;

    /// <summary>まだ最初の表示を待っている(門に入ったまま)か。</summary>
    public bool AwaitingDisplay => Volatile.Read(ref _ticket) is not null;

    /// <summary>
    /// 最初の表示が終わった。足せたファイルごとに import_ok を 1 行書き(ms は取り込みの操作を受け取ってからの経過時間で、どの行も同じ値)、
    /// 取り込みの門を出る。2 回目以降の呼び出しと、足せたものが無いときは何もしない。サンプルは via の値が表に無いので import_ok を書かない。
    /// </summary>
    public void CompleteDisplay()
    {
        var ticket = Interlocked.Exchange(ref _ticket, null);
        if (ticket is null) return;
        try
        {
            if (Via == ImportVia.Sample || _clock is null || AddedLayers.Count == 0) return;
            var ms = _clock.ElapsedMilliseconds;
            DisplayMs = ms;
            foreach (var layer in AddedLayers)
            {
                var img = layer.Image;
                _log.Write(LogEvent.Of("import_ok",
                    ("ms", ms), ("w", img.Width), ("h", img.Height), ("fmt", img.LogFormat), ("bytes", img.ByteCount),
                    ("via", ViaName(Via)), ("layers", LayersAfter)));
            }
        }
        finally
        {
            ticket.Dispose();
        }
    }

    /// <summary>ログの via の綴り(paste・drop・open・arg)。</summary>
    public static string ViaName(ImportVia via) => via switch
    {
        ImportVia.Paste => "paste",
        ImportVia.Drop => "drop",
        ImportVia.Open => "open",
        ImportVia.Arg => "arg",
        _ => throw new ArgumentOutOfRangeException(nameof(via), "サンプルはログの via に無い"),
    };
}
