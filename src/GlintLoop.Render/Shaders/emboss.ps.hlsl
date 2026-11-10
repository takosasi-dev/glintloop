// 仕上げのエンボス(emboss、§9.4 の「光」): sRGB で、n=(cos angle, sin angle)、Δ=h(x+n×px(depth))−h(x−n×px(depth))(h は双線形補間で読み、端を延ばす)。
// gray は y=sat(0.5+2×Δ)(3 チャンネルとも)、color は y=sat(x+Δ)。α は変えない。入力 t0 は src、t1 は pass:v(ぼかした E)。
// 定数バッファは bind の順に {mode, angle, depth, shortSide($shortSide)}。範囲外は表の範囲に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 mode;
    float4 angle;
    float4 depth;
    float4 shortSide;
};

// t1(ぼかした E)の位置 u の双線形補間。
float HeightAt(float2 u, int2 last)
{
    float2 t = u - 0.5;
    float2 f = floor(t);
    float2 a = t - f;
    int2 i0 = int2(f);
    float h00 = Input1.Load(int3(clamp(i0, int2(0, 0), last), 0)).r;
    float h10 = Input1.Load(int3(clamp(i0 + int2(1, 0), int2(0, 0), last), 0)).r;
    float h01 = Input1.Load(int3(clamp(i0 + int2(0, 1), int2(0, 0), last), 0)).r;
    float h11 = Input1.Load(int3(clamp(i0 + int2(1, 1), int2(0, 0), last), 0)).r;
    float top = h00 + (h10 - h00) * a.x;
    float bottom = h01 + (h11 - h01) * a.x;
    return top + (bottom - top) * a.y;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input1.GetDimensions(w, h);
    int2 last = int2(int(w) - 1, int(h) - 1);
    float4 p = LoadHere(Input0, pos);
    float th = radians(clamp(angle.x, 0.0, 360.0));
    float2 o = float2(cos(th), sin(th)) * Px(clamp(depth.x, 0.0, 40.0), shortSide.x);
    float d = HeightAt(pos.xy + o, last) - HeightAt(pos.xy - o, last);
    float3 y;
    if (mode.x == 1u)
    {
        y = saturate(enc3(Straight(p)) + d);
    }
    else
    {
        float g = saturate(0.5 + 2.0 * d);
        y = float3(g, g, g);
    }
    return Premul(lin3(y), p.a);
}
