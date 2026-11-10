// 上限を超えた結果を渡す前の確認ダイアログ(FR-76・E-30。幅 440 DIP): 文言は export.confirm_over、ボタンは「渡す」「やめる」で、既定は「やめる」(Enter と最初のフォーカス)。Esc と閉じる操作は「やめる」として扱う。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Shell;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Notices;

namespace GlintLoop.App.Export;

public sealed class ExportConfirmOverView : StackPanel
{
    /// <summary>「渡す」を選んだときの Close の値。</summary>
    public const string PassResult = "pass";

    public ExportConfirmOverView(DialogHost host)
    {
        AutomationProperties.SetAutomationId(this, "ExportConfirmOver");
        Message = new TextBlock { Text = NoticeTexts.DialogMessage(NoticeKeys.ExportConfirmOver), TextWrapping = TextWrapping.Wrap };
        Message.SetResourceReference(StyleProperty, "TextBody");
        Message.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Children.Add(Message);
        Pass = new Button { Content = NoticeTexts.UiText("export.confirm_over.pass") };
        AutomationProperties.SetName(Pass, (string)Pass.Content);
        Pass.Click += (_, _) => host.Close(PassResult, MotionCause.Pointer);
        Stop = new Button { Content = NoticeTexts.UiText("export.confirm_over.stop"), IsDefault = true };
        AutomationProperties.SetName(Stop, (string)Stop.Content);
        Stop.SetResourceReference(MarginProperty, "Space2LeftThickness");
        Stop.Click += (_, _) => host.Close(null, MotionCause.Pointer);
        // 最初のフォーカスは既定のボタン(やめる)。DialogHost は Tab の順の最初へフォーカスを移すので、やめるを先にする
        KeyboardNavigation.SetTabIndex(Stop, 0);
        KeyboardNavigation.SetTabIndex(Pass, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.SetResourceReference(MarginProperty, "Space4TopThickness");
        buttons.Children.Add(Pass);
        buttons.Children.Add(Stop);
        Children.Add(buttons);
    }

    public TextBlock Message { get; }

    public Button Pass { get; }

    public Button Stop { get; }

    /// <summary>出して、「渡す」を選んだら true(「やめる」・Esc・Enter は false)。</summary>
    public static async Task<bool> AskAsync(DialogHost host)
    {
        var view = new ExportConfirmOverView(host);
        return await host.Show(view, "DialogConfirmOverWidth", null, MotionCause.Keyboard) as string == PassResult;
    }
}
