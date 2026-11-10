// ルックのコントラスト(contrast、§9.4 の「基本」): sRGB の x に、c≥0 なら y=mix(x, S(x), c÷50)、c<0 なら y=mix(x, 0.5, −c÷100×0.8)。
// 入力 t0 は src。straight の C を取り出して sRGB で計算し、lin で戻してから α を掛け直す(α は変えない)。
// 定数バッファは bind の順に {"c": "c", "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 c;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    float3 y;
    if (c.x >= 0.0)
    {
        float3 s = x * x * (3.0 - 2.0 * x);
        y = x + (s - x) * (c.x / 50.0);
    }
    else
    {
        y = x + (0.5 - x) * (-c.x / 100.0 * 0.8);
    }
    return Premul(lin3(y), p.a);
}
