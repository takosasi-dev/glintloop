// 測定の記録(仕様書 §6.8 の「測定の記録」・§9.11 の perf・AC-59)。GLINT_PERF が文字列 "1" と一致するときだけ作り、perf-YYYYMMDD-HHMMSS.jsonl に {"t":…,"k":"…","v":…} を 1 行ずつ書いてフラッシュする。
// 時計・タイマー・プロセスの情報・ファイルシステムは外から渡す(テストは偽物)。同名のファイルがある・開けない・書けないときは記録だけをやめ、通知もログも出さない。
// 記録に入れるのは種類の名前(固定の語)と 0 以上の整数だけ(INV-15)。どのスレッドから呼んでもよい(1 行ずつ lock の中で書く)。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GlintLoop.Core.Platform;

namespace GlintLoop.Core.Diagnostics;

/// <summary>記録に使うプロセスの情報(テストは偽物)。</summary>
public interface IProcessStats
{
    /// <summary>OS が記録しているプロセスの作成時刻。</summary>
    DateTimeOffset StartTimeUtc { get; }

    /// <summary>作業セットの大きさ(バイト)。</summary>
    long WorkingSetBytes { get; }

    /// <summary>累積の CPU 時間。</summary>
    TimeSpan TotalProcessorTime { get; }
}

/// <summary>いまのプロセスの情報(読むたびに取り直す)。</summary>
public sealed class CurrentProcessStats : IProcessStats
{
    public static CurrentProcessStats Instance { get; } = new();

    public DateTimeOffset StartTimeUtc
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return new DateTimeOffset(p.StartTime).ToUniversalTime();
        }
    }

    public long WorkingSetBytes
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return p.WorkingSet64;
        }
    }

    public TimeSpan TotalProcessorTime
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return p.TotalProcessorTime;
        }
    }
}

public sealed class PerfRecorder : IDisposable
{
    public const string EnvironmentVariable = "GLINT_PERF";

    /// <summary>mem・cpu・render_calls を書く間隔。</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>ui_stall を測る UI スレッドのタイマーの周期。</summary>
    public static readonly TimeSpan UiInterval = TimeSpan.FromMilliseconds(32);

    private const long UiStallMin = 100;
    private const long BytesPerMB = 1_000_000;

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "startup", "paste", "gallery", "drag_frame", "ui_stall", "mem", "cpu", "render_calls", "export_begin", "export_end",
    };

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IProcessStats _process;
    private readonly TimeSpan _origin;
    private readonly IOneShotTimer _sampleTimer;
    private readonly IOneShotTimer _uiTimer;
    private Stream? _file;
    private long _renderCalls;
    private long? _lastUi;
    private bool _startupWritten;
    private bool _dragging;
    private long? _lastDisplay;
    private TimeSpan? _pendingImport;
    private TimeSpan? _galleryFrom;

    private PerfRecorder(Stream file, IClock clock, ITimerFactory sampleTimers, ITimerFactory uiTimers, IProcessStats process)
    {
        _file = file;
        _clock = clock;
        _process = process;
        _origin = clock.Monotonic;
        _sampleTimer = sampleTimers.Create();
        _sampleTimer.Elapsed += OnSample;
        _uiTimer = uiTimers.Create();
        _uiTimer.Elapsed += OnUiTimer;
        _sampleTimer.Start(SampleInterval);
        _uiTimer.Start(UiInterval);
    }

    /// <summary>
    /// env(GLINT_PERF の値)が "1" のときだけ、perfDir にいまのローカル時刻の名前のファイルを作って記録を始める。それ以外は何も作らずに null。
    /// 同名のファイルがある・フォルダかファイルを作れないときも null(その起動の記録をやめる)。sampleTimers は 1 秒ごとの行、uiTimers は UI スレッドの 32 ms のタイマー。
    /// </summary>
    public static PerfRecorder? TryStart(string? env, string perfDir, IClock clock, IFileSystem fileSystem, ITimerFactory sampleTimers, ITimerFactory uiTimers,
        IProcessStats process)
    {
        if (env != "1") return null;
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalZone);
        var path = Path.Combine(perfDir, "perf-" + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
        Stream file;
        try
        {
            fileSystem.CreateDirectory(perfDir);
            // shortcut: 調べてから開くまでの間に別のプロセスが同じ名前で作ると追記になる。同じ秒に 2 つ起動することは多重起動の判定で起きない
            if (fileSystem.Exists(path)) return null;
            file = fileSystem.OpenAppend(path);
        }
        catch (Exception e) when (IsIoFailure(e))
        {
            return null;
        }
        return new PerfRecorder(file, clock, sampleTimers, uiTimers, process);
    }

    private static bool IsIoFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or ObjectDisposedException or System.Security.SecurityException;

    /// <summary>まだ書いているか(書き込みに失敗したら false)。</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate) return _file is not null;
        }
    }

    /// <summary>記録を始めてからの経過ミリ秒(切り捨て)。</summary>
    public long NowMs => Ms(_clock.Monotonic - _origin);

    private static long Ms(TimeSpan d) => Math.Max(0, d.Ticks / TimeSpan.TicksPerMillisecond);

    /// <summary>種類 kind の値 value の行をいまの時刻で書く。表に無い種類は書かない。書いた行の t を返す(書かなかったら -1)。</summary>
    public long Write(string kind, long value)
    {
        lock (_gate) return WriteAt(NowMs, kind, value);
    }

    private long WriteAt(long t, string kind, long value)
    {
        if (_file is not { } file || !Kinds.Contains(kind)) return -1;
        var line = string.Create(CultureInfo.InvariantCulture, $"{{\"t\":{t},\"k\":\"{kind}\",\"v\":{Math.Max(0, value)}}}\n");
        try
        {
            file.Write(Encoding.ASCII.GetBytes(line));
            file.Flush();
            return t;
        }
        catch (Exception e) when (IsIoFailure(e))
        {
            // 記録だけをやめる(アプリの動作は変えない。通知もログも出さない)。タイマーは次に鳴ったときに掛け直さない
            _file = null;
            try
            {
                file.Dispose();
            }
            catch (Exception d) when (IsIoFailure(d))
            {
            }
            return -1;
        }
    }

    // ================================================================ 1 秒ごとの行(mem・cpu・render_calls)

    /// <summary>RenderService が描画の依頼を受け付けたとき、または表示の依頼が後の依頼に置き換えられたときに 1 足す。</summary>
    public void CountRenderCall() => Interlocked.Increment(ref _renderCalls);

    private void OnSample()
    {
        var ws = _process.WorkingSetBytes;
        var cpu = _process.TotalProcessorTime;
        var calls = Interlocked.Read(ref _renderCalls);
        long t;
        lock (_gate)
        {
            if (_file is null) return;
            t = NowMs;
            // 3 行は同じ t で続けて書く(ほかのスレッドの行を間に挟まない)
            WriteAt(t, "mem", ws / BytesPerMB);
            WriteAt(t, "cpu", Ms(cpu));
            WriteAt(t, "render_calls", calls);
            if (_file is null) return;
        }
        var interval = (long)SampleInterval.TotalMilliseconds;
        _sampleTimer.Start(TimeSpan.FromMilliseconds((t / interval + 1) * interval - t));
    }

    // ================================================================ ui_stall

    private void OnUiTimer()
    {
        UiTick();
        if (IsRecording) _uiTimer.Start(UiInterval);
    }

    /// <summary>UI スレッドの周期タイマーの呼び出し 1 回。直前の呼び出しからの間隔から 32 を引いた値が 100 以上なら ui_stall を書く(最初の呼び出しでは書かない)。</summary>
    public void UiTick()
    {
        lock (_gate)
        {
            var now = NowMs;
            if (_lastUi is { } last && now - last - (long)UiInterval.TotalMilliseconds is var stall && stall >= UiStallMin)
                WriteAt(now, "ui_stall", stall);
            _lastUi = now;
        }
    }

    // ================================================================ startup

    /// <summary>メインウィンドウの ContentRendered が届いた。プロセスの作成時刻からの ms を 1 回だけ書く(負なら 0)。</summary>
    public void ContentRendered()
    {
        var start = _process.StartTimeUtc;
        lock (_gate)
        {
            if (_startupWritten) return;
            _startupWritten = true;
            WriteAt(NowMs, "startup", Ms(_clock.UtcNow - start));
        }
    }

    // ================================================================ paste と gallery

    /// <summary>取り込みの操作(receivedAt に受け取った。時刻は IClock.Monotonic)で画像を文書に足した。次のギャラリーの作り直しで gallery を測る。</summary>
    public void ImportApplied(TimeSpan receivedAt)
    {
        lock (_gate) _pendingImport = receivedAt;
    }

    /// <summary>取り込みの最初の表示が終わった(ms はログの import_ok の ms と同じ値)。</summary>
    public void ImportDisplayed(long ms) => Write("paste", ms);

    /// <summary>ギャラリーの作り直しを始めた。その前の取り込みを、この作り直しで測る(途中の作り直しの分は捨てる)。</summary>
    public void GalleryRebuildStarted()
    {
        lock (_gate)
        {
            _galleryFrom = _pendingImport;
            _pendingImport = null;
        }
    }

    /// <summary>いまの作り直しで、組み込みのタイルがすべて作り終わった。測っている取り込みがあれば gallery を書く。</summary>
    public void GalleryBuiltInsDone()
    {
        lock (_gate)
        {
            if (_galleryFrom is not { } from) return;
            _galleryFrom = null;
            WriteAt(NowMs, "gallery", Ms(_clock.Monotonic - from));
        }
    }

    // ================================================================ drag_frame

    /// <summary>スライダーのつまみ・レイヤーの枠とハンドルを押した(true)・離した(false)。押すたびに数え直す。</summary>
    public void SetDragging(bool pressed)
    {
        lock (_gate)
        {
            _dragging = pressed;
            _lastDisplay = null;
        }
    }

    /// <summary>表示の更新(プロキシの描画の結果を WriteableBitmap に載せ終えた)。押している間の 2 回目以降で、1 つ前の更新からの ms を書く。</summary>
    public void DisplayUpdated()
    {
        lock (_gate)
        {
            if (!_dragging) return;
            var now = NowMs;
            if (_lastDisplay is { } last) WriteAt(now, "drag_frame", now - last);
            _lastDisplay = now;
        }
    }

    // ================================================================ export

    /// <summary>書き出しを始めた(ログの export_start を書いた)。export_begin を書き、その t を返す(書かなかったら -1)。</summary>
    public long ExportBegin() => Write("export_begin", 0);

    /// <summary>書き出しが完成・取り消し・失敗した。begin は ExportBegin の戻り値(-1 なら何もしない)。</summary>
    public void ExportEnd(long begin)
    {
        if (begin < 0) return;
        lock (_gate)
        {
            var now = NowMs;
            WriteAt(now, "export_end", now - begin);
        }
    }

    public void Dispose()
    {
        _sampleTimer.Stop();
        _sampleTimer.Dispose();
        _uiTimer.Stop();
        _uiTimer.Dispose();
        lock (_gate)
        {
            var file = _file;
            _file = null;
            try
            {
                file?.Dispose();
            }
            catch (Exception e) when (IsIoFailure(e))
            {
            }
        }
    }
}
