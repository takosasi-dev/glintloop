// モーションのふわふわ(hover、§9.5 の動き): 波形あり。offset=(0, −px(amp)×W(ψ))、θ=tilt×W(ψ+0.25) 度(時計回りが正)。基準点は枠の中心。
// xs=M⁻¹(x) をキャッチマル・ロムで読み、作業面の外は透明(規則 12 の (c))。px の S0 は枠の短辺(規則 12 の (a))。
// 定数バッファは bind の順に {amp, tilt, t($t), cycles($cycles), phase($phase), wave($wave), frame($frame)}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 amp;
    float4 tilt;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 wave;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    float lift = Px(amp.x, FrameShort(frame)) * Wave(psi, wave.x);
    float theta = radians(tilt.x * Wave(psi + 0.25, wave.x));
    float2 pivot = FramePoint(float2(0.5, 0.5), frame);
    float2 xs = MotionInverse(pos.xy, pivot, float2(0.0, -lift), theta, 0.0, 1.0, 1.0);
    return SampleCatRomTransparent(Input0, xs);
}
