// クラッシュの処理のテストの偽物(AC-50): 呼ばれた順を 1 つに残す記録・ダイアログ・プロセスの終了・未処理例外の発生元。
// 製品のダイアログとプロセスの終了を、テストから呼ばないようにする(INV-18)。TestApp の既定もこれを使う。
using GlintLoop.App.Startup;
using GlintLoop.Core.Diagnostics;

namespace GlintLoop.Tests.Ui;

/// <summary>呼ばれた順の記録(複数のスレッドから書ける)。</summary>
public sealed class CallOrder
{
    private readonly List<string> _steps = [];

    public void Add(string step)
    {
        lock (_steps) _steps.Add(step);
    }

    public IReadOnlyList<string> Steps
    {
        get
        {
            lock (_steps) return _steps.ToArray();
        }
    }
}

/// <summary>タイトル・本文・ボタンの一覧を記録してすぐ閉じるダイアログ。</summary>
public sealed class FakeCrashDialog(CallOrder? order = null) : ICrashDialog
{
    private readonly List<(string Title, string Body, IReadOnlyList<string> Buttons)> _shown = [];

    /// <summary>表示の中で行う処理(再入を調べるときに使う)。</summary>
    public Action? OnShow { get; set; }

    /// <summary>投げる例外(null なら投げない)。</summary>
    public Exception? Throw { get; set; }

    public IReadOnlyList<(string Title, string Body, IReadOnlyList<string> Buttons)> Shown
    {
        get
        {
            lock (_shown) return _shown.ToArray();
        }
    }

    public void Show(string title, string body, IReadOnlyList<string> buttons)
    {
        order?.Add("dialog");
        lock (_shown) _shown.Add((title, body, buttons));
        OnShow?.Invoke();
        if (Throw is { } e) throw e;
    }
}

/// <summary>終了の依頼を記録するだけの偽物。</summary>
public sealed class FakeProcessExit(CallOrder? order = null) : IProcessExit
{
    private readonly List<int> _codes = [];

    public IReadOnlyList<int> Codes
    {
        get
        {
            lock (_codes) return _codes.ToArray();
        }
    }

    public void Exit(int code)
    {
        order?.Add("exit:" + code);
        lock (_codes) _codes.Add(code);
    }
}

/// <summary>未処理例外の発生元の偽物。登録された個数を数え、テストが発生を起こす。</summary>
public sealed class FakeExceptionSource : IUnhandledExceptionSource
{
    public int UiThreadHandlers { get; private set; }

    public int AppDomainHandlers { get; private set; }

    private Action<Exception>? _ui;
    private Action<Exception>? _domain;

    public event Action<Exception>? UiThreadException
    {
        add { _ui += value; UiThreadHandlers++; }
        remove { _ui -= value; UiThreadHandlers--; }
    }

    public event Action<Exception>? AppDomainException
    {
        add { _domain += value; AppDomainHandlers++; }
        remove { _domain -= value; AppDomainHandlers--; }
    }

    public void RaiseUiThread(Exception e) => _ui?.Invoke(e);

    public void RaiseAppDomain(Exception e) => _domain?.Invoke(e);
}

/// <summary>ログの書き先の偽物。順の記録に event の名前を足してから、中の書き先へ渡す。</summary>
public sealed class RecordingLog(CallOrder order, ILogSink inner) : ILogSink
{
    /// <summary>投げる例外(null なら投げない。E-48)。</summary>
    public Exception? Throw { get; set; }

    public void Write(LogEvent e)
    {
        order.Add("log:" + e.Name);
        if (Throw is { } t) throw t;
        inner.Write(e);
    }
}
