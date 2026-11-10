// キャンバスの表示の座標の計算(FR-15〜FR-18)。どれも引数だけで決まる純粋な関数で、画面を立てずに検査できる。
// 倍率 zoom は出力の画素が基準(1=100% は出力の 1 画素が画面の 1 画素)。変換の Scale は合成のネイティブの 1 画素あたりの DIP で、
// Scale = zoom × outScale ÷ dpi(outScale は Lout÷max(Wc, Hc)、dpi は画面の画素÷DIP)。
using System.Windows;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Viewing;

public static class ViewMath
{
    /// <summary>倍率の下限 10%(FR-15)。</summary>
    public const double MinZoom = 0.1;

    /// <summary>倍率の上限 800%(FR-15)。</summary>
    public const double MaxZoom = 8;

    /// <summary>1 回の拡大縮小の倍率(FR-15)。</summary>
    public const double ZoomStep = 1.25;

    /// <summary>ホイールの 1 ノッチ(FR-15)。</summary>
    public const int WheelNotch = 120;

    /// <summary>矢印キーのパン(FR-16。DIP)。</summary>
    public const double PanStep = 32;

    /// <summary>Shift つきの矢印キーのパン(FR-16。DIP)。</summary>
    public const double PanStepLarge = 128;

    /// <summary>分割比較の縦線の 1 回の移動(FR-18。合成の幅に対する比)。</summary>
    public const double SplitStep = 0.02;

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>変換の Scale(合成のネイティブの 1 画素あたりの DIP)。</summary>
    public static double DipPerNative(double zoom, double outScale, double dpi) => zoom * outScale / dpi;

    /// <summary>倍率 zoom のときの合成の表示の大きさ(DIP)。100% では「出力の幅と高さ ÷ DPI の倍率」になる。</summary>
    public static Size ImageSize(SizeD native, double zoom, double outScale, double dpi)
    {
        var s = DipPerNative(zoom, outScale, dpi);
        return new Size(native.W * s, native.H * s);
    }

    /// <summary>フィットの倍率: 合成がキャンバスに収まる最大の倍率を 10〜800% に収めたもの。キャンバスか合成が空なら 1。</summary>
    public static double FitZoom(Size view, SizeD native, double outScale, double dpi)
    {
        var at100 = ImageSize(native, 1, outScale, dpi);
        if (!(view.Width > 0 && view.Height > 0 && at100.Width > 0 && at100.Height > 0)) return 1;
        return ClampZoom(Math.Min(view.Width / at100.Width, view.Height / at100.Height));
    }

    /// <summary>steps 回の拡大(正)か縮小(負)。1 回で 1.25 倍か 1÷1.25 倍にし、範囲の端で止める。</summary>
    public static double StepZoom(double zoom, int steps)
    {
        var z = zoom;
        for (var i = 0; i < steps; i++) z = ClampZoom(z * ZoomStep);
        for (var i = 0; i > steps; i--) z = ClampZoom(z / ZoomStep);
        return z;
    }

    /// <summary>
    /// 表示位置(文書の原点が来るキャンバスの位置)を、軸ごとに直す。画像がキャンバス以下の軸は中央に置き、
    /// 大きい軸はキャンバスに隙間ができない範囲(画像の端がキャンバスの内側に入らない範囲)に収める。
    /// </summary>
    public static Vector Constrain(Vector offset, Size image, Size view) =>
        new(ConstrainAxis(offset.X, image.Width, view.Width), ConstrainAxis(offset.Y, image.Height, view.Height));

    private static double ConstrainAxis(double offset, double image, double view)
    {
        if (image <= view) return (view - image) / 2;
        return Math.Clamp(offset, view - image, 0);
    }

    /// <summary>倍率を変えるとき、キャンバスの点 anchor の下にある文書の点を動かさない表示位置。</summary>
    public static Vector ZoomAbout(Point anchor, Vector offset, double oldScale, double newScale)
    {
        var docX = (anchor.X - offset.X) / oldScale;
        var docY = (anchor.Y - offset.Y) / oldScale;
        return new Vector(anchor.X - docX * newScale, anchor.Y - docY * newScale);
    }

    /// <summary>
    /// 大きさが変わるとき(合成の寸法・出力の長辺・キャンバスの大きさ)、キャンバスの中心にあった画像の中の割合の位置を、新しい画像でも中心に置く表示位置。
    /// </summary>
    public static Vector KeepRelativeCenter(Vector offset, Size oldImage, Size oldView, Size newImage, Size newView)
    {
        var ux = oldImage.Width > 0 ? (oldView.Width / 2 - offset.X) / oldImage.Width : 0.5;
        var uy = oldImage.Height > 0 ? (oldView.Height / 2 - offset.Y) / oldImage.Height : 0.5;
        return new Vector(newView.Width / 2 - ux * newImage.Width, newView.Height / 2 - uy * newImage.Height);
    }

    /// <summary>矢印キーのパンの量(DIP)。画像を押した向きに動かす(右の矢印で画像が右へ)。</summary>
    public static Vector PanDelta(int dx, int dy, bool large)
    {
        var step = large ? PanStepLarge : PanStep;
        return new Vector(dx * step, dy * step);
    }

    /// <summary>
    /// 加工前の合成(FR-17)を置く矩形(今の合成の文書の画素の座標)。同じ倍率(出力の画素の基準)で、合成の中心を今の合成の中心に合わせる。
    /// current・original は合成のネイティブ寸法、currentOut・originalOut はそれぞれの Lout÷max(Wc, Hc)。倍率と DPI の倍率は打ち消し合うので要らない。
    /// </summary>
    public static Rect OriginalRect(SizeD current, double currentOut, SizeD original, double originalOut)
    {
        var k = currentOut > 0 ? originalOut / currentOut : 1;
        var w = original.W * k;
        var h = original.H * k;
        return new Rect(current.W / 2 - w / 2, current.H / 2 - h / 2, w, h);
    }

    /// <summary>分割比較の縦線の位置を 0〜1 に直す。</summary>
    public static double ClampSplit(double f) => Math.Clamp(f, 0, 1);
}
