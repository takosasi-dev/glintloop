// キャンバス上のレイヤーの枠(§9.3・FR-50・FR-51)の計算を、座標を引数に取る純粋な関数にまとめる(テストが直接確かめる)。
// 枠の矩形(合成のネイティブ寸法の座標)・当たり判定(ハンドル 16 DIP 四方 → 枠の内側 → そのほか)・カーソルの種類・
// 枠の内側のドラッグの移動・隅のハンドルの拡縮(反対側の隅を止め、距離の比で一様に拡縮する。D-13)・クリックでの選択を求める。
using System.Windows;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Layers;

/// <summary>当たり判定の結果。ハンドルは 4 隅。</summary>
public enum FrameHit { None, Inside, TopLeft, TopRight, BottomRight, BottomLeft }

/// <summary>枠の上のカーソルの種類(移動・北西-南東・北東-南西の斜めの拡縮・矢印)。</summary>
public enum FrameCursor { Arrow, Move, SizeNwse, SizeNesw }

public static class LayerFrameMath
{
    /// <summary>クリックとみなす移動の上限(DIP。これ未満がクリック。FR-51)。</summary>
    public const double ClickSlop = 4;

    /// <summary>
    /// 枠(§9.7 の規則 11 の Q)を、合成のネイティブ寸法の座標の矩形にする(Q を出力の倍率 s で割る)。
    /// layout は Session.Layout の結果。
    /// </summary>
    public static Rect FrameNative(CompositeLayout layout, int layerIndex)
    {
        var q = layout.Layers[layerIndex].Frame;
        var s = layout.Output.S;
        return new Rect(q.Qx / s, q.Qy / s, q.W / s, q.H / s);
    }

    /// <summary>
    /// 配置から求めた、丸めない枠(ネイティブ寸法の座標)。中心は (Wc÷2+x, Hc÷2+y)、大きさは (Wn×scale÷100, Hn×scale÷100)。
    /// ドラッグの計算はこの矩形の隅を使う。
    /// </summary>
    public static Rect FrameExact(SizeD composite, SizeD layerNative, Placement place)
    {
        var w = layerNative.W * place.Scale / 100;
        var h = layerNative.H * place.Scale / 100;
        var cx = composite.W / 2 + place.X;
        var cy = composite.H / 2 + place.Y;
        return new Rect(cx - w / 2, cy - h / 2, w, h);
    }

    /// <summary>点 p(ネイティブ寸法の座標)が枠に入るか(左と上の辺は含み、右と下の辺は含まない。画素の判定と同じ)。</summary>
    public static bool Contains(Rect frame, Point p) =>
        !frame.IsEmpty && p.X >= frame.Left && p.X < frame.Right && p.Y >= frame.Top && p.Y < frame.Bottom;

    /// <summary>
    /// 当たり判定(キャンバスの DIP の座標)。ハンドルは隅を中心に handleHit 四方(中心から ±handleHit÷2 以内)。
    /// 優先は ハンドル、枠の内側、そのほか。
    /// </summary>
    public static FrameHit HitTest(Rect frameDip, Point p, double handleHit)
    {
        if (frameDip.IsEmpty) return FrameHit.None;
        var half = handleHit / 2;
        bool Near(Point c) => Math.Abs(p.X - c.X) <= half && Math.Abs(p.Y - c.Y) <= half;
        if (Near(frameDip.TopLeft)) return FrameHit.TopLeft;
        if (Near(frameDip.TopRight)) return FrameHit.TopRight;
        if (Near(frameDip.BottomRight)) return FrameHit.BottomRight;
        if (Near(frameDip.BottomLeft)) return FrameHit.BottomLeft;
        if (p.X >= frameDip.Left && p.X <= frameDip.Right && p.Y >= frameDip.Top && p.Y <= frameDip.Bottom) return FrameHit.Inside;
        return FrameHit.None;
    }

    /// <summary>カーソルの種類。枠の内側は移動、左上と右下は北西-南東、右上と左下は北東-南西、そのほかは矢印。</summary>
    public static FrameCursor CursorFor(FrameHit hit) => hit switch
    {
        FrameHit.Inside => FrameCursor.Move,
        FrameHit.TopLeft or FrameHit.BottomRight => FrameCursor.SizeNwse,
        FrameHit.TopRight or FrameHit.BottomLeft => FrameCursor.SizeNesw,
        _ => FrameCursor.Arrow,
    };

    /// <summary>
    /// 画面の 1 DIP あたりのネイティブ寸法の画素の逆数(DIP ÷ ネイティブの画素)。表示の倍率 zoom(プロキシの 1 画素を画面の何画素で出すか)、
    /// プロキシの倍率 s(Lout÷max(Wc, Hc))、DPI の倍率 dpiScale(画面の画素 ÷ DIP)から求める。製品では CanvasTransform.Scale がこの値。
    /// </summary>
    public static double DipPerNative(double zoom, double s, double dpiScale) => zoom * s / dpiScale;

    /// <summary>
    /// 枠の内側のドラッグ(FR-50)。押した点から離した点までの DIP の移動を、dipPerNative でネイティブ寸法の画素に直して位置に足す。
    /// 結果は小数第 2 位に round して範囲に直す(位置を合成の外へ出しても制限しない。E-17)。
    /// </summary>
    public static Placement Move(Placement start, Point pressDip, Point nowDip, double dipPerNative)
    {
        var dx = (nowDip.X - pressDip.X) / dipPerNative;
        var dy = (nowDip.Y - pressDip.Y) / dipPerNative;
        return start with { X = DocumentRules.RoundPlace(start.X + dx), Y = DocumentRules.RoundPlace(start.Y + dy) };
    }

    /// <summary>
    /// 隅のハンドルのドラッグ(FR-50)。ポインター(ネイティブ寸法の座標)と、ドラッグを始めたときの枠と配置から、新しい配置を求める。
    /// scale = 始めの scale × (反対の隅 → ポインターの距離) ÷ (反対の隅 → 始めのその隅の距離) を round して 5〜400 に収め、
    /// 反対の隅が動かないように位置を決める(round して範囲に直す)。
    /// </summary>
    public static Placement Scale(SizeD composite, Rect startFrame, Placement start, FrameHit corner, Point pointer)
    {
        var (handle, opposite) = corner switch
        {
            FrameHit.TopLeft => (startFrame.TopLeft, startFrame.BottomRight),
            FrameHit.TopRight => (startFrame.TopRight, startFrame.BottomLeft),
            FrameHit.BottomRight => (startFrame.BottomRight, startFrame.TopLeft),
            FrameHit.BottomLeft => (startFrame.BottomLeft, startFrame.TopRight),
            _ => throw new ArgumentOutOfRangeException(nameof(corner), "隅のハンドルでない"),
        };
        var d0 = (handle - opposite).Length;
        if (!(d0 > 0)) return start;
        var ratio = (pointer - opposite).Length / d0;
        var scale = DocumentRules.RoundScale(start.Scale * ratio);
        var k = scale / start.Scale;
        var w = startFrame.Width * k;
        var h = startFrame.Height * k;
        // 反対の隅から、ハンドルの向きへ新しい大きさを取った矩形の中心
        var sx = Math.Sign(handle.X - opposite.X);
        var sy = Math.Sign(handle.Y - opposite.Y);
        var cx = opposite.X + sx * w / 2;
        var cy = opposite.Y + sy * h / 2;
        return new Placement(DocumentRules.RoundPlace(cx - composite.W / 2), DocumentRules.RoundPlace(cy - composite.H / 2), scale);
    }

    /// <summary>
    /// キャンバスのクリックで選ぶレイヤー(FR-51)。frames はレイヤーの番号の順の枠(ネイティブ寸法の座標)、visible は表示。
    /// 点を含むオンのレイヤーのうち最も手前のもの。ただし選択中のレイヤーが土台でなく、その枠が点を含むときは選択を変えない(null)。
    /// どの枠も含まないときは null。
    /// </summary>
    public static int? PickLayer(IReadOnlyList<Rect> frames, IReadOnlyList<bool> visible, Target selected, Point p)
    {
        if (!selected.IsAll && selected.LayerIndex > 0 && selected.LayerIndex < frames.Count &&
            visible[selected.LayerIndex] && Contains(frames[selected.LayerIndex], p)) return null;
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            if (visible[i] && Contains(frames[i], p)) return i;
        }
        return null;
    }

    /// <summary>押してから離すまでの移動がクリックか(ユークリッド距離が ClickSlop 未満)。</summary>
    public static bool IsClick(Point press, Point release) => (release - press).Length < ClickSlop;
}
