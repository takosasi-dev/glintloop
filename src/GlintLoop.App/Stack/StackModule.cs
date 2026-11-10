// 担当 stack の部品を AppShell につなぐ(IShellModule、Order 200): 右ペインのスタック(StackHost)・パラメータ欄(ParamsHost)・左ペインのエフェクトのタブの中身・
// キャンバスのエフェクトのハンドル・スポイトを作り、スタックの操作のショートカット(複製・削除・並べ替え・検索欄)と Esc を登録する(Undo・Redo は担当 canvas)。
// スタックの行の選択(1 つだけ。履歴に入らない)はここが持ち、行を salt で覚える。左ペインのタブ・すべてリセット・レイヤーの設定は統合の ModuleWiring がつなぐ。
using System.Windows;
using System.Windows.Controls;
using GlintLoop.App.Input;
using GlintLoop.App.Startup;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

/// <summary>選んでいるスタックの行。Salt で行を覚え、Row はいまの文書での番号。</summary>
public sealed record StackRowSelection(Target Target, long Salt, int Row, string EffectId);

public sealed class StackModule : IShellModule, IDisposable
{
    private AppShell? _shell;
    private StackRowSelection? _selected;

    public int Order => 200;

    public AppShell Shell => _shell ?? throw new InvalidOperationException("Attach の前");

    public StackView StackView { get; private set; } = null!;

    public ParamsView ParamsView { get; private set; } = null!;

    /// <summary>左ペインの「エフェクト」のタブの中身(担当 looks の LeftPaneView.EffectsContent に入れる)。</summary>
    public EffectsTabView EffectsTab { get; private set; } = null!;

    /// <summary>左ペインの「エフェクト」のタブを選ぶ(Ctrl+F の検索欄の前。担当 looks の SelectTab をつなぐ)。選べなければ false(文書が無いとき)。</summary>
    public Func<bool>? SelectEffectsTab { get; set; }

    /// <summary>キャンバスのエフェクトのハンドル(OverlayLayer に入れる)。</summary>
    public EffectHandleOverlay Handles { get; private set; } = null!;

    public Eyedropper Eyedropper { get; private set; } = null!;

    /// <summary>
    /// 対象がレイヤーで行を選んでいないときにパラメータ欄に出す、レイヤーの設定(FR-48。担当 layers が入れる)。null ならパラメータ欄は空。
    /// </summary>
    public Func<Target, FrameworkElement?>? LayerSettingsFactory { get; set; }

    /// <summary>選んでいる行(無ければ null)。</summary>
    public StackRowSelection? SelectedRow => _selected;

    /// <summary>選んでいる行がハンドルを持つか(FR-21。行の値で visibleWhen を見る。レイヤーの枠のハンドルはこのとき出さない。FR-50)。</summary>
    public bool SelectedRowHasHandles
    {
        get
        {
            if (_selected is not { } s || _shell?.Session.Document is not { } doc || doc.Selected != s.Target) return false;
            var stack = doc.StackOf(s.Target);
            if (s.Row < 0 || s.Row >= stack.Count || stack[s.Row].Salt != s.Salt) return false;
            return _shell.Catalog.Find(s.EffectId) is { } def && HandleMath.HasHandles(def, stack[s.Row]);
        }
    }

    /// <summary>行の選択が変わったとき(UI スレッド)。</summary>
    public event Action? SelectedRowChanged;

    public void Attach(AppShell shell)
    {
        _shell = shell;
        StackView = new StackView(this);
        ParamsView = new ParamsView(this);
        EffectsTab = new EffectsTabView(this);
        Handles = new EffectHandleOverlay(this);
        Eyedropper = new Eyedropper(shell, this);

        var w = shell.Window;
        w.StackHost.Content = StackView;
        w.ParamsHost.Content = ParamsView;
        shell.CanvasView.OverlayLayer.Add(Handles);

        var r = shell.Router;
        r.Register(ShortcutAction.StackDuplicate, _ => Selected(out var s) && Notify(Shell.Session.DuplicateEffect(s.Target, s.Row), select: true));
        r.Register(ShortcutAction.StackDelete, _ => Selected(out var s) && Notify(Shell.Session.RemoveEffect(s.Target, s.Row), select: false));
        r.Register(ShortcutAction.StackMoveUp, _ => Selected(out var s) && Move(s, -1));
        r.Register(ShortcutAction.StackMoveDown, _ => Selected(out var s) && Move(s, +1));
        r.Register(ShortcutAction.FocusSearch, _ => SelectEffectsTab?.Invoke() == true && EffectsTab.FocusSearch());
        r.Escape.Register(EscapeTarget.StackSelection, () => _selected is not null, ClearRowSelection);

        shell.Session.Changed += OnSessionChanged;
        Refresh(SessionChange.Document | SessionChange.AllState);
    }

    private void OnSessionChanged(SessionChange what)
    {
        var d = Shell.Window.Dispatcher;
        if (d.CheckAccess()) Refresh(what);
        else d.BeginInvoke(() => Refresh(what));
    }

    /// <summary>セッションの変更を画面に写す(UI スレッド)。</summary>
    private void Refresh(SessionChange what)
    {
        var doc = Shell.Session.Document;
        var before = _selected;
        if (doc is null || (what & SessionChange.Document) != 0 || (what & SessionChange.Selection) != 0 && _selected is { } old && old.Target != doc.Selected)
        {
            _selected = null;
        }
        else if (_selected is { } s)
        {
            _selected = Resolve(doc, s.Target, s.Salt);
        }
        if (doc is null && Eyedropper.IsActive) Eyedropper.Cancel();
        StackView.Refresh(what);
        ParamsView.Refresh(what);
        EffectsTab.Refresh(what);
        Handles.Refresh();
        if (before != _selected) SelectedRowChanged?.Invoke();
    }

    private static StackRowSelection? Resolve(SessionDocument doc, Target t, long salt)
    {
        if (doc.Selected != t) return null;
        var stack = doc.StackOf(t);
        for (var i = 0; i < stack.Count; i++)
        {
            if (stack[i].Salt == salt) return new StackRowSelection(t, salt, i, stack[i].Id);
        }
        return null;
    }

    /// <summary>いまの対象のスタックの行 row を選ぶ(番号が範囲外なら選択を外す)。</summary>
    public void SelectRow(int row)
    {
        var doc = Shell.Session.Document;
        StackRowSelection? next = null;
        if (doc is not null)
        {
            var stack = doc.StackOf(doc.Selected);
            if (row >= 0 && row < stack.Count) next = new StackRowSelection(doc.Selected, stack[row].Salt, row, stack[row].Id);
        }
        SetSelection(next);
    }

    /// <summary>行の選択を外す(FR-48: パラメータ欄はレイヤーの設定か、全体の案内に戻る)。</summary>
    public void ClearRowSelection() => SetSelection(null);

    private void SetSelection(StackRowSelection? next)
    {
        if (next == _selected) return;
        if (Eyedropper.IsActive) Eyedropper.Cancel();
        _selected = next;
        StackView.UpdateSelection();
        ParamsView.Refresh(SessionChange.Selection);
        Handles.Refresh();
        SelectedRowChanged?.Invoke();
    }

    private bool Selected(out StackRowSelection s)
    {
        s = _selected!;
        return _selected is not null && Shell.Session.HasDocument;
    }

    private bool Move(StackRowSelection s, int dir)
    {
        var stack = Shell.Session.Document!.StackOf(s.Target);
        var to = s.Row + dir;
        if (to >= 0 && to < stack.Count) Shell.Session.MoveEffect(s.Target, s.Row, to);
        StackView.FocusSelected();
        return true;
    }

    /// <summary>操作の結果の通知を出し、select なら足した行(Row)を選ぶ。キーは処理済みにする。</summary>
    internal bool Notify(EditOutcome o, bool select)
    {
        if (o.Notice is { } n) Shell.NoticeBar.Show(n);
        if (select && o.Changed && o.Row is int row) SelectRow(row);
        StackView.FocusSelected();
        return true;
    }

    /// <summary>エフェクトを、選んでいる対象のスタックのそのステージの末尾に足し、足した行を選ぶ(FR-26)。</summary>
    public EditOutcome AddEffect(string effectId)
    {
        var s = Shell.Session;
        if (!s.HasDocument) return EditOutcome.Unchanged;
        var o = s.AddEffect(s.Selected, effectId);
        if (o.Notice is { } n) Shell.NoticeBar.Show(n);
        if (o.Changed && o.Row is int row) SelectRow(row);
        return o;
    }

    /// <summary>すべてリセット(FR-32)。</summary>
    public void ResetAll()
    {
        if (Shell.Session.HasDocument) Shell.Session.ResetAll();
    }

    /// <summary>行のエフェクトの定義(カタログに無ければ null)。</summary>
    internal EffectDef? DefOf(string id) => Shell.Catalog.Find(id);

    public void Dispose()
    {
        if (_shell is not null) _shell.Session.Changed -= OnSessionChanged;
    }
}
