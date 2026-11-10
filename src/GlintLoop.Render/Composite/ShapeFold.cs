// 形ステージの折りたたみ(§9.7 の規則 3)の幾何: 回転・切り抜き・余白から、出力の画素 → ソースの位置のアフィン変換・ds・覆いの率の矩形を求める。
// GPU を使わない純粋な計算で、shape_fold.ps の定数バッファの中身を作る。寸法は CompositeGeometry と同じ関数で求める(規則 1)。
// 切り抜きの ‰辺 は回転後の幅と高さに、余白の ‰ は切り抜き後の短辺に対する値(§9.2)。
using System.Buffers.Binary;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;

namespace GlintLoop.Render.Composite;

/// <summary>
/// 1 枚のレイヤーの折りたたみの計画。ソースの位置 p = M·x + B(x は出力の画素の位置。画素の中心は (i+0.5, j+0.5))。
/// CropRect は切り抜いたあとのキャンバス(余白を足す前)の、出力の画素での l・t・r・b。Fill は余白の色(線形光・プリマルチプライ)。
/// </summary>
internal sealed record FoldPlan(
    SizeD Source,
    SizeD Native,
    int OutW,
    int OutH,
    double M00, double M01, double M10, double M11,
    double Bx, double By,
    double Ds,
    double InvRow0, double InvRow1,
    double CropL, double CropT, double CropR, double CropB,
    double FillR, double FillG, double FillB, double FillA)
{
    /// <summary>出力の位置 (x, y) に対応するソースの位置。</summary>
    public (double X, double Y) SourceAt(double x, double y) => (M00 * x + M01 * y + Bx, M10 * x + M11 * y + By);

    /// <summary>ds>16 のとき、先に 1/2 の箱形縮小を何回するか(ds÷2^m≤16 になる最小の m)。</summary>
    public int HalvingCount
    {
        get
        {
            var m = 0;
            var d = Ds;
            while (d > ShapeFold.MaxTriangleDs)
            {
                d /= 2;
                m++;
            }
            return m;
        }
    }
}

internal static class ShapeFold
{
    /// <summary>三角窓で直接読む ds の上限(1 軸のタップ数 2×ceil(ds)+1 が 33 以下)。</summary>
    public const double MaxTriangleDs = 16;

    /// <summary>定数バッファのバイト数(float4 が 5 つと uint4 が 1 つ)。</summary>
    public const int ConstantBytes = 6 * 16;

    /// <summary>スタックのうち、目がオンの最初の回転・切り抜き・余白(CompositeGeometry.NativeSize と同じ選び方)。</summary>
    public static (StackEntry? Rotate, StackEntry? Crop, StackEntry? Pad) FixedEntries(IReadOnlyList<StackEntry> stack)
    {
        StackEntry? rotate = null, crop = null, pad = null;
        foreach (var e in stack)
        {
            if (!e.Enabled) continue;
            switch (e.Id)
            {
                case EffectSchema.Rotate: rotate ??= e; break;
                case EffectSchema.Crop: crop ??= e; break;
                case EffectSchema.Pad: pad ??= e; break;
            }
        }
        return (rotate, crop, pad);
    }

    /// <summary>角度(度)の cos と sin。90 度の倍数では正確な 0・±1 にする(画素の位置が整数からずれないように)。</summary>
    public static (double Cos, double Sin) CosSin(double deg)
    {
        var r = deg % 360;
        if (r < 0) r += 360;
        if (r == 0) return (1, 0);
        if (r == 90) return (0, 1);
        if (r == 180) return (-1, 0);
        if (r == 270) return (0, -1);
        var th = deg * Math.PI / 180.0;
        return (Math.Cos(th), Math.Sin(th));
    }

    /// <summary>
    /// 計画を立てる。source はソースの幅と高さ、outW・outH は出力(レイヤーの画像、規則 11 で縮めるときは wr・hr)の大きさ。
    /// </summary>
    public static FoldPlan Plan(SizeD source, IReadOnlyList<StackEntry> stack, int outW, int outH)
    {
        var (rotate, crop, pad) = FixedEntries(stack);
        var angle = rotate?.Get("angle") is NumberValue a ? a.Value : 0;
        var inside = rotate?.Get("fit") is StringValue f && f.Value == "inside";
        var cropRect = crop?.Get("rect") as RectValue;
        var padRect = pad?.Get("rect") as RectValue;
        var fill = pad?.Get("fill") is StringValue fs ? fs.Value : null;

        // 回転(画像の中心を軸に)。回さないときは単位変換
        var rotated = rotate is null ? source : CompositeGeometry.RotateSize(source, angle, inside);
        var (c, s) = rotate is null ? (1.0, 0.0) : CosSin(angle);

        // 切り抜き(回転後の幅と高さに対する‰辺)
        var cropped = cropRect is null ? rotated : CompositeGeometry.CropSize(rotated, cropRect);
        var cl = cropRect is null ? 0 : rotated.W * cropRect.L / 1000;
        var ct = cropRect is null ? 0 : rotated.H * cropRect.T / 1000;

        // 余白(切り抜き後の短辺に対する‰)
        var native = padRect is null ? cropped : CompositeGeometry.PadSize(cropped, padRect);
        var s0 = Math.Min(cropped.W, cropped.H);
        var pl = padRect is null ? 0 : padRect.L * s0 / 1000;
        var pt = padRect is null ? 0 : padRect.T * s0 / 1000;

        // 軸ごとの倍率(ネイティブ寸法 → 出力)
        var sx = outW / native.W;
        var sy = outH / native.H;

        // 逆変換: u = (x÷sx + cl − pl − Wr÷2, y÷sy + ct − pt − Hr÷2)、p = turn(−θ, u) + (W÷2, H÷2)
        var ox = cl - pl - rotated.W / 2;
        var oy = ct - pt - rotated.H / 2;
        var m00 = c / sx;
        var m01 = s / sy;
        var m10 = -s / sx;
        var m11 = c / sy;
        var bx = c * ox + s * oy + source.W / 2;
        var by = -s * ox + c * oy + source.H / 2;

        // ds はヤコビアンの列ベクトルの長さの大きい方(回転は長さを変えない)
        var ds = Math.Max(1 / sx, 1 / sy);
        var row0 = Math.Sqrt(m00 * m00 + m01 * m01);
        var row1 = Math.Sqrt(m10 * m10 + m11 * m11);

        var (fr, fg, fb, fa) = FillPremul(fill);
        return new FoldPlan(source, native, outW, outH, m00, m01, m10, m11, bx, by, ds, 1 / row0, 1 / row1,
            pl * sx, pt * sy, (pl + cropped.W) * sx, (pt + cropped.H) * sy, fr, fg, fb, fa);
    }

    /// <summary>色のパラメータ <c>#RRGGBBAA</c>(sRGB の straight)を線形光・プリマルチプライにする。読めないときは透明。</summary>
    public static (double R, double G, double B, double A) FillPremul(string? rgba)
    {
        if (!TryParseRgba(rgba, out var r, out var g, out var b, out var a)) return (0, 0, 0, 0);
        var alpha = a / 255.0;
        return (Srgb.Lin8(r) * alpha, Srgb.Lin8(g) * alpha, Srgb.Lin8(b) * alpha, alpha);
    }

    /// <summary><c>#RRGGBBAA</c> を 4 つの 8 ビットの値にする。</summary>
    public static bool TryParseRgba(string? text, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 0;
        if (!ColorText.TryNormalizeRgba(text, out var n)) return false;
        var v = Convert.ToUInt32(n[1..], 16);
        r = (byte)(v >> 24);
        g = (byte)(v >> 16);
        b = (byte)(v >> 8);
        a = (byte)v;
        return true;
    }

    /// <summary>
    /// shape_fold.ps の定数バッファ。texScale はソースの位置からテクスチャの画素への倍率(1/2^m)、
    /// premultipliedTexture はテクスチャが RGBA16F のプリマルチプライ済みか、uniform は一様なソース(empty のレイヤー)か。
    /// </summary>
    public static byte[] Constants(FoldPlan plan, double texScale, bool premultipliedTexture, bool uniform)
    {
        var bytes = new byte[ConstantBytes];
        var span = bytes.AsSpan();
        F(span, 0, plan.M00, plan.M01, plan.M10, plan.M11);
        F(span, 16, plan.Bx, plan.By, texScale, plan.Ds * texScale);
        F(span, 32, plan.Source.W, plan.Source.H, plan.InvRow0, plan.InvRow1);
        F(span, 48, plan.CropL, plan.CropT, plan.CropR, plan.CropB);
        F(span, 64, plan.FillR, plan.FillG, plan.FillB, plan.FillA);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], plan.Ds <= 1 ? 0u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[84..], premultipliedTexture ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[88..], uniform ? 1u : 0u);
        return bytes;
    }

    private static void F(Span<byte> span, int offset, double a, double b, double c, double d)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span[offset..], (float)a);
        BinaryPrimitives.WriteSingleLittleEndian(span[(offset + 4)..], (float)b);
        BinaryPrimitives.WriteSingleLittleEndian(span[(offset + 8)..], (float)c);
        BinaryPrimitives.WriteSingleLittleEndian(span[(offset + 12)..], (float)d);
    }
}
