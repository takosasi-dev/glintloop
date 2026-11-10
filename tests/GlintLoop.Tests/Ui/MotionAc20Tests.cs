// AC-20: UI の動きのトークン(§9.1 のモーションの表と KeySpline の 4 本)、アニメーションの縮小(FR-97)の 6 通り、動かさない操作(規則 1)を調べる。
// 表の値はこのファイルに書き写す。(5) の「0 ms でもキャンバスのループ再生は止まらない」は、再生(Phase 4)が無いので未検査。進捗リングの回転(規則 10)も確かめる。
using System.Windows;
using System.Windows.Media.Animation;
using GlintLoop.App.Theme;
using GlintLoop.Core.Settings;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class MotionAc20Tests
{
    private static readonly (string Key, double X1, double Y1, double X2, double Y2)[] Splines =
    [
        ("EaseOut", 0.23, 1, 0.32, 1),
        ("EaseInOut", 0.77, 0, 0.175, 1),
        ("EaseDrawer", 0.32, 0.72, 0, 1),
        ("EaseColor", 0.25, 0.1, 0.25, 1),
    ];

    // §9.1 の動きの表(トークン・時間 ms・イージング)
    private static readonly (MotionToken Token, int Ms, string Easing)[] Rows =
    [
        (MotionToken.PressDown, 120, "EaseOut"),
        (MotionToken.PressUp, 160, "EaseOut"),
        (MotionToken.Color, 120, "EaseColor"),
        (MotionToken.TooltipIn, 150, "EaseOut"),
        (MotionToken.TooltipOut, 100, "EaseOut"),
        (MotionToken.FlyoutIn, 180, "EaseOut"),
        (MotionToken.FlyoutOut, 120, "EaseOut"),
        (MotionToken.DialogIn, 200, "EaseDrawer"),
        (MotionToken.DialogOut, 140, "EaseOut"),
        (MotionToken.NoticeIn, 200, "EaseDrawer"),
        (MotionToken.NoticeOut, 120, "EaseOut"),
        (MotionToken.Pane, 200, "EaseInOut"),
        (MotionToken.ThumbIn, 150, "EaseOut"),
    ];

    private static object R(string key) => AppResources.LayoutTokens[key];

    [Test]
    public void AC20_1_KeySplineの4本が表と一致する()
    {
        UiHost.Invoke(() =>
        {
            foreach (var (key, x1, y1, x2, y2) in Splines)
            {
                var s = (KeySpline)R(key);
                Check.True(s.ControlPoint1 == new Point(x1, y1) && s.ControlPoint2 == new Point(x2, y2), $"{key} = {s.ControlPoint1} {s.ControlPoint2}");
            }
        });
    }

    [Test]
    public void AC20_2_13行の時間とイージングの割り当てと上限と出現の拡大率()
    {
        UiHost.Invoke(() =>
        {
            Check.Equal(13, Motion.Table.Count, "動きの表の行数");
            foreach (var (token, ms, easing) in Rows)
            {
                var spec = Motion.Spec(token);
                var d = (TimeSpan)R(spec.DurationKey);
                Check.Equal(TimeSpan.FromMilliseconds(ms), d, $"{token} の時間");
                Check.Equal(easing, spec.EasingKey, $"{token} のイージング");
                Check.True(d <= TimeSpan.FromMilliseconds(250), $"{token} が 250 ms を超える");
                if (spec.Kind is MotionKind.Appear or MotionKind.Disappear)
                    Check.True(spec.EasingKey != "EaseInOut", $"{token}(出現・消失)が ease-in-out を使う");
                if (spec.EasingKey == "EaseInOut") Check.Equal(MotionToken.Pane, token, "ease-in-out を使う行");
                if (spec.ScaleFromKey is { } k && spec.Kind == MotionKind.Appear)
                    Check.True((double)R(k) >= 0.96, $"{token} の拡大率の開始値 {(double)R(k)}");
            }
            Check.Equal(0.97, (double)R("MotionPressScale"), "押下の縮小");
            Check.Equal(0.96, (double)R("MotionAppearScale"), "出現の拡大率の開始値");
        });
    }

    [Test]
    public void AC20_3_消失は出現より短い()
    {
        UiHost.Invoke(() =>
        {
            foreach (var (inT, outT) in new[]
                     {
                         (MotionToken.TooltipIn, MotionToken.TooltipOut), (MotionToken.FlyoutIn, MotionToken.FlyoutOut),
                         (MotionToken.DialogIn, MotionToken.DialogOut), (MotionToken.NoticeIn, MotionToken.NoticeOut),
                     })
            {
                var a = (TimeSpan)R(Motion.Spec(inT).DurationKey);
                var b = (TimeSpan)R(Motion.Spec(outT).DurationKey);
                Check.True(b < a, $"{outT}({b.TotalMilliseconds} ms)が {inT}({a.TotalMilliseconds} ms)より短くない");
            }
        });
    }

    [Test]
    public void AC20_4_動かす対象は拡大率と不透明度と位置と色だけで高さはpaneの行だけ()
    {
        const MotionTarget allowed = MotionTarget.Scale | MotionTarget.Opacity | MotionTarget.Position | MotionTarget.BrushColor;
        foreach (var spec in Motion.Table)
        {
            if (spec.Token == MotionToken.Pane)
                Check.Equal(MotionTarget.Height, spec.Targets, "pane の行の対象");
            else
                Check.True((spec.Targets & ~allowed) == 0, $"{spec.Token} の対象 {spec.Targets} に許されないものがある");
        }
        Check.Equal(MotionTarget.BrushColor, Motion.Spec(MotionToken.Color).Targets, "color の行はブラシの色");
    }

    [Test]
    public void AC20_5_アニメーションの縮小の6通り()
    {
        (ReduceMotionSetting Setting, bool OsOn, bool Zero)[] cases =
        [
            (ReduceMotionSetting.System, true, false),
            (ReduceMotionSetting.System, false, true),
            (ReduceMotionSetting.On, true, true),
            (ReduceMotionSetting.On, false, true),
            (ReduceMotionSetting.Off, true, false),
            (ReduceMotionSetting.Off, false, false),
        ];
        UiHost.Invoke(() =>
        {
            foreach (var (setting, os, zero) in cases)
            {
                var reduced = Motion.IsReduced(setting, os);
                foreach (var (token, ms, _) in Rows)
                {
                    var d = Motion.Duration(R, token, reduced, MotionCause.Pointer);
                    Check.Equal(zero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms), d, $"({setting}, OS {(os ? "オン" : "オフ")}) の {token}");
                }
            }
        });
    }

    [Test]
    public void AC20_5_縮小が有効なウィンドウでは押下も通知も0msで切り替わる()
    {
        var os = new FakeOsInfo { AnimationsEnabled = false };
        using var app = TestApp.Start(os: os);
        app.Ui(() =>
        {
            var w = app.Shell.Window;
            Check.True(Motion.GetReduced(w), "OS のアニメーション効果がオフで、設定がシステムに合わせるのに、縮小が有効でない");
            var b = w.CommandBar.Button(Icons.Settings);
            var root = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(b, 0);
            Fx.AnimatePress(root, down: true);
            Check.Equal(0.97, Fx.GetPressTransform(root)!.ScaleX, "縮小が有効なときの押下の拡大率(すぐに 0.97)");
            Fx.AnimatePress(root, down: false);
            Check.Equal(1.0, Fx.GetPressTransform(root)!.ScaleX, "戻り");
            app.Shell.Settings.ReduceMotion = ReduceMotionSetting.Off;
            Check.True(!Motion.GetReduced(w), "設定をオフにしても縮小が有効のまま");
            os.AnimationsEnabled = true;
            app.Shell.Settings.ReduceMotion = ReduceMotionSetting.On;
            Check.True(Motion.GetReduced(w), "設定をオンにしても縮小が有効にならない");
        });
    }

    [Test]
    public void AC20_6_スライダーのドラッグとスクラブとタブの切替とキーボードは0ms()
    {
        UiHost.Invoke(() =>
        {
            foreach (var cause in new[] { MotionCause.SliderDrag, MotionCause.Scrub, MotionCause.TabSwitch, MotionCause.Keyboard })
            foreach (var (token, _, _) in Rows)
                Check.Equal(TimeSpan.Zero, Motion.Duration(R, token, reduced: false, cause), $"{cause} の {token}");
            Check.Equal(TimeSpan.FromMilliseconds(120), Motion.Duration(R, MotionToken.Color, reduced: false, MotionCause.Pointer), "ポインターの color");
        });
    }

    [Test]
    public void AC20_規則10_進捗リングは1周1_2秒で回り縮小が有効なときは止まる()
    {
        UiHost.Invoke(() =>
        {
            Check.Equal(TimeSpan.FromSeconds(1.2), (TimeSpan)R("MotionProgressRingTurn"), "1 周の時間");
            Check.True(Motion.Table.All(t => t.DurationKey != "MotionProgressRingTurn"), "進捗リングが動きの表(13 行)に入っている");
        });
        var os = new FakeOsInfo();
        using var app = TestApp.Start(os: os);
        app.Ui(() =>
        {
            var ring = app.Shell.Modules.OfType<GlintLoop.App.Screens.ScreensModule>().Single().Welcome.Ring;
            ring.Visibility = Visibility.Visible;
            ring.Update();
            Check.True(ring.IsSpinning, "縮小が無効なのに回らない");
            app.Shell.Settings.ReduceMotion = ReduceMotionSetting.On;
            ring.Update();
            Check.True(!ring.IsSpinning, "縮小が有効なのに回る");
            ring.Visibility = Visibility.Collapsed;
        });
    }

    [Test]
    public void AC20_規則10_表示中の進捗リングは縮小の設定が変わると止まりまた回る()
    {
        // 表示したあとは Update を呼ばない。Windows の「アニメーション効果」(偽の OS)と設定の「アニメーションの縮小」を変えるだけで追う(FR-97)
        var os = new FakeOsInfo();
        using var app = TestApp.Start(os: os);
        app.Ui(() =>
        {
            var ring = app.Shell.Modules.OfType<GlintLoop.App.Screens.ScreensModule>().Single().Welcome.Ring;
            var rotate = (System.Windows.Media.RotateTransform)((FrameworkElement)ring.Children[0]).RenderTransform;
            void Expect(bool spinning, string what)
            {
                Check.Equal(spinning, ring.IsSpinning, what);
                Check.Equal(spinning, rotate.HasAnimatedProperties, what + "(円弧の回転のアニメーション)");
            }
            Check.Equal(ReduceMotionSetting.System, app.Shell.Settings.ReduceMotion, "既定は「システムに合わせる」(テストの前提)");
            ring.Visibility = Visibility.Visible;
            Check.True(ring.IsVisible, "リングが表示されていない(テストの前提)");
            Expect(true, "表示した直後に回らない");
            os.AnimationsEnabled = false;
            Expect(false, "Windows のアニメーション効果をオフにしても止まらない");
            os.AnimationsEnabled = true;
            Expect(true, "Windows のアニメーション効果をオンに戻しても回らない");
            app.Shell.Settings.ReduceMotion = ReduceMotionSetting.On;
            Expect(false, "設定の縮小をオンにしても止まらない");
            app.Shell.Settings.ReduceMotion = ReduceMotionSetting.Off;
            Expect(true, "設定の縮小をオフにしても回らない");
            os.AnimationsEnabled = false;
            Expect(true, "設定がオフなのに Windows のアニメーション効果で止まった");
            app.Shell.Settings.ReduceMotion = ReduceMotionSetting.System;
            Expect(false, "「システムに合わせる」に戻しても止まらない");
            ring.Visibility = Visibility.Collapsed;
            Expect(false, "隠したのに回る");
        });
    }
}
