// 通知バー(FR-107・§9.1・§9.3): キャンバスの上端に重ねる高さ 40 の帯。同時に 1 つだけ出し、新しい通知は表示中のものを置き換える。
// info は 5 秒で消え(注入したタイマー)、warn と error は閉じるボタンを押すまで残る。面は surface-3、文字は種別の色(info は text-primary、warn は warning、error は danger)。
// 出現は notice-in(上から滑り降りる + 不透明度、200 ms・ease-drawer)、消失は notice-out(不透明度、120 ms・ease-out)。キーボードで起きたものは動かさない。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;

namespace GlintLoop.App.Shell;

public sealed class NoticeBar : Border
{
    /// <summary>info が消えるまでの時間(FR-107)。</summary>
    public static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(5);

    private readonly TextBlock _text;
    private readonly Button _close;
    private readonly TranslateTransform _slide = new();
    private readonly IOneShotTimer _timer;

    public NoticeBar(ITimerFactory timers)
    {
        SetResourceReference(HeightProperty, "NoticeBarHeight");
        SetResourceReference(BackgroundProperty, "Surface3Brush");
        SetResourceReference(CornerRadiusProperty, "RadiusCard");
        SetResourceReference(PaddingProperty, "Space3HorizontalThickness");
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;
        RenderTransform = _slide;
        AutomationProperties.SetAutomationId(this, "NoticeBar");

        _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _text.SetResourceReference(StyleProperty, "TextBody");
        _close = new Button { Content = Icons.Create(Icons.Close), VerticalAlignment = VerticalAlignment.Center };
        _close.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(_close, Icons.Close.Name);
        _close.ToolTip = Icons.Close.Name;
        _close.Click += (_, _) => Close();
        var dock = new DockPanel();
        DockPanel.SetDock(_close, Dock.Right);
        dock.Children.Add(_close);
        dock.Children.Add(_text);
        Child = dock;

        _timer = timers.Create();
        _timer.Elapsed += () => Close(MotionCause.Programmatic);
    }

    /// <summary>表示中の通知(消えていく途中と、表示していないときは null)。</summary>
    public Notice? Current { get; private set; }

    /// <summary>表示中の文(テスト用)。</summary>
    public string Text => _text.Text;

    public bool IsShowing => Current is not null;

    /// <summary>閉じるボタン(テストが UIA で押す)。</summary>
    public Button CloseButton => _close;

    /// <summary>通知を出す(表示中のものは置き換える)。</summary>
    public void Show(Notice notice, MotionCause cause = MotionCause.Programmatic)
    {
        var replacing = Current is not null;
        Current = notice;
        _text.Text = NoticeTexts.Format(notice);
        _text.SetResourceReference(TextBlock.ForegroundProperty, notice.Level switch
        {
            NoticeLevel.Warn => "WarningBrush",
            NoticeLevel.Error => "DangerBrush",
            _ => "TextPrimaryBrush",
        });
        AutomationProperties.SetName(this, _text.Text);
        _timer.Stop();
        if (notice.Level == NoticeLevel.Info) _timer.Start(InfoDuration);

        Visibility = Visibility.Visible;
        if (replacing)
        {
            StopAnimations();
            return;
        }
        var d = Motion.Duration(this, MotionToken.NoticeIn, cause);
        var ease = Motion.Easing(this, MotionToken.NoticeIn);
        var fade = Motion.DoubleTo(1, d, ease, from: 0);
        var slide = Motion.DoubleTo(0, d, ease, from: -ActualOrDesignHeight());
        StopAnimations();
        if (fade is not null) BeginAnimation(OpacityProperty, fade);
        if (slide is not null) _slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>閉じる(warn・error は閉じるボタンで、info は 5 秒で)。</summary>
    public void Close(MotionCause cause = MotionCause.Pointer)
    {
        if (Current is null) return;
        Current = null;
        _timer.Stop();
        var d = Motion.Duration(this, MotionToken.NoticeOut, cause);
        var fade = Motion.DoubleTo(0, d, Motion.Easing(this, MotionToken.NoticeOut));
        if (fade is null)
        {
            StopAnimations();
            Visibility = Visibility.Collapsed;
            return;
        }
        fade.Completed += (_, _) =>
        {
            if (Current is not null) return;
            StopAnimations();
            Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void StopAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 1;
        _slide.Y = 0;
    }

    /// <summary>UIA に見せる(AutomationId は NoticeBar、Name は表示中の文)。</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    private double ActualOrDesignHeight() => ActualHeight > 0 ? ActualHeight : (double)FindResource("NoticeBarHeight");
}
