// 合成の大きさの計算(仕様書 §9.7 の規則 1・3・11、§9.4 の形ステージの寸法の式)。GPU を使わない純粋な関数だけを置く。
// ネイティブ寸法は実数のまま持ち、出力の大きさ・レイヤーの画像の大きさ・枠の左上・描画の上限での縮めを整数で求める。
// round は floor(x+0.5)(MathX.RoundHalfUp)。表示・サムネイル・書き出しが同じ関数を使う(INV-6)。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Session;

/// <summary>実数の幅と高さ。</summary>
public readonly record struct SizeD(double W, double H);

/// <summary>出力(合成)の大きさ。S は倍率 Lout÷max(Wc, Hc)。</summary>
public readonly record struct OutputDims(double S, int Wo, int Ho);

/// <summary>レイヤーの画像の大きさ(Wl×Hl)。</summary>
public readonly record struct LayerDims(int Wl, int Hl);

/// <summary>描画の大きさの上限で縮めた結果。N=0 のとき縮めない(Wr=Wl、Hr=Hl)。</summary>
public readonly record struct RenderShrink(int N, int Wr, int Hr);

/// <summary>枠(§9.7 の規則 11 の Q)。</summary>
public readonly record struct FrameRect(int Qx, int Qy, int W, int H)
{
    /// <summary>作業面(0, 0, wo, ho)と重なる部分。重ならないときは幅か高さが 0。</summary>
    public FrameRect ClipTo(int wo, int ho)
    {
        var x0 = Math.Max(0, Qx);
        var y0 = Math.Max(0, Qy);
        var x1 = Math.Min(wo, Qx + W);
        var y1 = Math.Min(ho, Qy + H);
        return new FrameRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public bool IsEmpty => W <= 0 || H <= 0;
}

/// <summary>1 枚のレイヤーの配置の計算結果。</summary>
public sealed record LayerLayout(int Index, SizeD Native, LayerDims Image, FrameRect Frame, RenderShrink Shrink);

/// <summary>文書の全体の配置の計算結果(規則 1 と 11)。</summary>
public sealed record CompositeLayout(SizeD Native, int Lout, OutputDims Output, IReadOnlyList<LayerLayout> Layers);

public static class CompositeGeometry
{
    /// <summary>画面のプロキシの長辺の上限(D-18)。</summary>
    public const int ProxyMaxLongSide = 2048;

    /// <summary>ギャラリーのサムネイルの長辺(規則 14)。</summary>
    public const int ThumbnailLongSide = 192;

    /// <summary>レイヤーの行のサムネイルの長辺(規則 14)。</summary>
    public const int RowThumbnailLongSide = 64;

    /// <summary>レイヤーの画像の画素数の上限(規則 11。4096×4096)。</summary>
    public const long MaxRenderPixels = 16_777_216;

    // ------------------------------------------------ 形ステージの寸法(§9.4。丸めない)

    /// <summary>回転の出力の寸法。inside が false のとき fit=expand。</summary>
    public static SizeD RotateSize(SizeD input, double angleDeg, bool inside)
    {
        var th = angleDeg * Math.PI / 180.0;
        var c = Math.Abs(Math.Cos(th));
        var s = Math.Abs(Math.Sin(th));
        var bw = input.W * c + input.H * s;
        var bh = input.W * s + input.H * c;
        if (!inside) return new SizeD(bw, bh);
        var k = Math.Min(input.W / bw, input.H / bh);
        return new SizeD(k * input.W, k * input.H);
    }

    /// <summary>切り抜きの出力の寸法。rect の l・r は幅、t・b は高さに対する‰辺。</summary>
    public static SizeD CropSize(SizeD input, RectValue rect) =>
        new(input.W * (1 - (rect.L + rect.R) / 1000), input.H * (1 - (rect.T + rect.B) / 1000));

    /// <summary>余白の出力の寸法。rect は入力の短辺に対する‰(px(v)=v×S0÷1000)。</summary>
    public static SizeD PadSize(SizeD input, RectValue rect)
    {
        var s0 = Math.Min(input.W, input.H);
        return new SizeD(input.W + (rect.L + rect.R) * s0 / 1000, input.H + (rect.T + rect.B) * s0 / 1000);
    }

    /// <summary>
    /// ネイティブ寸法(規則 1)。ソースの寸法に、スタックの目がオンの回転・切り抜き・余白を、この順に当てる(実数のまま)。
    /// </summary>
    public static SizeD NativeSize(SizeD source, IReadOnlyList<StackEntry> stack)
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
        var size = source;
        if (rotate is not null)
        {
            var angle = rotate.Get("angle") is NumberValue a ? a.Value : 0;
            var inside = rotate.Get("fit") is StringValue f && f.Value == "inside";
            size = RotateSize(size, angle, inside);
        }
        if (crop is not null && crop.Get("rect") is RectValue cr) size = CropSize(size, cr);
        if (pad is not null && pad.Get("rect") is RectValue pr) size = PadSize(size, pr);
        return size;
    }

    // ------------------------------------------------ 出力の大きさ(規則 1)

    /// <summary>s=Lout÷max(Wc, Hc)、Wo=round(Wc×s)、Ho=round(Hc×s)(1 以上)。</summary>
    public static OutputDims Output(SizeD composite, int lout)
    {
        var s = lout / Math.Max(composite.W, composite.H);
        var wo = Math.Max(1, MathX.RoundHalfUpToInt(composite.W * s));
        var ho = Math.Max(1, MathX.RoundHalfUpToInt(composite.H * s));
        return new OutputDims(s, wo, ho);
    }

    /// <summary>上限 cap の長辺。元(max(Wc, Hc) の切り上げ)より大きくしない。</summary>
    public static int CappedLongSide(SizeD composite, int cap) =>
        (int)Math.Min(cap, Math.Ceiling(Math.Max(composite.W, composite.H)));

    /// <summary>画面のプロキシの長辺 Lout=min(2048, ceil(max(Wc, Hc)))。</summary>
    public static int ProxyLongSide(SizeD composite) => CappedLongSide(composite, ProxyMaxLongSide);

    /// <summary>サムネイルの長辺 Lout=min(192, ceil(max(Wc, Hc)))。</summary>
    public static int ThumbnailLongSideOf(SizeD composite) => CappedLongSide(composite, ThumbnailLongSide);

    // ------------------------------------------------ レイヤーの画像と枠(規則 1 と 11)

    /// <summary>Wl=max(1, round(Wn×p÷100×s))、Hl も同様。</summary>
    public static LayerDims LayerImage(SizeD native, double scalePercent, double s) =>
        new(Math.Max(1, MathX.RoundHalfUpToInt(native.W * scalePercent / 100 * s)),
            Math.Max(1, MathX.RoundHalfUpToInt(native.H * scalePercent / 100 * s)));

    /// <summary>枠の左上 qx=round(Wo÷2+s×x−Wl÷2)、qy=round(Ho÷2+s×y−Hl÷2)。</summary>
    public static FrameRect Frame(OutputDims output, double x, double y, LayerDims image)
    {
        var qx = MathX.RoundHalfUpToInt(output.Wo / 2.0 + output.S * x - image.Wl / 2.0);
        var qy = MathX.RoundHalfUpToInt(output.Ho / 2.0 + output.S * y - image.Hl / 2.0);
        return new FrameRect(qx, qy, image.Wl, image.Hl);
    }

    /// <summary>
    /// 描画の大きさの上限(規則 11)。幅と高さが maxTextureDim 以下で、画素数が 16,777,216 以下なら n=0。
    /// 超えるときは、wr=ceil(Wl÷2^n)、hr=ceil(Hl÷2^n) が上限を満たす最小の n(1 以上)。
    /// </summary>
    public static RenderShrink Shrink(LayerDims image, int maxTextureDim)
    {
        if (Fits(image.Wl, image.Hl, maxTextureDim)) return new RenderShrink(0, image.Wl, image.Hl);
        for (var n = 1; n < 31; n++)
        {
            var d = 1L << n;
            var wr = (int)((image.Wl + d - 1) / d);
            var hr = (int)((image.Hl + d - 1) / d);
            if (Fits(wr, hr, maxTextureDim)) return new RenderShrink(n, wr, hr);
        }
        return new RenderShrink(31, 1, 1);
    }

    private static bool Fits(int w, int h, int maxDim) => w <= maxDim && h <= maxDim && (long)w * h <= MaxRenderPixels;

    // ------------------------------------------------ 文書の全体

    /// <summary>
    /// レイヤーのソースの寸法(image はソースの幅と高さ、empty は size)。image のときは sourceSize で引く。
    /// </summary>
    public static SizeD SourceSize(Layer layer, Func<string, SizeD> sourceSize) =>
        layer.Kind == LayerKind.Empty
            ? new SizeD((layer.Size ?? DocumentRules.DefaultEmptySize).W, (layer.Size ?? DocumentRules.DefaultEmptySize).H)
            : sourceSize(layer.Asset!);

    /// <summary>レイヤーのネイティブ寸法。</summary>
    public static SizeD LayerNative(Layer layer, Func<string, SizeD> sourceSize) =>
        NativeSize(SourceSize(layer, sourceSize), layer.Stack);

    /// <summary>合成のネイティブ寸法(Wc, Hc)。土台のネイティブ寸法(D-12。土台の表示がオフでも変わらない)。</summary>
    public static SizeD CompositeNative(SessionDocument doc, Func<string, SizeD> sourceSize) => LayerNative(doc.Base, sourceSize);

    /// <summary>
    /// 文書の全体の配置を、長辺 lout で計算する。土台の配置は (0, 0, 100) として扱う。
    /// maxTextureDim はアダプターのテクスチャの最大寸法(規則 2)。
    /// </summary>
    public static CompositeLayout Layout(SessionDocument doc, Func<string, SizeD> sourceSize, int lout, int maxTextureDim)
    {
        var native = CompositeNative(doc, sourceSize);
        var output = Output(native, lout);
        var layers = new List<LayerLayout>(doc.Layers.Count);
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var l = doc.Layers[i];
            var place = i == 0 ? Placement.Identity : l.Place;
            var n = i == 0 ? native : LayerNative(l, sourceSize);
            var image = LayerImage(n, place.Scale, output.S);
            var frame = Frame(output, place.X, place.Y, image);
            layers.Add(new LayerLayout(i, n, image, frame, Shrink(image, maxTextureDim)));
        }
        return new CompositeLayout(native, lout, output, layers);
    }
}
