// 切り抜きと余白の専用ボタン(FR-39・FR-40)の値の計算。正方形に切り抜く(短辺に合わせて中央を残す)、正方形にする(長辺に合わせて短辺の側へ足す)、
// 余白を自動で切る(端から、透明か背景色だけの行と列を除く)。どれも純粋な関数で、パラメータ欄のボタンとテストが共有する。
// 画素は sRGB 8 ビット・straight の RGBA(取り込んだソースの並び)。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

public static class CropTools
{
    /// <summary>透明とみなす alpha の上限(FR-40)。</summary>
    public const int TransparentAlpha = 8;

    /// <summary>背景色と同じとみなす各チャンネルの差の上限(FR-40)。</summary>
    public const int SameColorTolerance = 6;

    /// <summary>正方形に切り抜く: 入力(回転のあと)の短辺に合わせ、長辺の側を両側から同じだけ切る(‰辺)。</summary>
    public static RectValue SquareCrop(SizeD input)
    {
        if (input.W > input.H)
        {
            var side = (input.W - input.H) / 2 / input.W * 1000;
            return new RectValue(side, 0, side, 0);
        }
        if (input.H > input.W)
        {
            var side = (input.H - input.W) / 2 / input.H * 1000;
            return new RectValue(0, side, 0, side);
        }
        return new RectValue(0, 0, 0, 0);
    }

    /// <summary>正方形にする: 入力(切り抜きのあと)の長辺に合わせ、短辺の側の両側へ同じだけ足す(入力の短辺に対する ‰)。</summary>
    public static RectValue SquarePad(SizeD input)
    {
        var s0 = Math.Min(input.W, input.H);
        var add = Math.Abs(input.W - input.H) / 2 / s0 * 1000;
        if (input.W > input.H) return new RectValue(0, add, 0, add);
        if (input.H > input.W) return new RectValue(add, 0, add, 0);
        return new RectValue(0, 0, 0, 0);
    }

    /// <summary>
    /// 余白を自動で切る(FR-40)。rgba は幅×高さ×4 バイト。背景色は、四隅がすべて不透明(alpha 255)で、左上との各チャンネルの差が 6 以下のときだけ左上の色。
    /// 除ける行と列が無くなる(全体が除かれる)ときは null(値を変えない)。各辺は「除く画素数 ÷ その辺の長さ × 1000」の小数点以下を切り捨てる。
    /// </summary>
    public static RectValue? AutoTrim(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4) return null;
        var bg = Background(width, height, rgba);
        var px = rgba;
        bool Empty(int x, int y)
        {
            var i = (y * width + x) * 4;
            if (px[i + 3] <= TransparentAlpha) return true;
            if (bg is not { } c) return false;
            return Math.Abs(px[i] - c.R) <= SameColorTolerance && Math.Abs(px[i + 1] - c.G) <= SameColorTolerance
                && Math.Abs(px[i + 2] - c.B) <= SameColorTolerance && Math.Abs(px[i + 3] - c.A) <= SameColorTolerance;
        }
        bool EmptyRow(int y)
        {
            for (var x = 0; x < width; x++) if (!Empty(x, y)) return false;
            return true;
        }
        bool EmptyCol(int x, int y0, int y1)
        {
            for (var y = y0; y <= y1; y++) if (!Empty(x, y)) return false;
            return true;
        }
        var top = 0;
        while (top < height && EmptyRow(top)) top++;
        if (top == height) return null;
        var bottom = height - 1;
        while (bottom > top && EmptyRow(bottom)) bottom--;
        var left = 0;
        while (left < width && EmptyCol(left, top, bottom)) left++;
        if (left == width) return null;
        var right = width - 1;
        while (right > left && EmptyCol(right, top, bottom)) right--;
        return new RectValue(
            Permille(left, width), Permille(top, height), Permille(width - 1 - right, width), Permille(height - 1 - bottom, height));
    }

    private static double Permille(int removed, int length) => Math.Floor((double)removed * 1000 / length);

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private static Rgba? Background(int width, int height, ReadOnlySpan<byte> px)
    {
        Rgba At(ReadOnlySpan<byte> p, int x, int y)
        {
            var i = (y * width + x) * 4;
            return new Rgba(p[i], p[i + 1], p[i + 2], p[i + 3]);
        }
        var tl = At(px, 0, 0);
        Rgba[] corners = [tl, At(px, width - 1, 0), At(px, 0, height - 1), At(px, width - 1, height - 1)];
        foreach (var c in corners)
        {
            if (c.A != 255) return null;
            if (Math.Abs(c.R - tl.R) > SameColorTolerance || Math.Abs(c.G - tl.G) > SameColorTolerance || Math.Abs(c.B - tl.B) > SameColorTolerance)
                return null;
        }
        return tl;
    }

    /// <summary>
    /// レイヤーの切り抜きの入力(ソースに回転を当てた寸法)と、余白の入力(さらに切り抜きを当てた寸法)。目がオフの行は通さない。
    /// </summary>
    public static (SizeD CropInput, SizeD PadInput) ShapeInputs(Layer layer, Func<string, SizeD> sourceSize)
    {
        var src = CompositeGeometry.SourceSize(layer, sourceSize);
        var rotated = src;
        var crop = default(RectValue);
        var seenRotate = false;
        foreach (var e in layer.Stack)
        {
            if (!e.Enabled) continue;
            if (e.Id == EffectSchema.Rotate && !seenRotate)
            {
                seenRotate = true;
                var angle = e.Get("angle") is NumberValue a ? a.Value : 0;
                rotated = CompositeGeometry.RotateSize(src, angle, e.Get("fit") is StringValue f && f.Value == "inside");
            }
            else if (e.Id == EffectSchema.Crop && crop is null && e.Get("rect") is RectValue r)
            {
                crop = r;
            }
        }
        return (rotated, crop is null ? rotated : CompositeGeometry.CropSize(rotated, crop));
    }
}
