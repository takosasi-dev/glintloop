// スポイト(FR-29): 色のパラメータの「スポイト」を押してからキャンバスをクリックすると、その点を含む画素の色の RGB をパラメータに入れる(AA は変えない)。
// 色を取る画像は EyedropperView の文書を共有の DocumentRenderer で描いたもの(DocumentRenderer は触らない)。画像の外のクリックは何も取らず続け、Esc で取り消す。
// PickRgbAsync は合成の座標を引数に取る関数で、テストが直接呼ぶ。キャンバスのクリックの受け取りは Eyedropper(画面の部品)が行う。
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using GlintLoop.App.Input;
using GlintLoop.App.Startup;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Stack;

public static class EyedropperPicker
{
    /// <summary>
    /// 合成の点 composite(合成のネイティブ寸法の座標)を含む画素の色を、"RRGGBB" で返す。対象がレイヤーで点が配置の枠の外、または対象が全体で点が合成の外のときは null。
    /// α が 0 の画素は "000000"(§9.4 の記法: α≤0.0001 なら C=0)。
    /// </summary>
    public static async Task<string?> PickRgbAsync(DocumentRenderer renderer, SessionDocument doc, Func<string, SizeD> sourceSize, Target target, Point composite,
        int frameK = 0, int frameN = 1)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(doc);
        var wc = CompositeGeometry.CompositeNative(doc, sourceSize);
        var proxyScale = CompositeGeometry.ProxyLongSide(wc) / Math.Max(wc.W, wc.H);
        Rect frame;
        SessionDocument view;
        int lout;
        if (target.IsAll)
        {
            frame = new Rect(0, 0, wc.W, wc.H);
            view = EyedropperView.ForAll(doc);
            lout = CompositeGeometry.ProxyLongSide(wc);
        }
        else
        {
            var g = HandleMath.Of(doc, target, sourceSize);
            frame = g.Frame;
            view = EyedropperView.ForLayer(doc, target.LayerIndex);
            var longSide = Math.Max(g.Native.W, g.Native.H);
            // 表示の解像度(プロキシ)の枠の長辺。元の大きさ(ネイティブ寸法)より大きくしない
            lout = (int)Math.Clamp(Math.Round(longSide * g.K * proxyScale, MidpointRounding.AwayFromZero), 1, Math.Max(1, Math.Ceiling(longSide)));
        }
        if (!(composite.X >= frame.X && composite.X < frame.Right && composite.Y >= frame.Y && composite.Y < frame.Bottom)) return null;
        // 全体のときは表示しているフレーム(frameK÷frameN)の合成(FR-29)。レイヤーのときはモーションを通さないので、フレームに依らない
        var image = await renderer.RenderAsync(view, lout, DocumentPixelFormat.StraightRgba8, frameK, frameN).ConfigureAwait(false);
        var x = Math.Clamp((int)Math.Floor((composite.X - frame.X) / frame.Width * image.Width), 0, image.Width - 1);
        var y = Math.Clamp((int)Math.Floor((composite.Y - frame.Y) / frame.Height * image.Height), 0, image.Height - 1);
        var i = (y * image.Width + x) * 4;
        var p = image.Pixels;
        if (p[i + 3] == 0) return "000000";
        return p[i].ToString("X2", CultureInfo.InvariantCulture) + p[i + 1].ToString("X2", CultureInfo.InvariantCulture)
            + p[i + 2].ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>色 #RRGGBBAA の RGB を rgb("RRGGBB")に替える(AA はそのまま)。</summary>
    public static string WithRgb(string color, string rgb)
    {
        var aa = ColorText.TryNormalizeRgba(color, out var c) ? c[7..] : "FF";
        return "#" + rgb.ToUpperInvariant() + aa;
    }
}

/// <summary>スポイトのモード(1 つの色のパラメータに対して 1 回)。キャンバスのクリックを受け、Esc で取り消す。</summary>
public sealed class Eyedropper
{
    private readonly AppShell _shell;
    private readonly StackModule _module;
    private (Target Target, long Salt, string Key)? _active;
    private bool _picking;

    public Eyedropper(AppShell shell, StackModule module)
    {
        _shell = shell;
        _module = module;
        shell.Router.Escape.Register(EscapeTarget.Eyedropper, () => IsActive, Cancel);
        shell.CanvasView.PreviewMouseLeftButtonDown += OnCanvasMouseDown;
    }

    public bool IsActive => _active is not null;

    /// <summary>スポイトを始めたパラメータ(テストと表示の確かめ)。</summary>
    public string? ActiveKey => _active?.Key;

    /// <summary>モードが変わったとき(色の部品のボタンの見た目を合わせる)。</summary>
    public event Action? Changed;

    /// <summary>選んでいる行の色のパラメータ key について始める。</summary>
    public void Start(Target target, long salt, string key)
    {
        _active = (target, salt, key);
        _shell.CanvasView.Cursor = Cursors.Cross;
        Changed?.Invoke();
    }

    /// <summary>取り消す(パラメータを変えない)。</summary>
    public void Cancel()
    {
        if (_active is null) return;
        _active = null;
        _shell.CanvasView.Cursor = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// 合成の点で色を取る。取れたら色のパラメータの RGB を替えて終わり、true を返す。画像の外・描画が無いときは何もせず続ける(false)。
    /// </summary>
    public async Task<bool> PickAtAsync(Point composite)
    {
        if (_active is not { } a || _picking || _shell.RendererReady is null) return false;
        var session = _shell.Session;
        var doc = session.Document;
        if (doc is null || doc.Selected != a.Target)
        {
            Cancel();
            return false;
        }
        _picking = true;
        // 表示しているフレーム(FR-29)。UI スレッドの値を、描画の前に控える
        var frames = (K: _shell.Frames.K, N: _shell.Frames.N);
        try
        {
            var renderer = await _shell.RendererReady;
            var rgb = await EyedropperPicker.PickRgbAsync(renderer, doc, session.SourceSize, a.Target, composite, frames.K, frames.N);
            if (rgb is null || _active != a) return false;
            var cur = session.Document;
            if (cur is null) return false;
            var stack = cur.StackOf(a.Target);
            var row = -1;
            for (var i = 0; i < stack.Count; i++)
            {
                if (stack[i].Salt == a.Salt) row = i;
            }
            if (row < 0 || stack[row].Get(a.Key) is not StringValue color)
            {
                Cancel();
                return false;
            }
            session.SetEffectParam(a.Target, row, a.Key, new StringValue(EyedropperPicker.WithRgb(color.Value, rgb)));
            Cancel();
            return true;
        }
        finally
        {
            _picking = false;
        }
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive) return;
        e.Handled = true;
        var p = _shell.CanvasTransform.ToDocument(e.GetPosition(_shell.CanvasView));
        _ = PickAtAsync(p);
    }

    internal StackModule Module => _module;
}
