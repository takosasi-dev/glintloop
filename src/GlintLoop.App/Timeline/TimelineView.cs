// タイムラインの部品(FR-59・§9.3): 再生・停止、スクラブバー(「k+1 / N」の表示つき)、L の入力、F の選択、ループの選択、停止時間の入力、振り直し(右端)。
// 幅は §9.3 のトークン(Timeline*Width)で、1 行の幅の合計(616 DIP)より狭いときは 2 行に折り返す(1 行目に再生とスクラブバー、2 行目に残り)。
// 部品は値を見せて、操作を外へ知らせるだけにする(文書の変更と再生の制御は TimelineModule がつなぐ)。Space が部品の操作を優先するよう、ボタンと選択にフォーカスの種類を付ける。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using GlintLoop.App.Input;
using GlintLoop.App.Theme;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Timelines;

public sealed class TimelineView : Grid
{
    private readonly Grid _main;
    private readonly StackPanel _settings;
    private readonly TextBlock _playIcon;
    private bool _updating;
    private bool _playing;

    public TimelineView()
    {
        SetResourceReference(MarginProperty, "Space3HorizontalThickness");
        VerticalAlignment = VerticalAlignment.Center;
        ShortcutScope.SetKind(this, FocusKind.Timeline);
        AutomationProperties.SetName(this, TimelineTexts.Bar);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1 行目: 再生・停止とスクラブバー
        PlayButton = new Button { VerticalAlignment = VerticalAlignment.Center };
        PlayButton.SetResourceReference(StyleProperty, "IconButton");
        PlayButton.SetResourceReference(WidthProperty, "TimelinePlayWidth");
        ShortcutScope.SetKind(PlayButton, FocusKind.Button);
        AutomationProperties.SetAutomationId(PlayButton, "timeline.play");
        _playIcon = (TextBlock)Icons.Create(Icons.Play);
        PlayButton.Content = _playIcon;
        PlayButton.Click += (_, _) => PlayClicked?.Invoke();

        Scrub = new Slider
        {
            Minimum = 0,
            Maximum = 0,
            SmallChange = 1,
            LargeChange = 1,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ShortcutScope.SetKind(Scrub, FocusKind.Scrub);
        AutomationProperties.SetName(Scrub, TimelineTexts.Scrub);
        AutomationProperties.SetAutomationId(Scrub, "timeline.scrub");
        Scrub.ValueChanged += OnScrubValueChanged;
        Scrub.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => ScrubStarted?.Invoke()));

        FrameLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        FrameLabel.SetResourceReference(StyleProperty, "TextBody");
        FrameLabel.SetResourceReference(MarginProperty, "Space2LeftThickness");
        Typography.SetNumeralAlignment(FrameLabel, FontNumeralAlignment.Tabular);
        Fx.SetText(FrameLabel, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(FrameLabel, "timeline.frame");

        var scrubArea = new Grid();
        scrubArea.SetResourceReference(MinWidthProperty, "TimelineScrubMinWidth");
        scrubArea.SetResourceReference(MarginProperty, "Space2LeftThickness");
        scrubArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrubArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(FrameLabel, 1);
        scrubArea.Children.Add(Scrub);
        scrubArea.Children.Add(FrameLabel);

        _main = new Grid();
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(scrubArea, 1);
        _main.Children.Add(PlayButton);
        _main.Children.Add(scrubArea);

        // 残り: L・F・ループ・停止時間・振り直し
        LengthBox = new TextBox();
        AutomationProperties.SetName(LengthBox, TimelineTexts.Length);
        AutomationProperties.SetAutomationId(LengthBox, "timeline.length");
        LengthBox.ToolTip = TimelineTexts.Length;
        WatchCommit(LengthBox, t => LengthCommitted?.Invoke(t));

        FpsBox = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        FpsBox.SetResourceReference(WidthProperty, "TimelineFpsWidth");
        ShortcutScope.SetKind(FpsBox, FocusKind.List);
        AutomationProperties.SetName(FpsBox, TimelineTexts.Fps);
        AutomationProperties.SetAutomationId(FpsBox, "timeline.fps");
        FpsBox.ToolTip = TimelineTexts.Fps + "(" + TimelineTexts.FpsUnit + ")";
        foreach (var f in TimeModel.FpsChoices)
        {
            var item = new ComboBoxItem { Content = f.ToString(System.Globalization.CultureInfo.InvariantCulture), Tag = f };
            FpsBox.Items.Add(item);
        }
        FpsBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && FpsBox.SelectedItem is ComboBoxItem { Tag: int f }) FpsChosen?.Invoke(f);
        };

        LoopBox = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        LoopBox.SetResourceReference(WidthProperty, "TimelineLoopWidth");
        ShortcutScope.SetKind(LoopBox, FocusKind.List);
        AutomationProperties.SetName(LoopBox, TimelineTexts.Loop);
        AutomationProperties.SetAutomationId(LoopBox, "timeline.loop");
        LoopBox.ToolTip = TimelineTexts.Loop;
        LoopBox.Items.Add(new ComboBoxItem { Content = TimelineTexts.LoopInfinite, Tag = LoopMode.Infinite });
        LoopBox.Items.Add(new ComboBoxItem { Content = TimelineTexts.LoopOnce, Tag = LoopMode.Once });
        LoopBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && LoopBox.SelectedItem is ComboBoxItem { Tag: LoopMode m }) LoopChosen?.Invoke(m);
        };

        HoldBox = new TextBox();
        AutomationProperties.SetName(HoldBox, TimelineTexts.Hold);
        AutomationProperties.SetAutomationId(HoldBox, "timeline.hold");
        HoldBox.ToolTip = TimelineTexts.Hold;
        WatchCommit(HoldBox, t => HoldCommitted?.Invoke(t));

        ReseedButton = new Button { Content = Icons.Create(Icons.Reseed), ToolTip = TimelineTexts.Reseed, VerticalAlignment = VerticalAlignment.Center };
        ReseedButton.SetResourceReference(StyleProperty, "IconButton");
        ReseedButton.SetResourceReference(WidthProperty, "TimelineReseedWidth");
        ShortcutScope.SetKind(ReseedButton, FocusKind.Button);
        AutomationProperties.SetName(ReseedButton, TimelineTexts.Reseed);
        AutomationProperties.SetAutomationId(ReseedButton, "timeline.reseed");
        ReseedButton.Click += (_, _) => ReseedClicked?.Invoke();

        _settings = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _settings.Children.Add(WithUnit(LengthBox, TimelineTexts.LengthUnit, "TimelineLengthWidth"));
        _settings.Children.Add(Spaced(FpsBox));
        _settings.Children.Add(Spaced(LoopBox));
        _settings.Children.Add(Spaced(WithUnit(HoldBox, TimelineTexts.HoldUnit, "TimelineHoldWidth")));
        _settings.Children.Add(Spaced(ReseedButton));

        Children.Add(_main);
        Children.Add(_settings);
        ApplyLayout();
        ShowTime(null);
        ShowFrames(0, 1, false);
    }

    public Button PlayButton { get; }

    public Slider Scrub { get; }

    /// <summary>スクラブバーの「k+1 / N」の表示。</summary>
    public TextBlock FrameLabel { get; }

    public TextBox LengthBox { get; }

    public ComboBox FpsBox { get; }

    public ComboBox LoopBox { get; }

    public TextBox HoldBox { get; }

    public Button ReseedButton { get; }

    /// <summary>2 行に折り返して並べているか。</summary>
    public bool IsWrapped { get; private set; }

    /// <summary>再生・停止ボタンを押した。</summary>
    public event Action? PlayClicked;

    /// <summary>スクラブバーのつまみをつかんだ(FR-60: 移動中は再生を止める)。</summary>
    public event Action? ScrubStarted;

    /// <summary>スクラブバーの操作でフレームが変わった(k は 0 から数えた番号)。</summary>
    public event Action<int>? ScrubMoved;

    /// <summary>L の欄の文字を反映する(Enter・フォーカスを失ったとき)。</summary>
    public event Action<string>? LengthCommitted;

    public event Action<int>? FpsChosen;

    public event Action<LoopMode>? LoopChosen;

    /// <summary>停止時間の欄の文字を反映する(Enter・フォーカスを失ったとき)。</summary>
    public event Action<string>? HoldCommitted;

    public event Action? ReseedClicked;

    /// <summary>1 行に並べたときの幅の合計(§9.3: 部品の幅 + 間隔 space-2 × 6 + 左右の余白 space-3 × 2。616 DIP)。これより狭いと 2 行に折り返す。</summary>
    public static double OneLineWidth()
    {
        var r = AppResources.LayoutTokens;
        double W(string key) => (double)r[key];
        return W("TimelinePlayWidth") + W("TimelineScrubMinWidth") + W("TimelineLengthWidth") + W("TimelineFpsWidth") + W("TimelineLoopWidth")
            + W("TimelineHoldWidth") + W("TimelineReseedWidth") + 6 * W("Space2") + 2 * W("Space3");
    }

    /// <summary>1 行か 2 行の並べ方にする(行の高さはウィンドウの側で変える)。</summary>
    public void SetWrapped(bool wrapped)
    {
        if (IsWrapped == wrapped) return;
        IsWrapped = wrapped;
        ApplyLayout();
    }

    /// <summary>時間の設定を欄に出す(文書が無ければ既定)。</summary>
    public void ShowTime(TimeSettings? time)
    {
        var t = time ?? TimeSettings.Default;
        _updating = true;
        try
        {
            LengthBox.Text = TimeInput.FormatLength10(t.Length10);
            HoldBox.Text = TimeInput.FormatHoldMs(t.HoldMs);
            FpsBox.SelectedItem = FpsBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int f && f == t.Fps);
            LoopBox.SelectedItem = LoopBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is LoopMode m && m == t.Loop);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>フレームの位置と再生中かを出す(スクラブバー・「k+1 / N」・再生と停止のボタン)。</summary>
    public void ShowFrames(int k, int n, bool playing)
    {
        _updating = true;
        try
        {
            // 動くフレームが無いとき(N≤1)は、範囲を 0〜1 にしてつまみを左端に置き、操作できなくする(範囲が 0 だとつまみが右端に出る)
            Scrub.Maximum = Math.Max(1, n - 1);
            Scrub.Value = Math.Clamp(k, 0, Math.Max(0, n - 1));
            Scrub.IsEnabled = n > 1;
        }
        finally
        {
            _updating = false;
        }
        FrameLabel.Text = TimelineTexts.Frame(k, n);
        AutomationProperties.SetHelpText(Scrub, FrameLabel.Text);
        if (_playing == playing && PlayButton.ToolTip is not null) return;
        _playing = playing;
        var name = playing ? TimelineTexts.Stop : TimelineTexts.Play;
        _playIcon.Text = (playing ? Icons.Stop : Icons.Play).Glyph!.Value.ToString();
        AutomationProperties.SetName(PlayButton, name);
        PlayButton.ToolTip = Shortcuts.ToolTipText(name, ShortcutAction.PlayPause);
    }

    private void OnScrubValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        ScrubMoved?.Invoke((int)Math.Round(e.NewValue));
    }

    private void ApplyLayout()
    {
        if (IsWrapped)
        {
            SetColumnSpan(_main, 2);
            SetRow(_settings, 1);
            SetColumn(_settings, 0);
            SetColumnSpan(_settings, 2);
            _settings.HorizontalAlignment = HorizontalAlignment.Left;
            _settings.SetResourceReference(MarginProperty, "Space2TopThickness");
        }
        else
        {
            SetColumnSpan(_main, 1);
            SetRow(_settings, 0);
            SetColumn(_settings, 1);
            SetColumnSpan(_settings, 1);
            _settings.HorizontalAlignment = HorizontalAlignment.Stretch;
            _settings.SetResourceReference(MarginProperty, "Space2LeftThickness");
        }
    }

    private static FrameworkElement Spaced(FrameworkElement e)
    {
        e.SetResourceReference(MarginProperty, "Space2LeftThickness");
        return e;
    }

    // 欄と単位(「秒」「ms」)を、§9.3 の幅の 1 つの枠に並べる
    private static Grid WithUnit(TextBox box, string unit, string widthKey)
    {
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
        g.SetResourceReference(WidthProperty, widthKey);
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "TextBody");
        label.SetResourceReference(MarginProperty, "Space1LeftThickness");
        Fx.SetText(label, "TextSecondaryBrush");
        SetColumn(label, 1);
        g.Children.Add(box);
        g.Children.Add(label);
        return g;
    }

    private static void WatchCommit(TextBox box, Action<string> commit)
    {
        // TextBox が Enter を先に処理済みにしても取りこぼさないよう、トンネルで受ける
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            commit(box.Text);
            e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) => commit(box.Text);
    }
}
