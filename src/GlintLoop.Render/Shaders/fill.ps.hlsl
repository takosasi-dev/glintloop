// 全画素を定数の色(線形光・プリマルチプライの RGBA)で塗るピクセルシェーダー。
// AC-1 のパス 1、FR-94 の自己検査、over のブレンドの重ねる側に使う。
// 定数バッファ b0 の A が色。

cbuffer PassConstants : register(b0)
{
    float4 A;
    float4 B;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    return A;
}
