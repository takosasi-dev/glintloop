// 文書の描画(DocumentRenderer)のテスト(WARP だけ): AC-17 の (1)〜(8)、AC-12 を文書の経路で、規則 5 のノードの再利用。
// 画像はすべてコードで作り、文書は EditorSession(カタログは埋め込みの EffectDef)で組み立てる。期待値は仕様書の式と AC の表の値。
// 形ステージの折りたたみの性質(AC-14 の (2))は CompositeFoldTests.cs、見た目の一致(AC-16)は CompositeAc16Tests.cs に置く。
using System.Security.Cryptography;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Render;

/// <summary>文書の描画のテストの道具。WARP の RenderService・画像の表・DocumentRenderer・EditorSession を 1 つにまとめる。</summary>
internal sealed class CompositeKit : IDisposable
{
    private readonly Dictionary<string, SourceImage> _images = new(StringComparer.Ordinal);

    public CompositeKit(DocumentRendererOptions? options = null)
    {
        Rs = RenderService.Create(GpuAdapterRequest.Warp);
        Renderer = new DocumentRenderer(Rs, new FuncSourceImages(sha => _images.TryGetValue(sha, out var im) ? im : null), options);
        Session = new EditorSession(Rs.Effects, new FixedSeedSource(12345));
    }

    public RenderService Rs { get; }
    public DocumentRenderer Renderer { get; }
    public EditorSession Session { get; }
    public SessionDocument Doc => Session.Document!;

    /// <summary>画像を表に入れ、取り込みの情報を返す(SHA-256 は画素のバイト列から作る)。</summary>
    public ImportedSource Source(CompositeImg img)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(img.Px));
        _images[sha] = new SourceImage(img.W, img.H, img.Px);
        return new ImportedSource(sha, "png", Math.Max(1, img.Px.Length), img.W, img.H, "img");
    }

    /// <summary>画像を順に取り込む(最初の 1 枚が土台。文書が無いとき)。足したレイヤーの配置は既定の (0, 0, 100) に戻す。</summary>
    public void Import(params CompositeImg[] imgs)
    {
        var before = Session.Document?.Layers.Count ?? 0;
        Session.AddImportedImages(imgs.Select(Source).ToArray());
        for (var i = Math.Max(1, before); i < Doc.Layers.Count; i++) Session.SetLayerPlacement(i, 0, 0, 100);
    }

    public void Place(int index, double x, double y, double scale) =>
        Check.True(Session.SetLayerPlacement(index, x, y, scale).Changed || Doc.Layers[index].Place == new Placement(x, y, scale), "配置");

    /// <summary>対象にエフェクトを足して、値を入れる。足した行の番号を返す。</summary>
    public int AddEffect(Target target, string id, params (string Key, ParamValue Value)[] values)
    {
        var outcome = Session.AddEffect(target, id);
        Check.True(outcome.Changed, $"{id} を足せない");
        var row = outcome.Row!.Value;
        foreach (var (k, v) in values) Check.True(Session.SetEffectParam(target, row, k, v).Changed, $"{id} の {k} を変えられない");
        return row;
    }

    public DocumentImage Render(int lout, DocumentPixelFormat format = DocumentPixelFormat.StraightRgba8) =>
        Renderer.RenderAsync(Doc, lout, format).GetAwaiter().GetResult();

    public DocumentImage Render(SessionDocument doc, int lout, DocumentPixelFormat format = DocumentPixelFormat.StraightRgba8) =>
        Renderer.RenderAsync(doc, lout, format).GetAwaiter().GetResult();

    public void Dispose()
    {
        Renderer.Dispose();
        Rs.Dispose();
    }
}

/// <summary>straight の sRGB 8 ビットの RGBA の画像(行を詰める)。</summary>
internal sealed record CompositeImg(int W, int H, byte[] Px)
{
    public static CompositeImg Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }
        return new CompositeImg(w, h, px);
    }

    public static CompositeImg Make(int w, int h, Func<int, int, (int R, int G, int B, int A)> f)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b, a) = f(x, y);
            var o = (y * w + x) * 4;
            px[o] = (byte)r;
            px[o + 1] = (byte)g;
            px[o + 2] = (byte)b;
            px[o + 3] = (byte)a;
        }
        return new CompositeImg(w, h, px);
    }

    public (int R, int G, int B, int A) At(int x, int y)
    {
        var o = (y * W + x) * 4;
        return (Px[o], Px[o + 1], Px[o + 2], Px[o + 3]);
    }
}

internal static class CompositeCheck
{
    public static (int R, int G, int B, int A) At(DocumentImage im, int x, int y)
    {
        var o = (y * im.Width + x) * 4;
        return (im.Pixels[o], im.Pixels[o + 1], im.Pixels[o + 2], im.Pixels[o + 3]);
    }

    public static void Size(DocumentImage im, int w, int h, string what)
    {
        Check.Equal(w, im.Width, what + " の幅");
        Check.Equal(h, im.Height, what + " の高さ");
    }

    public static void Pixel(DocumentImage im, int x, int y, (int R, int G, int B, int A) expected, int tol, string what)
    {
        var a = At(im, x, y);
        var d = Math.Max(Math.Max(Math.Abs(a.R - expected.R), Math.Abs(a.G - expected.G)),
            Math.Max(Math.Abs(a.B - expected.B), Math.Abs(a.A - expected.A)));
        Check.True(d <= tol, $"{what}: ({x}, {y}) 期待 {expected}±{tol}、実際 {a}");
    }

    public static void All(DocumentImage im, (int R, int G, int B, int A) expected, int tol, string what)
    {
        for (var y = 0; y < im.Height; y++)
        for (var x = 0; x < im.Width; x++)
            Pixel(im, x, y, expected, tol, what);
    }

    /// <summary>各画素が pred に従って a か b に 1 以内で一致する。</summary>
    public static void Region(DocumentImage im, Func<int, int, bool> inside, (int, int, int, int) a, (int, int, int, int) b, int tol, string what)
    {
        for (var y = 0; y < im.Height; y++)
        for (var x = 0; x < im.Width; x++)
            Pixel(im, x, y, inside(x, y) ? a : b, tol, what);
    }

    public static void Same(byte[] expected, byte[] actual, string what)
    {
        Check.Equal(expected.Length, actual.Length, what + " の長さ");
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                throw new CheckFailedException($"{what}: バイト {i}(画素 {i / 4} のチャンネル {i % 4})期待 {expected[i]}、実際 {actual[i]}");
        }
    }
}

public sealed class CompositeTests
{
    private static readonly (int, int, int, int) Gray = (128, 128, 128, 255);
    private static readonly (int, int, int, int) Red = (255, 0, 0, 255);
    private static readonly (int, int, int, int) Green = (0, 255, 0, 255);
    private static readonly (int, int, int, int) Clear = (0, 0, 0, 0);

    // ---- AC-12 の画像 ----

    private static CompositeImg AllValues256() => CompositeImg.Make(256, 256, (x, y) => (x, y, x ^ y, 255));

    private static CompositeImg HashImage(int size) => CompositeImg.Make(size, size, (x, y) =>
    {
        var h = Hash.Hash32((uint)(y * size + x));
        return ((int)(h & 255), (int)((h >> 8) & 255), (int)((h >> 16) & 255), 255);
    });

    private static readonly byte[] PartialAlphas = [1, 2, 17, 64, 128, 200, 254];

    private static CompositeImg PartialAlphaImage() => CompositeImg.Make(256, 8, (x, row) =>
        (x, 255 - x, (37 * x + 11) % 256, row < 7 ? PartialAlphas[row] : 0));

    // ================================================================ AC-17 (1)・AC-12

    [Test(Slow = true)]
    public void AC17_1_AC12_土台1枚の文書は取り込んだだけの出力と完全に一致する()
    {
        using var kit = new CompositeKit();
        foreach (var img in new[] { AllValues256(), HashImage(1024) })
        {
            var s = new EditorSession(kit.Rs.Effects, new FixedSeedSource(1));
            s.AddImportedImages([kit.Source(img)]);
            var doc = s.Document!;
            var plain = kit.Rs.RenderStraightRgba8Async(img.Px, img.W, img.H, [], FrameContext.Still).GetAwaiter().GetResult();
            var out1 = kit.Render(doc, Math.Max(img.W, img.H));
            CompositeCheck.Size(out1, img.W, img.H, "出力");
            CompositeCheck.Same(plain, out1.Pixels, $"{img.W}×{img.H} の取り込んだだけの出力");
            CompositeCheck.Same(img.Px, out1.Pixels, $"{img.W}×{img.H} の入力(AC-12 の (1))");
            // 画面のプロキシの経路(既定の長辺)も同じ値になる
            var proxy = kit.Renderer.RenderProxyAsync(doc, DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult();
            CompositeCheck.Same(out1.Pixels, proxy.Pixels, "プロキシの経路");
            // (3) 表示用(Pbgra32)は α=255 の画素の値が (1) と一致する(B・G・R・A の並び)
            var bgra = kit.Render(doc, Math.Max(img.W, img.H), DocumentPixelFormat.Pbgra32);
            for (var i = 0; i < img.W * img.H; i++)
            {
                if (bgra.Pixels[i * 4 + 2] != out1.Pixels[i * 4] || bgra.Pixels[i * 4 + 1] != out1.Pixels[i * 4 + 1] ||
                    bgra.Pixels[i * 4] != out1.Pixels[i * 4 + 2] || bgra.Pixels[i * 4 + 3] != 255)
                    throw new CheckFailedException($"Pbgra32 の画素 {i} が straight と違う");
            }
        }
    }

    [Test]
    public void AC17_1_AC12_半透明の画素は1以内でα0は全部0()
    {
        using var kit = new CompositeKit();
        var img = PartialAlphaImage();
        kit.Import(img);
        var out1 = kit.Render(256);
        CompositeCheck.Size(out1, 256, 8, "出力");
        for (var row = 0; row < 8; row++)
        for (var x = 0; x < 256; x++)
        {
            var (r, g, b, a) = CompositeCheck.At(out1, x, row);
            var e = img.At(x, row);
            if (e.A == 0)
            {
                Check.True(r == 0 && g == 0 && b == 0 && a == 0, $"α=0 の画素 ({x}, {row}) が {r},{g},{b},{a}");
                continue;
            }
            Check.Equal(e.A, a, $"({x}, {row}) の α");
            Check.True(Math.Abs(r - e.R) <= 1 && Math.Abs(g - e.G) <= 1 && Math.Abs(b - e.B) <= 1,
                $"({x}, {row}) α={e.A}: 期待 {e.R},{e.G},{e.B}、実際 {r},{g},{b}");
        }
    }

    [Test]
    public void AC17_1_同じ大きさの不透明な画像を重ねると出力はその画像と一致し透明なら土台と一致する()
    {
        using var kit = new CompositeKit();
        var baseImg = HashImage(64);
        var top = CompositeImg.Make(64, 64, (x, y) => ((x * 4) & 255, (y * 4) & 255, (x * y) & 255, 255));
        var clearTop = CompositeImg.Make(64, 64, (x, y) => (x * 3 & 255, 200, y, 0));
        kit.Import(baseImg, top);
        CompositeCheck.Same(top.Px, kit.Render(64).Pixels, "不透明な画像を重ねた出力");
        kit.Session.SetLayerVisible(1, false);
        kit.Import(clearTop);
        CompositeCheck.Same(baseImg.Px, kit.Render(64).Pixels, "全画素が α=0 の画像を重ねた出力");
    }

    // ================================================================ AC-17 (2) 配置(規則 11)

    private static CompositeKit PlacementKit(CompositeImg top)
    {
        var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(100, 100, 128, 128, 128), top);
        return kit;
    }

    private static void RedRect(DocumentImage im, int x0, int x1, int y0, int y1, string what) =>
        CompositeCheck.Region(im, (x, y) => x >= x0 && x <= x1 && y >= y0 && y <= y1, Red, Gray, 1, what);

    [Test]
    public void AC17_2_配置の枠の左上はroundで決まる()
    {
        using var kit = PlacementKit(CompositeImg.Solid(20, 10, 255, 0, 0));
        RedRect(kit.Render(100), 40, 59, 45, 54, "(0, 0, 100)");
        kit.Place(1, 10.5, -3.25, 100);
        RedRect(kit.Render(100), 51, 70, 42, 51, "(10.5, −3.25, 100)");
        kit.Place(1, 0.5, 0, 100);
        RedRect(kit.Render(100), 41, 60, 45, 54, "(0.5, 0, 100)");
        kit.Place(1, -0.5, 0, 100);
        RedRect(kit.Render(100), 40, 59, 45, 54, "(−0.5, 0, 100)");
        kit.Place(1, 0, 0, 50);
        RedRect(kit.Render(100), 45, 54, 48, 52, "(0, 0, 50)");
        kit.Place(1, 45, 0, 100);
        var edge = kit.Render(100);
        CompositeCheck.Size(edge, 100, 100, "(45, 0, 100)");
        RedRect(edge, 85, 99, 45, 54, "(45, 0, 100)");
        kit.Place(1, 200, 0, 100);
        CompositeCheck.All(kit.Render(100), Gray, 1, "(200, 0, 100) は土台のまま(E-17)");
    }

    [Test]
    public void AC17_2_大きいレイヤーは作業面で切られ市松模様は補間せずに写る()
    {
        using (var kit = PlacementKit(CompositeImg.Solid(400, 400, 255, 0, 0)))
        {
            var im = kit.Render(100);
            CompositeCheck.Size(im, 100, 100, "400×400 を置いた出力");
            CompositeCheck.All(im, Red, 1, "400×400 の赤");
        }
        var checker = CompositeImg.Make(20, 10, (x, y) => ((x + y) % 2 == 0 ? (255, 255, 255, 255) : (0, 0, 0, 255)));
        using (var kit = PlacementKit(checker))
        {
            foreach (var (px, py, qx, qy) in new[] { (0, 0, 40, 45), (7, 3, 47, 48) })
            {
                kit.Place(1, px, py, 100);
                var im = kit.Render(100);
                for (var y = 0; y < 100; y++)
                for (var x = 0; x < 100; x++)
                {
                    var inFrame = x >= qx && x < qx + 20 && y >= qy && y < qy + 10;
                    var e = inFrame ? checker.At(x - qx, y - qy) : (128, 128, 128, 255);
                    Check.Equal(e, CompositeCheck.At(im, x, y), $"市松模様 ({px}, {py}) の ({x}, {y})");
                }
            }
        }
    }

    [Test]
    public void AC17_2_長辺200では枠も2倍になる()
    {
        using var kit = PlacementKit(CompositeImg.Solid(20, 10, 255, 0, 0));
        kit.Place(1, 10, 0, 100);
        var im = kit.Render(200);
        CompositeCheck.Size(im, 200, 200, "長辺 200");
        RedRect(im, 100, 139, 90, 109, "s=2 の (10, 0, 100)");
    }

    // ================================================================ AC-17 (3) 重ね(規則 13)

    private static (int, int, int, int) Blend(CompositeImg baseImg, CompositeImg top, BlendMode blend, int opacity)
    {
        using var kit = new CompositeKit();
        kit.Import(baseImg, top);
        kit.Session.SetLayerBlend(1, blend);
        kit.Session.SetLayerOpacity(1, opacity);
        var im = kit.Render(4);
        return CompositeCheck.At(im, 1, 1);
    }

    private static void Near((int R, int G, int B, int A) expected, (int R, int G, int B, int A) actual, int tol, string what)
    {
        var d = Math.Max(Math.Max(Math.Abs(actual.R - expected.R), Math.Abs(actual.G - expected.G)),
            Math.Max(Math.Abs(actual.B - expected.B), Math.Abs(actual.A - expected.A)));
        Check.True(d <= tol, $"{what}: 期待 {expected}±{tol}、実際 {actual}");
    }

    [Test]
    public void AC17_3_不透明な灰に赤を重ねたブレンドの表()
    {
        var g = CompositeImg.Solid(4, 4, 128, 128, 128);
        var r = CompositeImg.Solid(4, 4, 255, 0, 0);
        var table = new (BlendMode, int, (int, int, int))[]
        {
            (BlendMode.Normal, 100, (255, 0, 0)), (BlendMode.Multiply, 100, (128, 0, 0)), (BlendMode.Screen, 100, (255, 128, 128)),
            (BlendMode.Overlay, 100, (255, 1, 1)), (BlendMode.SoftLight, 100, (181, 64, 64)), (BlendMode.Add, 100, (255, 128, 128)),
            (BlendMode.Normal, 50, (205, 92, 92)), (BlendMode.Multiply, 50, (128, 92, 92)), (BlendMode.Screen, 50, (205, 128, 128)),
            (BlendMode.Overlay, 50, (205, 92, 92)), (BlendMode.SoftLight, 50, (157, 102, 102)), (BlendMode.Add, 50, (220, 128, 128)),
        };
        foreach (var (mode, op, (er, eg, eb)) in table)
            Near((er, eg, eb, 255), Blend(g, r, mode, op), 2, $"{mode} 不透明度 {op}");
        foreach (var mode in Enum.GetValues<BlendMode>())
            Near((128, 128, 128, 255), Blend(g, r, mode, 0), 2, $"{mode} 不透明度 0");
    }

    [Test]
    public void AC17_3_線形光で混ぜ半透明と透明の下と加算の上限()
    {
        Near((188, 188, 188, 255), Blend(CompositeImg.Solid(4, 4, 0, 0, 0), CompositeImg.Solid(4, 4, 255, 255, 255), BlendMode.Normal, 50), 1,
            "黒に白を 50 で(線形光)");
        Near((205, 92, 92, 255), Blend(CompositeImg.Solid(4, 4, 128, 128, 128), CompositeImg.Solid(4, 4, 255, 0, 0, 128), BlendMode.Normal, 100), 1,
            "灰に半透明の赤");
        var b = CompositeImg.Solid(4, 4, 40, 160, 220, 128);
        var s = CompositeImg.Solid(4, 4, 250, 120, 30, 128);
        Near((156, 124, 136, 192), Blend(b, s, BlendMode.Multiply, 100), 2, "半透明どうしの multiply");
        Near((210, 166, 186, 192), Blend(b, s, BlendMode.Screen, 100), 2, "半透明どうしの screen");
        Near((160, 146, 174, 192), Blend(b, s, BlendMode.Overlay, 100), 2, "半透明どうしの overlay");
        Near((163, 147, 175, 192), Blend(b, s, BlendMode.SoftLight, 100), 2, "半透明どうしの softlight");
        foreach (var mode in Enum.GetValues<BlendMode>())
            Near((200, 100, 50, 255), Blend(CompositeImg.Solid(4, 4, 0, 0, 0, 0), CompositeImg.Solid(4, 4, 200, 100, 50), mode, 100), 1, $"透明の下に {mode}");
        Near((255, 255, 255, 255), Blend(CompositeImg.Solid(4, 4, 200, 200, 200), CompositeImg.Solid(4, 4, 200, 200, 200), BlendMode.Add, 100), 0,
            "add の上限");
    }

    // ================================================================ AC-17 (4) 順序と表示

    [Test]
    public void AC17_4_順序と表示と空のレイヤー()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(100, 100, 128, 128, 128), CompositeImg.Solid(50, 50, 255, 0, 0), CompositeImg.Solid(50, 50, 0, 255, 0));
        static bool Inside(int x, int y) => x >= 25 && x < 75 && y >= 25 && y < 75;
        CompositeCheck.Region(kit.Render(100), Inside, Green, Gray, 1, "重なりは緑");
        kit.Session.MoveLayer(2, 1);
        CompositeCheck.Region(kit.Render(100), Inside, Red, Gray, 1, "並べ替えると赤");
        kit.Session.Undo();
        kit.Session.SetLayerVisible(2, false);
        CompositeCheck.Region(kit.Render(100), Inside, Red, Gray, 1, "2 枚目をオフにすると赤");
        kit.Session.SetLayerVisible(2, true);
        kit.Session.SetLayerVisible(0, false);
        var noBase = kit.Render(100);
        CompositeCheck.Size(noBase, 100, 100, "土台をオフにした出力");
        CompositeCheck.Region(noBase, Inside, Green, Clear, 1, "土台をオフ");
        kit.Session.SetLayerVisible(1, false);
        kit.Session.SetLayerVisible(2, false);
        var none = kit.Render(100);
        CompositeCheck.Size(none, 100, 100, "全部オフ");
        CompositeCheck.All(none, Clear, 0, "全部オフ(E-18)");

        using var kit2 = new CompositeKit();
        kit2.Import(CompositeImg.Solid(100, 100, 128, 128, 128));
        Check.True(kit2.Session.AddEmptyLayer().Changed, "空のレイヤーを足せない");
        Check.True(kit2.Session.SetLayerFill(1, "#FF000080").Changed, "塗りを変えられない");
        CompositeCheck.All(kit2.Render(100), (205, 92, 92, 255), 1, "空のレイヤー #FF000080");
    }

    // ================================================================ AC-17 (5) 合成の大きさ

    [Test]
    public void AC17_5_合成の大きさは土台で決まる()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(400, 300, 128, 128, 128), CompositeImg.Solid(1000, 1000, 0, 0, 255));
        CompositeCheck.Size(kit.Render(400), 400, 300, "1000×1000 を足した出力");

        using var kit2 = new CompositeKit();
        kit2.Import(CompositeImg.Solid(400, 300, 128, 128, 128), CompositeImg.Solid(20, 10, 255, 0, 0));
        kit2.AddEffect(Target.OfLayer(0), EffectSchema.Crop, ("rect", new RectValue(250, 0, 250, 0)));
        var cropped = kit2.Render(300);
        CompositeCheck.Size(cropped, 200, 300, "土台を切り抜いた出力");
        CompositeCheck.Region(cropped, (x, y) => x >= 90 && x <= 109 && y >= 145 && y <= 154, Red, Gray, 1, "切り抜いた土台の上の枠");

        using var kit3 = new CompositeKit();
        kit3.Import(CompositeImg.Solid(400, 300, 128, 128, 128), CompositeImg.Solid(50, 50, 255, 0, 0));
        kit3.Session.MoveLayer(1, 0);
        var lout = kit3.Renderer.ProxyLongSide(kit3.Doc);
        Check.Equal(50, lout, "既定の長辺");
        var swapped = kit3.Renderer.RenderProxyAsync(kit3.Doc).GetAwaiter().GetResult();
        CompositeCheck.Size(swapped, 50, 50, "並べ替えて土台にした出力");
    }

    // ================================================================ AC-17 (6) スタック

    [Test]
    public void AC17_6_ルックの強さはレイヤーごとで全体の形ステージに回転は足せない()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(100, 100, 128, 128, 128), CompositeImg.Solid(20, 10, 60, 120, 180));
        kit.AddEffect(Target.OfLayer(0), "exposure", ("ev", new NumberValue(1)));
        kit.AddEffect(Target.OfLayer(1), "exposure", ("ev", new NumberValue(1)));
        // origin=look の行にする(ルックの適用は Phase 3 の操作なので、文書を直接書き換える)
        var doc = kit.Doc;
        var layers = doc.Layers.Select((l, i) => l with
        {
            Stack = l.Stack.Select(e => e with { Origin = EffectOrigin.Look }).ToArray(),
            LookStrength = i == 1 ? 0 : 100,
        }).ToArray();
        var im = kit.Render(doc with { Layers = layers }, 100);
        var brighter = (int)MathX.RoundHalfUp(255 * Srgb.Enc(Srgb.Lin8(128) * 2));
        CompositeCheck.Pixel(im, 5, 5, (brighter, brighter, brighter, 255), 1, "土台は明るくなる");
        CompositeCheck.Pixel(im, 50, 50, (60, 120, 180, 255), 1, "lookStrength=0 の最前面は変わらない");

        // 全体の形ステージに回転・切り抜き・余白を足す操作は拒否され、JSON が変わらない(FR-26)
        var json = kit.Session.Json;
        foreach (var id in new[] { EffectSchema.Rotate, EffectSchema.Crop, EffectSchema.Pad })
        {
            Check.True(!kit.Session.AddEffect(Target.All, id).Changed, $"全体に {id} を足せてしまう");
            Check.Equal(json, kit.Session.Json, $"全体に {id} を足したあとの JSON");
        }
    }

    [Test]
    public void AC17_6_最前面の反転と全体のinvertは統合後に確かめる()
    {
        // flip(形ステージの担当)と invert(ルックの担当)は 2 段目の別の worktree で作っている。カタログにあるときだけ確かめる。
        // 統合のあとは、この 2 つの検査が自動で走る(無いうちは何もしない)。
        using var kit = new CompositeKit();
        var half = CompositeImg.Make(20, 10, (x, _) => x < 10 ? (255, 0, 0, 255) : (0, 0, 255, 255));
        kit.Import(CompositeImg.Solid(100, 100, 128, 128, 128), half);
        if (kit.Rs.Effects.Find("flip") is not null)
        {
            kit.AddEffect(Target.OfLayer(1), "flip", ("h", new BoolValue(true)));
            var im = kit.Render(100);
            // 枠(x 40〜59、y 45〜54)の左半分が青、右半分が赤、枠の外は土台の灰
            for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
            {
                var inFrame = x >= 40 && x < 60 && y >= 45 && y < 55;
                var expected = !inFrame ? Gray : x < 50 ? (0, 0, 255, 255) : Red;
                CompositeCheck.Pixel(im, x, y, expected, 1, "反転した最前面(左半分は青、右半分は赤、外は灰)");
            }
        }
        if (kit.Rs.Effects.Find("invert") is not null)
        {
            var before = kit.Render(100);
            kit.AddEffect(Target.All, "invert");
            var after = kit.Render(100);
            for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
            {
                var b = CompositeCheck.At(before, x, y);
                // AC-14 の参照実装(invert は sRGB で 1−x)
                CompositeCheck.Pixel(after, x, y, (255 - b.R, 255 - b.G, 255 - b.B, b.A), 3, "全体の invert");
            }
        }
    }

    // ================================================================ AC-17 (7) 描画の大きさの上限(規則 11)

    [Test(Slow = true)]
    public void AC17_7_最大寸法8192で大きく置いたレイヤーは2のべきで縮めて一度だけ通知する()
    {
        using var kit = new CompositeKit(new DocumentRendererOptions { MaxTextureDimension = 8192 });
        kit.Session.NewEmptyCanvas(4000, 3000, "#808080FF");
        kit.Import(CompositeImg.Solid(3000, 2000, 30, 140, 210));
        kit.Place(1, 0, 0, 400);
        var layout = kit.Renderer.Layout(kit.Doc, 4000);
        Check.Equal(new RenderShrink(2, 3000, 2000), layout.Layers[1].Shrink, "レイヤーの縮め");
        var notices = new RenderShrinkNotices();
        var im = kit.Render(4000);
        CompositeCheck.Size(im, 4000, 3000, "出力");
        CompositeCheck.All(im, (30, 140, 210, 255), 1, "全画素がレイヤーの単色");
        Check.Equal(1, notices.Observe(im).Count, "1 回目の通知");
        Check.Equal(0, notices.Observe(kit.Render(4000)).Count, "同じレイヤーの 2 回目の描画");
        kit.Import(CompositeImg.Solid(3000, 2000, 30, 140, 210));
        kit.Place(2, 0, 0, 400);
        var two = notices.Observe(kit.Render(4000));
        Check.Equal(1, two.Count, "2 枚目のレイヤーの通知");
        Check.Equal(GlintLoop.Core.Notices.NoticeKeys.LayerRenderShrunk, two[0].Key, "通知のキー");
        Check.Equal(GlintLoop.Core.Notices.NoticeLevel.Info, two[0].Level, "通知の重さ");
        // 新規で文書を作り直して同じ操作をすると、通知が再び 1 回出る
        notices.Reset();
        kit.Session.CloseDocument();
        kit.Session.NewEmptyCanvas(4000, 3000, "#808080FF");
        kit.Import(CompositeImg.Solid(3000, 2000, 30, 140, 210));
        kit.Place(1, 0, 0, 400);
        Check.Equal(1, notices.Observe(kit.Render(4000)).Count, "新しい文書の通知");
    }

    [Test(Slow = true)]
    public void AC17_7_最大寸法16384で4096四方は縮めず4097は縮める()
    {
        using var kit = new CompositeKit(new DocumentRendererOptions { MaxTextureDimension = 16384 });
        kit.Import(CompositeImg.Solid(100, 100, 128, 128, 128), CompositeImg.Solid(4096, 4096, 10, 200, 60));
        var notices = new RenderShrinkNotices();
        var fits = kit.Render(100);
        Check.Equal(new RenderShrink(0, 4096, 4096), fits.Layout.Layers[1].Shrink, "4096×4096 の縮め");
        Check.Equal(0, notices.Observe(fits).Count, "4096×4096 の通知");
        CompositeCheck.All(fits, (10, 200, 60, 255), 1, "4096×4096 の色");
        kit.Session.SetLayerVisible(1, false);
        kit.Import(CompositeImg.Solid(4097, 4096, 220, 40, 90));
        var shrunk = kit.Render(100);
        Check.Equal(new RenderShrink(1, 2049, 2048), shrunk.Layout.Layers[2].Shrink, "4097×4096 の縮め");
        Check.Equal(1, notices.Observe(shrunk).Count, "4097×4096 の通知");
        CompositeCheck.All(shrunk, (220, 40, 90, 255), 1, "4097×4096 の色");
    }

    // ================================================================ AC-17 (8) 決定性と INV-6

    [Test]
    public void AC17_8_INV8_INV6_同じ文書の出力は一致し画面と書き出しは同じ経路を通る()
    {
        using var kit = new CompositeKit();
        kit.Import(HashImage(96), CompositeImg.Make(40, 30, (x, y) => (x * 6, y * 8, 90, 160 + x)));
        kit.Place(1, 13.25, -7.5, 75);
        kit.Session.SetLayerBlend(1, BlendMode.Overlay);
        kit.Session.SetLayerOpacity(1, 80);
        kit.AddEffect(Target.OfLayer(0), EffectSchema.Rotate, ("angle", new NumberValue(15)));
        kit.AddEffect(Target.OfLayer(1), "exposure", ("ev", new NumberValue(0.5)));
        kit.AddEffect(Target.All, "exposure", ("ev", new NumberValue(-0.3)));
        var doc = kit.Doc;
        var lout = kit.Renderer.ProxyLongSide(doc);
        var a = kit.Render(doc, lout).Pixels;
        var b = kit.Render(doc, lout).Pixels;
        CompositeCheck.Same(a, b, "2 回の描画(キャッシュから)");
        kit.Renderer.ClearCacheAsync().GetAwaiter().GetResult();
        CompositeCheck.Same(a, kit.Render(doc, lout).Pixels, "キャッシュを放したあとの描画");
        using var fresh = new CompositeKit();
        fresh.Import(HashImage(96), CompositeImg.Make(40, 30, (x, y) => (x * 6, y * 8, 90, 160 + x)));
        CompositeCheck.Same(a, fresh.Render(doc, lout).Pixels, "別の描画器");
        CompositeCheck.Same(a, kit.Renderer.RenderExportAsync(doc, lout).GetAwaiter().GetResult().Pixels, "書き出しの経路");
        CompositeCheck.Same(a, kit.Renderer.RenderProxyAsync(doc, DocumentPixelFormat.StraightRgba8).GetAwaiter().GetResult().Pixels,
            "画面のプロキシの経路");
    }

    // ================================================================ 規則 5: 静的な前段の再利用

    [Test]
    public void 規則5_配置と不透明度とブレンドを変えても静的な結果は作り直さない()
    {
        using var kit = new CompositeKit();
        kit.Import(HashImage(64), CompositeImg.Solid(20, 10, 255, 0, 0));
        kit.AddEffect(Target.OfLayer(1), "exposure", ("ev", new NumberValue(0.5)));
        kit.Render(64);
        var computed = kit.Renderer.Cache.Computed;
        kit.Place(1, 5, 5, 100);
        kit.Session.SetLayerOpacity(1, 60);
        kit.Session.SetLayerBlend(1, BlendMode.Screen);
        var moved = kit.Render(64);
        Check.Equal(computed + 1, kit.Renderer.Cache.Computed, "作ったノードの数(合成だけ)");
        using var fresh = new CompositeKit();
        fresh.Import(HashImage(64), CompositeImg.Solid(20, 10, 255, 0, 0));
        CompositeCheck.Same(fresh.Render(kit.Doc, 64).Pixels, moved.Pixels, "再利用した結果と作り直した結果");
    }

    [Test]
    public void 規則5_スライダーを動かすと下流だけを作り直す()
    {
        using var kit = new CompositeKit();
        kit.Import(HashImage(64));
        kit.AddEffect(Target.OfLayer(0), "exposure", ("ev", new NumberValue(0.5)));
        var second = kit.AddEffect(Target.OfLayer(0), "exposure", ("ev", new NumberValue(-0.25)));
        kit.Render(64);
        var computed = kit.Renderer.Cache.Computed;
        kit.Session.SetEffectParam(Target.OfLayer(0), second, "ev", new NumberValue(-0.5));
        var changed = kit.Render(64);
        // 作り直すのは、2 つ目の exposure と合成の 2 つ(折りたたみと 1 つ目の exposure は再利用)
        Check.Equal(computed + 2, kit.Renderer.Cache.Computed, "作ったノードの数");
        using var fresh = new CompositeKit();
        fresh.Import(HashImage(64));
        CompositeCheck.Same(fresh.Render(kit.Doc, 64).Pixels, changed.Pixels, "再利用した結果と作り直した結果");
    }

    [Test]
    public void 規則5_キャッシュの上限を超えると古いものから放し結果は変わらない()
    {
        using var small = new CompositeKit(new DocumentRendererOptions { CacheBytes = 64 * 64 * 8 * 2 });
        using var big = new CompositeKit();
        foreach (var kit in new[] { small, big })
        {
            kit.Import(HashImage(64), CompositeImg.Solid(20, 10, 255, 0, 0));
            kit.AddEffect(Target.OfLayer(0), "exposure", ("ev", new NumberValue(0.5)));
        }
        for (var i = 0; i < 3; i++)
        {
            small.Session.SetEffectParam(Target.OfLayer(0), 0, "ev", new NumberValue(0.1 * i));
            var a = small.Render(64);
            Check.True(small.Renderer.Cache.Bytes <= small.Renderer.Cache.LimitBytes, $"{i} 回目のキャッシュのバイト数");
            CompositeCheck.Same(big.Render(small.Doc, 64).Pixels, a.Pixels, $"{i} 回目の結果");
        }
    }
}
