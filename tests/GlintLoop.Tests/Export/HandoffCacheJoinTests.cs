// 渡す操作とキャッシュの管理の合流の確かめ: AC-44 の (5)(ドラッグ中の実体を本物の Trim が消さない)、AC-45 の (7)(コピー・ドラッグ・保存で確認しない)、AC-45 の (8)(コピーと保存で lastUsed が偽の時計の時刻になる)。
// 担当 handoff と担当 cache の別々のテストでは、相手の中身が空だったので確かめられなかった部分だけを、統合のアプリで調べる。
using System.IO;
using GlintLoop.Core.Export;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class HandoffCacheJoinTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>完了の確認(Post で 1 拍遅れて並ぶ)が済むまで待つ。上限を変える前や、回数の基準を取る前に呼ぶ。</summary>
    private static void WaitCacheWork(HandoffRig r)
    {
        UiHost.Idle();
        Check.True(r.App.Ui(() => r.Band.CacheWork).Wait(Limit), "キャッシュの確認が終わらない");
        UiHost.Idle();
    }

    private static void CopyAndWait(HandoffRig r)
    {
        var n = r.App.Ui(() => r.App.Clipboard.PlaceCalls);
        r.Copy();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == n + 1 && r.Handoff.Busy is null, Limit), "コピーされない");
        r.Settle("コピー");
    }

    private static void SaveAndWait(HandoffRig r, string path)
    {
        r.App.Ui(() => r.App.FileDialogs.SaveAnswers.Enqueue(path));
        r.Save();
        Check.True(UiHost.WaitUntil(() => File.Exists(path) && r.Handoff.Busy is null, Limit), "保存されない");
        r.Settle("保存");
    }

    private static void DragAndWait(HandoffRig r)
    {
        var n = r.App.DragRunner.Runs.Count;
        r.Drag();
        Check.True(UiHost.WaitUntil(() => r.App.DragRunner.Runs.Count == n + 1 && r.Handoff.Busy is null, Limit), "ドラッグの実行器が呼ばれない");
        r.Settle("ドラッグ");
    }

    // ---------------------------------------------------------------- AC-44 の (5)

    [Test]
    public void AC44_5_本物のTrimはドラッグ中の実体だけを残しほかを消して終わると守りが外れる()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        WaitCacheWork(r);
        var entry = r.App.Ui(() => r.Band.Auto.Entry!);
        Check.True(File.Exists(entry.Path), "前提: 完成したキャッシュがある");
        Check.Equal(0, r.Logs("cache_trim").Count, "前提: まだ何も消していない");

        r.App.Ui(() => r.Band.CacheManager.LimitBytesSource = () => 1);
        string[]? during = null;
        var entryDuring = true;
        r.App.DragRunner.During = () =>
        {
            r.Band.CacheManager.Trim();
            during = r.HandoffFiles();
            entryDuring = File.Exists(entry.Path);
        };
        DragAndWait(r);

        Check.SequenceEqual(["glint-20261014-203805.gif"], during ?? [], "ドラッグ中の実体は上限を超えていても残る");
        Check.True(!entryDuring, "上限 1 バイトなので、書き出しのキャッシュは消える(Trim が本当に働いた)");
        var log = r.Logs("cache_trim").Single();
        Check.Equal(2, log.GetProperty("removed").GetInt32(), "消したのは書き出しのキャッシュの 2 ファイルだけ");

        // 同じ条件でも、ドラッグが終わって守りが外れれば消える(残ったのが守りのおかげであることの確かめ)
        r.Band.CacheManager.Trim();
        Check.Equal(0, r.HandoffFiles().Length, "ドラッグのあとの確認で実体が消える");
    }

    // ---------------------------------------------------------------- AC-45 の (7)

    [Test]
    public void AC45_7_コピーとドラッグと保存では載せ方によらず確認しない()
    {
        foreach (var mode in new[] { CopyModeSetting.File, CopyModeSetting.Image, CopyModeSetting.Both })
        {
            using var r = HandoffRig.Start(mode);
            r.Export();
            WaitCacheWork(r);
            var manager = r.Band.CacheManager;
            Check.Equal(2, manager.TrimCalls, mode + ": 基準は起動 1 回と書き出しの完了 1 回");

            CopyAndWait(r);
            WaitCacheWork(r);
            Check.Equal(2, manager.TrimCalls, mode + ": コピーで増えない(静止画の完成でも増えない)");
            DragAndWait(r);
            WaitCacheWork(r);
            Check.Equal(2, manager.TrimCalls, mode + ": ドラッグで増えない");
            SaveAndWait(r, Path.Combine(r.Dir.Path, "out.gif"));
            WaitCacheWork(r);
            Check.Equal(2, manager.TrimCalls, mode + ": 保存で増えない");
            // 同じファイルをもう一度渡しても増えない
            CopyAndWait(r);
            WaitCacheWork(r);
            Check.Equal(2, manager.TrimCalls, mode + ": 2 回目のコピーで増えない");
        }
    }

    [Test]
    public void AC45_7_完成を待っていたコピーでは確認は完了の1回だけ()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        WaitCacheWork(r);
        Check.Equal(1, r.Band.CacheManager.TrimCalls, "基準は起動 1 回");
        r.Copy();
        Check.True(r.App.Ui(() => r.Handoff.IsCopyWaiting), "待機になる");
        r.StartExport();
        Check.True(UiHost.WaitUntil(() => r.App.Clipboard.PlaceCalls == 1 && r.Handoff.Busy is null, Limit), "コピーされない");
        WaitCacheWork(r);
        Check.Equal(2, r.Band.CacheManager.TrimCalls, "完了の 1 回だけ増える");
    }

    // ---------------------------------------------------------------- AC-45 の (8)

    private static DateTimeOffset LastUsed(HandoffRig r, ExportCacheEntry entry) =>
        ExportCache.ReadMeta(File.ReadAllBytes(r.Band.Cache.MetaPath(entry.Key)))!.LastUsed;

    [Test]
    public void AC45_8_コピーと保存でjsonのlastUsedが偽の時計の時刻になる()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        var entry = r.App.Ui(() => r.Band.Auto.Entry!);

        r.Advance(5000);
        Check.True(LastUsed(r, entry) < r.App.Clock.UtcNow, "コピーの前は古い");
        CopyAndWait(r);
        Check.Equal(r.App.Clock.UtcNow, LastUsed(r, entry), "コピーの lastUsed");

        r.Advance(5000);
        Check.True(LastUsed(r, entry) < r.App.Clock.UtcNow, "保存の前は古い");
        SaveAndWait(r, Path.Combine(r.Dir.Path, "out.gif"));
        Check.Equal(r.App.Clock.UtcNow, LastUsed(r, entry), "保存の lastUsed");
    }

    [Test]
    public void AC45_8_載せる静止画のjsonもコピーで偽の時計の時刻になる()
    {
        foreach (var mode in new[] { CopyModeSetting.Image, CopyModeSetting.Both })
        {
            using var r = HandoffRig.Start(mode);
            r.Export();
            var main = r.App.Ui(() => r.Band.Auto.Entry!);
            CopyAndWait(r);
            var still = r.App.Ui(() => r.Band.Auto.StillFor(r.App.Shell.Frames.K)!.Entry);
            Check.True(still.Key != main.Key, mode + ": 前提: 静止画は別のキャッシュ");

            r.Advance(5000);
            CopyAndWait(r);
            var now = r.App.Clock.UtcNow;
            Check.Equal(now, LastUsed(r, still), mode + ": 静止画の lastUsed");
            if (mode == CopyModeSetting.Both) Check.Equal(now, LastUsed(r, main), mode + ": ファイルの lastUsed");
            else Check.True(LastUsed(r, main) < now, mode + ": 載せないファイルの lastUsed は変わらない");
        }
    }
}
