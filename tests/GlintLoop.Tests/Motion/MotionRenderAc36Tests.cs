// レイヤーのモーションと全体のモーション(§9.7 の規則 5・9・12)を、WARP と見本の spin・slide で調べる: AC-36 の (1)(2)(3)(7)(8)(9) の spin・slide の分、
// AC-35 の (8) の重ねがけ、AC-34 の (1)(2)(3) の spin・slide の分と (5) 検査自身の確認。pulse などほかの id は各担当が同じ形で足す。
// 文書は CompositeKit(EditorSession と埋め込みの EffectDef)で組み立て、フレームは RenderAsync の frameK・frameN で渡す(k=N で t=1)。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Motions;

public sealed class MotionRenderAc36Tests
{
    private const int N = 40;
    private const int K = 10;

    /// <summary>100×50 の不透明な画像(左半分が赤、右半分が青。上の 10 行は少し明るくして上下も区別する)。</summary>
    private static CompositeImg LeftRight() => CompositeImg.Make(100, 50, (x, y) =>
        x < 50 ? (255, y < 10 ? 120 : 0, 0, 255) : (0, y < 10 ? 120 : 0, 255, 255));

    private static DocumentImage RenderAt(CompositeKit kit, SessionDocument doc, int lout, int k, int n = N) =>
        kit.Renderer.RenderAsync(doc, lout, DocumentPixelFormat.StraightRgba8, k, n).GetAwaiter().GetResult();

    /// <summary>AC-36 の (1) の文書: 土台 200×200 の透明で表示がオフ、2 枚目 100×50 を (40, −30, 100) に置き、2 枚目のモーションに id を入れる。</summary>
    private static void BuildFramed(CompositeKit kit, string id, params (string Key, ParamValue Value)[] values)
    {
        kit.Import(CompositeImg.Solid(200, 200, 0, 0, 0, 0), LeftRight());
        kit.Place(1, 40, -30, 100);
        Check.True(kit.Session.SetLayerVisible(0, false).Changed, "土台の表示をオフ");
        kit.AddEffect(Target.OfLayer(1), id, values);
    }

    /// <summary>参照: 100×50 の同じ画像だけを土台にして、同じモーションを入れた文書(出力は 100×50)。</summary>
    private static DocumentImage Reference(string id, int k, params (string Key, ParamValue Value)[] values)
    {
        using var kit = new CompositeKit();
        kit.Import(LeftRight());
        kit.AddEffect(Target.OfLayer(0), id, values);
        return RenderAt(kit, kit.Doc, 100, k);
    }

    private static readonly (string Id, (string, ParamValue)[] Values)[] SpinAndSlide =
    [
        ("spin", []),
        ("slide", []), // dir の既定は left
    ];

    [Test]
    public void AC36_1_枠を基準にした動きは参照の文書と枠の中で一致する()
    {
        foreach (var (id, values) in SpinAndSlide)
        {
            using var kit = new CompositeKit();
            BuildFramed(kit, id, values);
            var im = RenderAt(kit, kit.Doc, 200, K);
            var reference = Reference(id, K, values);
            CompositeCheck.Size(reference, 100, 50, "参照");
            for (var y = 0; y < 50; y++)
            for (var x = 0; x < 100; x++)
                CompositeCheck.Pixel(im, 90 + x, 45 + y, CompositeCheck.At(reference, x, y), 3, $"{id} の枠の中");
        }
    }

    [Test]
    public void AC36_2_回した像は枠の外に出てslideは枠の外を変えない()
    {
        using (var kit = new CompositeKit())
        {
            BuildFramed(kit, "spin");
            var im = RenderAt(kit, kit.Doc, 200, K);
            Check.Equal(255, CompositeCheck.At(im, 140, 30).A, "spin: 枠の外の点 (140, 30) の α");
            Check.Equal(0, CompositeCheck.At(im, 100, 70).A, "spin: 枠の中で回した像の外の点 (100, 70) の α");
            // 90° 回った像は x が 115〜164、y が 20〜119 を覆う
            Check.Equal(255, CompositeCheck.At(im, 116, 21).A, "spin: 像の左上の内側");
            Check.Equal(0, CompositeCheck.At(im, 113, 70).A, "spin: 像の左の外");
        }
        using (var kit = new CompositeKit())
        {
            BuildFramed(kit, "slide"); // dir=left(既定)
            var im = RenderAt(kit, kit.Doc, 200, K);
            Check.Equal(0, CompositeCheck.At(im, 80, 70).A, "slide: 点 (80, 70) の α");
            Check.Equal(0, CompositeCheck.At(im, 195, 70).A, "slide: 点 (195, 70) の α");
            // k=10(t=0.25)で左へ 25 画素: 枠の左端の列は元の x=25(赤)、x=25+50 の位置からは青
            CompositeCheck.Pixel(im, 90, 70, (255, 0, 0, 255), 1, "slide: 枠の左端");
            CompositeCheck.Pixel(im, 90 + 25, 70, (0, 0, 255, 255), 1, "slide: 元の x=50 が 25 に来る");
            CompositeCheck.Pixel(im, 90 + 75, 70, (255, 0, 0, 255), 1, "slide: 反対側から繰り返す");
        }
    }

    [Test]
    public void AC36_3_全体のモーションの入力は重ね終えた合成で枠は合成の全体()
    {
        var gradient = CompositeImg.Make(200, 200, (x, y) => (x, y, (x + y) / 2, 255));
        using var kit = new CompositeKit();
        kit.Import(gradient, CompositeImg.Solid(100, 50, 255, 0, 0));
        kit.Place(1, 40, -30, 100);
        var composite = RenderAt(kit, kit.Doc, 200, K); // 全体のスタックが空の合成
        kit.AddEffect(Target.All, "slide", ("dir", new StringValue("right")));
        var im = RenderAt(kit, kit.Doc, 200, K);

        using var refKit = new CompositeKit();
        refKit.Import(new CompositeImg(200, 200, composite.Pixels));
        refKit.AddEffect(Target.All, "slide", ("dir", new StringValue("right")));
        var reference = RenderAt(refKit, refKit.Doc, 200, K);
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
            CompositeCheck.Pixel(im, x, y, CompositeCheck.At(reference, x, y), 3, "全体の slide");
        // 右へ 50 画素: 赤の矩形(x 90〜189)の一部が左端から繰り返して出る
        CompositeCheck.Pixel(im, 10, 60, (255, 0, 0, 255), 1, "繰り返して左端に出た赤");
    }

    [Test]
    public void AC36_7_全体の形ステージの入力は各レイヤーのモーションを通した合成()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Solid(200, 200, 0, 0, 0, 0), LeftRight());
        kit.Place(1, 40, -30, 100);
        Check.True(kit.Session.SetLayerVisible(0, false).Changed, "土台の表示をオフ");
        kit.AddEffect(Target.OfLayer(1), "slide"); // dir=left(既定)
        var plain = RenderAt(kit, kit.Doc, 200, K);
        kit.AddEffect(Target.All, "flip", ("h", new BoolValue(true)));
        var flipped = RenderAt(kit, kit.Doc, 200, K);
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
            CompositeCheck.Pixel(flipped, x, y, CompositeCheck.At(plain, 199 - x, y), 0, "左右の反転");
    }

    [Test(Slow = true)]
    public void AC36_8_静的な前段は書き出し1回につき1回だけ作る()
    {
        using var kit = new CompositeKit();
        kit.Import(CompositeImg.Make(120, 120, (x, y) => (x * 2, y * 2, 90, 255)), LeftRight(), CompositeImg.Solid(60, 60, 30, 200, 90));
        kit.Place(1, -20, 20, 100);
        kit.Place(2, 30, -30, 100);
        var blurRow = new int[3];
        for (var i = 0; i < 3; i++)
        {
            blurRow[i] = kit.AddEffect(Target.OfLayer(i), "blur");
            kit.AddEffect(Target.OfLayer(i), "spin");
        }
        void Export()
        {
            for (var k = 0; k < N; k++) kit.Renderer.RenderExportAsync(kit.Doc, 120, k, N).GetAwaiter().GetResult();
        }
        kit.Renderer.ResetEffectRuns();
        Export();
        Check.Equal(3, kit.Renderer.EffectRuns("blur"), "40 フレームの書き出しのぼかしの実行(レイヤーごとに 1 回)");
        Check.Equal(3 * N, kit.Renderer.EffectRuns("spin"), "spin はフレームごと");

        kit.Renderer.ResetEffectRuns();
        kit.Place(1, -10, 25, 100);
        Export();
        Check.Equal(0, kit.Renderer.EffectRuns("blur"), "位置だけを動かしたあとのぼかしの実行");

        kit.Renderer.ResetEffectRuns();
        Check.True(kit.Session.SetEffectParam(Target.OfLayer(1), blurRow[1], "radius", new NumberValue(30)).Changed, "半径を変える");
        Export();
        Check.Equal(1, kit.Renderer.EffectRuns("blur"), "2 枚目の半径を変えたあとのぼかしの実行");
    }

    [Test]
    public void AC36_9_枠を基準にした動きの継ぎ目はt1とt0が1以内()
    {
        foreach (var (id, values) in SpinAndSlide)
        {
            using var kit = new CompositeKit();
            BuildFramed(kit, id, values);
            var t0 = RenderAt(kit, kit.Doc, 200, 0);
            var t1 = RenderAt(kit, kit.Doc, 200, N);
            Check.True(MotionTestKit.MaxDiff(t0.Pixels, t1.Pixels) <= 1, $"{id}: t=1 と t=0 の差の最大 {MotionTestKit.MaxDiff(t0.Pixels, t1.Pixels)}");
        }
    }

    [Test]
    public void AC35_8_spinとslideの重ねがけは順で結果が変わり1つずつ通した結果と一致する()
    {
        var img = MotionTestKit.Ac35Image();
        DocumentImage Run(CompositeImg source, params string[] ids)
        {
            using var kit = new CompositeKit();
            kit.Import(source);
            foreach (var id in ids) kit.AddEffect(Target.OfLayer(0), id);
            return RenderAt(kit, kit.Doc, 128, K);
        }
        var spinSlide = Run(img, "spin", "slide");
        var slideSpin = Run(img, "slide", "spin");
        var diff = MotionTestKit.MeanAbsDiffRgb(spinSlide.Pixels, slideSpin.Pixels);
        Check.True(diff >= 10, $"上下を入れ替えると結果が変わる(RGB の絶対差の平均 {diff:0.##})");
        var spinOnly = Run(img, "spin");
        var thenSlide = Run(new CompositeImg(128, 128, spinOnly.Pixels), "slide");
        Check.True(MotionTestKit.MaxDiff(spinSlide.Pixels, thenSlide.Pixels) <= 3, $"spin→slide と 1 つずつの差 {MotionTestKit.MaxDiff(spinSlide.Pixels, thenSlide.Pixels)}");
        var slideOnly = Run(img, "slide");
        var thenSpin = Run(new CompositeImg(128, 128, slideOnly.Pixels), "spin");
        Check.True(MotionTestKit.MaxDiff(slideSpin.Pixels, thenSpin.Pixels) <= 3, $"slide→spin と 1 つずつの差 {MotionTestKit.MaxDiff(slideSpin.Pixels, thenSpin.Pixels)}");
    }

    [Test]
    public void FR29_全体のスポイトは表示しているフレームの合成から取る()
    {
        using var kit = new CompositeKit();
        kit.Import(LeftRight());
        kit.AddEffect(Target.OfLayer(0), "slide"); // 左へ。k=10 で 25 画素
        var at = new System.Windows.Point(40.5, 25.5);
        string? Pick(Target target, int k) => App.Stack.EyedropperPicker.PickRgbAsync(kit.Renderer, kit.Doc, kit.Session.SourceSize, target, at, k, N)
            .GetAwaiter().GetResult();
        Check.Equal("FF0000", Pick(Target.All, 0), "フレーム 0(x=40 は赤)");
        Check.Equal("0000FF", Pick(Target.All, K), "フレーム 10(元の x=65 の青)");
        // レイヤーが対象のときは、モーションを通さない(FR-29)
        Check.Equal("FF0000", Pick(Target.OfLayer(0), K), "レイヤーのスポイトはフレームに依らない");
    }

    // ---- AC-34(spin・slide の分。ほかの id は各担当が MotionTestKit で同じ形に足す) ----

    [Test(Slow = true)]
    public void AC34_1_2_3_spinとslideの継ぎ目()
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        foreach (var id in new[] { "spin", "slide" })
        foreach (var img in MotionTestKit.Ac34Images())
        {
            foreach (var inv in MotionTestKit.Ac34Variants(rs.Effects, id))
            {
                var what = $"{id} cycles={inv.Cycles} phase={inv.Phase} wave={inv.Wave}";
                var render = MotionTestKit.Render(rs, img, inv);
                MotionTestKit.Pass(MotionTestKit.CheckClosure(render), what);
                if (inv.Wave != WaveKind.Saw) MotionTestKit.Pass(MotionTestKit.CheckContinuity(render), what);
                if (inv.Cycles == 2) MotionTestKit.Pass(MotionTestKit.CheckPeriod(render), what);
            }
        }
    }

    [Test]
    public void AC34_5_周期が整数でない偽のエフェクトでは閉じ方の検査が失敗する()
    {
        using var kit = new CompositeKit();
        var rs = kit.Rs;
        foreach (var img in MotionTestKit.Ac34Images())
        {
            var real = MotionTestKit.Render(rs, img, MotionTestKit.Invocation(rs.Effects, "spin"));
            MotionTestKit.Pass(MotionTestKit.CheckClosure(real), "本物の spin(cycles=1)");
            var fake = MotionTestKit.NonIntegerPeriod(real);
            Check.True(MotionTestKit.CheckClosure(fake) is not null, "ψ=1.5×t の偽のエフェクトで閉じ方の検査が通ってしまった");
        }
    }
}
