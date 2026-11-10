// ルックの分割トーン(splittone、§9.4 の「色」): sRGB で、e=E(C) の明るさで影の色とハイライトの色の softlight を振り分けて足す。
// 式は p=0.5+0.004×balance、wH=smoothstep(p−0.5, p+0.5, e)、wS=1−wH、y=x+(xs−x)×wS×strength÷100+(xh−x)×wH×strength÷100。
// 定数バッファは bind の順に {"shadow", "highlight"(lin 済みの色。enc で sRGB に戻す), "balance", "strength", "amount"}。色の不透明度は使わない。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 shadow;
    float4 highlight;
    float4 balance;
    float4 strength;
    float4 amount;
};

// §9.4 の「ブレンド」の softlight(cb が下の色、cs が重ねる色。どちらも sRGB の 0〜1)。チャンネルごと。
float SoftLightD(float x)
{
    return x <= 0.25 ? ((16.0 * x - 12.0) * x + 4.0) * x : sqrt(x);
}

float SoftLight1(float cb, float cs)
{
    return cs <= 0.5 ? cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb) : cb + (2.0 * cs - 1.0) * (SoftLightD(cb) - cb);
}

float3 SoftLight3(float3 cb, float3 cs)
{
    return float3(SoftLight1(cb.r, cs.r), SoftLight1(cb.g, cs.g), SoftLight1(cb.b, cs.b));
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 c = Straight(p);
    float3 x = enc3(c);
    float e = LumaE(c);
    float mid = 0.5 + 0.004 * balance.x;
    float wH = SmoothStepSpec(mid - 0.5, mid + 0.5, e);
    float wS = 1.0 - wH;
    float k = strength.x / 100.0;
    float3 xs = SoftLight3(x, enc3(shadow.rgb));
    float3 xh = SoftLight3(x, enc3(highlight.rgb));
    float3 y = x + (xs - x) * (wS * k) + (xh - x) * (wH * k);
    return Premul(lin3(y), p.a);
}
