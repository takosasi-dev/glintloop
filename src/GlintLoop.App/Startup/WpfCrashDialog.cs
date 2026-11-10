// クラッシュの処理の製品の実装: 自分の STA スレッドで開くダイアログ・Environment.Exit・未処理例外の発生元(Dispatcher と AppDomain)。
// 例外のあとの UI スレッドは使えないことがあるので、ダイアログは別のスレッドに作り、資源もそのスレッドで読み直す。閉じるまで呼び出し元を止める。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Startup;

public sealed class WpfCrashDialog(Func<ThemeVariant> theme) : ICrashDialog
{
    public void Show(string title, string body, IReadOnlyList<string> buttons)
    {
        var variant = theme();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Build(title, body, buttons, variant).ShowDialog();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "GlintLoop crash dialog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("ダイアログを開けない", failure);
    }

    /// <summary>ダイアログのウィンドウを作る(呼んだスレッドのものになる)。buttons のどれを押しても閉じる。</summary>
    public static Window Build(string title, string body, IReadOnlyList<string> buttons, ThemeVariant theme)
    {
        // Application の資源は UI スレッドのものなので、このスレッドで読み直してウィンドウに持たせる
        var layout = AppResources.Load("Tokens.Layout.xaml");
        var colors = theme == ThemeVariant.HighContrast
            ? HighContrastTokens.Build(AppResources.Load("Tokens.Dark.xaml"))
            : AppResources.Load(theme == ThemeVariant.Light ? "Tokens.Light.xaml" : "Tokens.Dark.xaml");
        var window = new Window
        {
            Title = title,
            Width = (double)layout["DialogErrorWidth"],
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Resources.MergedDictionaries.Add(colors);
        window.Resources.MergedDictionaries.Add(layout);
        window.Resources.MergedDictionaries.Add(AppResources.Load("Controls.xaml"));
        window.SetResourceReference(Control.BackgroundProperty, "BgBaseBrush");

        var panel = new StackPanel { Margin = (Thickness)layout["Space5Thickness"] };
        var heading = new TextBlock
        {
            Text = title,
            FontSize = (double)layout["FontSizeSubtitle"],
            FontFamily = (FontFamily)layout["FontFamilyText"],
            TextWrapping = TextWrapping.Wrap,
            Margin = (Thickness)layout["Space4BottomThickness"],
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(heading);
        var text = new TextBlock
        {
            Text = body,
            FontSize = (double)layout["FontSizeBody"],
            FontFamily = (FontFamily)layout["FontFamilyText"],
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(text);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = (Thickness)layout["Space4TopThickness"] };
        foreach (var label in buttons)
        {
            var button = new Button { Content = label, IsDefault = true, IsCancel = true };
            button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            button.Click += (_, _) => window.Close();
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        window.Content = panel;
        return window;
    }
}

public sealed class EnvironmentProcessExit : IProcessExit
{
    public static EnvironmentProcessExit Instance { get; } = new();

    public void Exit(int code) => Environment.Exit(code);
}

/// <summary>UI スレッド(Dispatcher.UnhandledException)と AppDomain.UnhandledException を、入口を呼ぶだけのイベントにつなぐ。</summary>
public sealed class WpfUnhandledExceptionSource : IUnhandledExceptionSource
{
    public WpfUnhandledExceptionSource(Dispatcher dispatcher)
    {
        dispatcher.UnhandledException += (_, e) =>
        {
            // 処理済みにする: 入口の再入で戻ったときに、WPF が投げ直して別の経路で落ちないようにする
            e.Handled = true;
            UiThreadException?.Invoke(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppDomainException?.Invoke(e.ExceptionObject as Exception ?? new InvalidOperationException());
    }

    public event Action<Exception>? UiThreadException;

    public event Action<Exception>? AppDomainException;
}
