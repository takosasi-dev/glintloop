// ルックの名前の入力と上書きの確認のダイアログの中身(§9.3 の画面の表の「名前の入力」「上書きの確認」・E-29)。shell.Dialogs.Show で重ねて出す。
// 名前の入力: 見出し(subtitle)・1 行の入力欄(40 コードポイントで止め、貼り付けは先頭の 40 で切り、改行を除く)・決定(既定のボタン)とキャンセル。空の間は決定を無効にする。
// 上書きの確認: name.overwrite の文言と「上書き」「キャンセル」。既定のボタンと最初のフォーカスは「キャンセル」。結果は Dialogs.Close の値で返す。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Shell;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Looks;

namespace GlintLoop.App.Looks;

/// <summary>名前の入力の種類(保存か変更か)。</summary>
public enum NameDialogMode { Save, Rename }

public sealed class NameInputDialog : StackPanel
{
    private readonly DialogHost _host;
    private bool _limiting;

    public NameInputDialog(DialogHost host, NameDialogMode mode, string initial)
    {
        _host = host;
        Mode = mode;
        AutomationProperties.SetAutomationId(this, "NameInputDialog");
        Title = new TextBlock { Text = LooksText.Get(mode == NameDialogMode.Save ? "name.title.save" : "name.title.rename") };
        Title.SetResourceReference(StyleProperty, "TextSubtitle");
        Title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Title.SetResourceReference(MarginProperty, "Space4BottomThickness");
        Children.Add(Title);

        Input = new TextBox { Text = LookNames.LimitInput(initial), AcceptsReturn = false };
        AutomationProperties.SetName(Input, LooksText.Get("name.field"));
        Input.TextChanged += (_, _) => OnTextChanged();
        Children.Add(Input);

        Confirm = new Button { Content = LooksText.Get(mode == NameDialogMode.Save ? "name.save" : "name.rename"), IsDefault = true };
        Confirm.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetName(Confirm, (string)Confirm.Content);
        Confirm.Click += (_, _) =>
        {
            if (LookNames.IsValid(Input.Text)) _host.Close(LookNames.Clean(Input.Text));
        };
        Cancel = new Button { Content = LooksText.Get("name.cancel") };
        AutomationProperties.SetName(Cancel, (string)Cancel.Content);
        Cancel.SetResourceReference(MarginProperty, "Space2LeftThickness");
        Cancel.Click += (_, _) => _host.Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.SetResourceReference(MarginProperty, "Space4TopThickness");
        buttons.Children.Add(Confirm);
        buttons.Children.Add(Cancel);
        Children.Add(buttons);
        Input.CaretIndex = Input.Text.Length;
        UpdateConfirm();
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public NameDialogMode Mode { get; }

    public TextBlock Title { get; }

    public TextBox Input { get; }

    public Button Confirm { get; }

    public Button Cancel { get; }

    private void OnTextChanged()
    {
        if (_limiting) return;
        var limited = LookNames.LimitInput(Input.Text);
        if (limited != Input.Text)
        {
            _limiting = true;
            try
            {
                Input.Text = limited;
                Input.CaretIndex = limited.Length;
            }
            finally
            {
                _limiting = false;
            }
        }
        UpdateConfirm();
    }

    private void UpdateConfirm() => Confirm.IsEnabled = LookNames.IsValid(Input.Text);

    /// <summary>出して、決定した名前(前後の空白を除いたもの)を返す。キャンセルと Esc は null。</summary>
    public static async Task<string?> AskAsync(DialogHost host, NameDialogMode mode, string initial, MotionCause cause)
    {
        var d = new NameInputDialog(host, mode, initial);
        return await host.Show(d, "DialogNameWidth", null, cause) as string;
    }
}

public sealed class OverwriteConfirmDialog : StackPanel
{
    /// <summary>上書きを選んだときの Close の値。</summary>
    public const string OverwriteResult = "overwrite";

    public OverwriteConfirmDialog(DialogHost host)
    {
        AutomationProperties.SetAutomationId(this, "OverwriteConfirmDialog");
        Message = new TextBlock { Text = NoticeTexts.DialogMessage("name.overwrite"), TextWrapping = TextWrapping.Wrap };
        Message.SetResourceReference(StyleProperty, "TextBody");
        Message.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Children.Add(Message);
        Overwrite = new Button { Content = NoticeTexts.UiText("name.overwrite.overwrite") };
        AutomationProperties.SetName(Overwrite, (string)Overwrite.Content);
        Overwrite.Click += (_, _) => host.Close(OverwriteResult);
        Cancel = new Button { Content = NoticeTexts.UiText("name.overwrite.cancel"), IsDefault = true };
        AutomationProperties.SetName(Cancel, (string)Cancel.Content);
        Cancel.SetResourceReference(MarginProperty, "Space2LeftThickness");
        Cancel.Click += (_, _) => host.Close(null);
        // 最初のフォーカスは既定のボタン(キャンセル)。DialogHost は Tab の順の最初へフォーカスを移すので、キャンセルを先にする
        KeyboardNavigation.SetTabIndex(Cancel, 0);
        KeyboardNavigation.SetTabIndex(Overwrite, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.SetResourceReference(MarginProperty, "Space4TopThickness");
        buttons.Children.Add(Overwrite);
        buttons.Children.Add(Cancel);
        Children.Add(buttons);
        Loaded += (_, _) => Cancel.Focus();
    }

    public TextBlock Message { get; }

    public Button Overwrite { get; }

    public Button Cancel { get; }

    /// <summary>出して、上書きを選んだら true(キャンセル・Esc・Enter は false)。</summary>
    public static async Task<bool> AskAsync(DialogHost host, MotionCause cause)
    {
        var d = new OverwriteConfirmDialog(host);
        return await host.Show(d, "DialogOverwriteWidth", null, cause) as string == OverwriteResult;
    }
}
