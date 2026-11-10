// モーションのキラキラ(sparkle、§9.5 の粒): 星 i の中心・半径・位相を H3(k0, 41〜44, i) で決め、明るさ bi=max(0, sin(π×fract(ψ+φ)))^4、
// 腕と核の形 si=max(arm, core)×bi を足し、P'.rgb=P.rgb+α×lin(color.rgb)×color の不透明度×intensity÷100×sat(Σsi)、α は変えない。中心と W・H・S0 は枠が基準(規則 12)。
// 定数バッファは bind の順に {count, size, color, intensity, t, cycles, phase, seed, salt, frame}(intensity は scale の実効値、color は線形 RGB と不透明度)。
#include "common.hlsli"
#include "motion.hlsli"
#include "star.hlsli"

cbuffer Params : register(b0)
{
    float4 count;
    float4 size;
    float4 color;
    float4 intensity;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 seed;
    uint4 salt;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    uint n = uint(clamp(count.x, 1.0, 40.0) + 0.5);
    float2 wh = frame.zw;
    float rBase = Px(clamp(size.x, 10.0, 200.0), FrameShort(frame));
    float u = MotionU(MotionPsi(t.x, cycles.x, phase.x));
    float2 local = ToFrame(pos.xy, frame);
    uint k0 = H2(seed.x, salt.x);
    uint h41 = H2(k0, 41u);
    uint h42 = H2(k0, 42u);
    uint h43 = H2(k0, 43u);
    uint h44 = H2(k0, 44u);
    float sum = 0.0;
    [loop]
    for (uint i = 0u; i < n; i++)
    {
        // H3(k0, c, i)=H2(H2(k0, c), i)
        float2 c = wh * float2(U01(H2(h41, i)), U01(H2(h42, i)));
        float r = max(rBase * (0.4 + 0.6 * U01(H2(h43, i))), 0.0001);
        float phi = U01(H2(h44, i));
        float sb = max(0.0, sin(3.14159265359 * MotionU(u + phi)));
        float sb2 = sb * sb;
        sum += StarShape(local - c, r) * sb2 * sb2;
    }
    float3 add = p.a * color.rgb * saturate(color.a) * clamp(intensity.x, 0.0, 300.0) / 100.0 * saturate(sum);
    return float4(p.rgb + add, p.a);
}
