// 出力オプションのフライアウト(§9.3。幅 320 DIP): 選んでいる形式の項目だけを上から並べる。値は文書の output.options と 1 対 1(FR-74・FR-69・FR-71・FR-72)。
// GIF は色数・ディザ・マット色・差分の最適化、JPEG は品質・背景色、WebP は品質・ロスレス(オンのとき品質は無効)、APNG は色(32 ビット・パレット)・ループ(無限・1〜4 回)。
// 色の部品は別の担当のものがまだ無いので、キャンバスの背景と同じく 16 進の入力欄(#RRGGBB)で決める(仮の決め)。スライダーのドラッグは Undo の 1 件にまとめる。
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Controls;
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Export;

public sealed class OutputOptionsFlyout
{
    private readonly EditorSession _session;
    private readonly ButtonBase _anchor;
    private readonly StackPanel _body = new();
    private bool _updating;

    public OutputOptionsFlyout(EditorSession session, ButtonBase anchor)
    {
        _session = session;
        _anchor = anchor;
        var face = new Border { Child = _body };
        face.SetResourceReference(FrameworkElement.WidthProperty, "OutputFlyoutWidth");
        face.SetResourceReference(Border.BackgroundProperty, "Surface1Brush");
        face.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        face.SetResourceReference(Border.BorderThicknessProperty, "StrokeThickness");
        face.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        face.SetResourceReference(Border.PaddingProperty, "Space3Thickness");
        face.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Focusable = false,
            Child = face,
        };
        Popup.Closed += (_, _) =>
        {
            if (_anchor.IsVisible && _anchor.IsEnabled) _anchor.Focus();
        };
    }

    public Popup Popup { get; }

    /// <summary>中身(テストで部品を探すため)。</summary>
    public StackPanel Body => _body;

    public bool IsOpen => Popup.IsOpen;

    public void Open()
    {
        Rebuild();
        Popup.IsOpen = true;
        if (_body.Children.Count > 0) _body.Children[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    public void Close() => Popup.IsOpen = false;

    /// <summary>文書が変わったとき(開いていれば値を入れ直し、フォーカスのあった部品(UIA の名前と型が同じもの)にフォーカスを戻す)。</summary>
    public void Refresh()
    {
        if (!Popup.IsOpen) return;
        // スライダーをドラッグしている間は作り直さない(つまみを失わないため。離したときの通知で入れ直す)
        if (Mouse.Captured is DependencyObject captured && IsDescendant(captured)) return;
        var focused = Keyboard.FocusedElement as FrameworkElement;
        var inside = focused is not null && IsDescendant(focused);
        var name = inside ? AutomationProperties.GetName(focused!) : null;
        var type = focused?.GetType();
        Rebuild();
        if (name is null) return;
        if (FindNamed(_body, name, type!) is { } again) again.Focus();
    }

    private bool IsDescendant(DependencyObject d)
    {
        for (var p = d; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
        {
            if (ReferenceEquals(p, _body)) return true;
        }
        return false;
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string name, Type type)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            if (d is FrameworkElement fe && fe.GetType() == type && AutomationProperties.GetName(fe) == name) return fe;
            if (FindNamed(d, name, type) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>いまの形式の項目を作り直す(開くときに呼ぶ。テストは開かずにこれで中身を調べる。INV-18)。</summary>
    public void Rebuild()
    {
        _updating = true;
        try
        {
            _body.Children.Clear();
            if (_session.Document is not { } doc) return;
            var o = doc.Output;
            switch (o.Format)
            {
                case ExportFormat.Gif:
                    BuildGif(doc);
                    break;
                case ExportFormat.Jpeg:
                    Quality(o.Jpeg.Quality, true, v => _session.SetJpegOptions(Doc().Output.Jpeg with { Quality = v }));
                    Color("band.options.jpeg.background", o.Jpeg.Background, c => _session.SetJpegOptions(Doc().Output.Jpeg with { Background = c }));
                    break;
                case ExportFormat.Webp:
                    Quality(o.Webp.Quality, !o.Webp.Lossless, v => _session.SetWebpOptions(Doc().Output.Webp with { Quality = v }));
                    Toggle("band.options.webp.lossless", o.Webp.Lossless, b => _session.SetWebpOptions(Doc().Output.Webp with { Lossless = b }));
                    break;
                case ExportFormat.Apng:
                    BuildApng(doc);
                    break;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private SessionDocument Doc() => _session.Document!;

    private void BuildGif(SessionDocument doc)
    {
        var g = doc.Output.Gif;
        var colors = new NumberBox { Minimum = OutputSettingsJson.MinColors, Maximum = OutputSettingsJson.MaxColors, Decimals = 0, Value = g.Colors };
        AutomationProperties.SetName(colors, BandTexts.P("band.options.gif.colors"));
        colors.SetResourceReference(FrameworkElement.WidthProperty, "TimelineFpsWidth");
        colors.HorizontalAlignment = HorizontalAlignment.Left;
        colors.ValueCommitted += (_, v) => Apply(() => _session.SetGifOptions(Doc().Output.Gif with { Colors = (long)v }));
        Item("band.options.gif.colors", colors);

        var dither = Segmented("band.options.gif.dither", ["band.options.gif.dither.blue", "band.options.gif.dither.error", "band.options.gif.dither.none"]);
        dither.SelectedIndex = Math.Max(0, OutputSettingsJson.Dithers.ToList().IndexOf(g.Dither));
        dither.SelectionChanged += (_, _) =>
        {
            if (dither.SelectedIndex >= 0) Apply(() => _session.SetGifOptions(Doc().Output.Gif with { Dither = OutputSettingsJson.Dithers[dither.SelectedIndex] }));
        };
        Item("band.options.gif.dither", dither);

        var none = new CheckBox { Content = BandTexts.P("band.options.gif.matte.none"), IsChecked = g.Matte is null };
        AutomationProperties.SetName(none, BandTexts.P("band.options.gif.matte") + " " + BandTexts.P("band.options.gif.matte.none"));
        var hex = HexBox("band.options.gif.matte", g.Matte ?? OutputSettings.Default.Jpeg.Background, c => _session.SetGifOptions(Doc().Output.Gif with { Matte = c }));
        hex.IsEnabled = g.Matte is not null;
        none.Click += (_, _) => Apply(() => _session.SetGifOptions(Doc().Output.Gif with { Matte = none.IsChecked == true ? null : NormalizedOr(hex.Text) }));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(none);
        hex.SetResourceReference(FrameworkElement.MarginProperty, "Space2LeftThickness");
        row.Children.Add(hex);
        Item("band.options.gif.matte", row);

        Toggle("band.options.gif.diff", g.Diff, b => _session.SetGifOptions(Doc().Output.Gif with { Diff = b }));
    }

    private void BuildApng(SessionDocument doc)
    {
        var a = doc.Output.Apng;
        var color = Segmented("band.options.apng.color", ["band.options.apng.color.rgba", "band.options.apng.color.palette"]);
        color.SelectedIndex = a.Palette ? 1 : 0;
        color.SelectionChanged += (_, _) =>
        {
            if (color.SelectedIndex >= 0) Apply(() => _session.SetApngPalette(color.SelectedIndex == 1));
        };
        Item("band.options.apng.color", color);

        var loop = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left };
        loop.SetResourceReference(FrameworkElement.WidthProperty, "TimelineLoopWidth");
        AutomationProperties.SetName(loop, BandTexts.P("band.options.apng.loop"));
        for (var n = 0; n <= OutputSettingsJson.MaxApngLoops; n++)
        {
            var text = n == 0 ? BandTexts.P("band.options.apng.loop.infinite") : BandTexts.P("band.options.apng.loop.n", ("n", n.ToString(CultureInfo.InvariantCulture)));
            var item = new ComboBoxItem { Content = text };
            AutomationProperties.SetName(item, text);
            loop.Items.Add(item);
        }
        loop.SelectedIndex = doc.Time.Loop == LoopMode.Infinite ? 0 : (int)Math.Clamp(a.Loops, 1, OutputSettingsJson.MaxApngLoops);
        loop.SelectionChanged += (_, _) =>
        {
            if (loop.SelectedIndex >= 0) Apply(() => _session.SetApngLoops(loop.SelectedIndex));
        };
        Item("band.options.apng.loop", loop);
    }

    private void Quality(long value, bool enabled, Action<long> set)
    {
        var slider = new Slider
        {
            Minimum = OutputSettingsJson.MinQuality,
            Maximum = OutputSettingsJson.MaxQuality,
            SmallChange = 1,
            LargeChange = 10,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = value,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, BandTexts.P("band.options.quality"));
        var box = new NumberBox { Minimum = OutputSettingsJson.MinQuality, Maximum = OutputSettingsJson.MaxQuality, Decimals = 0, Value = value, IsEnabled = enabled };
        AutomationProperties.SetName(box, BandTexts.P("band.options.quality"));
        box.SetResourceReference(FrameworkElement.WidthProperty, "TimelinePlayWidth");
        box.SetResourceReference(FrameworkElement.MarginProperty, "Space2LeftThickness");
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _session.BeginContinuous()));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _session.EndContinuous()));
        slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            box.Value = Math.Round(e.NewValue);
            Apply(() => set((long)Math.Round(e.NewValue)));
        };
        box.ValueCommitted += (_, v) => Apply(() => set((long)v));
        var row = new DockPanel();
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(slider);
        Item("band.options.quality", row);
    }

    private void Toggle(string key, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = BandTexts.P(key), IsChecked = value };
        AutomationProperties.SetName(c, BandTexts.P(key));
        c.Click += (_, _) => Apply(() => set(c.IsChecked == true));
        c.SetResourceReference(FrameworkElement.MarginProperty, "Space2BottomThickness");
        _body.Children.Add(c);
    }

    private void Color(string key, string value, Action<string> set) => Item(key, HexBox(key, value, set));

    private TextBox HexBox(string key, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, HorizontalAlignment = HorizontalAlignment.Left };
        box.SetResourceReference(FrameworkElement.WidthProperty, "TimelineLengthWidth");
        AutomationProperties.SetName(box, BandTexts.P(key));
        void Commit()
        {
            if (_updating) return;
            if (ColorText.TryNormalizeRgb(box.Text?.Trim(), out var hex)) Apply(() => set(hex));
            else Rebuild(); // 読めない入力は直前の値に戻す(FR-27 と同じ扱い)
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };
        box.LostKeyboardFocus += (_, _) => Commit();
        return box;
    }

    private static string? NormalizedOr(string? text) =>
        ColorText.TryNormalizeRgb(text?.Trim(), out var hex) ? hex : OutputSettings.Default.Jpeg.Background;

    private static ListBox Segmented(string nameKey, string[] itemKeys)
    {
        var lb = new ListBox { HorizontalAlignment = HorizontalAlignment.Left };
        lb.SetResourceReference(FrameworkElement.StyleProperty, "Segmented");
        AutomationProperties.SetName(lb, BandTexts.P(nameKey));
        foreach (var k in itemKeys)
        {
            var item = new ListBoxItem { Content = BandTexts.P(k) };
            AutomationProperties.SetName(item, BandTexts.P(k));
            lb.Items.Add(item);
        }
        return lb;
    }

    private void Item(string labelKey, FrameworkElement control)
    {
        var label = new TextBlock { Text = BandTexts.P(labelKey) };
        label.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        label.SetResourceReference(FrameworkElement.MarginProperty, "Space1Thickness");
        _body.Children.Add(label);
        control.SetResourceReference(FrameworkElement.MarginProperty, "Space2BottomThickness");
        _body.Children.Add(control);
    }

    // 反映したあとの値の入れ直しは、文書の変更の通知から Refresh で行う
    private void Apply(Action action)
    {
        if (_updating || !_session.HasDocument) return;
        action();
    }
}
