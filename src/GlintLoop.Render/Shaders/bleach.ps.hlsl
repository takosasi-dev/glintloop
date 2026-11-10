// ルックの銀残し(bleach、§9.4 の「表現」): sRGB で g=E(C)、o=overlay(x, g)、x2=mix(x, o, contrast÷100)、y=mix(g, x2, 0.6)。
// overlay は §9.4 の「ブレンド」(cb=x、cs=g、チャンネルごと)。α は変えない。範囲外の contrast は 0〜100 に切る。
// 定数バッファは bind の順に {"contrast": "contrast"}(common.hlsli の規則 3)。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 contrast;
};

float OverlayOne(float cb, float cs)
{
    return cb <= 0.5 ? 2.0 * cb * cs : 1.0 - 2.0 * (1.0 - cb) * (1.0 - cs);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 c = Straight(p);
    float3 x = enc3(c);
    float g = LumaE(c);
    float3 o = float3(OverlayOne(x.r, g), OverlayOne(x.g, g), OverlayOne(x.b, g));
    float3 x2 = lerp(x, o, clamp(contrast.x, 0.0, 100.0) / 100.0);
    float3 y = lerp(float3(g, g, g), x2, 0.6);
    return Premul(lin3(y), p.a);
}
