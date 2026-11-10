// 取り込んだ画像(R8G8B8A8_UNORM_SRGB、straight)を、線形光・プリマルチプライの RGBA16F へ直すピクセルシェーダー(§9.7 の規則 2)。
// sRGB の形式の読み出しでハードウェアが lin を行うので、ここでは straight の線形 RGB に α を掛けるだけ。
// 入力 t0 の画素を同じ位置から Load する(大きさは同じ)。書く前に RoundToHalf で最も近い half に丸める(値は 0〜1 なので範囲は変わらない)。
#include "common.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 s = LoadHere(Input0, pos);
    return RoundToHalf(Premul(s.rgb, s.a));
}
