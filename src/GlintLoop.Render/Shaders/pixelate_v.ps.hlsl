// ルックのモザイク(pixelate、§9.4 の「表現」)の 2 段目: 1 段目の出力を、升目の縦の範囲 [y0, min(y0+cell, H)) で平均する(列ごと)。
// 横の平均の縦の平均なので、升目(端は小さくなる)の線形・プリマルチプライの単純平均になり、升目の全画素が同じ値になる。
// 範囲外の size は 5〜200 に切る。定数バッファは bind の順に {"size": "size", "shortSide": "$shortSide", "texel": "$texel"}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 size;
    float4 shortSide;
    float4 texel;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint h = uint(texel.w);
    uint cell = uint(max(1.0, floor(Px(clamp(size.x, 5.0, 200.0), shortSide.x) + 0.5)));
    uint2 ij = uint2(pos.xy);
    uint y0 = (ij.y / cell) * cell;
    uint y1 = min(y0 + cell, h);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    [loop]
    for (uint y = y0; y < y1; y++)
    {
        sum += Input0.Load(int3(int(ij.x), int(y), 0));
    }
    return sum / float(max(y1 - y0, 1u));
}
