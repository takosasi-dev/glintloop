// ペインの寸法の規則(仕様書 §9.3): 左右のペインの幅の最小・最大と合計の制限、右ペインの縦の配分(起動時・高さの変化・境界の移動)。
// 座標を引数に取る純粋な関数にして、テストが直接確かめる(§11 Phase 3 の前書きの (b))。寸法の値は Tokens.Layout.xaml から Metrics で渡す。
using System.Windows;

namespace GlintLoop.App.Shell;

public enum PaneSide { Left, Right }

/// <summary>右ペインの縦の 3 つの領域の高さ(見出しの行と境界の隙間を除く)。</summary>
public readonly record struct RightPaneHeights(double Layers, double Stack, double Params)
{
    public double Total => Layers + Stack + Params;
}

public static class PaneLayout
{
    /// <summary>§9.3 の寸法。</summary>
    public sealed record Metrics(
        double Gap, double LeftMin, double LeftMax, double RightMin, double RightMax, double CanvasMin,
        double HeaderHeight, double LayersMin, double LayersDefault, double StackMin, double ParamsMin)
    {
        public static Metrics From(ResourceDictionary r) => new(
            (double)r["Space2"], (double)r["LeftPaneMinWidth"], (double)r["LeftPaneMaxWidth"], (double)r["RightPaneMinWidth"], (double)r["RightPaneMaxWidth"],
            (double)r["CanvasMinWidth"], (double)r["RightPaneHeaderHeight"], (double)r["LayerListMinHeight"], (double)r["LayerListDefaultHeight"],
            (double)r["StackMinHeight"], (double)r["ParamsMinHeight"]);
    }

    /// <summary>
    /// 左右の幅を最小・最大に収め、合計を「ウィンドウの幅 − キャンバスの最小 − 隙間 2 つ」以下にする。超えるときは動かしている方(復元では右)を縮め、
    /// それでも超えるときは、もう一方も最小まで縮める。
    /// </summary>
    public static (double Left, double Right) ClampWidths(Metrics m, double left, double right, double windowWidth, PaneSide moving)
    {
        left = Math.Clamp(left, m.LeftMin, m.LeftMax);
        right = Math.Clamp(right, m.RightMin, m.RightMax);
        var limit = windowWidth - m.CanvasMin - 2 * m.Gap;
        if (left + right <= limit) return (left, right);
        if (moving == PaneSide.Right)
        {
            right = Math.Max(m.RightMin, limit - left);
            if (left + right > limit) left = Math.Max(m.LeftMin, limit - right);
        }
        else
        {
            left = Math.Max(m.LeftMin, limit - right);
            if (left + right > limit) right = Math.Max(m.RightMin, limit - left);
        }
        return (left, right);
    }

    /// <summary>右ペインの高さから、3 つの領域に配れる高さ(見出しの行と境界の隙間 2 つを引く)。</summary>
    public static double Available(Metrics m, double rightPaneHeight) => rightPaneHeight - m.HeaderHeight - 2 * m.Gap;

    /// <summary>
    /// 起動のたびの配分: レイヤーの一覧は min(176, 右ペインの高さ − 32 − 16 − 320)(128 を下回らない)。残りをスタックとパラメータ欄で半分ずつ(奇数の余りの 1 はスタック)。
    /// </summary>
    public static RightPaneHeights Initial(Metrics m, double rightPaneHeight)
    {
        var avail = Available(m, rightPaneHeight);
        var layers = Math.Max(m.LayersMin, Math.Min(m.LayersDefault, avail - m.StackMin - m.ParamsMin));
        var rest = Math.Floor(Math.Max(0, avail - layers));
        var paramsH = Math.Floor(rest / 2);
        var stack = rest - paramsH;
        return new RightPaneHeights(layers, Math.Max(stack, m.StackMin), Math.Max(paramsH, m.ParamsMin));
    }

    /// <summary>高さが変わったとき: 増減はパラメータ欄が受け、足りないときはスタック、レイヤーの一覧の順に縮める(どれも最小を下回らない)。</summary>
    public static RightPaneHeights Resize(Metrics m, RightPaneHeights now, double rightPaneHeight)
    {
        var delta = Available(m, rightPaneHeight) - now.Total;
        var (l, s, p) = (now.Layers, now.Stack, now.Params + delta);
        if (p < m.ParamsMin)
        {
            var lack = m.ParamsMin - p;
            p = m.ParamsMin;
            var fromStack = Math.Min(lack, s - m.StackMin);
            s -= fromStack;
            lack -= fromStack;
            l = Math.Max(m.LayersMin, l - lack);
        }
        return new RightPaneHeights(l, s, p);
    }

    /// <summary>縦の境界(0 はレイヤーの一覧とスタックの間、1 はスタックとパラメータ欄の間)を delta 動かす。上下の領域だけが増減し、最小で止まる。</summary>
    public static RightPaneHeights MoveBoundary(Metrics m, RightPaneHeights now, int boundary, double delta)
    {
        if (boundary == 0)
        {
            var d = Math.Clamp(delta, m.LayersMin - now.Layers, now.Stack - m.StackMin);
            return now with { Layers = now.Layers + d, Stack = now.Stack - d };
        }
        var e = Math.Clamp(delta, m.StackMin - now.Stack, now.Params - m.ParamsMin);
        return now with { Stack = now.Stack + e, Params = now.Params - e };
    }
}
