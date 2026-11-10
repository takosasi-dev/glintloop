// ルックのフェード(fade、§9.4 の「基本」): sRGB の x に y=lift÷100+x×(1−lift÷100−drop÷100)。
// 入力 t0 は src。straight の C を取り出して sRGB で計算し、lin で戻してから α を掛け直す(α は変えない)。
// 定数バッファは bind の順に {"lift", "drop", "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 lift;
    float4 drop;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    float l = lift.x / 100.0;
    float3 y = l + x * (1.0 - l - drop.x / 100.0);
    return Premul(lin3(y), p.a);
}
