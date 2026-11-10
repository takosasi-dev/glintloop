// 担当 screens の部品をつなぐ(IShellModule。Order 600): ウェルカム画面・取り込みの配線(貼り付け・ドロップ・開く・サンプル・起動引数)・プロジェクトの保存・新規の置き換えの確認・設定・このソフトについて。
// ショートカットは PasteImage・Open・New・SaveProject・OpenSettings を登録する(New はここだけが登録する)。ドロップはウィンドウ全体で受け、受け入れ表示をすぐ出す(FR-3)。
// フォルダを開く処理(OpenFolder)とキャッシュの削除(ClearCache)は差し込み口で、テストは偽物に差し替える。空のキャンバスのダイアログは EmptyCanvasRequested(統合の ModuleWiring が担当 layers のものをつなぐ)。
using System.Diagnostics;
using System.Windows;
using GlintLoop.App.Input;
using GlintLoop.App.Startup;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Import;

namespace GlintLoop.App.Screens;

public sealed class ScreensModule : IShellModule
{
    private AppShell? _shell;

    public int Order => 600;

    public ImportCoordinator Imports { get; private set; } = null!;

    public WelcomeView Welcome { get; private set; } = null!;

    public DropOverlay DropOverlay { get; private set; } = null!;

    /// <summary>「空のキャンバス」のボタン(FR-44)。ダイアログは担当 layers が作り、統合の ModuleWiring がここへつなぐ。null のあいだはボタンを押しても何もしない。</summary>
    public Action? EmptyCanvasRequested { get; set; }

    /// <summary>フォルダを開く処理(ログのフォルダを開く)。既定はエクスプローラーで開く。テストは偽物に差し替える。</summary>
    public Action<string> OpenFolder { get; set; } = _ => { };

    /// <summary>キャッシュの削除(FR-105 の後半)。統合の ModuleWiring が書き出し帯のものをつなぐ。null のあいだはボタンを無効表示にする。</summary>
    public Action? ClearCache { get; set; }

    /// <summary>開いている設定の画面(無ければ null)。</summary>
    public SettingsView? Settings { get; private set; }

    public void Attach(AppShell shell)
    {
        _shell = shell;
        OpenFolder = OpenInExplorer;
        Imports = new ImportCoordinator(shell);
        Welcome = new WelcomeView(Imports);
        shell.Window.WelcomeHost.Content = Welcome;
        Welcome.PasteButton.Click += (_, _) => shell.Router.Invoke(ShortcutAction.PasteImage, Welcome.PasteButton);
        Welcome.OpenButton.Click += (_, _) => shell.Router.Invoke(ShortcutAction.Open, Welcome.OpenButton);
        Welcome.SampleButton.Click += (_, _) => _ = Imports.SampleAsync();
        Welcome.EmptyCanvasButton.Click += (_, _) =>
        {
            if (!shell.Dialogs.IsOpen && Imports.CanStart) EmptyCanvasRequested?.Invoke();
        };

        shell.Router.Register(ShortcutAction.PasteImage, inv =>
        {
            // ダイアログの表示中は、ダイアログの中の操作だけが働く(FR-2)
            if (shell.Dialogs.IsOpen) return false;
            _ = Imports.PasteAsync();
            return true;
        });
        shell.Router.Register(ShortcutAction.Open, inv =>
        {
            if (shell.Dialogs.IsOpen) return false;
            _ = Imports.OpenAsync();
            return true;
        });
        shell.Router.Register(ShortcutAction.New, inv =>
        {
            if (shell.Dialogs.IsOpen) return false;
            _ = RequestNewAsync();
            return true;
        });
        shell.Router.Register(ShortcutAction.SaveProject, inv =>
        {
            // 文書が無いとき・ダイアログの表示中・取り込みの処理中は何もしない(FR-2・E-20)
            if (!shell.Session.HasDocument || shell.Dialogs.IsOpen || Imports.IsImporting) return false;
            _ = Imports.Projects.SaveAsync();
            return true;
        });
        shell.Router.Register(ShortcutAction.OpenSettings, inv =>
        {
            if (shell.Dialogs.IsOpen) return false;
            ShowSettings();
            return true;
        });

        DropOverlay = new DropOverlay();
        // 受け入れ表示はウィンドウ全体を覆うので、編集画面の上・ダイアログの層の下に重ねる
        var root = shell.Window.RootLayer;
        root.Children.Insert(root.Children.IndexOf(shell.Window.DialogLayerHost), DropOverlay);
        var w = shell.Window;
        w.AllowDrop = true;
        w.DragEnter += (_, e) => OnDragOver(e);
        w.DragOver += (_, e) => OnDragOver(e);
        w.DragLeave += (_, e) =>
        {
            var p = e.GetPosition(root);
            if (p.X <= 0 || p.Y <= 0 || p.X >= root.ActualWidth || p.Y >= root.ActualHeight) HideDropOverlay();
        };
        w.Drop += (_, e) =>
        {
            HideDropOverlay();
            if (!IsImportData(e.Data)) return;
            e.Handled = true;
            _ = Imports.DropAsync(e.Data);
        };

    }

    /// <summary>起動引数(FR-7)を取り込む。製品の入口(Program.Main)が、ウィンドウを出したあとに呼ぶ。</summary>
    public Task<ImportOutcome?> ImportStartupArgs(IReadOnlyList<string> args) => Imports.StartupArgsAsync(args);

    /// <summary>ドラッグがウィンドウに入った・動いたとき(受け入れ表示をすぐ出す。FR-3)。テストはイベントの代わりにこれを直接呼べる。</summary>
    public void ShowDropOverlay() => DropOverlay.Show(_shell!.Session.HasDocument);

    public void HideDropOverlay() => DropOverlay.Hide();

    private void OnDragOver(DragEventArgs e)
    {
        if (e.Handled) return;
        if (!IsImportData(e.Data) || _shell!.Dialogs.IsOpen)
        {
            e.Effects = DragDropEffects.None;
            HideDropOverlay();
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            if (!DropOverlay.IsShowing) ShowDropOverlay();
        }
        e.Handled = true;
    }

    /// <summary>取り込みの対象になる外からのデータか(ファイル・画像・URL か文字列。画面の中の並べ替えのドラッグは対象にしない)。</summary>
    private static bool IsImportData(IDataObject data)
    {
        foreach (var f in new[] { ImportFormatNames.FileDrop, ImportFormatNames.Png, ImportFormatNames.DibV5, ImportFormatNames.Dib,
                     "UniformResourceLocatorW", "UniformResourceLocator", "UnicodeText", "Text" })
        {
            if (data.GetDataPresent(f, false)) return true;
        }
        return false;
    }

    /// <summary>
    /// 新規(Ctrl+N・「新規」。FR-43・FR-13)。文書が無ければ何もしない。未渡しなら置き換えの確認を出し(取り込みの門に入る。E-20)、
    /// 「保存して置き換え」で保存できたとき、または「保存せず置き換え」なら文書を閉じてウェルカム画面へ戻る。置き換えたら true。
    /// </summary>
    public async Task<bool> RequestNewAsync()
    {
        var shell = _shell!;
        var s = shell.Session;
        if (!s.HasDocument || Imports.Projects.IsConfirming || Imports.Projects.IsSaving || Imports.IsImporting || shell.Dialogs.IsOpen) return false;
        if (!s.NeedsReplaceConfirmation)
        {
            s.CloseDocument();
            return true;
        }
        using var ticket = Imports.Gate.TryEnter();
        if (ticket is null) return false;
        if (!await Imports.Projects.ConfirmReplaceAsync()) return false;
        s.CloseDocument();
        return true;
    }

    /// <summary>置き換えの確認ダイアログを出しているか。</summary>
    public bool IsConfirmingReplace => Imports.Projects.IsConfirming;

    /// <summary>設定の画面を開く(FR-90)。</summary>
    public void ShowSettings()
    {
        var shell = _shell!;
        if (shell.Dialogs.IsOpen) return;
        var view = new SettingsView(shell, this);
        Settings = view;
        _ = Track(shell.Dialogs.Show(view, "DialogSettingsWidth", "DialogSettingsHeight", MotionCause.Keyboard), view);
    }

    private async Task Track(Task<object?> shown, SettingsView view)
    {
        await shown;
        if (ReferenceEquals(Settings, view)) Settings = null;
    }

    /// <summary>「このソフトについて」を設定の上に重ねて開く(FR-103)。閉じると設定に戻る。</summary>
    public void ShowAbout()
    {
        var shell = _shell!;
        if (shell.Dialogs.Top is AboutView) return;
        shell.Dialogs.Show(new AboutView(shell.Dialogs, shell.Services.Version), "DialogAboutWidth", "DialogAboutHeight", MotionCause.Keyboard);
    }

    private void OpenInExplorer(string folder)
    {
        _shell!.Services.FileSystem.CreateDirectory(folder);
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = false });
    }
}
