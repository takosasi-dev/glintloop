// パラメータ欄の text の入力部品(§9.2 の「複数行の入力欄」、FR-37・E-27)。文字 200 個・改行 5 回まで入り、はみ出す入力は受け付けず、貼り付けは先頭から切る。
// 改行は LF 1 個にそろえて Session.SetEffectParam で反映する。Undo などで行の値が外から変わったら、欄の文字を読み直す。
// 規則の本体は Core の TextLimits。キー入力・Enter・貼り付けの入口で切り、ほかの経路(ドロップなど)ではみ出したときは直前の文字に戻す。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GlintLoop.App.Params;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.TextEffect;

public sealed class TextParamEditor : UserControl
{
    private readonly ParamEditorContext _ctx;
    private string _good = "";
    private bool _setting;
    private bool _subscribed;

    public TextParamEditor(ParamEditorContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        Box = new TextBox
        {
            AcceptsReturn = false, // Enter は自分で扱う(LF 1 個を入れ、6 回目を受け付けない)
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinLines = 3,
        };
        AutomationProperties.SetName(Box, ctx.Param.Label);
        Content = Box;
        IsTabStop = false;
        Focusable = false;

        Box.PreviewTextInput += OnPreviewTextInput;
        Box.PreviewKeyDown += OnPreviewKeyDown;
        Box.TextChanged += OnTextChanged;
        DataObject.AddPastingHandler(Box, OnPasting);
        Loaded += (_, _) => Subscribe(true);
        Unloaded += (_, _) => Subscribe(false);
        Show(ReadValue());
    }

    /// <summary>入力欄(テストが直接触る)。</summary>
    public TextBox Box { get; }

    /// <summary>行の今の値(無ければ既定値)。</summary>
    private string ReadValue()
    {
        var doc = _ctx.Shell.Session.Document;
        if (doc is null) return Default();
        var stack = doc.StackOf(_ctx.Target);
        if (_ctx.Row < 0 || _ctx.Row >= stack.Count || stack[_ctx.Row].Id != _ctx.Effect.Id) return Default();
        return stack[_ctx.Row].Get(_ctx.Param.Key) is StringValue s ? TextLimits.NormalizeNewlines(s.Value) : Default();
    }

    private string Default() => _ctx.Param.Default is StringValue s ? s.Value : "";

    private void Show(string text)
    {
        _good = text;
        if (Box.Text == text) return;
        _setting = true;
        try
        {
            var caret = Math.Min(Box.CaretIndex, text.Length);
            Box.Text = text;
            Box.CaretIndex = caret;
        }
        finally
        {
            _setting = false;
        }
    }

    /// <summary>選択の範囲を inserted で置き換える(上限を超える分は先頭から切る)。何も変わらなければ false。</summary>
    public bool InsertText(string inserted)
    {
        var current = Box.Text;
        var (text, caret) = TextLimits.Insert(current, Box.SelectionStart, Box.SelectionLength, inserted);
        if (text == current) return false;
        _setting = true;
        try
        {
            Box.Text = text;
            Box.CaretIndex = caret;
        }
        finally
        {
            _setting = false;
        }
        Accept(text);
        return true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var (text, _) = TextLimits.Insert(Box.Text, Box.SelectionStart, Box.SelectionLength, e.Text);
        var full = Box.Text[..Box.SelectionStart] + TextLimits.NormalizeNewlines(e.Text) + Box.Text[(Box.SelectionStart + Box.SelectionLength)..];
        if (text == full && e.Text.IndexOf('\r') < 0) return; // 全部が入るなら TextBox に任せる(IME の確定も同じ経路)
        e.Handled = true;
        InsertText(e.Text);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        InsertText("\n");
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        var data = e.DataObject;
        var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string
            : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string
            : null;
        if (!string.IsNullOrEmpty(text)) InsertText(text);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_setting) return;
        var text = Box.Text;
        var normalized = TextLimits.NormalizeNewlines(text);
        if (!TextLimits.Fits(normalized))
        {
            // 入口で切れなかった経路(ドロップなど)ではみ出したときは、直前の文字に戻す(受け付けない)
            Show(_good);
            return;
        }
        if (normalized != text) Show(normalized);
        Accept(normalized);
    }

    /// <summary>欄の文字を行の値として反映する。</summary>
    private void Accept(string text)
    {
        _good = text;
        if (_ctx.Shell.Session.HasDocument) _ctx.Shell.Session.SetEffectParam(_ctx.Target, _ctx.Row, _ctx.Param.Key, new StringValue(text));
    }

    private void Subscribe(bool on)
    {
        if (on == _subscribed) return;
        _subscribed = on;
        if (on)
        {
            _ctx.Shell.Session.Changed += OnSessionChanged;
            Show(ReadValue());
        }
        else
        {
            _ctx.Shell.Session.Changed -= OnSessionChanged;
        }
    }

    private void OnSessionChanged(SessionChange what)
    {
        if ((what & (SessionChange.Document | SessionChange.Stack | SessionChange.Params)) == 0) return;
        var d = Dispatcher;
        if (d.CheckAccess()) Show(ReadValue());
        else d.BeginInvoke(() => Show(ReadValue()));
    }
}
