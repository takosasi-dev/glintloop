// モーションのゆらゆら(wobble、§9.5 の動き): 波形あり。θ=angle×W(ψ) 度(時計回りが正)で、pivot(枠に対する 0〜1、既定 0.5,0.9)を中心に回す。
// xs=M⁻¹(x) をキャッチマル・ロムで読み、作業面の外は透明(規則 12 の (c))。
// 定数バッファは bind の順に {angle, pivot, t($t), cycles($cycles), phase($phase), wave($wave), frame($frame)}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 angle;
    float4 pivot;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 wave;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    float theta = radians(angle.x * Wave(psi, wave.x));
    float2 c = FramePoint(saturate(pivot.xy), frame);
    float2 xs = MotionInverse(pos.xy, c, float2(0.0, 0.0), theta, 0.0, 1.0, 1.0);
    return SampleCatRomTransparent(Input0, xs);
}
