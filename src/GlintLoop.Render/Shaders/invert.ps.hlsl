// ルックの色の反転(invert、§9.4 の「色」): sRGB で y=1−x。
// 入力 t0 は src。straight の C を取り出して sRGB で計算し、lin で戻してから α を掛け直す(α は変えない)。
// 定数バッファは bind の順に {"amount": "$amount"}(パラメータは無い)。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    return Premul(lin3(1.0 - x), p.a);
}
