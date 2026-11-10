// 線形 RGB と Oklab の変換(仕様書 §9.4 の toOk・fromOk)。係数は仕様書の表のとおり。
// fromOk の結果が負になったチャンネルは 0 に切り、上は切らない。
// パレット作成と最近傍探索(§9.8)が使う。CPU 側は double で計算する。
namespace GlintLoop.Core.Numerics;

public readonly record struct Lab(double L, double A, double B);

public readonly record struct LinearRgb(double R, double G, double B);

public static class Oklab
{
    public static Lab ToOk(double r, double g, double b)
    {
        var l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        var m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        var s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
        var l_ = Math.Cbrt(l);
        var m_ = Math.Cbrt(m);
        var s_ = Math.Cbrt(s);
        return new Lab(
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
    }

    public static Lab ToOk(LinearRgb c) => ToOk(c.R, c.G, c.B);

    /// <summary>Oklab から線形 RGB へ。負のチャンネルは 0 に切る(上は切らない)。</summary>
    public static LinearRgb FromOk(Lab p)
    {
        var c = FromOkUnclamped(p);
        return new LinearRgb(Math.Max(0, c.R), Math.Max(0, c.G), Math.Max(0, c.B));
    }

    /// <summary>切り取りをしない逆変換(往復誤差の検査用)。</summary>
    internal static LinearRgb FromOkUnclamped(Lab p)
    {
        var l_ = p.L + 0.3963377774 * p.A + 0.2158037573 * p.B;
        var m_ = p.L - 0.1055613458 * p.A - 0.0638541728 * p.B;
        var s_ = p.L - 0.0894841775 * p.A - 1.2914855480 * p.B;
        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;
        return new LinearRgb(
            4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }
}
