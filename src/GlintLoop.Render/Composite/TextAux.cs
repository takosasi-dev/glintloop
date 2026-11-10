// テキストのエフェクト(aux=text)の補助のビットマップを、描画の専用スレッドの上で GPU に置く(§9.4 の text、§9.2 の aux)。
// ITextRasterizer(実装は Codec)が描いた sRGB の straight の RGBA 8 ビットを、取り込みと同じ経路で lin と不透明度のプリマルチプライ(RGBA16F)にする。
// キーは文字・フォント・大きさ・色・揃え・位置・回転・行の間隔・線とキャンバスの大きさだけで作り、amount や上流のエフェクトが変わっても作り直さない。
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GlintLoop.Core.Effects;
using GlintLoop.Render.Effects;

namespace GlintLoop.Render.Composite;

/// <summary>1 つのテキストの行の補助のビットマップの計画(描く受け口・値・キャンバスの大きさ・キャッシュのキー)。</summary>
public sealed record TextAuxPlan(ITextRasterizer Rasterizer, TextSpec Spec, int Width, int Height, string Key);

public static class TextAux
{
    /// <summary>依頼の値(既定値を補ったもの)から計画を作る。width・height はエフェクトへの入力の大きさ(キャンバスと同じ大きさ)。</summary>
    public static TextAuxPlan Plan(ITextRasterizer rasterizer, EffectInvocation inv, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rasterizer);
        ArgumentNullException.ThrowIfNull(inv);
        var spec = TextSpec.From(EffectConstants.ResolveValues(inv));
        return new TextAuxPlan(rasterizer, spec, width, height, KeyOf(spec, width, height));
    }

    /// <summary>補助のビットマップのキャッシュのキー(値と大きさのハッシュ)。</summary>
    public static string KeyOf(TextSpec spec, int width, int height)
    {
        var s = "textaux|" + spec.Describe() + "|" + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }

    /// <summary>
    /// 補助のビットマップを描いて GPU に置く(線形光・プリマルチプライの RGBA16F。持ち主は呼び手)。文字が空なら全画素 (0, 0, 0, 0)。
    /// 描画の専用スレッドの上で呼ぶ(INV-5)。
    /// </summary>
    public static GpuTexture Build(RenderContext ctx, TextAuxPlan plan)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(plan);
        var rgba = plan.Rasterizer.Rasterize(plan.Spec, plan.Width, plan.Height);
        if (rgba is not null) return ctx.IngestSrgb8(rgba, plan.Width, plan.Height, plan.Width * 4);
        var empty = ctx.CreateWorking(plan.Width, plan.Height);
        try
        {
            ctx.Passes.Fill(empty, Vector4.Zero);
        }
        catch
        {
            empty.Dispose();
            throw;
        }
        return empty;
    }
}
