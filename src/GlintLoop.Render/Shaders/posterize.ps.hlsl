// ルックのポスタリゼーション(posterize、§9.4 の「表現」): sRGB で y=round(x×(n−1))÷(n−1)(n=levels、チャンネルごと)。α は変えない。
// 入力 t0 は src。round は floor(v+0.5)。範囲外の levels は 2〜16 に切ってから使う(0 での割り算を出さないため)。
// 定数バッファは bind の順に {"levels": "levels"}(common.hlsli の規則 3)。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 levels;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float n1 = clamp(floor(levels.x + 0.5), 2.0, 16.0) - 1.0;
    float3 x = enc3(Straight(p));
    float3 y = floor(x * n1 + 0.5) / n1;
    return Premul(lin3(y), p.a);
}
