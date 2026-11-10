// 仕上げの波打ち(waves、§9.4 の「ひずみ」): len'=max(px(len), 2)、φ=phase(ラジアン)。h は x'=x+px(amp)×sin(2π×y÷len'+φ)、
// v は y'=y+px(amp)×sin(2π×x÷len'+φ)、both は両方を同時に。(x', y') を双線形補間で読む(端を延ばす)。時間では変わらない。
// 定数バッファは bind の順に {dir, amp, len, phase, shortSide($shortSide)}。範囲外は表の範囲に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 dir;
    float4 amp;
    float4 len;
    float4 phase;
    float4 shortSide;
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
    uint w, h;
    Input0.GetDimensions(w, h);
    int2 last = int2(int(w) - 1, int(h) - 1);
    float a = Px(clamp(amp.x, 0.0, 60.0), shortSide.x);
    float l = max(Px(clamp(len.x, 10.0, 500.0), shortSide.x), 2.0);
    float ph = radians(clamp(phase.x, 0.0, 360.0));
    float k = 2.0 * 3.14159265 / l;
    float2 q = pos.xy;
    if (dir.x != 1u) q.x = pos.x + a * sin(k * pos.y + ph);
    if (dir.x != 0u) q.y = pos.y + a * sin(k * pos.x + ph);
    return Bilinear(q, last);
}
