// 部品のテンプレートの中で使う、色の変化(§9.1 の color の行)と押下の縮小(press-down・press-up)の添付プロパティ。
// Fx.Fill・Fx.Stroke・Fx.Text にはブラシのリソースのキーを書き、トリガーでキーを変えると 120 ms で色が移る。テーマの差し替えではすぐ替わる。
// Fx.Press はマウスの押下だけで縮める(キーボードの操作は動かさない。規則 1)。縮小が有効なときと、キーボードで起きた変化は 0 ms。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace GlintLoop.App.Theme;

public static class Fx
{
    private enum Channel { Fill, Stroke, Text }

    // ---- 色のキー(公開) ----

    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(string), typeof(Fx), new PropertyMetadata(null, (d, e) => OnKeyChanged(d, Channel.Fill, e)));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.RegisterAttached(
        "Stroke", typeof(string), typeof(Fx), new PropertyMetadata(null, (d, e) => OnKeyChanged(d, Channel.Stroke, e)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Fx), new PropertyMetadata(null, (d, e) => OnKeyChanged(d, Channel.Text, e)));

    public static string? GetFill(DependencyObject d) => (string?)d.GetValue(FillProperty);

    public static void SetFill(DependencyObject d, string? value) => d.SetValue(FillProperty, value);

    public static string? GetStroke(DependencyObject d) => (string?)d.GetValue(StrokeProperty);

    public static void SetStroke(DependencyObject d, string? value) => d.SetValue(StrokeProperty, value);

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    // ---- キーが指すブラシ(内部。SetResourceReference でテーマの差し替えにも追従する) ----

    private static readonly DependencyProperty FillTargetProperty = DependencyProperty.RegisterAttached(
        "FillTarget", typeof(object), typeof(Fx), new PropertyMetadata(null, (d, e) => OnTargetChanged(d, Channel.Fill, e.NewValue)));

    private static readonly DependencyProperty StrokeTargetProperty = DependencyProperty.RegisterAttached(
        "StrokeTarget", typeof(object), typeof(Fx), new PropertyMetadata(null, (d, e) => OnTargetChanged(d, Channel.Stroke, e.NewValue)));

    private static readonly DependencyProperty TextTargetProperty = DependencyProperty.RegisterAttached(
        "TextTarget", typeof(object), typeof(Fx), new PropertyMetadata(null, (d, e) => OnTargetChanged(d, Channel.Text, e.NewValue)));

    /// <summary>次のブラシの変化を動かすか(キーを変えたときだけ true。テーマの差し替えでは false)。</summary>
    private static readonly DependencyProperty AnimateNextProperty = DependencyProperty.RegisterAttached(
        "AnimateNext", typeof(int), typeof(Fx), new PropertyMetadata(0));

    /// <summary>実際に塗っている、色を動かすためのブラシ(チャンネルごと)。</summary>
    private static readonly DependencyProperty LiveFillProperty = DependencyProperty.RegisterAttached("LiveFill", typeof(SolidColorBrush), typeof(Fx));
    private static readonly DependencyProperty LiveStrokeProperty = DependencyProperty.RegisterAttached("LiveStroke", typeof(SolidColorBrush), typeof(Fx));
    private static readonly DependencyProperty LiveTextProperty = DependencyProperty.RegisterAttached("LiveText", typeof(SolidColorBrush), typeof(Fx));

    private static DependencyProperty TargetProperty(Channel c) => c switch
    {
        Channel.Fill => FillTargetProperty,
        Channel.Stroke => StrokeTargetProperty,
        _ => TextTargetProperty,
    };

    private static DependencyProperty LiveProperty(Channel c) => c switch
    {
        Channel.Fill => LiveFillProperty,
        Channel.Stroke => LiveStrokeProperty,
        _ => LiveTextProperty,
    };

    private static int Bit(Channel c) => 1 << (int)c;

    private static void OnKeyChanged(DependencyObject d, Channel channel, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        var key = e.NewValue as string;
        if (fe.IsLoaded && e.OldValue is not null) fe.SetValue(AnimateNextProperty, (int)fe.GetValue(AnimateNextProperty) | Bit(channel));
        if (string.IsNullOrEmpty(key))
        {
            fe.ClearValue(TargetProperty(channel));
            OnTargetChanged(fe, channel, null);
        }
        else
        {
            fe.SetResourceReference(TargetProperty(channel), key);
        }
    }

    private static void OnTargetChanged(DependencyObject d, Channel channel, object? target)
    {
        if (d is not FrameworkElement fe) return;
        var flags = (int)fe.GetValue(AnimateNextProperty);
        var animate = (flags & Bit(channel)) != 0;
        if (animate) fe.SetValue(AnimateNextProperty, flags & ~Bit(channel));
        var color = (target as SolidColorBrush)?.Color ?? Colors.Transparent;

        var live = (SolidColorBrush?)fe.GetValue(LiveProperty(channel));
        if (live is null)
        {
            live = new SolidColorBrush(color);
            fe.SetValue(LiveProperty(channel), live);
            Assign(fe, channel, live);
            return;
        }
        var from = live.Color;
        live.BeginAnimation(SolidColorBrush.ColorProperty, null);
        live.Color = color;
        if (!animate || from == color) return;
        var duration = Motion.Duration(fe, MotionToken.Color, CurrentCause());
        if (duration <= TimeSpan.Zero) return;
        var a = new ColorAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        a.KeyFrames.Add(new DiscreteColorKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new SplineColorKeyFrame(color, KeyTime.FromTimeSpan(duration), Motion.Easing(fe, MotionToken.Color)));
        live.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }

    private static void Assign(FrameworkElement fe, Channel channel, Brush brush)
    {
        switch (channel)
        {
            case Channel.Fill:
                switch (fe)
                {
                    case Border b: b.Background = brush; break;
                    case Panel p: p.Background = brush; break;
                    case Shape s: s.Fill = brush; break;
                    case Control c: c.Background = brush; break;
                }
                break;
            case Channel.Stroke:
                switch (fe)
                {
                    case Border b: b.BorderBrush = brush; break;
                    case Shape s: s.Stroke = brush; break;
                    case Control c: c.BorderBrush = brush; break;
                }
                break;
            default:
                fe.SetValue(TextElement.ForegroundProperty, brush);
                break;
        }
    }

    /// <summary>直近の入力がキーボードなら、変化は動かさない(規則 1)。</summary>
    private static MotionCause CurrentCause() =>
        InputManager.Current.MostRecentInputDevice is KeyboardDevice ? MotionCause.Keyboard : MotionCause.Pointer;

    // ---- 押下の縮小 ----

    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPressChanged));

    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);

    public static void SetPress(DependencyObject d, bool value) => d.SetValue(PressProperty, value);

    /// <summary>押下の縮小のための ScaleTransform(要素ごと)。</summary>
    private static readonly DependencyProperty PressScaleProperty = DependencyProperty.RegisterAttached("PressScale", typeof(ScaleTransform), typeof(Fx));

    /// <summary>押下の縮小のための ScaleTransform を返す(テスト用)。Press を付けていなければ null。</summary>
    public static ScaleTransform? GetPressTransform(DependencyObject d) => (ScaleTransform?)d.GetValue(PressScaleProperty);

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            var scale = new ScaleTransform(1, 1);
            fe.SetValue(PressScaleProperty, scale);
            fe.RenderTransform = scale;
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            fe.PreviewMouseLeftButtonDown += OnPressDown;
            fe.PreviewMouseLeftButtonUp += OnPressUp;
            fe.LostMouseCapture += OnPressUp;
            fe.MouseLeave += OnPressUp;
        }
        else
        {
            fe.PreviewMouseLeftButtonDown -= OnPressDown;
            fe.PreviewMouseLeftButtonUp -= OnPressUp;
            fe.LostMouseCapture -= OnPressUp;
            fe.MouseLeave -= OnPressUp;
        }
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.IsEnabled) AnimatePress(fe, down: true);
    }

    private static void OnPressUp(object sender, EventArgs e)
    {
        if (sender is FrameworkElement fe) AnimatePress(fe, down: false);
    }

    /// <summary>押下(down)か戻り(up)の縮小を動かす。テストからも呼べる。</summary>
    public static void AnimatePress(FrameworkElement fe, bool down)
    {
        if (fe.GetValue(PressScaleProperty) is not ScaleTransform scale) return;
        var token = down ? MotionToken.PressDown : MotionToken.PressUp;
        var to = down ? (double)fe.FindResource("MotionPressScale") : 1.0;
        if (!down && scale.ScaleX == 1.0 && !scale.HasAnimatedProperties) return;
        var duration = Motion.Duration(fe, token);
        var a = Motion.DoubleTo(to, duration, Motion.Easing(fe, token));
        if (a is null)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = to;
            scale.ScaleY = to;
            return;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
