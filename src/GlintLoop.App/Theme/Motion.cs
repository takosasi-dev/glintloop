// UI の動きのトークン(仕様書 §9.1 のモーションの表 13 行)と、アニメーションの縮小(FR-97)と、動かさない操作(規則 1)の判定。
// 時間とイージングの値は Tokens.Layout.xaml にあり、ここは表の行とリソースのキーの対応だけを持つ。
// 縮小の状態は、ウィンドウに置く継承の添付プロパティ Motion.Reduced で子の部品に伝える。
using System.Windows;
using System.Windows.Media.Animation;
using GlintLoop.Core.Settings;

namespace GlintLoop.App.Theme;

public enum MotionToken { PressDown, PressUp, Color, TooltipIn, TooltipOut, FlyoutIn, FlyoutOut, DialogIn, DialogOut, NoticeIn, NoticeOut, Pane, ThumbIn }

/// <summary>動きの種類。Appear・Disappear は出現と消失(規則 3)。</summary>
public enum MotionKind { Press, Color, Appear, Disappear, Layout }

/// <summary>動かす対象(規則 7)。Height は「詳細」の折りたたみ(pane の行)だけ。</summary>
[Flags]
public enum MotionTarget { None = 0, Scale = 1, Opacity = 2, Position = 4, BrushColor = 8, Height = 16 }

/// <summary>UI の変化を起こしたもの。Pointer と Programmatic のほかは、時間を 0 ms にする(規則 1)。</summary>
public enum MotionCause { Pointer, Programmatic, Keyboard, SliderDrag, Scrub, TabSwitch }

/// <summary>表の 1 行。ScaleFromKey は拡大率の開始値のキー(拡大を伴う行だけ)。</summary>
public sealed record MotionSpec(MotionToken Token, string DurationKey, string EasingKey, MotionKind Kind, MotionTarget Targets, string? ScaleFromKey);

public static class Motion
{
    public static IReadOnlyList<MotionSpec> Table { get; } =
    [
        new(MotionToken.PressDown, "MotionPressDown", "EaseOut", MotionKind.Press, MotionTarget.Scale, "MotionPressScale"),
        new(MotionToken.PressUp, "MotionPressUp", "EaseOut", MotionKind.Press, MotionTarget.Scale, null),
        new(MotionToken.Color, "MotionColor", "EaseColor", MotionKind.Color, MotionTarget.BrushColor, null),
        new(MotionToken.TooltipIn, "MotionTooltipIn", "EaseOut", MotionKind.Appear, MotionTarget.Opacity | MotionTarget.Scale, "MotionAppearScale"),
        new(MotionToken.TooltipOut, "MotionTooltipOut", "EaseOut", MotionKind.Disappear, MotionTarget.Opacity, null),
        new(MotionToken.FlyoutIn, "MotionFlyoutIn", "EaseOut", MotionKind.Appear, MotionTarget.Opacity | MotionTarget.Scale, "MotionAppearScale"),
        new(MotionToken.FlyoutOut, "MotionFlyoutOut", "EaseOut", MotionKind.Disappear, MotionTarget.Opacity, null),
        new(MotionToken.DialogIn, "MotionDialogIn", "EaseDrawer", MotionKind.Appear, MotionTarget.Opacity | MotionTarget.Scale, "MotionAppearScale"),
        new(MotionToken.DialogOut, "MotionDialogOut", "EaseOut", MotionKind.Disappear, MotionTarget.Opacity, null),
        new(MotionToken.NoticeIn, "MotionNoticeIn", "EaseDrawer", MotionKind.Appear, MotionTarget.Opacity | MotionTarget.Position, null),
        new(MotionToken.NoticeOut, "MotionNoticeOut", "EaseOut", MotionKind.Disappear, MotionTarget.Opacity, null),
        new(MotionToken.Pane, "MotionPane", "EaseInOut", MotionKind.Layout, MotionTarget.Height, null),
        new(MotionToken.ThumbIn, "MotionThumbIn", "EaseOut", MotionKind.Appear, MotionTarget.Opacity, null),
    ];

    public static MotionSpec Spec(MotionToken token) => Table[(int)token];

    /// <summary>
    /// アニメーションの縮小が有効か(FR-97・規則 8)。設定が「オン」、または「システムに合わせる」で Windows の「アニメーション効果」がオフのとき。
    /// </summary>
    public static bool IsReduced(ReduceMotionSetting setting, bool osAnimationsEnabled) => setting switch
    {
        ReduceMotionSetting.On => true,
        ReduceMotionSetting.Off => false,
        _ => !osAnimationsEnabled,
    };

    /// <summary>キーボード・スライダーのドラッグ・スクラブ・タブの切替で起きた変化は動かさない(規則 1)。</summary>
    public static bool IsInstantCause(MotionCause cause) => cause is not (MotionCause.Pointer or MotionCause.Programmatic);

    /// <summary>
    /// 表の時間を、縮小と原因で直した値。lookup は ResourceDictionary か FrameworkElement.FindResource(テストは辞書を渡す)。
    /// </summary>
    public static TimeSpan Duration(Func<string, object> lookup, MotionToken token, bool reduced, MotionCause cause)
    {
        if (reduced || IsInstantCause(cause)) return TimeSpan.Zero;
        return (TimeSpan)lookup(Spec(token).DurationKey);
    }

    public static TimeSpan Duration(FrameworkElement scope, MotionToken token, MotionCause cause = MotionCause.Pointer) =>
        Duration(scope.FindResource, token, GetReduced(scope), cause);

    public static KeySpline Easing(FrameworkElement scope, MotionToken token) => (KeySpline)scope.FindResource(Spec(token).EasingKey);

    /// <summary>拡大率の開始値(拡大を伴わない行は 1)。</summary>
    public static double ScaleFrom(FrameworkElement scope, MotionToken token) =>
        Spec(token).ScaleFromKey is { } key ? (double)scope.FindResource(key) : 1.0;

    /// <summary>縮小が有効か。ウィンドウに置くと子へ継承する。</summary>
    public static readonly DependencyProperty ReducedProperty = DependencyProperty.RegisterAttached(
        "Reduced", typeof(bool), typeof(Motion), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetReduced(DependencyObject d) => (bool)d.GetValue(ReducedProperty);

    public static void SetReduced(DependencyObject d, bool value) => d.SetValue(ReducedProperty, value);

    /// <summary>1 本の値のアニメーション(SplineDoubleKeyFrame 1 つ)。時間が 0 のときは null(呼び出し側が値を直接入れる)。</summary>
    public static DoubleAnimationUsingKeyFrames? DoubleTo(double to, TimeSpan duration, KeySpline easing, double? from = null)
    {
        if (duration <= TimeSpan.Zero) return null;
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        if (from is { } f) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(f, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), easing));
        return a;
    }
}
