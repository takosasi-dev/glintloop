// 製品だけが通る実装の確かめ: Win32OsInfo(RegGetValueW・モニターの一覧・DPI を読むだけ。書かない。INV-13)と DispatcherTimerFactory(UI スレッドで 1 回だけ鳴る)。
// テストのほかの検査は偽物を使うので、ここで実物の P/Invoke と DispatcherTimer を 1 度動かす。
using System.Windows;
using GlintLoop.App.Platform;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class PlatformU0Tests
{
    [Test]
    public void U0_実際のOSの情報を例外なく読める()
    {
        UiHost.Invoke(() =>
        {
            var os = new Win32OsInfo();
            Check.Equal(Environment.OSVersion.Version.Build, os.BuildNumber, "ビルド番号");
            Check.True(os.Monitors.Count >= 1, "モニターが無い");
            Check.Equal(1, os.Monitors.Count(m => m.IsPrimary), "プライマリモニターの数");
            Check.True(os.Monitors.All(m => m.WorkArea.Width > 0 && m.WorkArea.Height > 0), "作業領域の大きさが 0");
            _ = os.AppsUseLightTheme;
            _ = os.TransparencyEnabled;
            Check.Equal(SystemParameters.HighContrast, os.HighContrast, "ハイコントラスト");
            Check.Equal(SystemParameters.ClientAreaAnimation, os.AnimationsEnabled, "アニメーション効果");
            var changed = 0;
            os.Changed += () => changed++;
            os.Refresh();
            Check.Equal(0, changed, "値が同じなのに Changed が出た");
        });
    }

    [Test]
    public void U0_DispatcherTimerはUIスレッドで1回だけ鳴る()
    {
        var fired = 0;
        var onUi = false;
        var timer = UiHost.Invoke(() =>
        {
            var t = new DispatcherTimerFactory(UiHost.Dispatcher).Create();
            t.Elapsed += () =>
            {
                fired++;
                onUi = UiHost.Dispatcher.CheckAccess();
            };
            t.Start(TimeSpan.FromMilliseconds(200));
            t.Start(TimeSpan.FromMilliseconds(50));
            return t;
        });
        Check.True(UiHost.WaitUntil(() => fired > 0, TimeSpan.FromSeconds(3)), "タイマーが鳴らない");
        Thread.Sleep(400);
        UiHost.Idle();
        Check.Equal(1, fired, "鳴った回数(Start を 2 回呼んでも 1 回)");
        Check.True(onUi, "UI スレッドで鳴っていない");
        Check.True(!UiHost.Invoke(() => timer.IsRunning), "鳴ったあとも動いている");
        UiHost.Invoke(timer.Dispose);
    }
}
