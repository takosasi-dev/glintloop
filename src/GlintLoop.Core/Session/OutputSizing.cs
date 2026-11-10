// 書き出しの出力の長辺 Lout(FR-73・§9.9 の 1)を、出力設定と合成のネイティブ寸法・土台のソースの寸法から決める純粋な関数。
// キャンバスの倍率(FR-15。出力の画素を基準にする)と、後の書き出しの担当が同じ式を使う。GIF・APNG の N×W×H の上限(§9.8)はここでは見ない。
// 既定(auto)は GIF が min(土台のソースの長辺, 480)、ほかの形式が min(土台のソースの長辺, 4096)。最後に round して 16 以上 4096 以下に直す。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Session;

public static class OutputSizing
{
    public const int MinLongSide = 16;
    public const int MaxLongSide = 4096;

    /// <summary>GIF の既定の長辺の上限(FR-73)。</summary>
    public const int GifDefaultLongSide = 480;

    /// <summary>
    /// 出力の長辺 Lout(画素)。composite は合成のネイティブ寸法 (Wc, Hc)、baseSource は土台のソースの寸法(回転・切り抜き・余白の前)。
    /// 長辺 → そのまま、幅 w → w×max÷Wc、高さ h → h×max÷Hc、倍率 r% → max×r÷100、auto → FR-73 の既定の式。
    /// </summary>
    public static int LongSide(OutputSettings output, SizeD composite, SizeD baseSource)
    {
        ArgumentNullException.ThrowIfNull(output);
        var max = Math.Max(composite.W, composite.H);
        var v = output.Size.Value ?? 0;
        double lout = output.Size.Mode switch
        {
            "long" => v,
            "width" => composite.W > 0 ? v * max / composite.W : 0,
            "height" => composite.H > 0 ? v * max / composite.H : 0,
            "scale" => max * v / 100,
            _ => Math.Min(Math.Max(baseSource.W, baseSource.H), output.Format == "gif" ? GifDefaultLongSide : MaxLongSide),
        };
        if (!double.IsFinite(lout)) lout = MinLongSide;
        return Math.Clamp(MathX.RoundHalfUpToInt(Math.Min(lout, int.MaxValue / 2.0)), MinLongSide, MaxLongSide);
    }

    /// <summary>文書の出力の長辺。sourceSize は image のレイヤーのソースの寸法を引く。</summary>
    public static int LongSide(SessionDocument doc, Func<string, SizeD> sourceSize)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var native = CompositeGeometry.CompositeNative(doc, sourceSize);
        return LongSide(doc.Output, native, CompositeGeometry.SourceSize(doc.Base, sourceSize));
    }

    /// <summary>合成のネイティブの 1 画素あたりの出力の画素(Lout÷max(Wc, Hc))。</summary>
    public static double Scale(int longSide, SizeD composite) => longSide / Math.Max(composite.W, composite.H);
}
