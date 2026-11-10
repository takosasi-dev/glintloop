// テキストのエフェクトを画面につなぐ(担当 text。Order 500): パラメータ欄の text と font の入力部品を登録する(テキストのビットマップの受け口は AppShell が描画を作るときに入れる)。
// 文書の text の行の font が、この PC に無いフォントを指すときは、通知バーに font.missing を出す(文書ごと・名前ごとに 1 回。値は保存した名前のまま: E-26)。
// キャンバスのテキストのハンドル(FR-21)は担当 stack が作るので、ここでは作らない(塊の矩形は Rasterizer.Measure で引ける)。
using GlintLoop.App.Startup;
using GlintLoop.App.Text;
using GlintLoop.Codec.Text;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;

namespace GlintLoop.App.TextEffect;

public sealed class TextModule : IShellModule
{
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private AppShell? _shell;

    public int Order => 500;

    /// <summary>テキストのビットマップを描く受け口(AppShell.TextRasterizer。共有の DocumentRenderer に入っているもの。ハンドルの位置を求めるときにも使える)。</summary>
    public ITextRasterizer Rasterizer => _shell?.TextRasterizer ?? throw new InvalidOperationException("Attach の前");

    public void Attach(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        shell.ParamEditors.Register(ParamType.Text, ctx => new TextParamEditor(ctx));
        shell.ParamEditors.Register(ParamType.Font, ctx => new FontParamEditor(ctx));
        shell.Session.Changed += OnSessionChanged;
        CheckFonts(reset: true);
    }

    private void OnSessionChanged(SessionChange what)
    {
        if ((what & (SessionChange.Document | SessionChange.Stack | SessionChange.Params)) == 0) return;
        var reset = what.HasFlag(SessionChange.Document);
        var d = _shell!.Window.Dispatcher;
        if (d.CheckAccess()) CheckFonts(reset);
        else d.BeginInvoke(() => CheckFonts(reset));
    }

    /// <summary>文書の text の行が指すフォントのうち、この PC に無いもの(名前の重複なし、見つけた順)。</summary>
    public static IReadOnlyList<string> MissingFonts(SessionDocument? doc)
    {
        var result = new List<string>();
        if (doc is null) return result;
        foreach (var stack in doc.Layers.Select(l => l.Stack).Append(doc.All.Stack))
        {
            foreach (var e in stack)
            {
                if (e.Id != TextSpec.EffectId) continue;
                var name = e.Get("font") is StringValue s ? s.Value : TextSpec.DefaultFont;
                if (!InstalledFonts.IsInstalled(name) && !result.Contains(name, StringComparer.Ordinal)) result.Add(name);
            }
        }
        return result;
    }

    /// <summary>無いフォントを指す行があれば、まだ知らせていない名前があるときだけ font.missing を出す。reset は文書が入れ替わったとき。</summary>
    private void CheckFonts(bool reset)
    {
        var shell = _shell!;
        if (reset) _notified.Clear();
        var fresh = false;
        foreach (var name in MissingFonts(shell.Session.Document))
            fresh |= _notified.Add(name);
        if (fresh) shell.NoticeBar.Show(NoticeTexts.Notice(NoticeKeys.FontMissing));
    }
}
