// sRGB と線形光の 1 チャンネルの変換(仕様書 §9.4 の lin・enc)と、共通の小さな数値関数。
// round は 0.5 を大きい方へ丸める(.NET の Math.Round の偶数丸めは使わない)。
// CPU 側の実装はすべて double で書く。
namespace GlintLoop.Core.Numerics;

public static class Srgb
{
    /// <summary>sRGB の値(0〜1)を線形光にする。</summary>
    public static double Lin(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>線形光の値を sRGB の値にする。</summary>
    public static double Enc(double v) =>
        v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;

    private static readonly double[] LinTable8 = BuildLinTable8();

    /// <summary>8 ビットの sRGB 値(0〜255)を線形光にする(表引き)。</summary>
    public static double Lin8(byte c) => LinTable8[c];

    private static double[] BuildLinTable8()
    {
        var t = new double[256];
        for (var i = 0; i < 256; i++) t[i] = Lin(i / 255.0);
        return t;
    }
}

public static class MathX
{
    /// <summary>0 以上 1 以下に切る。</summary>
    public static double Sat(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    /// <summary>round(v)=floor(v+0.5)。0.5 は大きい方へ丸める。</summary>
    public static double RoundHalfUp(double v) => Math.Floor(v + 0.5);

    /// <summary>RoundHalfUp の結果を int にする。</summary>
    public static int RoundHalfUpToInt(double v) => (int)Math.Floor(v + 0.5);
}
