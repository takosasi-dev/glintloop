// ルックのモザイク(pixelate、§9.4 の「表現」)の 1 段目: 画素を含む升目の横の範囲 [x0, min(x0+cell, W)) の P の平均(行ごと)。
// cell=max(1, round(px(size)))、升目はキャンバスの左上から。2 段目(pixelate_v)が縦に平均して、升目の P の単純平均になる。
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
    uint w = uint(texel.z);
    uint cell = uint(max(1.0, floor(Px(clamp(size.x, 5.0, 200.0), shortSide.x) + 0.5)));
    uint2 ij = uint2(pos.xy);
    uint x0 = (ij.x / cell) * cell;
    uint x1 = min(x0 + cell, w);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    [loop]
    for (uint x = x0; x < x1; x++)
    {
        sum += Input0.Load(int3(int(x), int(ij.y), 0));
    }
    return sum / float(max(x1 - x0, 1u));
}
