// 仕上げのグレイン(grain、§9.4 の「光」): k0=H(seed, salt)、cell=max(1, round(px(size)))、idx=floor(y÷cell)×16384+floor(x÷cell)、
// n=u01(H3(k0, 100+c, idx))−u01(H3(k0, 200+c, idx))(mono が真なら c=0 を 3 チャンネルに)、m=1−0.7×(2×E(C)−1)²、sRGB で y=x+strength÷100×0.35×n×m。
// α は変えない。時間に依存しない。定数バッファは bind の順に {strength, size, mono, shortSide($shortSide), seed($seed), salt($salt)}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 strength;
    float4 size;
    uint4 mono;
    float4 shortSide;
    uint4 seed;
    uint4 salt;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 c = Straight(p);
    float3 x = enc3(c);
    uint cell = uint(max(1.0, floor(Px(clamp(size.x, 0.5, 20.0), shortSide.x) + 0.5)));
    uint2 ij = uint2(pos.xy);
    uint idx = (ij.y / cell) * 16384u + ij.x / cell;
    uint k0 = H2(seed.x, salt.x);
    float3 n;
    n.r = U01(H3(k0, 100u, idx)) - U01(H3(k0, 200u, idx));
    if (mono.x != 0u)
    {
        n.g = n.r;
        n.b = n.r;
    }
    else
    {
        n.g = U01(H3(k0, 101u, idx)) - U01(H3(k0, 201u, idx));
        n.b = U01(H3(k0, 102u, idx)) - U01(H3(k0, 202u, idx));
    }
    float e = 2.0 * LumaE(c) - 1.0;
    float m = 1.0 - 0.7 * e * e;
    float3 y = x + clamp(strength.x, 0.0, 100.0) / 100.0 * 0.35 * n * m;
    return Premul(lin3(y), p.a);
}
