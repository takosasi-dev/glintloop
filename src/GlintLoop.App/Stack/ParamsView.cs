// 右ペインのパラメータ欄(FR-24・FR-27・FR-28・FR-31・FR-39・FR-40): 選んだ行のエフェクトのパラメータを、EffectDef の宣言から自動で作る(§9.2 の UI の自動生成の規則)。
// 並びは、強さ(amountMode が none でないとき)、共通パラメータ、そのほか。advanced は「詳細」(既定は閉じる)に入れ、visibleWhen で表示を切り替える。
// 行を選んでいないときは、対象が全体なら案内の文、レイヤーなら担当 layers のレイヤーの設定(StackModule.LayerSettingsFactory)を出す。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlintLoop.App.Params;
using GlintLoop.App.Text;
using GlintLoop.App.Theme;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

public sealed class ParamsView : UserControl
{
    private readonly StackModule _module;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _panel;
    private readonly List<ParamRow> _rows = new();
    private object? _builtKey;
    private ToggleButton? _advancedToggle;
    private Border? _advancedHost;
    private ContentControl? _layerHost;

    public ParamsView(StackModule module)
    {
        _module = module;
        AutomationProperties.SetName(this, StackTexts.ParamsName);
        _panel = new StackPanel();
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _panel,
        };
        _scroll.SetResourceReference(PaddingProperty, "Space3HorizontalThickness");
        Content = _scroll;
    }

    internal StackModule Module => _module;

    /// <summary>更新中(画面の値をセッションに合わせている間)は、部品の変更をセッションへ送らない。</summary>
    internal bool Updating { get; private set; }

    /// <summary>「詳細」の折りたたみの開閉(無ければ null)。</summary>
    public ToggleButton? AdvancedToggle => _advancedToggle;

    /// <summary>「詳細」の中身(無ければ null)。</summary>
    public FrameworkElement? AdvancedHost => _advancedHost;

    /// <summary>作ったパラメータの行(キーと行の根)。テストが部品の種類を確かめる。</summary>
    public IReadOnlyList<(string Key, FrameworkElement Root)> Rows => _rows.Select(r => (r.Key, (FrameworkElement)r.Root)).ToList();

    public void Refresh(SessionChange what)
    {
        var session = _module.Shell.Session;
        var doc = session.Document;
        if (doc is null)
        {
            ShowMessage("welcome", NoticeTexts.UiText("panes.disabled"));
            return;
        }
        var sel = _module.SelectedRow;
        if (sel is null)
        {
            var target = doc.Selected;
            if (target.IsAll)
            {
                ShowMessage("all", StackTexts.ParamsEmptyAll);
                return;
            }
            var key = ("layer", target.LayerIndex, doc.Layers.Count);
            if (Equals(_builtKey, key) && (what & SessionChange.Document) == 0) return;
            _builtKey = key;
            _rows.Clear();
            _panel.Children.Clear();
            ResetAdvanced();
            // 入れ物は使い回す(担当 layers の設定の部品は 1 つの実体なので、別の入れ物に入れ直すと論理の親が 2 つになる)
            if (_layerHost is null)
            {
                _layerHost = new ContentControl { Focusable = false, IsTabStop = false };
                AutomationProperties.SetAutomationId(_layerHost, "params.layerSettings");
            }
            _layerHost.Content = _module.LayerSettingsFactory?.Invoke(target);
            _panel.Children.Add(_layerHost);
            return;
        }
        var entry = doc.StackOf(sel.Target)[sel.Row];
        var def = _module.DefOf(entry.Id);
        var built = ("row", sel.Target, sel.Salt, entry.Id);
        if (def is null)
        {
            ShowMessage(built, entry.Id);
            return;
        }
        if (!Equals(_builtKey, built) || (what & SessionChange.Document) != 0)
        {
            Build(sel, entry, def);
            _builtKey = built;
        }
        UpdateValues(entry);
    }

    private void ShowMessage(object key, string text)
    {
        if (Equals(_builtKey, key)) return;
        _builtKey = key;
        _rows.Clear();
        _panel.Children.Clear();
        ResetAdvanced();
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, "TextBody");
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        t.SetResourceReference(MarginProperty, "Space3Thickness");
        AutomationProperties.SetAutomationId(t, "params.message");
        _panel.Children.Add(t);
    }

    private void ResetAdvanced()
    {
        _advancedToggle = null;
        _advancedHost = null;
    }

    // ------------------------------------------------ 作る

    private void Build(StackRowSelection sel, StackEntry entry, EffectDef def)
    {
        _rows.Clear();
        _panel.Children.Clear();
        ResetAdvanced();
        var ctx = new ParamRowContext(this, sel.Target, sel.Salt, def);

        // 見出し: 名前と「リセット」(FR-31)
        var head = new DockPanel { LastChildFill = true };
        head.SetResourceReference(MarginProperty, "Space2BottomThickness");
        var reset = new Button { Content = StackTexts.Reset };
        AutomationProperties.SetAutomationId(reset, "params.reset");
        reset.Click += (_, _) => ctx.Apply(row => _module.Shell.Session.ResetEffect(sel.Target, row));
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        var title = new TextBlock { Text = def.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(StyleProperty, "TextBodyStrong");
        head.Children.Add(title);
        _panel.Children.Add(head);

        // 切り抜きと余白の専用ボタン(FR-39・FR-40)
        if (!sel.Target.IsAll && def.Id is EffectSchema.Crop or EffectSchema.Pad) _panel.Children.Add(ShapeButtons(sel.Target, ctx, def.Id));

        if (def.AmountMode != AmountMode.None)
        {
            var amount = new AmountRow(ctx);
            _rows.Add(amount);
            _panel.Children.Add(amount.Root);
        }

        StackPanel? advanced = null;
        foreach (var p in EffectSchema.AllParams(def))
        {
            var row = CreateRow(ctx, p, entry);
            _rows.Add(row);
            if (p.Advanced)
            {
                advanced ??= new StackPanel();
                advanced.Children.Add(row.Root);
            }
            else
            {
                _panel.Children.Add(row.Root);
            }
        }
        if (advanced is not null) AddAdvanced(advanced);
    }

    private ParamRow CreateRow(ParamRowContext ctx, ParamDef p, StackEntry entry)
    {
        var shell = _module.Shell;
        var custom = shell.ParamEditors.TryCreate(new ParamEditorContext(shell, ctx.Target, ctx.CurrentRow(), ctx.Def, p));
        if (custom is not null) return new CustomRow(ctx, p, custom);
        return p.Type switch
        {
            ParamType.Float or ParamType.Int or ParamType.Angle => new NumberRow(ctx, p),
            ParamType.Bool => new BoolRow(ctx, p),
            ParamType.Enum => new EnumRow(ctx, p),
            ParamType.Color => new ColorRow(ctx, p),
            ParamType.Point => new PointRow(ctx, p),
            ParamType.Rect => new RectRow(ctx, p),
            _ => new TextRow(ctx, p),
        };
    }

    private void AddAdvanced(StackPanel content)
    {
        var toggle = new ToggleButton { IsChecked = false, Width = double.NaN, HorizontalAlignment = HorizontalAlignment.Left };
        toggle.SetResourceReference(StyleProperty, "IconToggleButton");
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        var chevron = new TextBlock { Text = Icons.ChevronDown.ToString(), VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamilyIcon");
        chevron.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        chevron.SetResourceReference(MarginProperty, "Space1RightThickness");
        label.Children.Add(chevron);
        label.Children.Add(new TextBlock { Text = StackTexts.Advanced, VerticalAlignment = VerticalAlignment.Center });
        toggle.Content = label;
        toggle.SetResourceReference(PaddingProperty, "Space2HorizontalThickness");
        AutomationProperties.SetName(toggle, StackTexts.Advanced);
        AutomationProperties.SetAutomationId(toggle, "params.advanced");
        var host = new Border { Child = content, Visibility = Visibility.Collapsed, ClipToBounds = true };
        toggle.Click += (_, _) => SetAdvancedOpen(host, toggle.IsChecked == true,
            InputManager.Current.MostRecentInputDevice is KeyboardDevice ? MotionCause.Keyboard : MotionCause.Pointer);
        _panel.Children.Add(toggle);
        _panel.Children.Add(host);
        _advancedToggle = toggle;
        _advancedHost = host;
    }

    /// <summary>「詳細」の開閉。高さを pane の動き(200 ms・ease-in-out)で変える(キーボードと縮小のときは動かさない)。</summary>
    private void SetAdvancedOpen(Border host, bool open, MotionCause cause)
    {
        var duration = Motion.Duration(this, MotionToken.Pane, cause);
        host.BeginAnimation(HeightProperty, null);
        if (open)
        {
            host.Visibility = Visibility.Visible;
            host.Height = double.NaN;
            host.Measure(new Size(Math.Max(0, _panel.ActualWidth), double.PositiveInfinity));
            var to = host.DesiredSize.Height;
            var a = Motion.DoubleTo(to, duration, Motion.Easing(this, MotionToken.Pane), 0);
            if (a is null) return;
            a.Completed += (_, _) =>
            {
                host.BeginAnimation(HeightProperty, null);
                host.Height = double.NaN;
            };
            host.BeginAnimation(HeightProperty, a);
        }
        else
        {
            var a = Motion.DoubleTo(0, duration, Motion.Easing(this, MotionToken.Pane), host.ActualHeight);
            if (a is null)
            {
                host.Visibility = Visibility.Collapsed;
                return;
            }
            a.Completed += (_, _) =>
            {
                if (_advancedToggle?.IsChecked == true) return;
                host.BeginAnimation(HeightProperty, null);
                host.Height = double.NaN;
                host.Visibility = Visibility.Collapsed;
            };
            host.BeginAnimation(HeightProperty, a);
        }
    }

    private FrameworkElement ShapeButtons(Target target, ParamRowContext ctx, string id)
    {
        var panel = new WrapPanel();
        panel.SetResourceReference(MarginProperty, "Space2BottomThickness");
        Button Add(string text, string automationId, Action click)
        {
            var b = new Button { Content = text };
            b.SetResourceReference(MarginProperty, "Space1RightThickness");
            AutomationProperties.SetAutomationId(b, automationId);
            b.Click += (_, _) => click();
            panel.Children.Add(b);
            return b;
        }
        var session = _module.Shell.Session;
        Layer? LayerNow() => session.Document is { } d && !target.IsAll && target.LayerIndex < d.Layers.Count ? d.Layers[target.LayerIndex] : null;
        if (id == EffectSchema.Crop)
        {
            Add(StackTexts.SquareCrop, "params.squareCrop", () =>
            {
                if (LayerNow() is not { } l) return;
                var input = CropTools.ShapeInputs(l, session.SourceSize).CropInput;
                ctx.Set("rect", CropTools.SquareCrop(input));
            });
            Add(StackTexts.AutoTrim, "params.autoTrim", () =>
            {
                if (LayerNow() is not { } l || l.Kind != LayerKind.Image || l.Asset is null) return;
                if (!_module.Shell.ImportTarget.Images.TryGetValue(l.Asset, out var im)) return;
                if (CropTools.AutoTrim(im.Width, im.Height, im.Pixels) is { } r) ctx.Set("rect", r);
            });
        }
        else
        {
            Add(StackTexts.SquarePad, "params.squarePad", () =>
            {
                if (LayerNow() is not { } l) return;
                var input = CropTools.ShapeInputs(l, session.SourceSize).PadInput;
                ctx.Set("rect", CropTools.SquarePad(input));
            });
        }
        return panel;
    }

    // ------------------------------------------------ 値を合わせる

    private void UpdateValues(StackEntry entry)
    {
        Updating = true;
        try
        {
            foreach (var r in _rows)
            {
                r.Update(entry);
                r.Root.Visibility = r.IsVisible(entry) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            Updating = false;
        }
    }
}

/// <summary>パラメータの行が共有する情報: 対象・行の salt・EffectDef と、値をセッションへ送る口。</summary>
internal sealed class ParamRowContext(ParamsView view, Target target, long salt, EffectDef def)
{
    public ParamsView View { get; } = view;
    public Target Target { get; } = target;
    public long Salt { get; } = salt;
    public EffectDef Def { get; } = def;

    private StackModule Module => View.Module;

    /// <summary>いまの文書での行の番号(無ければ -1)。</summary>
    public int CurrentRow()
    {
        var doc = Module.Shell.Session.Document;
        if (doc is null || doc.Selected != Target) return -1;
        var stack = doc.StackOf(Target);
        for (var i = 0; i < stack.Count; i++)
        {
            if (stack[i].Salt == Salt) return i;
        }
        return -1;
    }

    public void Set(string key, ParamValue value)
    {
        if (View.Updating) return;
        Apply(row => Module.Shell.Session.SetEffectParam(Target, row, key, value));
    }

    public void SetAmount(double value)
    {
        if (View.Updating) return;
        Apply(row => Module.Shell.Session.SetEffectAmount(Target, row, value));
    }

    public void Apply(Func<int, EditOutcome> op)
    {
        var row = CurrentRow();
        if (row < 0) return;
        var o = op(row);
        if (o.Notice is { } n) Module.Shell.NoticeBar.Show(n);
    }

    public void BeginContinuous() => Module.Shell.Session.BeginContinuous();

    public void EndContinuous() => Module.Shell.Session.EndContinuous();

    public T Resource<T>(string key) => (T)View.FindResource(key);
}
