// 1/2 の箱形縮小(§9.7 の規則 2・3): 出力の画素 (i, j) は入力の (2i〜2i+1, 2j〜2j+1) の 4 画素の平均(線形光・プリマルチプライ)。
// 入力の辺が奇数のときは端の 1 列(行)を複製したものとして読む(LoadClamp)。出力の辺は ceil(辺÷2) を呼び手が作る。
// Mode.x が 0 なら入力は straight(sRGB のソース)でタップごとにプリマルチプライし、1 なら RGBA16F のプリマルチプライ済み。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 Mode;
};

float4 Tap(int2 q)
{
    float4 s = LoadClamp(Input0, q);
    return Mode.x != 0u ? s : Premul(s.rgb, s.a);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    int2 q = int2(pos.xy) * 2;
    float4 sum = Tap(q) + Tap(q + int2(1, 0)) + Tap(q + int2(0, 1)) + Tap(q + int2(1, 1));
    return RoundToHalf(sum * 0.25);
}
