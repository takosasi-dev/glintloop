// 設定の画面(FR-90・§9.3 の画面の表。幅 520×高さ 640 DIP のダイアログ): テーマ・コピーの載せ方・既定の書き出し形式・既定の保存先・アニメーションの縮小・
// ログのフォルダを開く・キャッシュの上限・キャッシュの削除・設定の初期化の 9 項目と「このソフトについて」。変更はすぐ AppSettings に入れる(保存は SettingsStore が 500 ms 後)。
// 保存に失敗したら上端に通知バーと同じ書式の帯で settings.save_failed を出す。WARP で動いているあいだは「ソフトウェア描画」を出す(FR-94)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlintLoop.App.Controls;
using GlintLoop.App.Shell;
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Settings;

namespace GlintLoop.App.Screens;

public sealed class SettingsView : DockPanel
{
    private readonly AppShell _shell;
    private readonly ScreensModule _module;
    private bool _updating;

    public SettingsView(AppShell shell, ScreensModule module)
    {
        _shell = shell;
        _module = module;
        AutomationProperties.SetAutomationId(this, "Settings");

        var header = new DockPanel();
        header.SetResourceReference(MarginProperty, "Space4BottomThickness");
        CloseButton = new Button { Content = Icons.Create(Icons.Close), ToolTip = Icons.Close.Name };
        CloseButton.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(CloseButton, Icons.Close.Name);
        CloseButton.Click += (_, _) => shell.Dialogs.Close(null, MotionCause.Pointer);
        SetDock(CloseButton, Dock.Right);
        header.Children.Add(CloseButton);
        var title = new TextBlock { Text = ScreenTexts.Get("settings.title") };
        title.SetResourceReference(StyleProperty, "TextSubtitle");
        header.Children.Add(title);
        SetDock(header, Dock.Top);
        Children.Add(header);

        // 保存の失敗の帯(通知バーと同じ部品。閉じるボタンつき)
        SaveFailedBand = new NoticeBar(shell.Services.Timers) { VerticalAlignment = VerticalAlignment.Top };
        SaveFailedBand.SetResourceReference(MarginProperty, "Space2BottomThickness");
        SetDock(SaveFailedBand, Dock.Top);
        Children.Add(SaveFailedBand);

        var body = new StackPanel();
        SoftwareRendering = new TextBlock { Text = ScreenTexts.Get("settings.software_rendering"), Visibility = Visibility.Collapsed };
        SoftwareRendering.SetResourceReference(StyleProperty, "TextBody");
        SoftwareRendering.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        SoftwareRendering.SetResourceReference(MarginProperty, "Space4BottomThickness");
        body.Children.Add(SoftwareRendering);

        Theme = Segmented("settings.theme", ["settings.theme.system", "settings.theme.light", "settings.theme.dark"]);
        Theme.SelectionChanged += (_, _) => Apply(() => _shell.Settings.Theme = (ThemeSetting)Theme.SelectedIndex);
        AddItem(body, "settings.theme", Theme);

        CopyMode = Segmented("settings.copy_mode", ["settings.copy_mode.file", "settings.copy_mode.image", "settings.copy_mode.both"]);
        CopyMode.SelectionChanged += (_, _) => Apply(() => _shell.Settings.CopyMode = (CopyModeSetting)CopyMode.SelectedIndex);
        AddItem(body, "settings.copy_mode", CopyMode);

        DefaultFormat = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left };
        DefaultFormat.SetResourceReference(WidthProperty, "ExportFormatWidth");
        foreach (var f in FormatNames) DefaultFormat.Items.Add(new ComboBoxItem { Content = f });
        AutomationProperties.SetName(DefaultFormat, ScreenTexts.Get("settings.default_format"));
        DefaultFormat.SelectionChanged += (_, _) =>
            Apply(() => _shell.Settings.SetDefaultFormatFromSettingsScreen((ExportFormatSetting)DefaultFormat.SelectedIndex));
        AddItem(body, "settings.default_format", DefaultFormat);

        DefaultSaveDir = new Button { HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(DefaultSaveDir, ScreenTexts.Get("settings.default_save_dir"));
        DefaultSaveDir.Click += (_, _) => PickSaveDir();
        AddItem(body, "settings.default_save_dir", DefaultSaveDir);

        ReduceMotion = Segmented("settings.reduce_motion", ["settings.reduce_motion.system", "settings.reduce_motion.on", "settings.reduce_motion.off"]);
        ReduceMotion.SelectionChanged += (_, _) => Apply(() => _shell.Settings.ReduceMotion = (ReduceMotionSetting)ReduceMotion.SelectedIndex);
        AddItem(body, "settings.reduce_motion", ReduceMotion);

        OpenLogs = ActionButton("settings.open_logs", () => _module.OpenFolder(_shell.Services.Paths.LogsDir));
        AddItem(body, null, OpenLogs);

        CacheLimit = new NumberBox { Minimum = AppSettings.CacheLimitMinMB, Maximum = AppSettings.CacheLimitMaxMB, Decimals = 0 };
        CacheLimit.SetResourceReference(WidthProperty, "TimelineHoldWidth");
        AutomationProperties.SetName(CacheLimit, ScreenTexts.Get("settings.cache_limit"));
        CacheLimit.ValueCommitted += (_, v) => Apply(() => _shell.Settings.CacheLimitMB = (int)v);
        var unit = new TextBlock { Text = ScreenTexts.Get("settings.cache_limit.unit"), VerticalAlignment = VerticalAlignment.Center };
        unit.SetResourceReference(StyleProperty, "TextCaption");
        unit.SetResourceReference(MarginProperty, "Space2LeftThickness");
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal };
        limitRow.Children.Add(CacheLimit);
        limitRow.Children.Add(unit);
        AddItem(body, "settings.cache_limit", limitRow);

        ClearCache = ActionButton("settings.clear_cache", () => _module.ClearCache?.Invoke());
        AddItem(body, null, ClearCache);

        Reset = ActionButton("settings.reset", () => _shell.Settings.ResetToDefaults());
        AddItem(body, null, Reset);

        About = ActionButton("settings.about", () => _module.ShowAbout());
        AddItem(body, null, About);
        Children.Add(body);

        Refresh();
        Loaded += (_, _) =>
        {
            _shell.Settings.Changed -= OnSettingChanged;
            _shell.Settings.Changed += OnSettingChanged;
            _shell.SettingsStore.SaveFailed -= OnSaveFailed;
            _shell.SettingsStore.SaveFailed += OnSaveFailed;
            if (_shell.Recovery is { } recovery)
            {
                recovery.Recovered -= UpdateSoftwareRendering;
                recovery.Recovered += UpdateSoftwareRendering;
            }
            Refresh();
            UpdateSoftwareRendering();
        };
        Unloaded += (_, _) =>
        {
            _shell.Settings.Changed -= OnSettingChanged;
            _shell.SettingsStore.SaveFailed -= OnSaveFailed;
            if (_shell.Recovery is { } recovery) recovery.Recovered -= UpdateSoftwareRendering;
        };
        _shell.RenderReady?.ContinueWith(_ => Dispatcher.BeginInvoke(UpdateSoftwareRendering), TaskScheduler.Default);
    }

    // WARP で動いているあいだだけ出す(起動時の切り替えと、消失のあとの切り替えの両方。FR-93・FR-94)
    private void UpdateSoftwareRendering() =>
        SoftwareRendering.Visibility = _shell.RenderReady is { IsCompletedSuccessfully: true, Result.IsWarp: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>既定の書き出し形式の一覧の並び(ExportFormatSetting の順)。</summary>
    public static readonly string[] FormatNames = ["PNG", "JPEG", "GIF", "WebP", "APNG"];

    public Button CloseButton { get; }

    public NoticeBar SaveFailedBand { get; }

    public TextBlock SoftwareRendering { get; }

    public ListBox Theme { get; }

    public ListBox CopyMode { get; }

    public ComboBox DefaultFormat { get; }

    public Button DefaultSaveDir { get; }

    public ListBox ReduceMotion { get; }

    public Button OpenLogs { get; }

    public NumberBox CacheLimit { get; }

    public Button ClearCache { get; }

    public Button Reset { get; }

    public Button About { get; }

    /// <summary>設定の値を部品に写す(初期化のあとなど)。</summary>
    public void Refresh()
    {
        _updating = true;
        try
        {
            var s = _shell.Settings;
            Theme.SelectedIndex = (int)s.Theme;
            CopyMode.SelectedIndex = (int)s.CopyMode;
            DefaultFormat.SelectedIndex = (int)s.DefaultFormat;
            ReduceMotion.SelectedIndex = (int)s.ReduceMotion;
            DefaultSaveDir.Content = string.IsNullOrEmpty(s.DefaultSaveDir) ? ScreenTexts.Get("settings.default_save_dir.none") : s.DefaultSaveDir;
            DefaultSaveDir.ToolTip = s.DefaultSaveDir.Length > 0 ? s.DefaultSaveDir : null;
            CacheLimit.Value = s.CacheLimitMB;
            ClearCache.IsEnabled = _module.ClearCache is not null;
        }
        finally
        {
            _updating = false;
        }
    }

    private void Apply(Action change)
    {
        if (_updating) return;
        change();
    }

    private void OnSettingChanged(string key)
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    private void OnSaveFailed(Exception _)
    {
        void Show() => SaveFailedBand.Show(NoticeTexts.Notice(NoticeKeys.SettingsSaveFailed));
        if (Dispatcher.CheckAccess()) Show();
        else Dispatcher.BeginInvoke(Show);
    }

    private void PickSaveDir()
    {
        var s = _shell.Settings;
        var picked = _shell.Services.FileDialogs.PickFolder(s.DefaultSaveDir.Length > 0 ? s.DefaultSaveDir : null);
        if (picked is not null) s.DefaultSaveDir = picked;
    }

    private static ListBox Segmented(string nameKey, string[] itemKeys)
    {
        var lb = new ListBox();
        lb.SetResourceReference(StyleProperty, "Segmented");
        AutomationProperties.SetName(lb, ScreenTexts.Get(nameKey));
        foreach (var k in itemKeys)
        {
            var item = new ListBoxItem { Content = ScreenTexts.Get(k) };
            AutomationProperties.SetName(item, ScreenTexts.Get(k));
            lb.Items.Add(item);
        }
        return lb;
    }

    private static Button ActionButton(string key, Action click)
    {
        var b = new Button { Content = ScreenTexts.Get(key), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(b, ScreenTexts.Get(key));
        b.Click += (_, _) => click();
        return b;
    }

    private static void AddItem(Panel body, string? labelKey, FrameworkElement control)
    {
        if (labelKey is not null)
        {
            var label = new TextBlock { Text = ScreenTexts.Get(labelKey) };
            label.SetResourceReference(StyleProperty, "TextBody");
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            label.SetResourceReference(MarginProperty, "Space2BottomThickness");
            body.Children.Add(label);
        }
        control.SetResourceReference(MarginProperty, "Space4BottomThickness");
        body.Children.Add(control);
    }
}
