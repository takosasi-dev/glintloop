// AC-25 の (6)(FR-21): キャンバスのエフェクトのハンドルの当たり判定(16 DIP 四方以上)とドラッグの値の更新を、座標を引数に取る関数(HandleMath)で調べる。
// 切り抜きの辺・隅(アスペクト比の固定)、配置の倍率を通した換算(scale 50)、点・角度・テキストのサイズのハンドル、ドラッグの落とし先(StackDrag)も確かめる。
using System.Windows;
using GlintLoop.App.Stack;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Stack.StackTestKit;

namespace GlintLoop.Tests.Stack;

public sealed class HandleAc25Tests
{
    private static SizeD Sizes(string sha) => sha switch
    {
        "base" => new SizeD(2000, 1000),
        "a" => new SizeD(1000, 500),
        _ => new SizeD(1000, 1000),
    };

    private static IReadOnlyList<PlacedHandle> CropHandles(SessionDocument doc, Target t, StackEntry crop)
    {
        var g = HandleMath.Of(doc, t, Sizes);
        return HandleMath.Layout(Def("crop"), crop, g, p => p, 1, 64);
    }

    [Test]
    public void AC25_6_切り抜きの8つのハンドルの当たり判定は中心から8DIP以内()
    {
        var crop = Entry("crop", 1, ("rect", new RectValue(100, 50, 100, 50)));
        var doc = Doc(ImageLayer("a", Placement.Identity, crop));
        var hs = CropHandles(doc, Target.OfLayer(0), crop);
        Check.Equal(8, hs.Count, "切り抜きのハンドルの数");
        var roles = hs.Select(h => h.Role).ToHashSet();
        foreach (var r in new[] { HandleRole.Left, HandleRole.Top, HandleRole.Right, HandleRole.Bottom, HandleRole.TopLeft, HandleRole.TopRight, HandleRole.BottomRight, HandleRole.BottomLeft })
            Check.True(roles.Contains(r), $"{r} のハンドルが無い");
        const double half = 8; // 当たり判定 16 DIP 四方(Tokens.Layout.xaml の LayerFrameHandleHit)
        foreach (var h in hs)
        {
            foreach (var (dx, dy) in new[] { (-8.0, -8.0), (8.0, 8.0), (8.0, -8.0), (-8.0, 8.0), (0.0, 0.0), (8.0, 0.0), (0.0, -8.0) })
            {
                var hit = HandleMath.HitTest([h], new Point(h.Canvas.X + dx, h.Canvas.Y + dy), half);
                Check.True(hit == h, $"{h.Role} の中心から ({dx}, {dy}) で当たらない");
            }
            foreach (var (dx, dy) in new[] { (8.5, 0.0), (0.0, -8.5), (-8.5, 8.0) })
                Check.True(HandleMath.HitTest([h], new Point(h.Canvas.X + dx, h.Canvas.Y + dy), half) is null, $"{h.Role} の中心から ({dx}, {dy}) で当たった");
        }
    }

    [Test]
    public void AC25_6_切り抜きのハンドルは切り抜いた画像の辺と隅に出る()
    {
        // 土台 1000×500 に crop (100, 50, 100, 50): 切り抜きの結果(800×450)が合成になり、ハンドルは合成の辺に出る
        var crop = Entry("crop", 1, ("rect", new RectValue(100, 100, 100, 100)));
        var doc = Doc(ImageLayer("a", Placement.Identity, crop));
        var hs = CropHandles(doc, Target.OfLayer(0), crop);
        var tl = hs.Single(h => h.Role == HandleRole.TopLeft).Canvas;
        var br = hs.Single(h => h.Role == HandleRole.BottomRight).Canvas;
        Check.Near(0, tl.X, 1e-9, "左上の x");
        Check.Near(0, tl.Y, 1e-9, "左上の y");
        Check.Near(800, br.X, 1e-9, "右下の x");
        Check.Near(400, br.Y, 1e-9, "右下の y");
        var right = hs.Single(h => h.Role == HandleRole.Right).Canvas;
        Check.Near(800, right.X, 1e-9, "右辺の中点の x");
        Check.Near(200, right.Y, 1e-9, "右辺の中点の y");
    }

    [Test]
    public void AC25_6_幅1000の画像で右辺を画像の座標で100内側へ動かすとrが100増える()
    {
        var input = new SizeD(1000, 500);
        var r = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.Right, new Vector(-100, 0), input, true, 0, 900, null);
        Check.Near(100, r.R, 1e-9, "r");
        Check.Near(0, r.L, 1e-9, "l");
        var r2 = HandleMath.DragRect(new RectValue(0, 0, 50, 0), HandleRole.Right, new Vector(-100, 0), input, true, 0, 900, null);
        Check.Near(150, r2.R, 1e-9, "r(50 から)");
        var l = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.Left, new Vector(100, 0), input, true, 0, 900, null);
        Check.Near(100, l.L, 1e-9, "左辺を 100 内側へ");
        var t = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.Top, new Vector(0, 50), input, true, 0, 900, null);
        Check.Near(100, t.T, 1e-9, "上辺を 50 内側へ(高さ 500 の 100‰)");
    }

    [Test]
    public void AC25_6_scale50のレイヤーで右辺を合成の座標で50内側へ動かすとrが100増える()
    {
        var crop = Entry("crop", 2, ("rect", new RectValue(0, 0, 0, 0)));
        var doc = Doc(ImageLayer("base", Placement.Identity), ImageLayer("a", new Placement(0, 0, 50), crop));
        var g = HandleMath.Of(doc, Target.OfLayer(1), Sizes);
        Check.Near(0.5, g.K, 1e-12, "K");
        // 枠は中心 (1000, 500)、大きさ 500×250
        Check.Near(750, g.Frame.X, 1e-9, "枠の左");
        Check.Near(500, g.Frame.Width, 1e-9, "枠の幅");
        var hs = HandleMath.Layout(Def("crop"), crop, g, p => p, 1, 64);
        Check.Near(1250, hs.Single(h => h.Role == HandleRole.Right).Canvas.X, 1e-9, "右辺のハンドルの x(合成の座標)");
        var composite = new Vector(-50, 0);
        var r = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.Right, composite / g.K, g.Rotated, true, 0, 900, null);
        Check.Near(100, r.R, 1e-9, "r");
    }

    [Test]
    public void AC25_6_aspectを1_1に固定して隅を動かすと比が保たれ値は範囲に入る()
    {
        var input = new SizeD(1000, 500);
        var a = HandleMath.AspectRatio("1_1", input);
        Check.Equal(1.0, a ?? 0, "1_1 の比");
        var moves = new (HandleRole Role, Vector D)[]
        {
            (HandleRole.BottomRight, new Vector(-300, -50)),
            (HandleRole.BottomRight, new Vector(-10, -200)),
            (HandleRole.TopLeft, new Vector(250, 10)),
            (HandleRole.TopRight, new Vector(-600, 100)),
            (HandleRole.BottomLeft, new Vector(5000, -5000)),
            (HandleRole.TopLeft, new Vector(-5000, -5000)),
            (HandleRole.Right, new Vector(-400, 0)),
            (HandleRole.Bottom, new Vector(0, -100)),
        };
        foreach (var (role, d) in moves)
        {
            var r = HandleMath.DragRect(new RectValue(0, 0, 0, 0), role, d, input, true, 0, 900, a);
            var w = input.W * (1 - (r.L + r.R) / 1000);
            var h = input.H * (1 - (r.T + r.B) / 1000);
            Check.Near(1, w / h, 0.005, $"{role} {d} の幅÷高さ(l={r.L} t={r.T} r={r.R} b={r.B})");
            foreach (var v in new[] { r.L, r.T, r.R, r.B }) Check.InRange(0, 900, v, $"{role} {d} の辺の値");
            Check.True(r.L + r.R <= 980 + 1e-9 && r.T + r.B <= 980 + 1e-9, $"{role} {d} の l+r・t+b");
        }
        // 16:9 と元の比でも保たれる
        foreach (var aspect in new[] { "16_9", "orig", "3_4" })
        {
            var ar = HandleMath.AspectRatio(aspect, input)!.Value;
            var r = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.BottomRight, new Vector(-200, -30), input, true, 0, 900, ar);
            var w = input.W * (1 - (r.L + r.R) / 1000);
            var h = input.H * (1 - (r.T + r.B) / 1000);
            Check.Near(ar, w / h, ar * 0.005, $"{aspect} の比");
        }
        Check.True(HandleMath.AspectRatio("free", input) is null, "free は固定しない");
    }

    [Test]
    public void AC25_6_切り抜きの値は範囲の端と980で止まる()
    {
        var input = new SizeD(1000, 1000);
        var r = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.Right, new Vector(-5000, 0), input, true, 0, 900, null);
        Check.Near(900, r.R, 1e-9, "r は 900 で止まる");
        var r2 = HandleMath.DragRect(new RectValue(500, 0, 0, 0), HandleRole.Right, new Vector(-900, 0), input, true, 0, 900, null);
        Check.Near(480, r2.R, 1e-9, "l=500 のとき r は 980−500 で止まる");
        Check.Near(500, r2.L, 1e-9, "反対側は動かない");
        var r3 = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.Left, new Vector(-100, 0), input, true, 0, 900, null);
        Check.Near(0, r3.L, 1e-9, "外へは 0 で止まる");
    }

    [Test]
    public void AC25_6_余白のハンドルは外へ引くと短辺の千分率で増える()
    {
        // 1000×500 の入力: 短辺 500。右辺を外へ 50 画素で r=100
        var r = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.Right, new Vector(50, 0), new SizeD(1000, 500), false, 0, 3000, null);
        Check.Near(100, r.R, 1e-9, "r");
        var l = HandleMath.DragRect(new RectValue(0, 0, 0, 0), HandleRole.TopLeft, new Vector(-25, -50), new SizeD(1000, 500), false, 0, 3000, null);
        Check.Near(50, l.L, 1e-9, "l");
        Check.Near(100, l.T, 1e-9, "t");
        var pad = Entry("pad", 1, ("rect", new RectValue(0, 100, 0, 100)));
        var doc = Doc(ImageLayer("a", Placement.Identity, pad));
        var g = HandleMath.Of(doc, Target.OfLayer(0), Sizes);
        var hs = HandleMath.Layout(Def("pad"), pad, g, p => p, 1, 64);
        Check.Near(0, hs.Single(h => h.Role == HandleRole.Top).Canvas.Y, 1e-9, "余白の上辺のハンドルは外側の辺");
        Check.Near(600, hs.Single(h => h.Role == HandleRole.Bottom).Canvas.Y, 1e-9, "余白の下辺のハンドル(500+50+50)");
    }

    [Test]
    public void AC25_6_点と角度のハンドルの位置と当たり判定とドラッグ()
    {
        var zb = Entry("zoomblur", 1, ("center", new PointValue(0.25, 0.5)));
        var doc = Doc(ImageLayer("a", Placement.Identity, zb));
        var g = HandleMath.Of(doc, Target.OfLayer(0), Sizes);
        var hs = HandleMath.Layout(Def("zoomblur"), zb, g, p => p, 1, 64);
        var point = hs.Single(h => h.Role == HandleRole.Point);
        Check.Near(250, point.Canvas.X, 1e-9, "点の x");
        Check.Near(250, point.Canvas.Y, 1e-9, "点の y");
        Check.True(HandleMath.HitTest(hs, new Point(258, 242), 8) == point, "点のハンドルに ±8 で当たる");
        var moved = HandleMath.DragPoint(new PointValue(0.25, 0.5), new Vector(100, -50), g.Native);
        Check.Near(0.35, moved.X, 1e-12, "点の x の移動");
        Check.Near(0.4, moved.Y, 1e-12, "点の y の移動");
        var clamped = HandleMath.DragPoint(new PointValue(0.9, 0.1), new Vector(1000, -1000), g.Native);
        Check.Equal(new PointValue(1, 0), clamped, "点は 0〜1 に入る");

        // 角度: blur(方向)の angle。軸は画像の中心、ハンドルは軸から 64 DIP の向き
        var blur = Entry("blur", 2, ("kind", new StringValue("directional")), ("angle", new NumberValue(90)));
        var hb = HandleMath.Layout(Def("blur"), blur, g, p => p, 1, 64).Single(h => h.Role == HandleRole.Angle);
        Check.Near(500, hb.Axis.X, 1e-9, "角度の軸の x(画像の中心)");
        Check.Near(250 + 64, hb.Canvas.Y, 1e-9, "90 度のハンドルは軸の下");
        Check.Near(-90, HandleMath.DragAngle(90, hb.Axis, new Point(500, 100), -180, 180, 1), 1e-9, "上へ引くと −90 度");
        Check.Near(180, HandleMath.DragAngle(0, hb.Axis, new Point(400, 250), -180, 180, 1), 1e-9, "左へ引くと 180 度");
        // 範囲が 0〜360 の角度(emboss の光の向き)は −90 を 270 にする
        Check.Near(270, HandleMath.DragAngle(0, new Point(0, 0), new Point(0, -10), 0, 360, 1), 1e-9, "0〜360 の範囲");
        Check.Near(90, HandleMath.DragAngle(0, new Point(0, 0), new Point(0, 10), 0, 90, 1), 1e-9, "0〜90 の範囲");
        Check.Near(0, HandleMath.DragAngle(45, new Point(0, 0), new Point(10, -1), 0, 90, 1), 1e-9, "0〜90 の範囲の外は近い端");
        // handle が false の角度(hue の shift)はハンドルを出さない
        var hue = Entry("hue", 3);
        Check.True(!HandleMath.Layout(Def("hue"), hue, g, p => p, 1, 64).Any(), "hue の色相のずらしにハンドルが出た");
        // ハンドルを持つかは行の値で決まる(角度は visibleWhen で見えているときだけ。AC-29 の (3))
        Check.True(HandleMath.HasHandles(Def("blur"), blur), "方向のぼかしはハンドルを持つ");
        Check.True(!HandleMath.HasHandles(Def("blur"), Entry("blur", 4)), "ぼかし(ガウス)はハンドルを持たない");
        Check.True(!HandleMath.HasHandles(Def("exposure"), Entry("exposure", 5)), "exposure はハンドルを持たない");
        Check.True(!HandleMath.HasHandles(Def("hue"), hue), "hue(handle が false の角度)はハンドルを持たない");
    }

    [Test]
    public void AC25_6_visibleWhenで隠れた角度にはハンドルを置かずHasHandlesと置いたハンドルが一致する()
    {
        var doc = Doc(ImageLayer("a", Placement.Identity));
        var g = HandleMath.Of(doc, Target.OfLayer(0), Sizes);
        // ぼかし(ガウス)の角度は visibleWhen で隠れるのでハンドルを置かない。方向のぼかしでは角度のハンドルを 1 つ置く
        var gaussian = Entry("blur", 1, ("kind", new StringValue("gaussian")), ("angle", new NumberValue(30)));
        Check.Equal(0, HandleMath.Layout(Def("blur"), gaussian, g, p => p, 1, 64).Count, "ぼかし(ガウス)に置いたハンドルの数");
        var directional = Entry("blur", 2, ("kind", new StringValue("directional")), ("angle", new NumberValue(30)));
        var hs = HandleMath.Layout(Def("blur"), directional, g, p => p, 1, 64);
        Check.Equal(1, hs.Count, "方向のぼかしに置いたハンドルの数");
        Check.Equal(HandleRole.Angle, hs[0].Role, "方向のぼかしのハンドルの役割");
        Check.Equal("angle", hs[0].Key, "方向のぼかしのハンドルのキー");

        // 製品の全エフェクトで、enum と bool の各値を入れた行について、HasHandles と Layout(テキストのサイズを除く)の判定が一致する
        var mismatches = new List<string>();
        foreach (var def in Catalog.Effects)
        {
            var variants = new List<StackEntry> { Entry(def.Id) };
            foreach (var p in EffectSchema.AllParams(def))
            {
                if (p.Type == ParamType.Enum && p.Options is { } opts)
                    foreach (var o in opts) variants.Add(Entry(def.Id, 1, (p.Key, new StringValue(o.Value))));
                else if (p.Type == ParamType.Bool)
                    foreach (var b in new[] { false, true }) variants.Add(Entry(def.Id, 1, (p.Key, new BoolValue(b))));
            }
            foreach (var e in variants)
            {
                var placed = HandleMath.Layout(def, e, g, p => p, 1, 64).Any(h => h.Role != HandleRole.Size);
                if (placed != HandleMath.HasHandles(def, e))
                    mismatches.Add($"{def.Id}({string.Join(",", e.Params.Where(kv => kv.Value is StringValue or BoolValue).Select(kv => kv.Key + "=" + kv.Value))})");
            }
        }
        Check.True(mismatches.Count == 0, "HasHandles と置いたハンドルが食い違う行: " + string.Join(" / ", mismatches));
    }

    [Test]
    public void AC25_6_テキストの移動と回転とサイズのハンドル()
    {
        // テキストのエフェクト(§9.4 の text)の宣言を手で組む(EffectDef は担当 text が足す)
        var ps = new ParamDef[]
        {
            new("text", "テキスト", ParamType.Text, null, null, null, new StringValue(""), "", null, null, false, true, null, 200),
            new("size", "大きさ", ParamType.Float, 10, 500, 1, new NumberValue(100), "‰", null, null, false, true, null, null),
            new("pos", "位置", ParamType.Point, null, null, null, new PointValue(0.5, 0.5), "", null, null, false, true, null, null),
            new("rot", "回転", ParamType.Angle, -180, 180, 1, new NumberValue(0), "deg", null, null, false, true, null, null),
        };
        var def = new EffectDef(1, "text", "テキスト", EffectStage.Finish, "文字", [], AmountMode.Mix, [], false, false, WaveKind.Sine,
            SizeRule.Same, AuxKind.Text, ps, []);
        var e = new StackEntry("text", true, EffectOrigin.User, 1, 100, EffectSchema.DefaultParams(def));
        var doc = Doc(ImageLayer("a", Placement.Identity));
        var g = HandleMath.Of(doc, Target.OfLayer(0), Sizes);
        var hs = HandleMath.Layout(def, e, g, p => p, 1, 64);
        Check.SequenceEqual([HandleRole.Point, HandleRole.Angle, HandleRole.Size], hs.Select(h => h.Role).ToList(), "テキストのハンドル");
        var size = hs.Single(h => h.Role == HandleRole.Size);
        // px(size)=100×500÷1000=50、その半分 25 を軸から x と y に
        Check.Near(525, size.Canvas.X, 1e-9, "サイズのハンドルの x");
        Check.Near(275, size.Canvas.Y, 1e-9, "サイズのハンドルの y");
        // 軸からの距離を 2 倍にすると size が 2 倍
        Check.Near(200, HandleMath.DragSize(100, size.Axis, size.Canvas, new Point(550, 300), 10, 500), 1e-9, "サイズ 2 倍");
        Check.Near(500, HandleMath.DragSize(100, size.Axis, size.Canvas, new Point(1500, 1300), 10, 500), 1e-9, "上限 500");
        Check.Near(10, HandleMath.DragSize(100, size.Axis, size.Canvas, new Point(500, 250), 10, 500), 1e-9, "下限 10");
        foreach (var h in hs) Check.True(HandleMath.HitTest([h], new Point(h.Canvas.X - 8, h.Canvas.Y + 8), 8) == h, $"{h.Role} に ±8 で当たる");
    }

    [Test]
    public void FR25_ドラッグの落とし先はステージの中で固定の行より上に来ない()
    {
        double[] mids = [10, 30, 50, 70];
        Check.Equal(0, StackDrag.DropIndex(mids, 2, 5, 0), "先頭へ");
        Check.Equal(3, StackDrag.DropIndex(mids, 0, 100, 0), "末尾へ");
        Check.Equal(1, StackDrag.DropIndex(mids, 3, 25, 0), "2 番目へ");
        Check.Equal(2, StackDrag.DropIndex(mids, 3, 5, 2), "固定の 2 行より上へは行かない");
        Check.Equal(2, StackDrag.DropIndex(mids, 2, 55, 0), "動かない");
    }
}
