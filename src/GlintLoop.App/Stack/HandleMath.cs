// キャンバス上のエフェクトのハンドル(FR-21)の計算: 対象の画像の位置(合成の座標)、ハンドルの置き場(キャンバスの DIP)、当たり判定、ドラッグ中の値の更新。
// どれも座標を引数に取る純粋な関数で、画面の部品(EffectHandleOverlay)とテストが共有する。切り抜きは入力(回転のあと)の画素、余白は入力の短辺の‰で動かす。
// 合成の画素 → 対象の画像の画素は、レイヤーの配置の倍率 K=scale÷100 で割る(土台と全体は K=1)。
using System.Windows;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Stack;

/// <summary>ハンドルの役割。Left〜BottomLeft は矩形の 4 辺と 4 隅。</summary>
public enum HandleRole { Left, Top, Right, Bottom, TopLeft, TopRight, BottomRight, BottomLeft, Point, Angle, Size }

/// <summary>置いたハンドル。Canvas は中心(キャンバスの DIP)、Axis は角度とサイズのハンドルの軸(キャンバスの DIP)。</summary>
public sealed record PlacedHandle(string Key, HandleRole Role, Point Canvas, Point Axis);

/// <summary>
/// 対象の画像の、合成の中の位置。Frame はネイティブ寸法の画像の枠(合成の座標)、K は合成の画素 ÷ 画像の画素。
/// Rotated は切り抜きの入力(回転のあと)、Cropped は余白の入力(切り抜きのあと)、PadOffset は余白で足した左と上の量(画像の画素)。
/// </summary>
public sealed record TargetGeometry(Rect Frame, double K, SizeD Rotated, RectValue CropApplied, SizeD Cropped, Vector PadOffset, SizeD Native)
{
    /// <summary>切り抜きの入力の左上(合成の座標)。</summary>
    public Point RotatedOrigin => new(
        Frame.X + (PadOffset.X - CropApplied.L * Rotated.W / 1000) * K,
        Frame.Y + (PadOffset.Y - CropApplied.T * Rotated.H / 1000) * K);

    /// <summary>切り抜きのあとの画像(余白の入力)の枠(合成の座標)。</summary>
    public Rect CroppedRect => new(Frame.X + PadOffset.X * K, Frame.Y + PadOffset.Y * K, Cropped.W * K, Cropped.H * K);
}

public static class HandleMath
{
    /// <summary>切り抜きの l+r・t+b の上限(‰辺)。</summary>
    public const double CropSumLimit = ParamRules.CropSumLimit;

    /// <summary>
    /// 対象の画像の位置。全体は合成の全体(K=1)、レイヤーは配置の枠(中心が (Wc/2+x, Hc/2+y)、大きさがネイティブ寸法×K。土台は配置 (0, 0, 100))。
    /// </summary>
    public static TargetGeometry Of(SessionDocument doc, Target target, Func<string, SizeD> sourceSize)
    {
        var wc = CompositeGeometry.CompositeNative(doc, sourceSize);
        if (target.IsAll)
        {
            var all = new SizeD(wc.W, wc.H);
            return new TargetGeometry(new Rect(0, 0, wc.W, wc.H), 1, all, new RectValue(0, 0, 0, 0), all, default, all);
        }
        var i = target.LayerIndex;
        var layer = doc.Layers[i];
        var place = i == 0 ? Placement.Identity : layer.Place;
        var k = place.Scale / 100;
        var src = CompositeGeometry.SourceSize(layer, sourceSize);
        StackEntry? rotate = null, crop = null, pad = null;
        foreach (var e in layer.Stack)
        {
            if (!e.Enabled) continue;
            switch (e.Id)
            {
                case EffectSchema.Rotate: rotate ??= e; break;
                case EffectSchema.Crop: crop ??= e; break;
                case EffectSchema.Pad: pad ??= e; break;
            }
        }
        var rotated = src;
        if (rotate is not null)
        {
            var angle = rotate.Get("angle") is NumberValue a ? a.Value : 0;
            rotated = CompositeGeometry.RotateSize(src, angle, rotate.Get("fit") is StringValue f && f.Value == "inside");
        }
        var cropRect = crop?.Get("rect") as RectValue ?? new RectValue(0, 0, 0, 0);
        var cropped = CompositeGeometry.CropSize(rotated, cropRect);
        var padOffset = default(Vector);
        var native = cropped;
        if (pad?.Get("rect") is RectValue pr)
        {
            var s0 = Math.Min(cropped.W, cropped.H);
            padOffset = new Vector(pr.L * s0 / 1000, pr.T * s0 / 1000);
            native = CompositeGeometry.PadSize(cropped, pr);
        }
        var w = native.W * k;
        var h = native.H * k;
        var frame = new Rect(wc.W / 2 + place.X - w / 2, wc.H / 2 + place.Y - h / 2, w, h);
        return new TargetGeometry(frame, k, rotated, cropRect, cropped, padOffset, native);
    }

    /// <summary>
    /// スタックの行がキャンバスのハンドルを持つか(FR-21・FR-50・AC-29 の (3)): 矩形と点のパラメータ、handle が false でない角度のパラメータのうち、
    /// visibleWhen で見えているもの(行の値で判定する)が 1 つでもあるとき。エフェクトのハンドル・レイヤーの枠・スタックの行の判定はすべてこれを使う。
    /// </summary>
    public static bool HasHandles(EffectDef def, StackEntry entry) =>
        EffectSchema.AllParams(def).Any(p => (p.Type is ParamType.Rect or ParamType.Point || (p.Type == ParamType.Angle && p.Handle)) && EffectSchema.IsVisible(p, entry));

    /// <summary>切り抜きの矩形(単位 ‰辺)か。そうでない rect は余白(単位 ‰)として扱う。</summary>
    public static bool IsCropRect(ParamDef p) => p.Unit == "‰辺";

    /// <summary>テキストのサイズのハンドルを持つか(aux=text で、size・point・angle を持つ)。</summary>
    public static bool HasTextSize(EffectDef def) =>
        def.Aux == AuxKind.Text && def.FindParam("size") is { Type: ParamType.Float };

    /// <summary>
    /// 行のハンドルを置く。toCanvas は合成の座標 → キャンバスの DIP、scale はキャンバスの DIP ÷ 合成の画素、radius は角度のハンドルの軸からの距離(DIP)。
    /// visibleWhen で見えていないパラメータ(EffectSchema.IsVisible。HasHandles と同じ判定)のハンドルは置かない。
    /// </summary>
    public static IReadOnlyList<PlacedHandle> Layout(EffectDef def, StackEntry entry, TargetGeometry g, Func<Point, Point> toCanvas, double scale, double radius)
    {
        var list = new List<PlacedHandle>();
        var ps = EffectSchema.AllParams(def);
        var axisDoc = new Point(g.Frame.X + g.Frame.Width / 2, g.Frame.Y + g.Frame.Height / 2);
        var pointParam = ps.FirstOrDefault(p => p.Type == ParamType.Point);
        if (pointParam is not null && entry.Get(pointParam.Key) is PointValue pv) axisDoc = ImagePoint(g, pv);
        var axis = toCanvas(axisDoc);
        foreach (var p in ps)
        {
            if (!EffectSchema.IsVisible(p, entry)) continue;
            switch (p.Type)
            {
                case ParamType.Rect when entry.Get(p.Key) is RectValue rv:
                {
                    var r = IsCropRect(p) ? CropRectDoc(g, rv) : PadRectDoc(g, rv);
                    var tl = toCanvas(r.TopLeft);
                    var br = toCanvas(r.BottomRight);
                    var cx = (tl.X + br.X) / 2;
                    var cy = (tl.Y + br.Y) / 2;
                    list.Add(new PlacedHandle(p.Key, HandleRole.Left, new Point(tl.X, cy), axis));
                    list.Add(new PlacedHandle(p.Key, HandleRole.Top, new Point(cx, tl.Y), axis));
                    list.Add(new PlacedHandle(p.Key, HandleRole.Right, new Point(br.X, cy), axis));
                    list.Add(new PlacedHandle(p.Key, HandleRole.Bottom, new Point(cx, br.Y), axis));
                    list.Add(new PlacedHandle(p.Key, HandleRole.TopLeft, tl, axis));
                    list.Add(new PlacedHandle(p.Key, HandleRole.TopRight, new Point(br.X, tl.Y), axis));
                    list.Add(new PlacedHandle(p.Key, HandleRole.BottomRight, br, axis));
                    list.Add(new PlacedHandle(p.Key, HandleRole.BottomLeft, new Point(tl.X, br.Y), axis));
                    break;
                }
                case ParamType.Point when entry.Get(p.Key) is PointValue v:
                    list.Add(new PlacedHandle(p.Key, HandleRole.Point, toCanvas(ImagePoint(g, v)), axis));
                    break;
                case ParamType.Angle when p.Handle && entry.Get(p.Key) is NumberValue a:
                    list.Add(new PlacedHandle(p.Key, HandleRole.Angle, Polar(axis, a.Value, radius), axis));
                    break;
            }
        }
        if (HasTextSize(def) && entry.Get("size") is NumberValue size)
        {
            var rot = ps.FirstOrDefault(p => p.Type == ParamType.Angle) is { } rp && entry.Get(rp.Key) is NumberValue rv2 ? rv2.Value : 0;
            var half = TextHalfSize(g, size.Value) * scale;
            list.Add(new PlacedHandle("size", HandleRole.Size, Turn(axis, rot, half, half), axis));
        }
        return list;
    }

    /// <summary>テキストのサイズのハンドルの軸からの x・y の距離(合成の画素): px(size)÷2(画像の短辺の‰を画素にして K を掛ける)。</summary>
    public static double TextHalfSize(TargetGeometry g, double size) => size * Math.Min(g.Native.W, g.Native.H) / 1000 / 2 * g.K;

    /// <summary>画像の点(0〜1)→ 合成の座標(ネイティブ寸法の枠の中)。</summary>
    public static Point ImagePoint(TargetGeometry g, PointValue v) => new(g.Frame.X + v.X * g.Frame.Width, g.Frame.Y + v.Y * g.Frame.Height);

    /// <summary>切り抜きの矩形(rect の値)の、合成の座標の矩形。</summary>
    public static Rect CropRectDoc(TargetGeometry g, RectValue r)
    {
        var o = g.RotatedOrigin;
        var w = g.Rotated.W * g.K;
        var h = g.Rotated.H * g.K;
        var x0 = o.X + r.L / 1000 * w;
        var y0 = o.Y + r.T / 1000 * h;
        var x1 = o.X + (1 - r.R / 1000) * w;
        var y1 = o.Y + (1 - r.B / 1000) * h;
        return new Rect(new Point(x0, y0), new Point(Math.Max(x0, x1), Math.Max(y0, y1)));
    }

    /// <summary>余白の矩形(rect の値)の、合成の座標の外側の矩形。</summary>
    public static Rect PadRectDoc(TargetGeometry g, RectValue r)
    {
        var c = g.CroppedRect;
        var s0 = Math.Min(g.Cropped.W, g.Cropped.H) * g.K;
        return new Rect(new Point(c.X - r.L * s0 / 1000, c.Y - r.T * s0 / 1000), new Point(c.Right + r.R * s0 / 1000, c.Bottom + r.B * s0 / 1000));
    }

    /// <summary>軸から角度 deg(y が下向きなので正は時計回り)の向きに距離 radius の点。</summary>
    public static Point Polar(Point axis, double deg, double radius)
    {
        var th = deg * Math.PI / 180;
        return new Point(axis.X + radius * Math.Cos(th), axis.Y + radius * Math.Sin(th));
    }

    /// <summary>(dx, dy) を角度 deg だけ回して軸に足す(§9.4 の turn)。</summary>
    public static Point Turn(Point axis, double deg, double dx, double dy)
    {
        var th = deg * Math.PI / 180;
        return new Point(axis.X + dx * Math.Cos(th) - dy * Math.Sin(th), axis.Y + dx * Math.Sin(th) + dy * Math.Cos(th));
    }

    /// <summary>
    /// 当たり判定: 中心から縦横 ±half(DIP)以内のハンドル(重なるときは後に置いたもの)。half は当たり判定の大きさ(16 DIP 以上)の半分。無ければ null。
    /// </summary>
    public static PlacedHandle? HitTest(IReadOnlyList<PlacedHandle> handles, Point p, double half)
    {
        for (var i = handles.Count - 1; i >= 0; i--)
        {
            var h = handles[i];
            if (Math.Abs(p.X - h.Canvas.X) <= half && Math.Abs(p.Y - h.Canvas.Y) <= half) return h;
        }
        return null;
    }

    /// <summary>crop の aspect の値 → 幅÷高さ(free と不明な値は null。orig は切り抜きの入力の比)。</summary>
    public static double? AspectRatio(string? value, SizeD input) => value switch
    {
        "orig" => input.H > 0 ? input.W / input.H : null,
        "1_1" => 1.0,
        "4_3" => 4.0 / 3,
        "3_2" => 3.0 / 2,
        "16_9" => 16.0 / 9,
        "9_16" => 9.0 / 16,
        "3_4" => 3.0 / 4,
        "2_3" => 2.0 / 3,
        _ => null,
    };

    private static bool MovesLeft(HandleRole r) => r is HandleRole.Left or HandleRole.TopLeft or HandleRole.BottomLeft;
    private static bool MovesRight(HandleRole r) => r is HandleRole.Right or HandleRole.TopRight or HandleRole.BottomRight;
    private static bool MovesTop(HandleRole r) => r is HandleRole.Top or HandleRole.TopLeft or HandleRole.TopRight;
    private static bool MovesBottom(HandleRole r) => r is HandleRole.Bottom or HandleRole.BottomLeft or HandleRole.BottomRight;

    /// <summary>
    /// 矩形のハンドルのドラッグ。start はドラッグを始めたときの値、delta はポインターの移動量(対象の画像の画素)、input は rect の入力の寸法。
    /// 切り抜き(crop が true)は辺を内側へ動かす量の ‰辺、余白は入力の短辺の ‰ で外側へ足す量。値は [min, max] に入れ、切り抜きの l+r・t+b は 980 で
    /// 動かした辺を止める。aspect(幅÷高さ)があるときは切り抜きの比を保つ(隅は動かした辺で合わせ、辺は反対の向きの 2 辺を中心を保って合わせる)。
    /// </summary>
    public static RectValue DragRect(RectValue start, HandleRole role, Vector delta, SizeD input, bool crop, double min, double max, double? aspect)
    {
        double l = start.L, t = start.T, r = start.R, b = start.B;
        bool ml = MovesLeft(role), mr = MovesRight(role), mt = MovesTop(role), mb = MovesBottom(role);
        if (crop)
        {
            var fx = 1000 / input.W;
            var fy = 1000 / input.H;
            if (ml) l += delta.X * fx;
            if (mr) r -= delta.X * fx;
            if (mt) t += delta.Y * fy;
            if (mb) b -= delta.Y * fy;
        }
        else
        {
            var f = 1000 / Math.Min(input.W, input.H);
            if (ml) l -= delta.X * f;
            if (mr) r += delta.X * f;
            if (mt) t -= delta.Y * f;
            if (mb) b += delta.Y * f;
        }
        var v = new Edges(l, t, r, b);
        Clamp(ref v, ml, mt, crop, min, max);
        if (crop && aspect is { } a && a > 0) ApplyAspect(ref v, role, input, a, min, max);
        return new RectValue(v.L, v.T, v.R, v.B);
    }

    private struct Edges(double l, double t, double r, double b)
    {
        public double L = l, T = t, R = r, B = b;
    }

    private static void Clamp(ref Edges v, bool movedLeft, bool movedTop, bool crop, double min, double max)
    {
        v.L = Math.Clamp(v.L, min, max);
        v.T = Math.Clamp(v.T, min, max);
        v.R = Math.Clamp(v.R, min, max);
        v.B = Math.Clamp(v.B, min, max);
        if (!crop) return;
        if (v.L + v.R > CropSumLimit)
        {
            if (movedLeft) v.L = CropSumLimit - v.R;
            else v.R = CropSumLimit - v.L;
        }
        if (v.T + v.B > CropSumLimit)
        {
            if (movedTop) v.T = CropSumLimit - v.B;
            else v.B = CropSumLimit - v.T;
        }
    }

    private static double Width(in Edges v, SizeD input) => input.W * (1 - (v.L + v.R) / 1000);
    private static double Height(in Edges v, SizeD input) => input.H * (1 - (v.T + v.B) / 1000);

    private static void ApplyAspect(ref Edges v, HandleRole role, SizeD input, double a, double min, double max)
    {
        bool ml = MovesLeft(role), mr = MovesRight(role), mt = MovesTop(role), mb = MovesBottom(role);
        var corner = (ml || mr) && (mt || mb);
        if (corner)
        {
            var w = Width(v, input);
            var h = Height(v, input);
            var byHeight = w / a <= h;
            if (byHeight) SetHeight(ref v, w / a, input, mt, min, max);
            else SetWidth(ref v, h * a, input, ml, min, max);
            // 範囲で止められて比が崩れたときは、止められた方に、もう一方を合わせ直す
            w = Width(v, input);
            h = Height(v, input);
            if (Math.Abs(w / h - a) > 1e-9)
            {
                if (byHeight) SetWidth(ref v, h * a, input, ml, min, max);
                else SetHeight(ref v, w / a, input, mt, min, max);
            }
            return;
        }
        if (ml || mr)
        {
            var h = Width(v, input) / a;
            if (h > input.H * (1 - 2 * min / 1000))
            {
                h = input.H * (1 - 2 * min / 1000);
                SetWidth(ref v, h * a, input, ml, min, max);
            }
            CenterHeight(ref v, h, input, min, max);
        }
        else if (mt || mb)
        {
            var w = Height(v, input) * a;
            if (w > input.W * (1 - 2 * min / 1000))
            {
                w = input.W * (1 - 2 * min / 1000);
                SetHeight(ref v, w / a, input, mt, min, max);
            }
            CenterWidth(ref v, w, input, min, max);
        }
    }

    /// <summary>動かした縦の辺(top か bottom)で高さを h にする。</summary>
    private static void SetHeight(ref Edges v, double h, SizeD input, bool top, double min, double max)
    {
        var total = (1 - h / input.H) * 1000;
        if (top) v.T = Math.Clamp(total - v.B, min, Math.Min(max, CropSumLimit - v.B));
        else v.B = Math.Clamp(total - v.T, min, Math.Min(max, CropSumLimit - v.T));
    }

    /// <summary>動かした横の辺(left か right)で幅を w にする。</summary>
    private static void SetWidth(ref Edges v, double w, SizeD input, bool left, double min, double max)
    {
        var total = (1 - w / input.W) * 1000;
        if (left) v.L = Math.Clamp(total - v.R, min, Math.Min(max, CropSumLimit - v.R));
        else v.R = Math.Clamp(total - v.L, min, Math.Min(max, CropSumLimit - v.L));
    }

    private static void CenterHeight(ref Edges v, double h, SizeD input, double min, double max)
    {
        var total = Math.Clamp((1 - h / input.H) * 1000, 2 * min, Math.Min(2 * max, CropSumLimit));
        var diff = v.T - v.B;
        var t = (total + diff) / 2;
        t = Math.Clamp(t, Math.Max(min, total - max), Math.Min(max, total - min));
        v.T = t;
        v.B = total - t;
    }

    private static void CenterWidth(ref Edges v, double w, SizeD input, double min, double max)
    {
        var total = Math.Clamp((1 - w / input.W) * 1000, 2 * min, Math.Min(2 * max, CropSumLimit));
        var diff = v.L - v.R;
        var l = (total + diff) / 2;
        l = Math.Clamp(l, Math.Max(min, total - max), Math.Min(max, total - min));
        v.L = l;
        v.R = total - l;
    }

    /// <summary>点のハンドルのドラッグ。delta は対象の画像の画素、native は画像の寸法。0〜1 に入れる。</summary>
    public static PointValue DragPoint(PointValue start, Vector delta, SizeD native) =>
        new(Math.Clamp(start.X + delta.X / native.W, 0, 1), Math.Clamp(start.Y + delta.Y / native.H, 0, 1));

    /// <summary>
    /// 角度のハンドル: 軸からポインターへの向き(度、y が下向き)を、範囲 [min, max] に入る 360 の倍数ずれの値にして返す(どれも入らなければ近い端)。
    /// 軸とポインターが同じ点なら start を返す。step があれば刻みに丸める。
    /// </summary>
    public static double DragAngle(double start, Point axis, Point pointer, double min, double max, double? step)
    {
        var dx = pointer.X - axis.X;
        var dy = pointer.Y - axis.Y;
        if (dx == 0 && dy == 0) return start;
        var deg = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (step is { } s && s > 0) deg = Math.Round(deg / s, MidpointRounding.AwayFromZero) * s;
        // 範囲に入る候補のうち始めの値に近いもの(同じ近さなら atan2 のままの値)。入る候補が無ければ、範囲の端のうち近いもの
        double best = double.NaN, bestKey = double.PositiveInfinity;
        foreach (var k in new[] { 0, -1, 1, -2, 2 })
        {
            var c = deg + 360 * k;
            var clamped = Math.Clamp(c, min, max);
            var key = clamped == c ? Math.Abs(c - start) : 1e6 + Math.Abs(clamped - c);
            if (key < bestKey)
            {
                bestKey = key;
                best = clamped;
            }
        }
        return best;
    }

    /// <summary>
    /// テキストのサイズのハンドル(仮の決め): 新しい size は、始めの size に「軸からポインターまでの距離 ÷ 軸からドラッグを始めた点までの距離」を掛け、[min, max] に入れる。
    /// </summary>
    public static double DragSize(double start, Point axis, Point startPointer, Point pointer, double min, double max)
    {
        var d0 = (startPointer - axis).Length;
        if (d0 <= 0) return start;
        return Math.Clamp(start * (pointer - axis).Length / d0, min, max);
    }
}
