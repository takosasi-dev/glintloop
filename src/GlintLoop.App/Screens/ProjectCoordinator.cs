// .glint の保存と開くの画面の経路(FR-89・FR-6・FR-13・E-20・E-34・E-44・E-45)。読み込みと書き込みは UI スレッドの外で行う(INV-5)。
// 開くは先に読み込みと検査を終え、開けるファイルだけで置き換えの確認を出す。失敗は通知だけで、編集中の内容を変えない。
// 取り込みの門(E-20)は呼び出し側(ImportCoordinator・ScreensModule.RequestNewAsync)が持つ。
using System.Globalization;
using System.IO;
using GlintLoop.App.Platform;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.Codec;
using GlintLoop.Codec.Project;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Screens;

public sealed class ProjectCoordinator(AppShell shell, ImportCoordinator imports)
{
    private bool _confirming;
    private bool _saving;

    /// <summary>置き換えの確認(「保存して置き換え」の保存を含む)の途中か。</summary>
    public bool IsConfirming => _confirming;

    /// <summary>プロジェクトの書き込みの途中か。</summary>
    public bool IsSaving => _saving;

    /// <summary>プロジェクトを保存する(Ctrl+Shift+S)。保存できたら true。</summary>
    public Task<bool> SaveAsync() => shell.Session.HasDocument && !_saving && !shell.Dialogs.IsOpen ? SaveCoreAsync() : Task.FromResult(false);

    private async Task<bool> SaveCoreAsync()
    {
        var session = shell.Session;
        var services = shell.Services;
        var filters = new[] { new FileDialogFilter(ScreenTexts.Get("save.filter.project"), "*" + GlintProjectFile.FileExtension) };
        var path = services.FileDialogs.SaveFile(InitialSaveDir(), DefaultFileName(), filters);
        if (path is null) return false;

        // 書き込みの間も編集できるので、文書と画素はここで写し取る
        var doc = session.Document;
        var json = session.Json;
        if (doc is null) return false;
        _saving = true;
        try
        {
            var images = new Dictionary<string, ImportedImage>(StringComparer.Ordinal);
            foreach (var a in doc.Assets) images[a.Sha256] = shell.ImportTarget.Images[a.Sha256];
            // 取り込んだ画像ファイルへは書かない(INV-2・E-34)
            if (shell.ImportTarget.IsImportedPath(path)) throw new IOException("取り込んだ画像ファイルと同じパス");
            await Task.Run(() => GlintProjectFile.Save(path, doc, images, services.Clock, services.Version, services.FileSystem));
        }
        catch (Exception)
        {
            // 理由を問わず、書けなかったときは同じ扱いにする(E-34)。呼び出し側は結果の Task を待たないことがあるので、ここで受ける
            shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.SaveFailed));
            shell.Log.Write(LogEvents.ExportFail(LogExportFailReason.WriteError));
            return false;
        }
        finally
        {
            _saving = false;
        }
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) shell.Settings.LastSaveDir = dir;
        // 書き込みの間に編集されたときは、書いた内容と違うので未渡しのまま
        if (session.Json == json) session.MarkDelivered();
        shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.ProjectSaved));
        return true;
    }

    // 既定のフォルダと名前は FR-84・FR-85 の保存と同じ形にする(仮の決め。仕様は .glint の既定を決めていない)
    private string InitialSaveDir()
    {
        var fs = shell.Services.FileSystem;
        var settings = shell.Settings;
        if (settings.DefaultSaveDir.Length > 0 && fs.DirectoryExists(settings.DefaultSaveDir)) return settings.DefaultSaveDir;
        if (settings.LastSaveDir.Length > 0 && fs.DirectoryExists(settings.LastSaveDir)) return settings.LastSaveDir;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private string DefaultFileName()
    {
        var clock = shell.Services.Clock;
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalZone);
        return "glint-" + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + GlintProjectFile.FileExtension;
    }

    /// <summary>
    /// 置き換えの確認(FR-13)。未渡しでなければ確認せず true。「保存して置き換え」は保存に成功したときだけ、「保存せず置き換え」はそのまま true。
    /// 呼び出し側が取り込みの門の中で呼び、true のときに文書を置き換える。
    /// </summary>
    public async Task<bool> ConfirmReplaceAsync()
    {
        var session = shell.Session;
        if (!session.NeedsReplaceConfirmation) return true;
        _confirming = true;
        try
        {
            return await ReplaceConfirmView.AskAsync(shell.Dialogs, session.ReplaceButtons) switch
            {
                ReplaceChoice.ReplaceWithoutSaving => true,
                ReplaceChoice.SaveAndReplace => await SaveCoreAsync(),
                _ => false,
            };
        }
        finally
        {
            _confirming = false;
        }
    }

    /// <summary>
    /// path の .glint を開く(FR-6)。開けないときは通知を出して編集中の内容を変えない(E-44・E-45)。確認でやめたときも変えない。
    /// 取り込みの門の中で呼ぶ。文書を置き換えたら true。
    /// </summary>
    public async Task<bool> OpenAsync(string path)
    {
        var services = shell.Services;
        var catalog = shell.Session.Catalog;
        var log = shell.Log;
        var maxSide = imports.Importer.MaxTextureSide;
        GlintProject project;
        try
        {
            project = await Task.Run(() => GlintProjectFile.Open(path, catalog, log, maxSide, services.FileSystem));
        }
        catch (GlintProjectOpenException e)
        {
            shell.NoticeBar.Show(NoticeTexts.Notice(e.NoticeKey));
            return false;
        }
        if (!await ConfirmReplaceAsync()) return false;
        shell.ImportTarget.OpenDocument(project.Document, project.Images);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) shell.Settings.LastOpenDir = dir;
        foreach (var n in project.Notices) shell.NoticeBar.Show(n);
        return true;
    }
}
