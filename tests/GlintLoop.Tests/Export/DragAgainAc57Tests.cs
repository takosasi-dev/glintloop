// AC-57 の (1) の 2026-11-05 の注記: 上限超過のドラッグは、確認のあいだにマウスのボタンが離れるので、「渡す」のあとに案内を出し、同じファイルのもう一度のドラッグで確認なしに渡す。
// 偽のドラッグ実行器と偽の中身(上限に収まらない結果)で、案内のキーと種別・実行器の呼び出しの回数・もう一度聞く場合を調べる。
using System.Windows;
using System.Windows.Controls.Primitives;
using GlintLoop.App.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Exports;

public sealed class DragAgainAc57Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static HandoffRig Over()
    {
        var r = HandoffRig.Start(CopyModeSetting.File, pipeline: new HandoffPipeline { SizeFail = true });
        r.Export();
        return r;
    }

    private static void Answer(HandoffRig r, Func<ExportConfirmOverView, System.Windows.Controls.Button> button)
    {
        var v = r.WaitConfirm();
        r.App.Ui(() => button(v).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        Check.True(UiHost.WaitUntil(() => !r.App.Shell.Dialogs.IsOpen && r.Handoff.Busy is null, Limit), "確認が閉じない");
        UiHost.Idle();
    }

    private static void WaitRuns(HandoffRig r, int count) =>
        Check.True(UiHost.WaitUntil(() => r.App.DragRunner.Runs.Count >= count && r.Handoff.Busy is null, Limit), "ドラッグの実行器が呼ばれない");

    private static bool NoDialog(HandoffRig r) => r.App.Ui(() => !r.App.Shell.Dialogs.IsOpen);

    [Test]
    public void AC57_1_ドラッグの確認で渡すを選ぶと案内を出すだけでもう一度のドラッグで確認なしに渡す()
    {
        using var r = Over();
        r.Drag();
        Answer(r, v => v.Pass);
        Check.Equal(0, r.App.DragRunner.Runs.Count, "「渡す」の直後は実行器を呼ばない(ボタンが離れている)");
        Check.Equal(0, r.Logs("export_handoff").Count, "まだ渡していない");
        Check.Equal(0, r.HandoffFiles().Length, "実体をまだ作らない");
        var shown = r.App.Ui(() => r.App.Shell.NoticeBar.Current);
        Check.Equal(NoticeKeys.ExportDragAgain, shown?.Key, "案内のキー");
        Check.Equal(NoticeLevel.Info, shown?.Level, "案内は info");
        Check.Equal("もう一度ドラッグすると渡します", r.Notice(), "案内の文");

        r.Drag();
        WaitRuns(r, 1);
        Check.True(NoDialog(r), "もう一度のドラッグでは確認を出さない");
        var log = r.Logs("export_handoff").Single();
        Check.Equal("drag", log.GetProperty("kind").GetString(), "kind");
        Check.Equal(HandoffRig.GifBytes, log.GetProperty("bytes").GetInt32(), "最後の段の結果が渡る");
    }

    [Test]
    public void AC57_1_やめるでは案内を出さず次のドラッグでまた確認する()
    {
        using var r = Over();
        r.Drag();
        Answer(r, v => v.Stop);
        Check.Equal("", r.Notice(), "案内を出さない");
        Check.Equal(0, r.App.DragRunner.Runs.Count, "実行器を呼ばない");
        r.Drag();
        r.WaitConfirm();
        Check.Equal(0, r.App.DragRunner.Runs.Count, "また確認する");
    }

    [Test]
    public void AC57_1_編集でファイルが変わるとまた確認する()
    {
        using var r = Over();
        r.Drag();
        Answer(r, v => v.Pass);
        r.App.Ui(() => r.Session.SetLength10(21));
        r.Export();
        r.Drag();
        r.WaitConfirm();
        Check.Equal(0, r.App.DragRunner.Runs.Count, "別のファイルは確認なしに渡さない");
    }

    [Test]
    public void AC57_1_もう一度のドラッグを取り消しても次は確認なしで渡したあとはまた確認する()
    {
        using var r = Over();
        r.Drag();
        Answer(r, v => v.Pass);
        r.App.DragRunner.Result = DragDropEffects.None;
        r.Drag();
        WaitRuns(r, 1);
        Check.True(NoDialog(r), "1 回目のもう一度");
        Check.Equal(0, r.Logs("export_handoff").Count, "取り消しではログを書かない(E-38)");
        r.App.DragRunner.Result = DragDropEffects.Copy;
        r.Drag();
        WaitRuns(r, 2);
        Check.True(NoDialog(r), "取り消しのあとも確認しない");
        Check.Equal(1, r.Logs("export_handoff").Count, "渡した");
        r.Drag();
        r.WaitConfirm();
        Check.Equal(2, r.App.DragRunner.Runs.Count, "渡したあとの新しいドラッグはまた確認する");
    }

    [Test]
    public void AC57_4_上限を超えていないファイルは確認も案内もなく最初のドラッグで渡す()
    {
        using var r = HandoffRig.Start(CopyModeSetting.File);
        r.Export();
        r.Drag();
        WaitRuns(r, 1);
        Check.Equal("", r.Notice(), "案内を出さない");
        Check.True(NoDialog(r), "確認を出さない");
    }
}
