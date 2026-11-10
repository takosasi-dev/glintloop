// モーションの波紋(ripple、§9.5 の乱れ): d=distance(x, c)、dir=(x−c)÷max(d, 0.001)、a(d)=px(amp)×exp(−fall÷100×2×d÷(S0÷2))、
// xs=x+dir×a(d)×sin(2π×(d÷px(len)−ψ))(外へ進む波)を双線形で読む。作業面の外は最も近い画素(clamp。§9.7 の規則 12 の (c))。c と S0 は枠が基準。
// 定数バッファは bind の順に {amp, len, center, fall, t, cycles, phase, frame}(amp は scale の実効値)。sin の前に ψ を u=fract(ψ) にする(周期 1 なので同じ値)。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 amp;
    float4 len;
    float4 center;
    float4 fall;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float s0 = max(FrameShort(frame), 1.0);
    float a0 = Px(clamp(amp.x, 0.0, 40.0), s0);
    float wl = max(Px(clamp(len.x, 30.0, 500.0), s0), 0.0001);
    float fl = clamp(fall.x, 0.0, 100.0) / 100.0;
    float u = MotionU(MotionPsi(t.x, cycles.x, phase.x));
    float2 c = FramePoint(saturate(center.xy), frame);
    float2 v = pos.xy - c;
    float d = length(v);
    float2 dir = v / max(d, 0.001);
    float a = a0 * exp(-fl * 2.0 * d / (s0 * 0.5));
    float s, co;
    SinCos2Pi(d / wl - u, s, co);
    return SampleBilinearClamp(Input0, pos.xy + dir * a * s);
}
