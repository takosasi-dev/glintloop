// モーションの動くグレイン(grainmove、§9.5 の粒): 仕上げの grain(grain.ps.hlsl)と同じ計算で、雑音の用途の定数 100+c・200+c に 1000×j を足す
// (j=floor(ψ×rate) mod (C×rate))。粒の番号 idx と px(size) は枠が基準(§9.7 の規則 12 の (a)(b))。α は変えない。
// 定数バッファは bind の順に {strength, size, mono, rate, shortSide($shortSide), t, cycles, phase, seed, salt, frame}(strength は scale の実効値)。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 strength;
    float4 size;
    uint4 mono;
    float4 rate;
    float4 shortSide;
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
    float3 c = Straight(p);
    float3 x = enc3(c);
    uint cell = uint(max(1.0, floor(Px(clamp(size.x, 0.5, 20.0), shortSide.x) + 0.5)));
    // 枠の左上を原点にした画素の番号(枠の左と上の外は負になり、idx は 32 ビットで回る)
    int2 ij = int2(floor(ToFrame(pos.xy, frame)));
    int fc = int(cell);
    int2 g = int2(floor(float2(ij) / float(fc)));
    uint idx = uint(g.y) * 16384u + uint(g.x);
    uint k = uint(clamp(rate.x, 1.0, 24.0) + 0.5);
    uint j = LatticeStep(MotionPsi(t.x, cycles.x, phase.x), k, cycles.x);
    uint off = 1000u * j;
    uint k0 = H2(seed.x, salt.x);
    float3 n;
    n.r = U01(H3(k0, 100u + off, idx)) - U01(H3(k0, 200u + off, idx));
    if (mono.x != 0u)
    {
        n.g = n.r;
        n.b = n.r;
    }
    else
    {
        n.g = U01(H3(k0, 101u + off, idx)) - U01(H3(k0, 201u + off, idx));
        n.b = U01(H3(k0, 102u + off, idx)) - U01(H3(k0, 202u + off, idx));
    }
    float e = 2.0 * LumaE(c) - 1.0;
    float m = 1.0 - 0.7 * e * e;
    float3 y = x + clamp(strength.x, 0.0, 100.0) / 100.0 * 0.35 * n * m;
    return Premul(lin3(y), p.a);
}
