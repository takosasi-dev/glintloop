// AC-61 の組み立て: 実際の時計とタイマー・実際の一時フォルダ・WARP で製品の窓を画面の外(x=-20000)に非アクティブで出す。指定の長さ(SoakOptions)もここに置く。
// TestApp は偽の時計が固定で、偽のファイルシステムと偽のクリップボードが呼び出しをためる(1 時間のメモリの増え方に偽物の分が混ざる)ので使わない。
// 偽のクリップボードは最後に置かれた 1 件だけを持つ。クラッシュの処理は呼び出しを数える包みを通す。ログは Dispose の前に読む。
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GlintLoop.App.Platform;
using GlintLoop.App.Startup;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Platform;
using GlintLoop.Render;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Soak;

/// <summary>テストのランナーが --soak-minutes から決める長さ(分)。既定は仕様のとおり 60。</summary>
public static class SoakOptions
{
    public const int FullMinutes = 60;

    public static int Minutes { get; set; } = FullMinutes;
}

/// <summary>読み取りは Data を返し、置かれたものは最後の 1 件と回数だけを持つ偽のクリップボード(本物と同じく前の 1 件は残らない)。</summary>
internal sealed class SoakClipboard : IClipboardService
{
    public IImportData Data { get; set; } = new MemoryImportData();

    public int PlaceCalls { get; private set; }

    public IDataObject? Last { get; private set; }

    public IImportData Read() => Data;

    public void Place(IDataObject data, bool keepAfterExit)
    {
        PlaceCalls++;
        Last = data;
    }
}

/// <summary>クラッシュの処理の入口の呼び出しを数えてから本物へ渡す包み。</summary>
internal sealed class SoakCrashCounter(ICrashHandler inner) : ICrashHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Handle(Exception exception, CrashSource source)
    {
        Interlocked.Increment(ref _calls);
        inner.Handle(exception, source);
    }
}

internal sealed class SoakRig : IDisposable
{
    /// <summary>ログに 1 行も出てはいけない event(AC-61)。</summary>
    public static readonly string[] ForbiddenEvents = ["crash", "export_fail", "render_fail", "clipboard_failed", "import_fail"];

    private readonly FakeModifierKeys _modifiers = new();
    private readonly string _root;
    private readonly AppPaths _paths;
    private SoakCrashCounter? _crash;
    private bool _closed;

    public SoakRig()
    {
        _root = Directory.CreateTempSubdirectory("glintloop-soak-").FullName;
        _paths = AppPaths.UnderOneRoot(_root);
        var services = new ShellServices(_paths, SystemClock.Instance, new DispatcherTimerFactory(UiHost.Dispatcher), new FakeOsInfo(), _modifiers, new FakeFileDialogs(), Clipboard,
            GpuAdapterRequest.Warp, "0.0.0-test")
        {
            Offscreen = true,
            DragRunner = new FakeDragRunner(),
            CrashDialog = CrashDialog,
            ProcessExit = ProcessExit,
            CrashFactory = s => _crash = new SoakCrashCounter(CrashHandler.ForShell(s)),
        };
        Shell = UiHost.Invoke(() =>
        {
            var s = AppShell.Create(Application.Current, services);
            s.Show();
            return s;
        });
        UiHost.Idle();
        Hwnd = UiHost.Invoke(() => new WindowInteropHelper(Shell.Window).Handle);
    }

    public SoakClipboard Clipboard { get; } = new();

    public FakeCrashDialog CrashDialog { get; } = new();

    public FakeProcessExit ProcessExit { get; } = new();

    public AppShell Shell { get; }

    public IntPtr Hwnd { get; }

    public int CrashCalls => _crash?.Calls ?? 0;

    public T Ui<T>(Func<T> func) => UiHost.Invoke(func);

    public void Ui(Action action) => UiHost.Invoke(action);

    public T M<T>() where T : class => Ui(() => Shell.Modules.OfType<T>().Single());

    /// <summary>修飾キーを入れて、フォーカスのある要素へキーを 1 回送る。処理済みなら true。</summary>
    public bool Key(Key key, ModifierKeys mods) => Ui(() =>
    {
        _modifiers.Current = mods;
        try
        {
            return UiInput.Key(Shell.Window, key);
        }
        finally
        {
            _modifiers.Current = ModifierKeys.None;
        }
    });

    /// <summary>ログのファイルを読み直し、出てはいけない event の行をそのまま返す。</summary>
    public List<(string Event, string Line)> ForbiddenLines()
    {
        var found = new List<(string, string)>();
        if (!Directory.Exists(_paths.LogsDir)) return found; // まだ 1 行も書いていない
        foreach (var f in Directory.GetFiles(_paths.LogsDir, "*.jsonl"))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var name = doc.RootElement.GetProperty("event").GetString()!;
                if (ForbiddenEvents.Contains(name)) found.Add((name, line));
            }
        }
        return found;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        UiHost.Invoke(() =>
        {
            Shell.Window.Close();
            Shell.Dispose();
        });
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
