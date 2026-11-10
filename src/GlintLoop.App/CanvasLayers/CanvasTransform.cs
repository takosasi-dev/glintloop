// キャンバスの座標の変換: 文書の画素(合成のネイティブ寸法 Wc×Hc の座標。レイヤーの配置の x・y と同じ)↔ キャンバスの DIP(CanvasView の左上が原点)。
// canvas = doc × Scale + Offset。Scale は文書の 1 画素あたりの DIP(DPI の倍率は含めない)、Offset は文書の原点が来るキャンバスの位置。
// 値は Set か FitTo で外から入れる(FR-15 の倍率の範囲・1.25 倍の段・パンは後の担当)。値が変わったときだけ Changed を上げる。UI スレッドで使う。
using System.Windows;
using System.Windows.Media;

namespace GlintLoop.App.CanvasLayers;

public sealed class CanvasTransform
{
    /// <summary>文書の 1 画素あたりの DIP(正の有限の数)。</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>文書の原点 (0, 0) が来るキャンバスの DIP の位置。</summary>
    public Vector Offset { get; private set; }

    /// <summary>Scale か Offset が変わったときに上がる。</summary>
    public event Action? Changed;

    /// <summary>文書 → キャンバスの行列(描画の PushTransform などに使う)。</summary>
    public Matrix Matrix => new(Scale, 0, 0, Scale, Offset.X, Offset.Y);

    /// <summary>倍率とずれを入れる。どちらも前と同じなら何もしない。倍率が正の有限の数でない・ずれが有限でないときは例外。</summary>
    public void Set(double scale, Vector offset)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "倍率は正の有限の数");
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y)) throw new ArgumentOutOfRangeException(nameof(offset), "ずれは有限の数");
        if (scale == Scale && offset == Offset) return;
        Scale = scale;
        Offset = offset;
        Changed?.Invoke();
    }

    /// <summary>
    /// 仮のフィット: 文書の全体がキャンバスに収まる最大の倍率で中央に置く。FR-15 の 10〜800% の範囲には収めない(拡大縮小の担当が置き換える)。
    /// キャンバスか文書の幅・高さが 0 以下のときは何もしない。
    /// </summary>
    public void FitTo(Size canvas, Size document)
    {
        if (!(canvas.Width > 0 && canvas.Height > 0 && document.Width > 0 && document.Height > 0)) return;
        var s = Math.Min(canvas.Width / document.Width, canvas.Height / document.Height);
        Set(s, new Vector((canvas.Width - document.Width * s) / 2, (canvas.Height - document.Height * s) / 2));
    }

    public Point ToCanvas(Point doc) => new(doc.X * Scale + Offset.X, doc.Y * Scale + Offset.Y);

    public Point ToDocument(Point canvas) => new((canvas.X - Offset.X) / Scale, (canvas.Y - Offset.Y) / Scale);

    /// <summary>文書の矩形 → キャンバスの矩形。Rect.Empty は Rect.Empty のまま。</summary>
    public Rect ToCanvas(Rect doc) =>
        doc.IsEmpty ? Rect.Empty : new Rect(ToCanvas(doc.TopLeft), new Size(doc.Width * Scale, doc.Height * Scale));

    /// <summary>キャンバスの矩形 → 文書の矩形。Rect.Empty は Rect.Empty のまま。</summary>
    public Rect ToDocument(Rect canvas) =>
        canvas.IsEmpty ? Rect.Empty : new Rect(ToDocument(canvas.TopLeft), new Size(canvas.Width / Scale, canvas.Height / Scale));

    /// <summary>文書の長さ(画素)→ キャンバスの長さ(DIP)。</summary>
    public double ToCanvasLength(double doc) => doc * Scale;

    /// <summary>キャンバスの長さ(DIP)→ 文書の長さ(画素)。</summary>
    public double ToDocumentLength(double canvas) => canvas / Scale;
}
