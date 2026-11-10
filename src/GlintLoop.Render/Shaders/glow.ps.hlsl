// 仕上げのグロー(glow、§9.4 の「光」): g=0.6×Blur(B, σ1)+0.4×Blur(B, 3σ1)、k=intensity÷100×color の不透明度、
// P'.rgb=P.rgb+g.rgb×lin(color.rgb)×k、α'=sat(α+g.a×k×(1−α))。入力 t0 は src、t1 は pass:v1(σ1)、t2 は pass:v3(3σ1)。
// 定数バッファは bind の順に {intensity, color}(intensity は scale の実効値。範囲外は 0〜300 に切る)。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 intensity;
    float4 color;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float4 g = 0.6 * LoadHere(Input1, pos) + 0.4 * LoadHere(Input2, pos);
    float k = clamp(intensity.x, 0.0, 300.0) / 100.0 * saturate(color.a);
    return float4(p.rgb + g.rgb * color.rgb * k, saturate(p.a + g.a * k * (1.0 - p.a)));
}
