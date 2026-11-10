// 置き換えの確認ダイアログ(FR-13・§9.3 の画面の表。幅 440 DIP): 文言は replace.confirm、ボタンは EditorSession.ReplaceButtons の並び(「保存して置き換え」「保存せず置き換え」「キャンセル」)。
// 既定のボタンは「キャンセル」(Enter と最初のフォーカス)。Esc と閉じる操作はキャンセルとして扱う。結果は AskAsync の Task で返す(UI スレッドを止めない)。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GlintLoop.App.Shell;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Screens;

public sealed class ReplaceConfirmView : StackPanel
{
    private readonly DialogHost _host;
    private readonly Dictionary<ReplaceChoice, Button> _buttons = new();

    public ReplaceConfirmView(DialogHost host, IReadOnlyList<ReplaceChoice> buttons, ReplaceChoice defaultButton)
    {
        _host = host;
        AutomationProperties.SetAutomationId(this, "ReplaceConfirm");
        var message = new TextBlock { Text = NoticeTexts.DialogMessage(NoticeKeys.ReplaceConfirm), TextWrapping = TextWrapping.Wrap };
        message.SetResourceReference(StyleProperty, "TextBody");
        message.SetResourceReference(MarginProperty, "Space4BottomThickness");
        Children.Add(message);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var c in buttons)
        {
            var b = new Button { Content = Label(c), IsDefault = c == defaultButton };
            if (c == ReplaceChoice.SaveAndReplace) b.SetResourceReference(StyleProperty, "PrimaryButton");
            AutomationProperties.SetName(b, Label(c));
            if (row.Children.Count > 0) b.SetResourceReference(MarginProperty, "Space2LeftThickness");
            var choice = c;
            b.Click += (_, _) => _host.Close(choice, MotionCause.Pointer);
            _buttons[c] = b;
            row.Children.Add(b);
        }
        Children.Add(row);
        DefaultButton = _buttons[defaultButton];
        // DialogHost が最初の部品にフォーカスを置いたあとで、既定のボタンへ移す
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(DefaultButton));
    }

    /// <summary>既定のボタン(キャンセル)。</summary>
    public Button DefaultButton { get; }

    public IReadOnlyDictionary<ReplaceChoice, Button> Buttons => _buttons;

    public static string Label(ReplaceChoice c) => NoticeTexts.UiText(c switch
    {
        ReplaceChoice.SaveAndReplace => "replace.confirm.save",
        ReplaceChoice.ReplaceWithoutSaving => "replace.confirm.discard",
        _ => "replace.confirm.cancel",
    });

    /// <summary>ダイアログを出して、押されたボタンを返す(Esc・閉じたときはキャンセル)。</summary>
    public static async Task<ReplaceChoice> AskAsync(DialogHost host, IReadOnlyList<ReplaceChoice> buttons, ReplaceChoice defaultButton = ReplaceChoice.Cancel)
    {
        var view = new ReplaceConfirmView(host, buttons, defaultButton);
        var result = await host.Show(view, "DialogReplaceWidth", null, MotionCause.Keyboard);
        return result is ReplaceChoice c ? c : ReplaceChoice.Cancel;
    }
}
