// 仕上げのひずみ(warp、§9.4 の「ひずみ」): R=px(radius)、rn=distance(x, c)÷R(c=center×(W, H))。rn が 1 以上なら変えない。
// bulge は k=0.8×strength÷100 で c+(x−c)×(1−k×(1−rn²)²)、twirl は θ=strength÷100×2π×(1−rn)² で c+turn(θ, x−c) を双線形補間で読む(端を延ばす)。
// 定数バッファは bind の順に {kind, center, radius, strength, shortSide($shortSide), texel($texel)}。範囲外は表の範囲に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 kind;
    float4 center;
    float4 radius;
    float4 strength;
    float4 shortSide;
    float4 texel;
};

float4 Bilinear(float2 u, int2 last)
{
    float2 t = u - 0.5;
    float2 f = floor(t);
    float2 a = t - f;
    int2 i0 = int2(f);
    float4 p00 = Input0.Load(int3(clamp(i0, int2(0, 0), last), 0));
    float4 p10 = Input0.Load(int3(clamp(i0 + int2(1, 0), int2(0, 0), last), 0));
    float4 p01 = Input0.Load(int3(clamp(i0 + int2(0, 1), int2(0, 0), last), 0));
    float4 p11 = Input0.Load(int3(clamp(i0 + int2(1, 1), int2(0, 0), last), 0));
    float4 top = p00 + (p10 - p00) * a.x;
    float4 bottom = p01 + (p11 - p01) * a.x;
    return top + (bottom - top) * a.y;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    int2 last = int2(int(texel.z) - 1, int(texel.w) - 1);
    float2 c = saturate(center.xy) * texel.zw;
    float r = Px(clamp(radius.x, 50.0, 1000.0), shortSide.x);
    float2 v = pos.xy - c;
    float rn = length(v) / r;
    if (!(rn < 1.0)) return LoadHere(Input0, pos);
    float s = clamp(strength.x, -100.0, 100.0) / 100.0;
    float2 q;
    if (kind.x == 1u)
    {
        float u = 1.0 - rn;
        q = c + Turn(s * 2.0 * 3.14159265 * u * u, v);
    }
    else
    {
        float u = 1.0 - rn * rn;
        q = c + v * (1.0 - 0.8 * s * u * u);
    }
    return Bilinear(q, last);
}
