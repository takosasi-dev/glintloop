// バッチ画面(FR-87・§9.3 の画面の表。幅 640×高さ 560 DIP): 上部の batch.layers_ignored、「開く」「実行」「取り消し」、出力フォルダの選択、進捗「n / N」と処理中の名前、画像の一覧(空のときの文)、失敗の一覧。
// 中身の操作は BatchModule に任せ、この部品は表示と状態だけを持つ。実行中の Esc は「取り消し」と同じで閉じない(§9.3 の規則 (3))。
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using GlintLoop.App.Shell;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Notices;

namespace GlintLoop.App.Batch;

public sealed class BatchView : Grid, IDialogEscape
{
    private readonly List<BatchFailure> _failures = new();
    private int _done;

    public BatchView()
    {
        AutomationProperties.SetAutomationId(this, "Batch");
        // ダイアログのスクロールの領域いっぱいに広げ、2 つの一覧がそれぞれの中でスクロールする(一覧の長さでダイアログの全体を伸ばさない)
        SetBinding(HeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ScrollViewer), 1) });
        for (var i = 0; i < 5; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel();
        header.SetResourceReference(MarginProperty, "Space2BottomThickness");
        Close = new Button { Content = Icons.Create(Icons.Close), ToolTip = Icons.Close.Name };
        Close.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(Close, Icons.Close.Name);
        AutomationProperties.SetAutomationId(Close, "BatchClose");
        DockPanel.SetDock(Close, Dock.Right);
        header.Children.Add(Close);
        header.Children.Add(Label(Ui("batch.title"), "TextSubtitle"));
        Place(header, 0);

        LayersIgnored = Label(NoticeTexts.Format(NoticeKeys.BatchLayersIgnored), "TextBody");
        LayersIgnored.TextWrapping = TextWrapping.Wrap;
        LayersIgnored.SetResourceReference(MarginProperty, "Space2BottomThickness");
        LayersIgnored.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(LayersIgnored, "BatchLayersIgnored");
        Place(LayersIgnored, 1);

        OpenFiles = ActionButton("batch.open", "BatchOpen");
        RunButton = ActionButton("batch.run", "BatchRun");
        RunButton.SetResourceReference(StyleProperty, "PrimaryButton");
        RunButton.SetResourceReference(MarginProperty, "Space2LeftThickness");
        CancelButton = ActionButton("batch.cancel", "BatchCancel");
        CancelButton.SetResourceReference(MarginProperty, "Space2LeftThickness");
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.SetResourceReference(MarginProperty, "Space2BottomThickness");
        actions.Children.Add(OpenFiles);
        actions.Children.Add(RunButton);
        actions.Children.Add(CancelButton);
        Place(actions, 2);

        PickFolder = ActionButton("batch.folder.pick", "BatchPickFolder");
        FolderText = Label("", "TextBody");
        FolderText.TextTrimming = TextTrimming.CharacterEllipsis;
        FolderText.VerticalAlignment = VerticalAlignment.Center;
        FolderText.SetResourceReference(MarginProperty, "Space2LeftThickness");
        AutomationProperties.SetAutomationId(FolderText, "BatchFolder");
        var folderLabel = Label(Ui("batch.folder"), "TextBodyStrong");
        folderLabel.VerticalAlignment = VerticalAlignment.Center;
        folderLabel.SetResourceReference(MarginProperty, "Space2RightThickness");
        var folder = new DockPanel();
        folder.SetResourceReference(MarginProperty, "Space2BottomThickness");
        DockPanel.SetDock(folderLabel, Dock.Left);
        DockPanel.SetDock(PickFolder, Dock.Left);
        folder.Children.Add(folderLabel);
        folder.Children.Add(PickFolder);
        folder.Children.Add(FolderText);
        Place(folder, 3);

        ProgressText = Label("", "TextBody");
        AutomationProperties.SetAutomationId(ProgressText, "BatchProgress");
        CurrentName = Label("", "TextCaption");
        CurrentName.TextTrimming = TextTrimming.CharacterEllipsis;
        CurrentName.VerticalAlignment = VerticalAlignment.Center;
        CurrentName.SetResourceReference(MarginProperty, "Space2LeftThickness");
        AutomationProperties.SetAutomationId(CurrentName, "BatchCurrent");
        var progress = new DockPanel();
        progress.SetResourceReference(MarginProperty, "Space2BottomThickness");
        DockPanel.SetDock(ProgressText, Dock.Left);
        progress.Children.Add(ProgressText);
        progress.Children.Add(CurrentName);
        Place(progress, 4);

        List = NewList("batch.list", "BatchList");
        Place(List, 5);
        EmptyText = Label(Ui("batch.empty"), "TextBody");
        EmptyText.TextWrapping = TextWrapping.Wrap;
        EmptyText.HorizontalAlignment = HorizontalAlignment.Center;
        EmptyText.VerticalAlignment = VerticalAlignment.Center;
        Fx.SetText(EmptyText, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(EmptyText, "BatchEmpty");
        Place(EmptyText, 5);

        var failuresHeading = Label(Ui("batch.failures"), "TextBodyStrong");
        failuresHeading.SetResourceReference(MarginProperty, "Space2TopThickness");
        Place(failuresHeading, 6);
        FailureList = NewList("batch.failures", "BatchFailures");
        Place(FailureList, 7);

        Paths = new BatchList();
        Refresh();
    }

    public Button Close { get; }

    public Button OpenFiles { get; }

    public Button RunButton { get; }

    public Button CancelButton { get; }

    public Button PickFolder { get; }

    public TextBlock LayersIgnored { get; }

    public TextBlock FolderText { get; }

    public TextBlock ProgressText { get; }

    public TextBlock CurrentName { get; }

    public TextBlock EmptyText { get; }

    public ListBox List { get; }

    public ListBox FailureList { get; }

    /// <summary>追加した画像の一覧。</summary>
    public BatchList Paths { get; }

    /// <summary>失敗の一覧の中身(古い順)。</summary>
    public IReadOnlyList<BatchFailure> Failures => _failures;

    /// <summary>出力フォルダ(未選択なら null)。</summary>
    public string? Folder { get; private set; }

    /// <summary>実行中か。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>実行中の Esc(「取り消し」のボタンと同じ)。</summary>
    public event Action? CancelRequested;

    public bool HandleEscape()
    {
        if (!IsRunning) return false;
        CancelRequested?.Invoke();
        return true;
    }

    public void SetFolder(string? folder)
    {
        Folder = folder;
        Refresh();
    }

    /// <summary>文書のレイヤーが 2 枚以上のあいだ、上部の文を出す(FR-87)。</summary>
    public void SetLayersIgnored(bool show) => LayersIgnored.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>paths を一覧に足す。200 個を超えて足せなかったものがあれば true。</summary>
    public bool AddPaths(IEnumerable<string> paths)
    {
        var from = Paths.Paths.Count;
        var (_, full) = Paths.Add(paths);
        for (var i = from; i < Paths.Paths.Count; i++)
        {
            var name = Path.GetFileName(Paths.Paths[i]);
            List.Items.Add(Row(name, new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis }));
        }
        Refresh();
        return full;
    }

    public void BeginRun()
    {
        IsRunning = true;
        _done = 0;
        _failures.Clear();
        FailureList.Items.Clear();
        CurrentName.Text = "";
        Refresh();
        MoveFocus(CancelButton);
    }

    public void Apply(BatchStep step)
    {
        _done = step.Done;
        CurrentName.Text = step.Current ?? "";
        if (step.Failure is { } f) AddFailure(f);
        Refresh();
    }

    public void EndRun()
    {
        IsRunning = false;
        CurrentName.Text = "";
        Refresh();
        MoveFocus(RunButton);
    }

    private void AddFailure(BatchFailure f)
    {
        _failures.Add(f);
        var name = Label(f.Name, "TextBodyStrong");
        var reason = Label(f.Reason, "TextCaption");
        reason.TextWrapping = TextWrapping.Wrap;
        var cell = new StackPanel();
        cell.Children.Add(name);
        cell.Children.Add(reason);
        FailureList.Items.Add(Row(f.Name + " " + f.Reason, cell));
    }

    private void Refresh()
    {
        var count = Paths.Paths.Count;
        ProgressText.Text = Ui("batch.progress")
            .Replace("{done}", _done.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{n}", count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        FolderText.Text = Folder ?? Ui("batch.folder.none");
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenFiles.IsEnabled = !IsRunning;
        PickFolder.IsEnabled = !IsRunning;
        Close.IsEnabled = !IsRunning;
        CancelButton.IsEnabled = IsRunning;
        RunButton.IsEnabled = !IsRunning && count > 0 && Folder is not null;
    }

    // 押したボタンが無効になるとフォーカスが行き場を失うので、次に使うボタンへ移す(前面を取らないよう、ウィンドウがアクティブなときだけ)
    private void MoveFocus(Button next)
    {
        if (Window.GetWindow(this) is { IsActive: true } && next.IsEnabled) next.Focus();
    }

    private void Place(UIElement element, int row)
    {
        SetRow(element, row);
        Children.Add(element);
    }

    private static string Ui(string key) => NoticeTexts.UiText(key);

    private static TextBlock Label(string text, string style)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }

    private static Button ActionButton(string key, string id)
    {
        var b = new Button { Content = Ui(key) };
        AutomationProperties.SetName(b, Ui(key));
        AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    private static ListBox NewList(string nameKey, string id)
    {
        var list = new ListBox { Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTextSearchEnabled = false };
        var t = new ControlTemplate(typeof(ListBox));
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.FocusableProperty, false);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, false);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        t.VisualTree = scroll;
        list.Template = t;
        VirtualizingPanel.SetIsVirtualizing(list, false);
        AutomationProperties.SetName(list, Ui(nameKey));
        AutomationProperties.SetAutomationId(list, id);
        return list;
    }

    private static ListBoxItem Row(string name, FrameworkElement content)
    {
        var item = new ListBoxItem { Content = content };
        item.SetResourceReference(StyleProperty, "RowItem");
        AutomationProperties.SetName(item, name);
        return item;
    }
}
