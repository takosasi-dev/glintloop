// 形ステージの折りたたみ(§9.7 の規則 3)の性質のテスト(WARP だけ): AC-14 の (2) の rotate・crop・pad と「形ステージの折りたたみ」の行。
// catalog_points の性質の表のとおりの入力と出力の長辺で、文書(土台 1 枚)の経路を通して確かめる。画像はコードで作る。
// あわせて、折りたたみの幾何(ShapeFold.Plan)の逆変換が回転→切り抜き→余白を順に当てたものと一致することを CPU で確かめる。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Render;

public sealed class CompositeFoldTests
{
    /// <summary>写真に近い滑らかな変化と細かい模様を持つ不透明な画像(位置ごとに値が違う)。</summary>
    private static CompositeImg Pattern(int w, int h) => CompositeImg.Make(w, h, (x, y) =>
        ((x * 7 + y * 3) % 256, (int)(127 + 120 * Math.Sin(x * 0.05) * Math.Cos(y * 0.07)), (x ^ y) & 255, 255));

    private static void SameRegion(DocumentImage im, CompositeImg src, int ox, int oy, int fromX, int toX, int tol, string what)
    {
        for (var y = 0; y < im.Height; y++)
        for (var x = fromX; x <= toX; x++)
        {
            var e = src.At(x - fromX + ox, y + oy);
            CompositeCheck.Pixel(im, x, y, e, tol, $"{what} ({x}, {y})");
        }
    }

    // ================================================================ rotate

    [Test]
    public void AC14_2_rotateは0度で入力と一致し90度で大きさと印の位置が回る()
    {
        using var kit = new CompositeKit();
        var src = Pattern(60, 40);
        kit.Import(src);
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Rotate);
        var zero = kit.Render(60);
        CompositeCheck.Size(zero, 60, 40, "angle=0");
        SameRegion(zero, src, 0, 0, 0, 59, 1, "angle=0");

        // 左上の角に白い印(3×3)を置いた黒い画像を 90 度回すと、印は右上の角に来る
        using var kit2 = new CompositeKit();
        kit2.Import(CompositeImg.Make(60, 40, (x, y) => x < 3 && y < 3 ? (255, 255, 255, 255) : (0, 0, 0, 255)));
        kit2.AddEffect(Target.OfLayer(0), EffectSchema.Rotate, ("angle", new NumberValue(90)));
        var turned = kit2.Render(60);
        CompositeCheck.Size(turned, 40, 60, "angle=90 の大きさ(H×W)");
        double sx = 0, sy = 0, sw = 0;
        for (var y = 0; y < turned.Height; y++)
        for (var x = 0; x < turned.Width; x++)
        {
            var g = CompositeCheck.At(turned, x, y).G;
            sx += g * (x + 0.5);
            sy += g * (y + 0.5);
            sw += g;
        }
        Check.Near(40 - 1.5, sx / sw, 1, "印の重心の x(右上の角)");
        Check.Near(1.5, sy / sw, 1, "印の重心の y(右上の角)");
    }

    [Test]
    public void AC14_2_rotateは45度insideで大きさが式どおり()
    {
        using var kit = new CompositeKit();
        kit.Import(Pattern(100, 60));
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Rotate, ("angle", new NumberValue(45)), ("fit", new StringValue("inside")));
        // k=min(W÷(W×c+H×s), H÷(W×s+H×c))
        var c = Math.Cos(Math.PI / 4);
        var k = Math.Min(100 / (100 * c + 60 * c), 60 / (100 * c + 60 * c));
        var ew = (int)Math.Floor(k * 100 + 0.5);
        var eh = (int)Math.Floor(k * 60 + 0.5);
        var im = kit.Render(ew);
        CompositeCheck.Size(im, ew, eh, "45 度 inside");
        // 回した画像の内側に収まるので、全画素が不透明
        for (var y = 0; y < im.Height; y++)
        for (var x = 0; x < im.Width; x++)
            Check.Equal(255, CompositeCheck.At(im, x, y).A, $"inside の ({x}, {y}) の α");
    }

    // ================================================================ crop・pad

    [Test(Slow = true)]
    public void AC14_2_cropは1000x500から800x450を切り出す()
    {
        using var kit = new CompositeKit();
        var src = Pattern(1000, 500);
        kit.Import(src);
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Crop, ("rect", new RectValue(100, 50, 100, 50)));
        var im = kit.Render(800);
        CompositeCheck.Size(im, 800, 450, "切り抜き");
        SameRegion(im, src, 100, 25, 0, 799, 1, "切り抜き");
    }

    [Test(Slow = true)]
    public void AC14_2_padは左に50列の赤を足す()
    {
        using var kit = new CompositeKit();
        var src = Pattern(1000, 500);
        kit.Import(src);
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Pad, ("rect", new RectValue(100, 0, 0, 0)), ("fill", new StringValue("#FF0000FF")));
        var im = kit.Render(1050);
        CompositeCheck.Size(im, 1050, 500, "余白");
        for (var y = 0; y < 500; y++)
        for (var x = 0; x < 50; x++)
            CompositeCheck.Pixel(im, x, y, (255, 0, 0, 255), 1, "余白の赤");
        SameRegion(im, src, 0, 0, 50, 1049, 1, "余白の右の入力");
    }

    // ================================================================ 形ステージの折りたたみ(§9.7 の 3)

    [Test(Slow = true)]
    public void AC14_2_折りたたみは切り抜きと余白を1回で当てる()
    {
        using var kit = new CompositeKit();
        var src = Pattern(1000, 500);
        kit.Import(src);
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Crop, ("rect", new RectValue(100, 0, 0, 0)));
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Pad, ("rect", new RectValue(100, 0, 0, 0)), ("fill", new StringValue("#FF0000FF")));
        var im = kit.Render(950);
        CompositeCheck.Size(im, 950, 500, "切り抜きと余白");
        for (var y = 0; y < 500; y++)
        for (var x = 0; x < 50; x++)
            CompositeCheck.Pixel(im, x, y, (255, 0, 0, 255), 1, "左端の 50 列");
        SameRegion(im, src, 100, 0, 50, 949, 1, "出力の x=50+i が入力の x=100+i");
    }

    [Test]
    public void AC14_2_折りたたみは不透明な画像をどの大きさでも不透明に保つ()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Make(100, 100, (x, y) => (2 * x, 2 * y, 128, 255)));
        foreach (var lout in new[] { 50, 150, 400 })
        {
            var im = kit.Render(lout);
            CompositeCheck.Size(im, lout, lout, $"長辺 {lout}");
            for (var y = 0; y < lout; y++)
            for (var x = 0; x < lout; x++)
                Check.Equal(255, CompositeCheck.At(im, x, y).A, $"長辺 {lout} の ({x}, {y}) の α");
        }
    }

    [Test]
    public void AC14_2_折りたたみは一様な画像を拡大しても縮小しても変えない()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(128, 128, 0x64, 0x96, 0xC8));
        foreach (var lout in new[] { 64, 192, 512 })
        {
            var im = kit.Render(lout);
            CompositeCheck.Size(im, lout, lout, $"長辺 {lout}");
            CompositeCheck.All(im, (0x64, 0x96, 0xC8, 255), 1, $"長辺 {lout}");
        }
    }

    [Test]
    public void AC14_2_折りたたみは小さな画像の四隅を不透明のまま拡大する()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Make(8, 4, (x, y) => (x * 30, y * 60, 200, 255)));
        var im = kit.Render(16);
        CompositeCheck.Size(im, 16, 8, "8×4 を長辺 16");
        foreach (var (x, y) in new[] { (0, 0), (15, 0), (0, 7), (15, 7) })
            Check.Equal(255, CompositeCheck.At(im, x, y).A, $"四隅 ({x}, {y}) の α");

        using var one = new CompositeKit();
        one.Import(CompositeImg.Solid(1, 1, 255, 0, 0));
        var red = one.Render(16);
        CompositeCheck.Size(red, 16, 16, "1×1 を長辺 16");
        CompositeCheck.All(red, (255, 0, 0, 255), 1, "1×1 の赤");
    }

    [Test]
    public void AC14_2_折りたたみは大きく縮めるときも一様な画像を変えない()
    {
        // ds>16 のときは先に 1/2 の箱形縮小を繰り返す(規則 3)。奇数の辺でも端の複製で値は変わらない
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(1999, 1001, 0x64, 0x96, 0xC8));
        var im = kit.Render(40);
        CompositeCheck.Size(im, 40, 20, "1999×1001 を長辺 40");
        CompositeCheck.All(im, (0x64, 0x96, 0xC8, 255), 1, "長辺 40");
    }

    [Test]
    public void 規則3_回転で生じた隅はcovRの分だけ透明になり余白の色は足さない()
    {
        // 100×100 の不透明な灰を 45 度回し(expand)、余白 100‰ を赤で足す。回した正方形の外の隅は透明、余白は赤、中心は灰
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(100, 100, 128, 128, 128));
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Rotate, ("angle", new NumberValue(45)));
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Pad, ("rect", new RectValue(100, 100, 100, 100)), ("fill", new StringValue("#FF0000FF")));
        // 回した外接は 141.42 四方、余白は 14.14 画素ずつで、ネイティブ寸法は 169.7 四方
        var im = kit.Render(170);
        CompositeCheck.Size(im, 170, 170, "出力");
        CompositeCheck.Pixel(im, 85, 85, (128, 128, 128, 255), 1, "中心");
        CompositeCheck.Pixel(im, 5, 85, (255, 0, 0, 255), 1, "左の余白");
        CompositeCheck.Pixel(im, 85, 5, (255, 0, 0, 255), 1, "上の余白");
        CompositeCheck.Pixel(im, 25, 25, (0, 0, 0, 0), 0, "回転で生じた左上の隅(切り抜いたあとの矩形の中なので余白の色を足さない)");
        CompositeCheck.Pixel(im, 144, 144, (0, 0, 0, 0), 0, "回転で生じた右下の隅");
        // 回した正方形の辺の上の画素は半分ほど覆われる(灰の straight、α はおよそ 128)
        var edge = CompositeCheck.At(im, 49, 49);
        Check.InRange(64, 192, edge.A, "辺の上の画素の α");
    }

    // ================================================================ 再標本化の参照実装(CPU)

    /// <summary>
    /// 規則 3 の再標本化を double で書き直した参照(回転・切り抜き・余白なしの拡大縮小だけ)。ds≤1 はキャッチマル・ロム、ds>1 は三角窓で、
    /// ds>16 のときは先に 1/2 の箱形縮小(端の複製)を繰り返す。結果は straight の sRGB 8 ビット(ディザなしの round)。
    /// </summary>
    private static byte[] ReferenceResample(CompositeImg src, int w, int h)
    {
        // 線形光・プリマルチプライ
        var sw = src.W;
        var sh = src.H;
        var p = new double[sw * sh * 4];
        for (var i = 0; i < sw * sh; i++)
        {
            var a = src.Px[i * 4 + 3] / 255.0;
            for (var c = 0; c < 3; c++) p[i * 4 + c] = GlintLoop.Core.Numerics.Srgb.Lin8(src.Px[i * 4 + c]) * a;
            p[i * 4 + 3] = a;
        }
        var sx = (double)sw / w;
        var sy = (double)sh / h;
        var ds = Math.Max(sx, sy);
        var scale = 1.0;
        while (ds * scale > 16)
        {
            var nw = (sw + 1) / 2;
            var nh = (sh + 1) / 2;
            var q = new double[nw * nh * 4];
            for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            for (var c = 0; c < 4; c++)
            {
                double sum = 0;
                for (var j = 0; j < 2; j++)
                for (var i = 0; i < 2; i++)
                    sum += p[(Math.Min(2 * y + j, sh - 1) * sw + Math.Min(2 * x + i, sw - 1)) * 4 + c];
                q[(y * nw + x) * 4 + c] = sum / 4;
            }
            p = q;
            sw = nw;
            sh = nh;
            scale /= 2;
        }
        double[] Tap(int x, int y)
        {
            var o = (Math.Clamp(y, 0, sh - 1) * sw + Math.Clamp(x, 0, sw - 1)) * 4;
            return [p[o], p[o + 1], p[o + 2], p[o + 3]];
        }
        static double[] Cr(double t) =>
            [-0.5 * t * t * t + t * t - 0.5 * t, 1.5 * t * t * t - 2.5 * t * t + 1, -1.5 * t * t * t + 2 * t * t + 0.5 * t, 0.5 * t * t * t - 0.5 * t * t];
        var dst = new byte[w * h * 4];
        var dt = ds * scale;
        for (var oy = 0; oy < h; oy++)
        for (var ox = 0; ox < w; ox++)
        {
            var px = (ox + 0.5) * sx * scale;
            var py = (oy + 0.5) * sy * scale;
            var acc = new double[4];
            if (ds <= 1)
            {
                var fx = Math.Floor(px - 0.5);
                var fy = Math.Floor(py - 0.5);
                var wx = Cr(px - 0.5 - fx);
                var wy = Cr(py - 0.5 - fy);
                for (var j = 0; j < 4; j++)
                for (var i = 0; i < 4; i++)
                {
                    var t = Tap((int)fx - 1 + i, (int)fy - 1 + j);
                    for (var c = 0; c < 4; c++) acc[c] += wx[i] * wy[j] * t[c];
                }
                for (var c = 0; c < 4; c++) acc[c] = Math.Max(0, acc[c]);
            }
            else
            {
                double wsum = 0;
                for (var y = (int)Math.Ceiling(py - dt - 0.5); y <= (int)Math.Floor(py + dt - 0.5); y++)
                for (var x = (int)Math.Ceiling(px - dt - 0.5); x <= (int)Math.Floor(px + dt - 0.5); x++)
                {
                    var k = Math.Max(0, 1 - Math.Abs(x + 0.5 - px) / dt) * Math.Max(0, 1 - Math.Abs(y + 0.5 - py) / dt);
                    if (k <= 0) continue;
                    var t = Tap(x, y);
                    for (var c = 0; c < 4; c++) acc[c] += k * t[c];
                    wsum += k;
                }
                for (var c = 0; c < 4; c++) acc[c] /= wsum;
            }
            var al = Math.Min(1, acc[3]);
            var o = (oy * w + ox) * 4;
            if (al <= 0.0001) continue;
            for (var c = 0; c < 3; c++)
                dst[o + c] = (byte)GlintLoop.Core.Numerics.MathX.RoundHalfUpToInt(255 * GlintLoop.Core.Numerics.Srgb.Enc(GlintLoop.Core.Numerics.MathX.Sat(acc[c] / al)));
            dst[o + 3] = (byte)GlintLoop.Core.Numerics.MathX.RoundHalfUpToInt(255 * al);
        }
        return dst;
    }

    [Test(Slow = true)]
    public void 規則3_再標本化はキャッチマルロムと三角窓と箱形縮小の参照実装と1以内で一致する()
    {
        // 半透明を含む、位置ごとに値が違う画像(α は 96〜255。straight の値が α の誤差に敏感にならない範囲)
        var src = CompositeImg.Make(300, 200, (x, y) =>
        {
            var h = GlintLoop.Core.Numerics.Hash.Hash32((uint)(y * 300 + x));
            return ((x * 5 + (int)(h & 31)) % 256, (int)(128 + 100 * Math.Sin(x * 0.11 + y * 0.07)), (int)((h >> 8) & 255), 96 + (int)((h >> 16) % 160));
        });
        using var kit = new CompositeKit();
        kit.Import(src);
        foreach (var lout in new[] { 700, 450, 120, 37, 9 }) // ds は 0.43・0.67・2.5・8.1・33.3(1/2 に 2 回縮めてから三角窓)
        {
            var im = kit.Render(lout);
            var reference = ReferenceResample(src, im.Width, im.Height);
            for (var i = 0; i < reference.Length; i++)
            {
                if (Math.Abs(reference[i] - im.Pixels[i]) > 1)
                    throw new CheckFailedException($"長辺 {lout}: 画素 ({i / 4 % im.Width}, {i / 4 / im.Width}) のチャンネル {i % 4} 参照 {reference[i]}、実際 {im.Pixels[i]}");
            }
        }
    }

    // ================================================================ 幾何(CPU)

    [Test]
    public void 規則3_折りたたみの逆変換は回転と切り抜きと余白を順に当てた写像と一致する()
    {
        var src = new SizeD(300, 200);
        var stack = new[]
        {
            Entry(EffectSchema.Rotate, ("angle", new NumberValue(30)), ("fit", new StringValue("expand"))),
            Entry(EffectSchema.Crop, ("rect", new RectValue(100, 50, 80, 20))),
            Entry(EffectSchema.Pad, ("rect", new RectValue(40, 10, 0, 70)), ("fill", new StringValue("#00000000"))),
        };
        var native = CompositeGeometry.NativeSize(src, stack);
        const int outW = 157, outH = 111;
        var plan = ShapeFold.Plan(src, stack, outW, outH);
        Check.Near(native.W, plan.Native.W, 1e-9, "ネイティブ寸法の幅");
        Check.Near(native.H, plan.Native.H, 1e-9, "ネイティブ寸法の高さ");
        // 前向き: p → 回転 → 切り抜き → 余白 → 出力の倍率
        var th = 30 * Math.PI / 180;
        var rot = CompositeGeometry.RotateSize(src, 30, false);
        var crop = CompositeGeometry.CropSize(rot, new RectValue(100, 50, 80, 20));
        var s0 = Math.Min(crop.W, crop.H);
        foreach (var (px, py) in new[] { (0.5, 0.5), (150.0, 100.0), (299.5, 10.25), (37.0, 180.0) })
        {
            var vx = px - src.W / 2;
            var vy = py - src.H / 2;
            var qx = vx * Math.Cos(th) - vy * Math.Sin(th) + rot.W / 2;
            var qy = vx * Math.Sin(th) + vy * Math.Cos(th) + rot.H / 2;
            qx -= rot.W * 100 / 1000;
            qy -= rot.H * 50 / 1000;
            qx += 40 * s0 / 1000;
            qy += 10 * s0 / 1000;
            var ox = qx * outW / native.W;
            var oy = qy * outH / native.H;
            var (bx, by) = plan.SourceAt(ox, oy);
            Check.Near(px, bx, 1e-9, $"({px}, {py}) の逆変換の x");
            Check.Near(py, by, 1e-9, $"({px}, {py}) の逆変換の y");
        }
        Check.Near(Math.Max(native.W / outW, native.H / outH), plan.Ds, 1e-12, "ds");
    }

    [Test]
    public void INV7_白黒の1画素の縞を50パーセントに縮めると中間の灰はsRGBの188()
    {
        // 線形光で平均すると (0+1)/2 → enc(0.5)≈188。sRGB の値で平均すると 128 になる(INV-7 の検査方法)
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Make(200, 100, (x, _) => x % 2 == 0 ? (0, 0, 0, 255) : (255, 255, 255, 255)));
        var im = kit.Render(100);
        CompositeCheck.Size(im, 100, 50, "50% の出力");
        // 左右の端の列は、三角窓(半径 2)が外側を端の画素で延ばして読むので、黒か白の重みが増える(規則 3 の端の扱い)。内側だけを見る
        for (var y = 0; y < im.Height; y++)
        for (var x = 1; x < im.Width - 1; x++)
            CompositeCheck.Pixel(im, x, y, (188, 188, 188, 255), 2, $"縞を縮めた灰 ({x}, {y})");
    }

    private static StackEntry Entry(string id, params (string Key, ParamValue Value)[] values) =>
        new(id, true, EffectOrigin.User, 1, 100, values.Select(v => new KeyValuePair<string, ParamValue>(v.Key, v.Value)).ToArray());
}
