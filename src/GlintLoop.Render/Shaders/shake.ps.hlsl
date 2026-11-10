// モーションのブルブル(shake、§9.5 の動き): 波形なし。K=rate、k0=H(seed, salt)、格子の値 rx_j=2×u01(H3(k0,11,j))−1・ry_j(用途 12)・rr_j(用途 13)。
// smooth が偽なら j=floor(ψ×K) mod (C×K) の値、真なら隣の格子との補間。offset=(px(amp)×rx, px(amp)×ry)、θ=rot×rr 度。基準点は枠の中心、外は透明。
// 定数バッファは bind の順に {amp, rot, rate, smooth, t($t), cycles($cycles), phase($phase), seed($seed), salt($salt), frame($frame)}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 amp;
    float4 rot;
    float4 rate;
    uint4 smooth;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 seed;
    uint4 salt;
    float4 frame;
};

// 格子の値 2×u01(H3(k0, use, j))−1(use は 11・12・13)。
float Lattice(uint k0, uint use, uint j)
{
    return 2.0 * U01(H3(k0, use, j)) - 1.0;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    uint k = uint(max(1.0, floor(rate.x + 0.5)));
    uint k0 = H2(seed.x, salt.x);
    float3 r;
    if (smooth.x != 0u)
    {
        uint i, next;
        float f;
        LatticeSmooth(psi, k, cycles.x, i, next, f);
        r = lerp(float3(Lattice(k0, 11u, i), Lattice(k0, 12u, i), Lattice(k0, 13u, i)),
                 float3(Lattice(k0, 11u, next), Lattice(k0, 12u, next), Lattice(k0, 13u, next)), f);
    }
    else
    {
        uint j = LatticeStep(psi, k, cycles.x);
        r = float3(Lattice(k0, 11u, j), Lattice(k0, 12u, j), Lattice(k0, 13u, j));
    }
    float d = Px(amp.x, FrameShort(frame));
    float theta = radians(rot.x * r.z);
    float2 pivot = FramePoint(float2(0.5, 0.5), frame);
    float2 xs = MotionInverse(pos.xy, pivot, float2(d * r.x, d * r.y), theta, 0.0, 1.0, 1.0);
    return SampleCatRomTransparent(Input0, xs);
}
