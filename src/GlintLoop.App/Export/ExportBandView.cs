// 書き出し帯(§9.3: 下端・全幅・高さ 72)。左から 形式・出力オプション・サイズ・容量上限・状態・コピー・保存・ドラッグ欄。間隔は space-2、左右の余白は space-3。
// 形式・サイズ・容量上限・オプションの変更は EditorSession の出力設定の操作(Undo の 1 件)にする。容量上限の一覧は「上限なし」・組み込み・区切り・ユーザーのプリセット・区切り・「現在の設定を保存…」。
// コピー・保存・ドラッグ欄の動作は ExportHandoff が持つ。ここは表示(待機の文言・有効無効・サムネイル)とドラッグの開始の判定だけ。状態欄は 2 行で、あふれる文は「…」で省き、全文を ToolTip と UIA の名前に入れる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Controls;
using GlintLoop.App.Theme;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Export;

public sealed class ExportBandView : Grid
{
    private static readonly string[] SizeModes = ["long", "width", "height", "scale"];

    private readonly EditorSession _session;
    private bool _updating;
    private bool _sizeDirty;
    private bool _bytesDirty;
    private IReadOnlyList<UserExportPreset> _userPresets = [];
    private readonly ComboBoxItem _saveItem;
    private Point? _dragPressed;

    public ExportBandView(EditorSession session)
    {
        _session = session;
        AutomationProperties.SetName(this, BandTexts.Ui("band.bar"));
        SetResourceReference(MarginProperty, "Space3HorizontalThickness");
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < 8; i++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        // 1. 形式
        Format = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        Format.SetResourceReference(WidthProperty, "ExportFormatWidth");
        AutomationProperties.SetName(Format, BandTexts.Ui("band.format"));
        foreach (var f in ExportFormat.All)
        {
            var item = new ComboBoxItem { Content = BandTexts.FormatName(f), Tag = f };
            AutomationProperties.SetName(item, BandTexts.FormatName(f));
            Format.Items.Add(item);
        }
        Format.SelectionChanged += (_, _) =>
        {
            if (!_updating && Format.SelectedItem is ComboBoxItem { Tag: string f } && _session.HasDocument) _session.SetOutputFormat(f);
        };
        Place(Format, 0);

        // 2. 出力オプション
        Options = new Button { Content = Icons.Create(Icons.OutputOptions), VerticalAlignment = VerticalAlignment.Center };
        Options.SetResourceReference(StyleProperty, "IconButton");
        Options.SetResourceReference(WidthProperty, "ExportOptionsSize");
        Options.SetResourceReference(HeightProperty, "ExportOptionsSize");
        AutomationProperties.SetName(Options, BandTexts.Ui("band.options"));
        Options.ToolTip = BandTexts.Ui("band.options");
        Flyout = new OutputOptionsFlyout(session, Options);
        Options.Click += (_, _) =>
        {
            if (Flyout.IsOpen) Flyout.Close();
            else Flyout.Open();
        };
        Place(Options, 1);

        // 3. サイズ(指定の切替と数値)
        SizeMode = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(SizeMode, BandTexts.P("band.size.mode"));
        foreach (var m in SizeModes)
        {
            var text = BandTexts.Ui("band.size." + m);
            var item = new ComboBoxItem { Content = text, Tag = m };
            AutomationProperties.SetName(item, text);
            SizeMode.Items.Add(item);
        }
        SizeMode.SelectionChanged += (_, _) => OnSizeModeChanged();
        SizeValue = new NumberBox { Minimum = OutputSettingsJson.MinSizePx, Maximum = OutputSettingsJson.MaxSizePx, Decimals = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(SizeValue, BandTexts.Ui("band.size"));
        SizeValue.TextChanged += (_, _) =>
        {
            if (!_updating) _sizeDirty = true;
        };
        SizeValue.ValueCommitted += (_, v) =>
        {
            if (!_sizeDirty || _updating || !_session.HasDocument) return;
            _sizeDirty = false;
            _session.SetOutputSize(CurrentSizeMode, v);
        };
        var size = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        size.SetResourceReference(WidthProperty, "ExportSizeWidth");
        DockPanel.SetDock(SizeMode, Dock.Right);
        SizeMode.SetResourceReference(MarginProperty, "Space1LeftThickness");
        size.Children.Add(SizeMode);
        size.Children.Add(SizeValue);
        Place(size, 2);

        // 4. 容量上限(一覧とバイト数の直接入力)
        Limit = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(Limit, BandTexts.Ui("band.limit"));
        _saveItem = new ComboBoxItem { Content = BandTexts.P("band.limit.save_current"), Tag = SaveTag };
        AutomationProperties.SetName(_saveItem, BandTexts.P("band.limit.save_current"));
        Limit.SelectionChanged += (_, _) => OnLimitSelected();
        LimitBytes = new NumberBox
        {
            Minimum = OutputSettingsJson.MinLimitBytes,
            Maximum = OutputSettingsJson.MaxLimitBytes,
            Decimals = 0,
            Value = OutputSettingsJson.MinLimitBytes,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(LimitBytes, BandTexts.Ui("band.limit.bytes"));
        LimitBytes.TextChanged += (_, _) =>
        {
            if (!_updating) _bytesDirty = true;
        };
        LimitBytes.ValueCommitted += (_, v) =>
        {
            if (!_bytesDirty || _updating || !_session.HasDocument) return;
            _bytesDirty = false;
            _session.SetOutputLimit(new OutputLimit("bytes", null, (long)v));
        };
        var limit = new Grid { VerticalAlignment = VerticalAlignment.Center };
        limit.SetResourceReference(WidthProperty, "ExportLimitWidth");
        limit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        limit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        LimitBytes.SetResourceReference(MarginProperty, "Space1LeftThickness");
        SetColumn(LimitBytes, 1);
        limit.Children.Add(Limit);
        limit.Children.Add(LimitBytes);
        Place(limit, 3);

        // 5. 状態(2 行。「ソフトウェア描画」は 1 行目の末尾で省略しない)
        StatusLine1 = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        StatusLine1.SetResourceReference(StyleProperty, "TextBody");
        StatusLine1.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Software = new TextBlock { Text = BandTexts.Ui("band.status.software"), Visibility = Visibility.Collapsed };
        Software.SetResourceReference(StyleProperty, "TextBody");
        Software.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Software.SetResourceReference(MarginProperty, "Space2LeftThickness");
        StatusLine2 = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        StatusLine2.SetResourceReference(StyleProperty, "TextCaption");
        StatusLine2.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var line1 = new DockPanel();
        DockPanel.SetDock(Software, Dock.Right);
        line1.Children.Add(Software);
        line1.Children.Add(StatusLine1);
        Status = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Status.SetResourceReference(MinWidthProperty, "ExportStatusMinWidth");
        Status.Children.Add(line1);
        Status.Children.Add(StatusLine2);
        AutomationProperties.SetName(StatusLine1, BandTexts.Ui("band.status"));
        Place(Status, 4);

        // 6〜8. コピー・保存・ドラッグ欄(文書が無い間は無効。ドラッグ欄は完成の前も無効。INV-9)
        Copy = new Button { Content = BandTexts.Ui("band.copy"), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        Copy.SetResourceReference(StyleProperty, "PrimaryButton");
        Copy.SetResourceReference(MinWidthProperty, "ExportCopyMinWidth");
        AutomationProperties.SetName(Copy, BandTexts.Ui("band.copy"));
        Place(Copy, 5);
        Save = new Button { Content = BandTexts.Ui("band.save"), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        Save.SetResourceReference(MinWidthProperty, "ExportSaveMinWidth");
        AutomationProperties.SetName(Save, BandTexts.Ui("band.save"));
        Place(Save, 6);
        DragThumbnail = new Image { Stretch = System.Windows.Media.Stretch.Uniform };
        // 背景が無いとマウスの入力を受けない
        Drag = new Border { IsEnabled = false, VerticalAlignment = VerticalAlignment.Center, ToolTip = BandTexts.Ui("band.drag"), Background = System.Windows.Media.Brushes.Transparent, Child = DragThumbnail };
        Drag.SetResourceReference(WidthProperty, "DragAreaWidth");
        Drag.SetResourceReference(HeightProperty, "DragAreaHeight");
        Drag.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Drag.SetResourceReference(Border.BorderThicknessProperty, "StrokeThickness");
        Drag.SetResourceReference(Border.CornerRadiusProperty, "RadiusControl");
        AutomationProperties.SetName(Drag, BandTexts.Ui("band.drag"));
        Drag.MouseLeftButtonDown += (_, e) =>
        {
            DragPress(e.GetPosition(Drag));
            if (_dragPressed is not null) Drag.CaptureMouse();
        };
        Drag.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) DragRelease();
            else DragMove(e.GetPosition(Drag), SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
        };
        Drag.MouseLeftButtonUp += (_, _) => DragRelease();
        Drag.LostMouseCapture += (_, _) => _dragPressed = null;
        Place(Drag, 7);

        RebuildLimitItems();
    }

    private static readonly object SaveTag = new();

    public ComboBox Format { get; }

    public Button Options { get; }

    public OutputOptionsFlyout Flyout { get; }

    public ComboBox SizeMode { get; }

    public NumberBox SizeValue { get; }

    public ComboBox Limit { get; }

    public NumberBox LimitBytes { get; }

    public StackPanel Status { get; }

    public TextBlock StatusLine1 { get; }

    public TextBlock StatusLine2 { get; }

    /// <summary>「ソフトウェア描画」(FR-94。WARP のときだけ出す)。</summary>
    public TextBlock Software { get; }

    public Button Copy { get; }

    public Button Save { get; }

    public Border Drag { get; }

    /// <summary>ドラッグ欄の中の、完成したファイルのサムネイル(§9.3)。</summary>
    public Image DragThumbnail { get; }

    /// <summary>ドラッグ欄で、押した位置から動かし始めたとき(FR-82)。</summary>
    public event Action? DragStarted;

    /// <summary>状態欄の全文(1 行目・ソフトウェア描画・2 行目。ToolTip と UIA の名前に入れるもの)。</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>「現在の設定を保存…」を選んだとき。</summary>
    public event Action? SavePresetRequested;

    /// <summary>ユーザーのプリセットの「名前を変更」。</summary>
    public event Action<UserExportPreset>? RenamePresetRequested;

    /// <summary>ユーザーのプリセットの「削除」。</summary>
    public event Action<UserExportPreset>? DeletePresetRequested;

    private void Place(UIElement e, int column)
    {
        SetColumn(e, column);
        if (column > 0 && e is FrameworkElement fe) fe.SetResourceReference(MarginProperty, "Space2LeftThickness");
        Children.Add(e);
    }

    private string CurrentSizeMode => SizeMode.SelectedItem is ComboBoxItem { Tag: string m } ? m : "long";

    // ================================================================ 文書から値を入れる

    /// <summary>文書の出力設定を部品に入れる(UI スレッド)。</summary>
    public void Refresh()
    {
        _updating = true;
        try
        {
            // 文書が無い間(ウェルカム画面)は既定の値を無効の表示で出し、文書で決まるサイズの欄は空にする
            var doc = _session.Document;
            var o = doc?.Output ?? OutputSettings.Default;
            Format.SelectedIndex = ExportFormat.All.ToList().IndexOf(o.Format);
            Options.IsEnabled = o.Format != ExportFormat.Png;
            if (!Options.IsEnabled) Flyout.Close();
            var mode = o.Size.Mode == "auto" ? "long" : o.Size.Mode;
            SizeMode.SelectedIndex = Array.IndexOf(SizeModes, mode);
            SizeValue.Decimals = mode == "scale" ? 1 : 0;
            SizeValue.Minimum = mode == "scale" ? OutputSettingsJson.MinScale : OutputSettingsJson.MinSizePx;
            SizeValue.Maximum = mode == "scale" ? OutputSettingsJson.MaxScale : OutputSettingsJson.MaxSizePx;
            if (doc is not null) SizeValue.Value = o.Size.Mode == "auto" ? OutputSizing.LongSide(doc, _session.SourceSize) : o.Size.Value ?? 0;
            SizeValue.IsBlank = doc is null;
            _sizeDirty = false;
            SelectLimit(o.Limit);
            // バイト数の欄は直接入力のときだけ値を出す(上限なしとプリセットは空)
            if (o.Limit is { Kind: "bytes", Bytes: long b }) LimitBytes.Value = b;
            LimitBytes.IsBlank = o.Limit.Kind != "bytes";
            _bytesDirty = false;
        }
        finally
        {
            _updating = false;
        }
        Flyout.Refresh();
    }

    /// <summary>状態欄を入れる。software は WARP のとき。</summary>
    public void SetStatus(string line1, string line2, bool software)
    {
        StatusLine1.Text = line1;
        StatusLine2.Text = line2;
        StatusLine2.Visibility = line2.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Software.Visibility = software ? Visibility.Visible : Visibility.Collapsed;
        var parts = new List<string> { line1 };
        if (software) parts.Add(Software.Text);
        if (line2.Length > 0) parts.Add(line2);
        StatusText = string.Join(" ", parts);
        Status.ToolTip = StatusText;
        AutomationProperties.SetName(StatusLine1, StatusText);
    }

    // ================================================================ コピー・保存・ドラッグ欄

    /// <summary>有効無効と待機の文言を入れる(FR-83・INV-9)。ドラッグ欄が無効の間は状態欄の文を理由とする(§9.3)。</summary>
    public void SetHandoff(bool hasDocument, bool copyWaiting, bool saveWaiting, bool dragReady)
    {
        Copy.IsEnabled = hasDocument;
        Save.IsEnabled = hasDocument;
        SetLabel(Copy, copyWaiting ? "band.copy.waiting" : "band.copy");
        SetLabel(Save, saveWaiting ? "band.save.waiting" : "band.save");
        Drag.IsEnabled = hasDocument && dragReady;
        if (!Drag.IsEnabled) DragRelease();
    }

    private static void SetLabel(Button button, string key)
    {
        var text = BandTexts.Ui(key);
        if (button.Content as string == text) return;
        button.Content = text;
        AutomationProperties.SetName(button, text);
    }

    /// <summary>押した位置からの移動量が、どちらかの軸でしきい値を超えたか(FR-82。ちょうどしきい値では始まらない)。</summary>
    public static bool DragThresholdExceeded(Point pressed, Point current, double minHorizontal, double minVertical) =>
        Math.Abs(current.X - pressed.X) > minHorizontal || Math.Abs(current.Y - pressed.Y) > minVertical;

    /// <summary>ドラッグ欄で左ボタンを押した(無効の間は何もしない)。</summary>
    public void DragPress(Point position)
    {
        if (Drag.IsEnabled) _dragPressed = position;
    }

    /// <summary>押したまま動かした。しきい値を超えたらドラッグを始める。</summary>
    public void DragMove(Point position, double minHorizontal, double minVertical)
    {
        if (_dragPressed is not { } pressed || !DragThresholdExceeded(pressed, position, minHorizontal, minVertical)) return;
        DragRelease();
        DragStarted?.Invoke();
    }

    /// <summary>左ボタンを離した。</summary>
    public void DragRelease()
    {
        _dragPressed = null;
        if (Drag.IsMouseCaptured) Drag.ReleaseMouseCapture();
    }

    // ================================================================ サイズ

    private void OnSizeModeChanged()
    {
        if (_updating || _session.Document is not { } doc) return;
        var mode = CurrentSizeMode;
        // 切り替えた指定で、いまの出力の大きさと同じになる値を入れる
        var native = _session.CompositeNative();
        var lout = OutputSizing.LongSide(doc, _session.SourceSize);
        var max = Math.Max(native.W, native.H);
        double value = mode switch
        {
            "width" => Math.Round(lout * native.W / max, MidpointRounding.AwayFromZero),
            "height" => Math.Round(lout * native.H / max, MidpointRounding.AwayFromZero),
            "scale" => Math.Round(lout / max * 100, 1, MidpointRounding.AwayFromZero),
            _ => lout,
        };
        _session.SetOutputSize(mode, value);
    }

    // ================================================================ 容量上限

    /// <summary>ユーザーの書き出しプリセット(名前の順)を一覧に入れ直す。</summary>
    public void SetUserPresets(IReadOnlyList<UserExportPreset> presets)
    {
        _userPresets = presets;
        RebuildLimitItems();
    }

    private void RebuildLimitItems()
    {
        _updating = true;
        try
        {
            Limit.Items.Clear();
            foreach (var p in BandLimitPresets.Builtin)
            {
                var item = new ComboBoxItem { Content = p.Name, Tag = p };
                AutomationProperties.SetName(item, p.Name);
                Limit.Items.Add(item);
            }
            Limit.Items.Add(SeparatorItem());
            foreach (var u in _userPresets)
            {
                var item = new ComboBoxItem { Content = u.Name, Tag = u };
                AutomationProperties.SetName(item, u.Name);
                item.ContextMenu = PresetMenu(u);
                Limit.Items.Add(item);
            }
            Limit.Items.Add(SeparatorItem());
            Limit.Items.Add(_saveItem);
            if (_session.Document is { } doc) SelectLimit(doc.Output.Limit);
        }
        finally
        {
            _updating = false;
        }
    }

    private static ComboBoxItem SeparatorItem() => new() { Content = new Separator(), IsEnabled = false, Focusable = false, IsTabStop = false };

    private ContextMenu PresetMenu(UserExportPreset preset)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = BandTexts.P("band.limit.rename") };
        AutomationProperties.SetName(rename, BandTexts.P("band.limit.rename"));
        rename.Click += (_, _) => RenamePresetRequested?.Invoke(preset);
        var delete = new MenuItem { Header = BandTexts.P("band.limit.delete") };
        AutomationProperties.SetName(delete, BandTexts.P("band.limit.delete"));
        delete.Click += (_, _) => DeletePresetRequested?.Invoke(preset);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>ユーザーのプリセットの項目(テストで右クリックの代わりに使う)。</summary>
    public ComboBoxItem? UserPresetItem(string name) =>
        Limit.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is UserExportPreset u && u.Name == name);

    private void SelectLimit(OutputLimit limit)
    {
        var was = _updating;
        _updating = true;
        try
        {
            var key = limit.Kind switch { "none" => "none", "preset" => limit.Key, _ => null };
            var index = -1;
            for (var i = 0; i < Limit.Items.Count; i++)
            {
                if (Limit.Items[i] is ComboBoxItem { Tag: LimitPresetItem p } && p.Key == key)
                {
                    index = i;
                    break;
                }
            }
            Limit.SelectedIndex = index;
        }
        finally
        {
            _updating = was;
        }
    }

    private void OnLimitSelected()
    {
        if (_updating || _session.Document is not { } doc || Limit.SelectedItem is not ComboBoxItem item) return;
        switch (item.Tag)
        {
            case LimitPresetItem p:
                _session.ApplyLimitPreset(p.Key, p.Format, p.LongSide, p.Loop);
                break;
            case UserExportPreset u:
                _session.ApplyOutputPreset(u.Output);
                break;
            default:
                if (ReferenceEquals(item.Tag, SaveTag)) SavePresetRequested?.Invoke();
                break;
        }
        // 一覧の表示は文書の容量上限に合わせ直す(ユーザーのプリセットと「現在の設定を保存…」は選んだままにしない)
        if (_session.Document is { } now) SelectLimit(now.Output.Limit);
        else SelectLimit(doc.Output.Limit);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // 帯の中の Esc はフライアウトを閉じるだけ(開いていないときは何もしない)
        if (e.Key == Key.Escape && Flyout.IsOpen)
        {
            Flyout.Close();
            e.Handled = true;
        }
    }
}
