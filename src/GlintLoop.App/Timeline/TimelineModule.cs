// 担当 timeline の部品をつなぐ(IShellModule、Order 650): タイムライン(TimelineView)を TimelineHost に入れ、時間の設定の操作(EditorSession.Time.cs)と再生の制御(PlaybackController)につなぐ。
// shell.Frames に呼ぶのは Set(k) と SetPlaying(bool) だけ(N は AppShell が入れる)。キャンバスの幅が 616 DIP 未満のときは MainWindow.SetTimelineWrapped で 2 行にする。
// ショートカット: Space(再生・停止)と、スクラブバーの左右・Home・End(フレームの移動。再生を止める)を Router に登録する(§9.12 の 21〜23 行)。
using System.Windows;
using System.Windows.Threading;
using GlintLoop.App.Input;
using GlintLoop.App.Startup;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Timelines;

public sealed class TimelineModule : IShellModule, IDisposable
{
    private AppShell? _shell;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    public int Order => 650;

    public TimelineView View { get; private set; } = null!;

    public PlaybackController Playback { get; private set; } = null!;

    public void Attach(AppShell shell)
    {
        _shell = shell;
        _dispatcher = shell.Window.Dispatcher;
        var session = shell.Session;
        var frames = shell.Frames;

        View = new TimelineView();
        shell.Window.TimelineHost.Content = View;
        Playback = new PlaybackController(frames, shell.Services.Clock, shell.Services.Timers, () => session.Time);

        View.PlayClicked += () =>
        {
            if (session.HasDocument) Playback.Toggle();
        };
        View.ScrubStarted += Playback.Stop;
        View.ScrubMoved += k =>
        {
            Playback.Stop();
            frames.Set(k);
        };
        View.LengthCommitted += text =>
        {
            if (session.HasDocument && TimeInput.ParseLength10(text) is { } l10) session.SetLength10(l10);
            View.ShowTime(session.Time);
        };
        View.FpsChosen += f =>
        {
            if (session.HasDocument) session.SetFps(f);
            View.ShowTime(session.Time);
        };
        View.LoopChosen += m =>
        {
            if (session.HasDocument) session.SetLoop(m);
            View.ShowTime(session.Time);
        };
        View.HoldCommitted += text =>
        {
            if (session.HasDocument && TimeInput.ParseHoldMs(text) is { } ms) session.SetHoldMs(ms);
            View.ShowTime(session.Time);
        };
        View.ReseedClicked += () =>
        {
            if (session.HasDocument) session.RerollSeed();
        };

        frames.Changed += OnFramesChanged;
        session.Changed += OnSessionChanged;
        shell.Window.TimelineHost.SizeChanged += OnHostSizeChanged;
        RegisterShortcuts(shell);
        View.ShowTime(session.Time);
        OnFramesChanged();
    }

    private void RegisterShortcuts(AppShell shell)
    {
        var r = shell.Router;
        var s = shell.Session;
        var frames = shell.Frames;
        r.Register(ShortcutAction.PlayPause, _ =>
        {
            if (!s.HasDocument) return false;
            Playback.Toggle();
            return true;
        });
        r.Register(ShortcutAction.FramePrev, _ => MoveFrame(() => frames.K - 1));
        r.Register(ShortcutAction.FrameNext, _ => MoveFrame(() => frames.K + 1));
        r.Register(ShortcutAction.FrameFirst, _ => MoveFrame(() => 0));
        r.Register(ShortcutAction.FrameLast, _ => MoveFrame(() => frames.N - 1));
    }

    /// <summary>スクラブバーのキーでフレームを移す(端で止まる。再生は止める)。</summary>
    private bool MoveFrame(Func<int> to)
    {
        var shell = _shell!;
        if (!shell.Session.HasDocument) return false;
        Playback.Stop();
        shell.Frames.Set(to());
        return true;
    }

    /// <summary>タイムラインの幅(キャンバスの幅)が 1 行の幅の合計より狭ければ 2 行に折り返す(§9.3)。</summary>
    public void UpdateWrap(double width)
    {
        if (_shell is null || width <= 0) return;
        var wrapped = width < TimelineView.OneLineWidth();
        View.SetWrapped(wrapped);
        if (_shell.Window.IsTimelineWrapped != wrapped) _shell.Window.SetTimelineWrapped(wrapped);
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateWrap(e.NewSize.Width);
    }

    private void OnFramesChanged()
    {
        if (_disposed || _shell is null) return;
        var f = _shell.Frames;
        View.ShowFrames(f.K, f.N, f.IsPlaying);
    }

    private void OnSessionChanged(SessionChange what)
    {
        if (_disposed || _dispatcher is null) return;
        if (_dispatcher.CheckAccess()) Apply(what);
        else _dispatcher.BeginInvoke(() => Apply(what));
    }

    private void Apply(SessionChange what)
    {
        if (_disposed || _shell is null) return;
        if ((what & (SessionChange.Time | SessionChange.Document)) == 0) return;
        View.ShowTime(_shell.Session.Time);
        Playback.TimeChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_shell is { } shell)
        {
            shell.Frames.Changed -= OnFramesChanged;
            shell.Session.Changed -= OnSessionChanged;
            shell.Window.TimelineHost.SizeChanged -= OnHostSizeChanged;
        }
        Playback?.Dispose();
    }
}
