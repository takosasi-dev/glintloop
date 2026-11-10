// モーションのぷくぷく(pulse、§9.5 の動き): 波形なし(shape で選ぶ)。shape が波形のとき s=1+amp÷100×W(ψ)、heartbeat は b(x)=exp(−(x÷0.05)²)、
// g(u)=b(u−0.2)+0.6×b(u−0.4)、s=1+amp÷100×g(u)。sx=sy=s で pivot(枠に対する 0〜1)を中心に拡大し、キャッチマル・ロムで読む(外は透明)。
// 定数バッファは bind の順に {shape, amp, pivot, t($t), cycles($cycles), phase($phase), frame($frame)}。shape は sine 0・tri 1・ease 2・saw 3・heartbeat 4。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    uint4 shape;
    float4 amp;
    float4 pivot;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
};

// b(x)=exp(−(x÷0.05)²)。
float Beat(float x)
{
    float r = x / 0.05;
    return exp(-r * r);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    float g;
    if (shape.x == 4u)
    {
        float u = MotionU(psi);
        g = Beat(u - 0.2) + 0.6 * Beat(u - 0.4);
    }
    else
    {
        g = Wave(psi, shape.x);
    }
    float s = 1.0 + amp.x / 100.0 * g;
    float2 c = FramePoint(saturate(pivot.xy), frame);
    float2 xs = MotionInverse(pos.xy, c, float2(0.0, 0.0), 0.0, 0.0, s, s);
    return SampleCatRomTransparent(Input0, xs);
}
