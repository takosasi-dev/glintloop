// コマンドバーの「キャンバスの背景」のボタンで開く一覧(FR-20・§9.3 の表の 10): 市松模様・黒・白・灰・選んだ色の 5 項目と、選んだ色の 16 進の入力欄。
// 選ぶと設定の bgPreview に入れて閉じる。Esc とほかの場所のクリックでも閉じ、閉じたらボタンにフォーカスを戻す。
// 色の部品(color の部品)は別の担当が作るので、それまでは 16 進の入力欄(#RRGGBB)で色を決める(仮の決め)。ComboBox と同じく開閉の動きは付けない。
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Text;
using GlintLoop.Core.Session;
using GlintLoop.Core.Settings;

namespace GlintLoop.App.Viewing;

public sealed class BackgroundPicker
{
    /// <summary>一覧の UIA の名前(ui.tsv の canvas.bg.list)。</summary>
    public static string ListName => NoticeTexts.UiText("canvas.bg.list");

    /// <summary>選んだ色の入力欄の UIA の名前(ui.tsv の canvas.bg.color_input)。</summary>
    public static string ColorInputName => NoticeTexts.UiText("canvas.bg.color_input");

    private readonly AppSettings _settings;
    private readonly ButtonBase _anchor;
    private bool _updating;

    public BackgroundPicker(AppSettings settings, ButtonBase anchor)
    {
        _settings = settings;
        _anchor = anchor;
        List = new ListBox { BorderThickness = new Thickness(), Background = null };
        AutomationProperties.SetName(List, ListName);
        foreach (var (mode, label) in CanvasBackground.Items)
        {
            var item = new ListBoxItem { Content = label, Tag = mode };
            item.SetResourceReference(FrameworkElement.StyleProperty, "RowItem");
            AutomationProperties.SetName(item, label);
            List.Items.Add(item);
        }
        List.SelectionChanged += (_, _) => OnSelected();
        List.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem) Close();
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                Close();
            }
        };

        ColorInput = new TextBox();
        AutomationProperties.SetName(ColorInput, ColorInputName);
        ColorInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CommitColor();
        };
        ColorInput.LostKeyboardFocus += (_, _) => CommitColor();

        var panel = new StackPanel();
        panel.Children.Add(List);
        var colorRow = new Border { Child = ColorInput };
        colorRow.SetResourceReference(FrameworkElement.MarginProperty, "Space1Thickness");
        panel.Children.Add(colorRow);
        var face = new Border { Child = panel };
        face.SetResourceReference(Border.BackgroundProperty, "Surface1Brush");
        face.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        face.SetResourceReference(Border.BorderThicknessProperty, "StrokeThickness");
        face.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        face.SetResourceReference(Border.PaddingProperty, "Space1Thickness");
        face.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };

        Popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Focusable = false,
            Child = face,
        };
        Popup.Closed += (_, _) =>
        {
            if (_anchor.IsVisible && _anchor.IsEnabled && (Keyboard.FocusedElement is null || IsInside(Keyboard.FocusedElement as DependencyObject)))
                _anchor.Focus();
        };
        Sync();
        settings.Changed += key =>
        {
            if (key == "bgPreview") Sync();
        };
    }

    public Popup Popup { get; }

    public ListBox List { get; }

    public TextBox ColorInput { get; }

    public bool IsOpen => Popup.IsOpen;

    /// <summary>開く。今の選択の項目にフォーカスを移す。</summary>
    public void Open()
    {
        Sync();
        Popup.IsOpen = true;
        if (List.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, List.SelectedIndex)) is ListBoxItem item) item.Focus();
    }

    public void Close() => Popup.IsOpen = false;

    private bool IsInside(DependencyObject? d)
    {
        for (; d is not null; d = LogicalTreeHelperEx.Parent(d))
        {
            if (ReferenceEquals(d, Popup)) return true;
        }
        return false;
    }

    private void Sync()
    {
        _updating = true;
        try
        {
            var bg = _settings.BgPreview;
            List.SelectedIndex = CanvasBackground.Items.ToList().FindIndex(i => i.Mode == bg.Mode);
            ColorInput.Text = ColorText.Rgb((uint)bg.Color);
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnSelected()
    {
        if (_updating || List.SelectedItem is not ListBoxItem { Tag: BgPreviewMode mode }) return;
        _settings.BgPreview = _settings.BgPreview with { Mode = mode };
    }

    private void CommitColor()
    {
        if (_updating) return;
        if (!ColorText.TryNormalizeRgb(ColorInput.Text?.Trim(), out var hex))
        {
            Sync(); // 読めない入力は直前の値に戻す(FR-27 と同じ扱い)
            return;
        }
        var rgb = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        _settings.BgPreview = new BgPreview(BgPreviewMode.Custom, rgb);
    }
}

internal static class LogicalTreeHelperEx
{
    public static DependencyObject? Parent(DependencyObject d) =>
        LogicalTreeHelper.GetParent(d) ?? (d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : null);
}
