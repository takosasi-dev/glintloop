// ルックの白黒(mono、§9.4 の「色」): sRGB で g=(rw×x.r+gw×x.g+bw×x.b)÷(rw+gw+bw)、y=(g,g,g)。重みの和が 0 のときは既定値(30, 59, 11)を使う。
// 入力 t0 は src。straight の C を取り出して sRGB で計算し、lin で戻してから α を掛け直す(α は変えない)。
// 定数バッファは bind の順に {"rw", "gw", "bw", "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 rw;
    float4 gw;
    float4 bw;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    float3 w = float3(rw.x, gw.x, bw.x);
    float sum = w.x + w.y + w.z;
    if (abs(sum) < 1e-6)
    {
        w = float3(30.0, 59.0, 11.0);
        sum = 100.0;
    }
    float g = dot(w, x) / sum;
    return Premul(lin3(float3(g, g, g)), p.a);
}
