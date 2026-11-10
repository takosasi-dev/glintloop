// ルックの彩度(saturation、§9.4 の「色」): Oklab で k=max(0, (1+saturation÷100)×(1+vibrance÷100×(1−min(Ch÷0.32, 1))))、(a,b)'=(a,b)×k。
// 入力 t0 は src。straight の C を Oklab にして a・b だけを変え(L は変えない)、fromOk で戻してから α を掛け直す。
// 定数バッファは bind の順に {"saturation", "vibrance", "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。

#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 saturation;
    float4 vibrance;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 lab = ToOk(Straight(p));
    float ch = sqrt(lab.y * lab.y + lab.z * lab.z);
    float k = max(0.0, (1.0 + saturation.x / 100.0) * (1.0 + vibrance.x / 100.0 * (1.0 - min(ch / 0.32, 1.0))));
    lab.yz *= k;
    return Premul(FromOk(lab), p.a);
}
