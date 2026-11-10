// ルックのしきい値(threshold、§9.4 の「表現」): e=E(C)、w=smoothstep(t÷100−soft÷200, t÷100+soft÷200, e)、C'=mix(lin(dark), lin(light), w)。
// soft が 0 のときは SmoothStepSpec が e≥t÷100 の段になる。色の不透明度は使わない。α は変えない。範囲外の t・soft は範囲の端に切る。
// 定数バッファは bind の順に {"t": "t", "soft": "soft", "dark": "dark", "light": "light"}(色は lin 済みの RGB と不透明度)。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 t;
    float4 soft;
    float4 dark;
    float4 light;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float e = LumaE(Straight(p));
    float t0 = clamp(t.x, 0.0, 100.0) / 100.0;
    float hw = clamp(soft.x, 0.0, 30.0) / 200.0;
    float w = SmoothStepSpec(t0 - hw, t0 + hw, e);
    float3 c = lerp(dark.rgb, light.rgb, w);
    return Premul(c, p.a);
}
