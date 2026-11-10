// 前のパスの出力の RGB に「×A.rgb + B.rgb」を掛けるピクセルシェーダー。α はそのまま通す。
// AC-1 のパス 2(×0.5)とパス 3(+0.5)に使う。値は切らない(1.0 を超えてよい)。
// 入力は t0 の画素をそのまま Load する(サンプラーを使わない)。

Texture2D<float4> Src : register(t0);

cbuffer PassConstants : register(b0)
{
    float4 A;
    float4 B;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = Src.Load(int3(int2(pos.xy), 0));
    return float4(p.rgb * A.rgb + B.rgb, p.a);
}
