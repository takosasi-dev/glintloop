// パラメータ欄の font の入力部品(§9.2 の「フォントの一覧」、FR-38)。システムのフォントを名前順に並べ、各項目をそのフォントで表示する(一覧は仮想化して必要な分だけ作る)。
// 保存した名前がこの PC に無いときは、その名前を先頭の仮の項目として見せ、値は変えない(E-26。描くのは Yu Gothic UI)。選び直したときだけ Session.SetEffectParam で反映する。
// 一覧の名前は SkiaSharp のファミリ名(InstalledFonts)で、描画が引く名前と同じ。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GlintLoop.App.Params;
using GlintLoop.Codec.Text;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.TextEffect;

/// <summary>一覧の 1 項目。Family は項目の表示に使う WPF のフォント。Missing はこの PC に無い保存済みの名前の仮の項目。</summary>
public sealed record FontItem(string Name, FontFamily Family, bool Missing)
{
    public override string ToString() => Name;
}

public sealed class FontParamEditor : UserControl
{
    private static readonly Lazy<IReadOnlyList<FontItem>> Installed = new(() =>
        InstalledFonts.Families.Select(n => new FontItem(n, new FontFamily(n), false)).ToList());

    private readonly ParamEditorContext _ctx;
    private bool _setting;
    private bool _subscribed;

    public FontParamEditor(ParamEditorContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        List = new ComboBox { ItemTemplate = ItemTemplate(), IsEditable = false };
        VirtualizingPanel.SetIsVirtualizing(List, true);
        VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(List, true);
        List.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        AutomationProperties.SetName(List, ctx.Param.Label);
        Content = List;
        IsTabStop = false;
        Focusable = false;
        List.SelectionChanged += OnSelectionChanged;
        Loaded += (_, _) => Subscribe(true);
        Unloaded += (_, _) => Subscribe(false);
        Show(ReadValue());
    }

    /// <summary>一覧の部品(テストが直接触る)。</summary>
    public ComboBox List { get; }

    /// <summary>インストールされているフォントの項目(名前順)。</summary>
    public static IReadOnlyList<FontItem> InstalledItems => Installed.Value;

    private static DataTemplate ItemTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FontItem.Name)));
        text.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(FontItem.Family)));
        return new DataTemplate(typeof(FontItem)) { VisualTree = text };
    }

    private string ReadValue()
    {
        var def = _ctx.Param.Default is StringValue d ? d.Value : TextSpec.DefaultFont;
        var doc = _ctx.Shell.Session.Document;
        if (doc is null) return def;
        var stack = doc.StackOf(_ctx.Target);
        if (_ctx.Row < 0 || _ctx.Row >= stack.Count || stack[_ctx.Row].Id != _ctx.Effect.Id) return def;
        return stack[_ctx.Row].Get(_ctx.Param.Key) is StringValue s ? s.Value : def;
    }

    /// <summary>name を選んだ状態にする。一覧に無い名前は先頭に仮の項目を置いて選ぶ(既定のフォントで表示する)。</summary>
    private void Show(string name)
    {
        if (List.SelectedItem is FontItem cur && cur.Name == name) return;
        _setting = true;
        try
        {
            var installed = InstalledItems;
            var hit = installed.FirstOrDefault(i => i.Name == name);
            if (hit is null)
            {
                hit = new FontItem(name, new FontFamily(TextSpec.DefaultFont), true);
                List.ItemsSource = new[] { hit }.Concat(installed).ToList();
            }
            else if (!ReferenceEquals(List.ItemsSource, installed))
            {
                List.ItemsSource = installed;
            }
            List.SelectedItem = hit;
        }
        finally
        {
            _setting = false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_setting || List.SelectedItem is not FontItem item) return;
        if (_ctx.Shell.Session.HasDocument)
            _ctx.Shell.Session.SetEffectParam(_ctx.Target, _ctx.Row, _ctx.Param.Key, new StringValue(item.Name));
        if (!item.Missing && List.ItemsSource is IReadOnlyList<FontItem> list && list.Count > 0 && list[0].Missing) Show(item.Name);
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
