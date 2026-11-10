// モーションのぷるぷる(jelly、§9.5 の動き): 波形あり。sy=1+amp÷100×W(ψ)、sx=1−0.8×amp÷100×W(ψ)、x 方向のせん断 x'=x+sh×(pivot.y−y)
// (sh=wobble÷100×0.15×W(ψ+0.25))。pivot は枠の下端中央 (0.5, 1.0) で固定。xs=M⁻¹(x) をキャッチマル・ロムで読み、外は透明。
// 定数バッファは bind の順に {amp, wobble, t($t), cycles($cycles), phase($phase), wave($wave), frame($frame)}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 amp;
    float4 wobble;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 wave;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    float w = Wave(psi, wave.x);
    float a = amp.x / 100.0;
    float sy = 1.0 + a * w;
    float sx = 1.0 - 0.8 * a * w;
    float sh = wobble.x / 100.0 * 0.15 * Wave(psi + 0.25, wave.x);
    float2 pivot = FramePoint(float2(0.5, 1.0), frame);
    float2 xs = MotionInverse(pos.xy, pivot, float2(0.0, 0.0), 0.0, sh, sx, sy);
    return SampleCatRomTransparent(Input0, xs);
}
