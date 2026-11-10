// 外から渡す小さな口(修飾キー・ファイルダイアログ・クリップボード・ドラッグの実行)と、製品の実装(WPF の Keyboard・Microsoft.Win32 のダイアログ・DispatcherTimer)。
// テストは偽の実装を渡す(INV-18: 実際のキーボード・クリップボードを使わない)。クリップボードの読み取りは取り込みの IImportData に直して返す。
// クリップボードの読み書きは 1 回だけ呼び、再試行は WPF の既定(Clipboard の中の再試行)に任せる。独自の再試行は持たない(§9.10)。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Platform;

namespace GlintLoop.App.Platform;

/// <summary>修飾キーの状態(§11 Phase 3 の前書きの (c))。キーの判定はこれを使い、Keyboard.Modifiers を直接読まない。</summary>
public interface IModifierKeys
{
    ModifierKeys Current { get; }
}

public sealed class WpfModifierKeys : IModifierKeys
{
    public static WpfModifierKeys Instance { get; } = new();

    public ModifierKeys Current => Keyboard.Modifiers;
}

/// <summary>ファイルダイアログの絞り込み(表示名と、;で区切ったパターン)。</summary>
public sealed record FileDialogFilter(string Name, string Patterns);

public interface IFileDialogs
{
    /// <summary>開くダイアログ。キャンセルなら null。</summary>
    IReadOnlyList<string>? OpenFiles(string? initialDir, IReadOnlyList<FileDialogFilter> filters, bool multiselect);

    /// <summary>保存のダイアログ。キャンセルなら null。</summary>
    string? SaveFile(string? initialDir, string fileName, IReadOnlyList<FileDialogFilter> filters);

    /// <summary>フォルダの選択。キャンセルなら null。</summary>
    string? PickFolder(string? initialDir);
}

/// <summary>Microsoft.Win32 のダイアログ(owner は表示中のメインウィンドウ)。</summary>
public sealed class Win32FileDialogs(Func<Window?> owner) : IFileDialogs
{
    private static string Filter(IReadOnlyList<FileDialogFilter> filters) => string.Join("|", filters.Select(f => f.Name + "|" + f.Patterns));

    public IReadOnlyList<string>? OpenFiles(string? initialDir, IReadOnlyList<FileDialogFilter> filters, bool multiselect)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = Filter(filters), Multiselect = multiselect };
        if (!string.IsNullOrEmpty(initialDir)) d.InitialDirectory = initialDir;
        return d.ShowDialog(owner()) == true ? d.FileNames : null;
    }

    public string? SaveFile(string? initialDir, string fileName, IReadOnlyList<FileDialogFilter> filters)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = Filter(filters), FileName = fileName };
        if (!string.IsNullOrEmpty(initialDir)) d.InitialDirectory = initialDir;
        return d.ShowDialog(owner()) == true ? d.FileName : null;
    }

    public string? PickFolder(string? initialDir)
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        if (!string.IsNullOrEmpty(initialDir)) d.InitialDirectory = initialDir;
        return d.ShowDialog(owner()) == true ? d.FolderName : null;
    }
}

/// <summary>クリップボードの口(読み取りと書き込み)。</summary>
public interface IClipboardService
{
    /// <summary>今のクリップボードの中身を取り込みのデータにして返す。開けないときは ImportDataUnavailableException。</summary>
    IImportData Read();

    /// <summary>data を置く。keepAfterExit が真なら、アプリを閉じても残す。置けないときの COMException はそのまま呼び出し側へ返す。</summary>
    void Place(IDataObject data, bool keepAfterExit);
}

/// <summary>ドラッグの実行の口(FR-82)。テストは偽物を渡し、実際の DoDragDrop を動かさない。</summary>
public interface IDragRunner
{
    /// <summary>source からドラッグを始め、終わるまで待って結果の効果を返す。</summary>
    DragDropEffects Run(DependencyObject source, IDataObject data, DragDropEffects allowed);
}

/// <summary>WPF の DragDrop でドラッグを実行する。</summary>
public sealed class WpfDragRunner : IDragRunner
{
    public static WpfDragRunner Instance { get; } = new();

    // 実体を作る間や確認のダイアログの間にボタンが離れていたら、始めない(始めるとその場所へのドロップになる)
    public DragDropEffects Run(DependencyObject source, IDataObject data, DragDropEffects allowed) =>
        Mouse.LeftButton == MouseButtonState.Pressed ? DragDrop.DoDragDrop(source, data, allowed) : DragDropEffects.None;
}

/// <summary>WPF のクリップボードを読み書きする(どちらも 1 回だけ試す)。</summary>
public sealed class WpfClipboard : IClipboardService
{
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    private readonly Func<IDataObject?> _getDataObject;

    public WpfClipboard()
        : this(System.Windows.Clipboard.GetDataObject)
    {
    }

    /// <summary>読み取りの呼び出しだけを差し替える(テスト用。COMException の扱いを実際のクリップボードなしで調べる)。</summary>
    internal WpfClipboard(Func<IDataObject?> getDataObject) => _getDataObject = getDataObject;

    public IImportData Read()
    {
        try
        {
            var data = _getDataObject();
            return data is null ? new MemoryImportData() : DataObjectImportData.From(data);
        }
        catch (COMException e) when (e.HResult == ClipboardCantOpen)
        {
            throw new ImportDataUnavailableException(unchecked((uint)e.HResult));
        }
    }

    public void Place(IDataObject data, bool keepAfterExit) => System.Windows.Clipboard.SetDataObject(data, keepAfterExit);
}

/// <summary>WPF の IDataObject(クリップボードかドロップ)を、取り込みが読む形に写す。作るときに必要なバイト列を読み切る。</summary>
public static class DataObjectImportData
{
    private static readonly string[] UrlOrTextFormats = ["UniformResourceLocatorW", "UniformResourceLocator", "UnicodeText", "Text"];

    public static IImportData From(IDataObject data)
    {
        var result = new MemoryImportData();
        var formats = data.GetFormats(false) ?? [];
        foreach (var f in new[] { ImportFormatNames.Png, ImportFormatNames.DibV5, ImportFormatNames.Dib })
        {
            if (!formats.Contains(f)) continue;
            var bytes = data.GetData(f) switch
            {
                System.IO.MemoryStream ms => ms.ToArray(),
                byte[] b => b,
                _ => null,
            };
            if (bytes is not null) result.With(f, bytes);
        }
        if (formats.Contains(ImportFormatNames.FileDrop) && data.GetData(ImportFormatNames.FileDrop) is string[] files) result.WithFiles(files);
        var url = UrlOrTextFormats.FirstOrDefault(formats.Contains);
        if (url is not null) result.WithUrlOrText(url);
        return result;
    }
}

/// <summary>DispatcherTimer で鳴る 1 回のタイマー(UI スレッドで鳴る)。</summary>
public sealed class DispatcherTimerFactory(Dispatcher dispatcher) : ITimerFactory
{
    public IOneShotTimer Create() => new Timer(dispatcher);

    private sealed class Timer : IOneShotTimer
    {
        private readonly DispatcherTimer _t;

        public Timer(Dispatcher d)
        {
            _t = new DispatcherTimer(DispatcherPriority.Normal, d);
            _t.Tick += (_, _) =>
            {
                _t.Stop();
                Elapsed?.Invoke();
            };
        }

        public bool IsRunning => _t.IsEnabled;

        public event Action? Elapsed;

        public void Start(TimeSpan due)
        {
            _t.Stop();
            _t.Interval = due;
            _t.Start();
        }

        public void Stop() => _t.Stop();

        public void Dispose() => _t.Stop();
    }
}
