// テキストのエフェクト(§9.4 の仕上げの text)の、ビットマップを描くのに要るパラメータの組と、それを描く受け口(ITextRasterizer)。
// 描く実装は SkiaSharp を持つ Codec に置き(D-35)、描画(Render)と画面(App)はこの受け口だけを使う。
// TextBlockLayout は文字の塊の矩形(§9.4 の text の定義)で、キャンバスのハンドル(FR-21)の位置にも使える。
using System.Globalization;
using System.Text;

namespace GlintLoop.Core.Effects;

public enum TextAlign { Left, Center, Right }

/// <summary>
/// テキストのビットマップを決める値(文字・フォント・大きさ・色・揃え・位置・回転・行の間隔・線)。値はパラメータの定義の単位のまま
/// (size と stroke は ‰、line は %、rot は度、pos は幅と高さに対する 0〜1、色は #RRGGBBAA)。
/// </summary>
public sealed record TextSpec(
    string Text,
    string Font,
    double Size,
    string Color,
    TextAlign Align,
    double PosX,
    double PosY,
    double Rotation,
    double Line,
    double Stroke,
    string StrokeColor)
{
    /// <summary>テキストのエフェクトの id。</summary>
    public const string EffectId = "text";

    /// <summary>既定のフォント(FR-38。保存済みのフォントが無いときもこれで描く)。</summary>
    public const string DefaultFont = "Yu Gothic UI";

    /// <summary>
    /// 描画に使う値(EffectConstants.ResolveValues の結果のような、全キーの揃った値)から組を作る。
    /// 無いキー・型の違う値は表の既定値、数は表の範囲に切る(呼び手が直してから渡す約束だが、描く側でも外れた値で崩れないようにする)。
    /// </summary>
    public static TextSpec From(IReadOnlyDictionary<string, ParamValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string S(string key, string def) => values.TryGetValue(key, out var v) && v is StringValue s ? s.Value : def;
        double N(string key, double def, double min, double max)
        {
            var x = values.TryGetValue(key, out var v) && v is NumberValue n && !double.IsNaN(n.Value) ? n.Value : def;
            return Math.Clamp(x, min, max);
        }
        var align = S("align", "center") switch
        {
            "left" => TextAlign.Left,
            "right" => TextAlign.Right,
            _ => TextAlign.Center,
        };
        var pos = values.TryGetValue("pos", out var pv) && pv is PointValue p && !double.IsNaN(p.X) && !double.IsNaN(p.Y)
            ? (Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1))
            : (0.5, 0.5);
        return new TextSpec(
            TextLimits.NormalizeNewlines(S("text", "")),
            S("font", DefaultFont),
            N("size", 100, 10, 500),
            S("color", ""), // 無いときは描く側の既定(白)
            align,
            pos.Item1,
            pos.Item2,
            N("rot", 0, -180, 180),
            N("line", 120, 80, 200),
            N("stroke", 0, 0, 60),
            S("stroke_color", "")); // 無いときは描く側の既定(黒)
    }

    /// <summary>行(LF で分けたもの。文字が空なら 1 行の空)。</summary>
    public IReadOnlyList<string> Lines => Text.Split('\n');

    /// <summary>描く値をすべて並べた文字列(ビットマップを作り直すかどうかの判定のキーに使う)。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(Text).Append('|');
        sb.Append(Font.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(Font).Append('|');
        foreach (var d in new[] { Size, PosX, PosY, Rotation, Line, Stroke })
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Color).Append('|').Append(StrokeColor).Append('|').Append((int)Align);
        return sb.ToString();
    }
}

/// <summary>
/// 文字の塊(§9.4: 幅は最も長い行の描画幅、高さは 行数×px(size)×line÷100)。座標はキャンバスの画素。
/// 回す前の塊の左上は 基準点 + (Left, Top) で、塊は基準点まわりに RotationDeg だけ時計回りに回る。
/// </summary>
public readonly record struct TextBlockLayout(double AnchorX, double AnchorY, double Left, double Top, double Width, double Height, double RotationDeg, int LineCount);

/// <summary>テキストのビットマップを描く受け口(実装は Codec の SkiaTextRasterizer)。どのスレッドから呼んでもよい。</summary>
public interface ITextRasterizer
{
    /// <summary>
    /// width×height(キャンバスと同じ大きさ)のビットマップを描き、sRGB の straight の RGBA 8 ビット(行を詰める)で返す。
    /// 文字が空なら null(何も描かない。FR-37)。
    /// </summary>
    byte[]? Rasterize(TextSpec spec, int width, int height);

    /// <summary>width×height のキャンバスに描くときの文字の塊。</summary>
    TextBlockLayout Measure(TextSpec spec, int width, int height);
}
