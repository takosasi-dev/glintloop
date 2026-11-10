// 仕上げのハーフトーン(halftone、§9.4 の「ひずみ」): s=max(2, px(size))、c0=(W÷2, H÷2)、x'=turn(−θ, x−c0)、q'=round(x'÷s)×s、q=c0+turn(θ, q')、
// A は q の升目の C の平均(t1 の箱形平均を q で双線形補間して近似)。mono は d=1−E(A)、cmy は d=1−enc(sat(A.i))(θ は angle+15°・+75°・+0°)。
// r=0.7071×s×sqrt(d)、cov=sat(0.5+r−dist)。α は変えない。入力 t0 は src、t1 は pass:v。bind の順は {mode, size, angle, ink, paper, shortSide, texel}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 mode;
    float4 size;
    float4 angle;
    float4 ink;
    float4 paper;
    float4 shortSide;
    float4 texel;
};

// t1(升目の平均の C)の位置 u の双線形補間(端を延ばす)。
float3 AverageAt(float2 u, int2 last)
{
    float2 t = u - 0.5;
    float2 f = floor(t);
    float2 a = t - f;
    int2 i0 = int2(f);
    float3 p00 = Input1.Load(int3(clamp(i0, int2(0, 0), last), 0)).rgb;
    float3 p10 = Input1.Load(int3(clamp(i0 + int2(1, 0), int2(0, 0), last), 0)).rgb;
    float3 p01 = Input1.Load(int3(clamp(i0 + int2(0, 1), int2(0, 0), last), 0)).rgb;
    float3 p11 = Input1.Load(int3(clamp(i0 + int2(1, 1), int2(0, 0), last), 0)).rgb;
    float3 top = p00 + (p10 - p00) * a.x;
    float3 bottom = p01 + (p11 - p01) * a.x;
    return top + (bottom - top) * a.y;
}

// 角度 theta(ラジアン)の格子で、画素の中心 x に最も近い格子点 q。
float2 GridPoint(float2 x, float2 c0, float s, float theta)
{
    float2 xr = Turn(-theta, x - c0);
    float2 qr = floor(xr / s + 0.5) * s;
    return c0 + Turn(theta, qr);
}

// 点の被覆 cov=sat(0.5+r−dist)、r=0.7071×s×sqrt(d)。
float Coverage(float d, float s, float dist)
{
    return saturate(0.5 + 0.7071 * s * sqrt(max(d, 0.0)) - dist);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    int2 last = int2(int(texel.z) - 1, int(texel.w) - 1);
    float s = max(2.0, Px(clamp(size.x, 5.0, 100.0), shortSide.x));
    float2 c0 = texel.zw * 0.5;
    float base = clamp(angle.x, 0.0, 90.0);
    float3 c;
    if (mode.x == 1u)
    {
        float3 deg = float3(base + 15.0, base + 75.0, base);
        float3 o = float3(0.0, 0.0, 0.0);
        [unroll]
        for (int i = 0; i < 3; i++)
        {
            float2 q = GridPoint(pos.xy, c0, s, radians(deg[i]));
            float a = AverageAt(q, last)[i];
            float cov = Coverage(1.0 - enc1(saturate(a)), s, length(pos.xy - q));
            o[i] = 1.0 - cov;
        }
        c = lin3(o);
    }
    else
    {
        float2 q = GridPoint(pos.xy, c0, s, radians(base));
        float cov = Coverage(1.0 - LumaE(AverageAt(q, last)), s, length(pos.xy - q));
        c = paper.rgb + (ink.rgb - paper.rgb) * cov;
    }
    return Premul(c, p.a);
}
