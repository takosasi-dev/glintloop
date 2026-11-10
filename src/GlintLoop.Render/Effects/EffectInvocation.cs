// エフェクトの 1 行を描くための依頼(宣言・解決済みのパラメータの値・実効 amount・salt・時間の共通パラメータ)と、フレームの情報。
// 値の範囲の直し(§9.7 の規則 4 の 5)は呼び手が済ませてから渡す。無いキーは宣言の既定値を使う。
// Aux は GPU のテクスチャなので、この依頼は描画の専用スレッドの上でだけ使う(INV-5)。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.Render.Effects;

public sealed record EffectInvocation(EffectDef Def)
{
    private static readonly IReadOnlyDictionary<string, ParamValue> NoValues = new Dictionary<string, ParamValue>();

    /// <summary>パラメータの値(キー → 値)。無いキーは既定値。scale モードの実効値への直しは描画が行うので、ここには元の値を入れる。</summary>
    public IReadOnlyDictionary<string, ParamValue> Values { get; init; } = NoValues;

    /// <summary>実効 amount(0〜1。EffectAmount.Effective の結果)。amountMode が none のときは使わない。</summary>
    public double Amount { get; init; } = 1.0;

    /// <summary>行ごとの salt(§9.7 の規則 6)。</summary>
    public uint Salt { get; init; }

    /// <summary>timeDependent の共通パラメータ cycles(1〜8)。</summary>
    public int Cycles { get; init; } = 1;

    /// <summary>timeDependent の共通パラメータ phase(0〜100%)。</summary>
    public double Phase { get; init; }

    /// <summary>timeDependent の共通パラメータ wave。null なら宣言の waveDefault。</summary>
    public WaveKind? Wave { get; init; }

    /// <summary>
    /// モーションステージの枠(§9.7 の規則 12 の Q。全体のモーションでは作業面の全体)。null はモーションステージの外(または枠の無い単独の実行)。
    /// 入れると、組み込み変数 $frame がこの矩形、$width・$height・$shortSide が枠の Wl・Hl・min(Wl, Hl) になる(Q-A。$texel は入力のまま)。
    /// </summary>
    public FrameRect? MotionFrame { get; init; }

    /// <summary>aux が text のエフェクトに渡す補助のビットマップ(線形光・プリマルチプライの RGBA16F)。</summary>
    public GpuTexture? Aux { get; init; }
}

/// <summary>フレームごとに変わる入力。Seed はセッション共通の seed、K と N はフレーム番号とフレーム数(t=K÷N)。</summary>
public readonly record struct FrameContext(uint Seed, int K, int N)
{
    /// <summary>静止画(k=0, N=1, seed=0)。</summary>
    public static FrameContext Still => new(0, 0, 1);

    /// <summary>位相 t=K÷N(N が 0 以下なら 0)。</summary>
    public double T => N > 0 ? (double)K / N : 0.0;
}
