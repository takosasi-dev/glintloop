// モーションの回転(spin、§9.5 の動き): θ=±360°×ψ(cw が正、波形なし)、pivot(枠に対する 0〜1)を中心に回し、xs=M⁻¹(x) を
// 線形・プリマルチプライのキャッチマル・ロムで読む。作業面の外は透明(§9.7 の規則 12 の (c))。三角関数の前に ψ を fract する(周期 1 なので同じ角)。
// 定数バッファは bind の順に {dir, pivot, t($t), cycles($cycles), phase($phase), frame($frame)}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    uint4 dir;
    float4 pivot;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    float theta = TurnAngle(psi) * (dir.x == 1u ? -1.0 : 1.0);
    float2 c = FramePoint(saturate(pivot.xy), frame);
    float2 xs = MotionInverse(pos.xy, c, float2(0.0, 0.0), theta, 0.0, 1.0, 1.0);
    return SampleCatRomTransparent(Input0, xs);
}
