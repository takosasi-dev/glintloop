// ルックの色相(hue、§9.4 の「色」): Oklab の (a,b) を s(shift をラジアンにした値)だけ回す。(a,b)'=(a×cos s−b×sin s, a×sin s+b×cos s)。
// 入力 t0 は src。straight の C を Oklab にして L は変えず、fromOk で戻してから α を掛け直す。
// 定数バッファは bind の順に {"shift"(度), "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。

#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 shift;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 lab = ToOk(Straight(p));
    float s = radians(shift.x);
    float cs = cos(s);
    float sn = sin(s);
    float3 r = float3(lab.x, lab.y * cs - lab.z * sn, lab.y * sn + lab.z * cs);
    return Premul(FromOk(r), p.a);
}
