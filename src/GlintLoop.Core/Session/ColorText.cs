// 色の文字列(§9.4 の記法の「色のパラメータ」の形)を作り、検査する。
// ソースに色を直書きしない決まり(INV-14 の検査)があるので、既定の色もここで整数から組み立てる。
// 読み込んだ色は大文字の 16 進にそろえて持つ(書き出しの JSON を決定的にするため)。
using System.Globalization;

namespace GlintLoop.Core.Session;

public static class ColorText
{
    /// <summary>RRGGBBAA の整数から <c>#RRGGBBAA</c> の文字列を作る。</summary>
    public static string Rgba(uint rgba) => "#" + rgba.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>RRGGBB の整数から <c>#RRGGBB</c> の文字列を作る(JPEG の背景色など)。</summary>
    public static string Rgb(uint rgb) => "#" + (rgb & 0xFFFFFFu).ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>透明(FR-41 の塗りの既定)。</summary>
    public static string Transparent { get; } = Rgba(0x00000000u);

    /// <summary>不透明の白(FR-44 の空のキャンバスの塗りの既定)。</summary>
    public static string OpaqueWhite { get; } = Rgba(0xFFFFFFFFu);

    /// <summary><c>#</c> と 16 進 8 桁なら、大文字にそろえて返す。</summary>
    public static bool TryNormalizeRgba(string? s, out string normalized) => TryNormalize(s, 8, out normalized);

    /// <summary><c>#</c> と 16 進 6 桁なら、大文字にそろえて返す。</summary>
    public static bool TryNormalizeRgb(string? s, out string normalized) => TryNormalize(s, 6, out normalized);

    private static bool TryNormalize(string? s, int digits, out string normalized)
    {
        normalized = "";
        if (s is null || s.Length != digits + 1 || s[0] != '#') return false;
        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            var hex = c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
            if (!hex) return false;
        }
        normalized = s.ToUpperInvariant();
        return true;
    }
}
