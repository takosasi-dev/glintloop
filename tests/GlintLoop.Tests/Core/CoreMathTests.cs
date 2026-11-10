// AC-2 の検査: hash32・H の試験値、u01 の範囲、Oklab の往復誤差と、sRGB の全色の Oklab の範囲。
// 往復の検査は、hash32 と u01 で作った擬似乱数の線形 RGB 1,000,000 色を double で計算する。
// 全色の検査は 8 ビット sRGB の 16,777,216 色を lin してから toOk する。
using GlintLoop.Core.Numerics;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Core;

public sealed class CoreMathTests
{
    [Test]
    public void AC2_1_hash32とHの試験値()
    {
        Check.Equal(0x00000000u, Hash.Hash32(0), "hash32(0)");
        Check.Equal(0x514E28B7u, Hash.Hash32(1), "hash32(1)");
        Check.Equal(0x30F4C306u, Hash.Hash32(2), "hash32(2)");
        Check.Equal(0x81F16F39u, Hash.Hash32(0xFFFFFFFF), "hash32(0xFFFFFFFF)");
        Check.Equal(0x17CD7194u, Hash.H(1, 2), "H(1,2)");
        Check.Equal(0xAA3E5B61u, Hash.H(0, 0), "H(0,0)");
        Check.Equal(0x317ED98Du, Hash.H(123456, 789), "H(123456,789)");
    }

    [Test]
    public void AC2_2_u01は0以上1未満()
    {
        foreach (var h in new uint[] { 0, 0xFFFFFFFF })
        {
            var u = Hash.U01(h);
            Check.True(u >= 0 && u < 1, $"u01({h:X8})={u} が 0 以上 1 未満でない");
        }
    }

    [Test]
    public void AC2_3_Oklabの往復誤差と白()
    {
        for (uint i = 0; i < 1_000_000; i++)
        {
            var c = new LinearRgb(Hash.U01(Hash.H(i, 0)), Hash.U01(Hash.H(i, 1)), Hash.U01(Hash.H(i, 2)));
            var back = Oklab.FromOk(Oklab.ToOk(c));
            if (Math.Abs(back.R - c.R) >= 1e-6 || Math.Abs(back.G - c.G) >= 1e-6 || Math.Abs(back.B - c.B) >= 1e-6)
                throw new CheckFailedException($"往復誤差が 1e-6 以上: {c} → {back}");
        }

        var white = Oklab.ToOk(1, 1, 1);
        Check.Near(1, white.L, 1e-6, "白の L");
        Check.Near(0, white.A, 1e-6, "白の a");
        Check.Near(0, white.B, 1e-6, "白の b");
    }

    [Test]
    public void AC2_4_sRGBの全色のOklabの範囲()
    {
        double minL = double.MaxValue, maxL = double.MinValue;
        double minA = double.MaxValue, maxA = double.MinValue;
        double minB = double.MaxValue, maxB = double.MinValue;
        for (var r = 0; r < 256; r++)
        for (var g = 0; g < 256; g++)
        for (var b = 0; b < 256; b++)
        {
            var p = Oklab.ToOk(Srgb.Lin8((byte)r), Srgb.Lin8((byte)g), Srgb.Lin8((byte)b));
            if (p.L < minL) minL = p.L;
            if (p.L > maxL) maxL = p.L;
            if (p.A < minA) minA = p.A;
            if (p.A > maxA) maxA = p.A;
            if (p.B < minB) minB = p.B;
            if (p.B > maxB) maxB = p.B;
        }

        Check.InRange(0, 1, minL, "L の最小");
        Check.InRange(0, 1, maxL, "L の最大");
        Check.InRange(-0.24, 0.28, minA, "a の最小");
        Check.InRange(-0.24, 0.28, maxA, "a の最大");
        Check.InRange(-0.32, 0.20, minB, "b の最小");
        Check.InRange(-0.32, 0.20, maxB, "b の最大");
    }
}
