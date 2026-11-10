// ルックのグラデーションマップ(gradmap、§9.4 の「色」): t=E(C) に対する色を、隣り合う 2 色の Oklab の線形補間で求め、fromOk で線形にする。
// n=2(番号 0)は c0 を t=0・c2 を t=1、n=3(番号 1)は c0 を 0・c1 を mid÷100・c2 を 1 に置く。色の不透明度は使わず、α は入力のまま。
// 定数バッファは bind の順に {"n"(番号), "c0", "c1", "c2"(lin 済みの色), "mid", "amount"}。mid は範囲外でも 0 除算しないよう内側に収める。

#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 n;
    float4 c0;
    float4 c1;
    float4 c2;
    float4 mid;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float t = LumaE(Straight(p));
    float3 ok0 = ToOk(c0.rgb);
    float3 ok2 = ToOk(c2.rgb);
    float3 lab;
    if (n.x == 1u)
    {
        float3 ok1 = ToOk(c1.rgb);
        float m = clamp(mid.x / 100.0, 1e-4, 1.0 - 1e-4);
        lab = t <= m ? lerp(ok0, ok1, t / m) : lerp(ok1, ok2, (t - m) / (1.0 - m));
    }
    else
    {
        lab = lerp(ok0, ok2, t);
    }
    return Premul(FromOk(lab), p.a);
}
