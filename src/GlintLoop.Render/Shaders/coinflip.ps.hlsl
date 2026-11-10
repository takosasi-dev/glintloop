// モーションのコイン(coinflip、§9.5 の動き): 波形なし。φ=360°×ψ、c=cos φ(abs(c) が 0.003 未満なら符号を保って 0.003)。axis=y は x 方向、
// axis=x は y 方向の拡大率を c にする(中心は枠の中心)。c が負なら裏返った像。xs=M⁻¹(x) をキャッチマル・ロムで読み、外は透明。
// 定数バッファは bind の順に {axis, t($t), cycles($cycles), phase($phase), frame($frame)}。axis は y 0・x 1。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    uint4 axis;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float s, c;
    SinCos2Pi(MotionPsi(t.x, cycles.x, phase.x), s, c);
    // c がちょうど 0 のときは正の側(+0.003)にする
    if (abs(c) < 0.003) c = c < 0.0 ? -0.003 : 0.003;
    float sx = axis.x == 0u ? c : 1.0;
    float sy = axis.x == 0u ? 1.0 : c;
    float2 center = FramePoint(float2(0.5, 0.5), frame);
    float2 xs = MotionInverse(pos.xy, center, float2(0.0, 0.0), 0.0, 0.0, sx, sy);
    return SampleCatRomTransparent(Input0, xs);
}
