// ルックのレベル(levels、§9.4 の「基本」): sRGB の x に y=pow(sat((x−b)÷(w−b)), 1÷gamma)(b=black÷100、w=white÷100)。
// white−black<5 の直しは文書の側(読み込みと操作)が行うので、ここでは行わない。範囲外の値でも NaN にしないため、w−b と gamma の下限だけを守る。
// 定数バッファは bind の順に {"black", "white", "gamma", "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 black;
    float4 white;
    float4 gamma;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    float b = black.x / 100.0;
    float d = max(white.x / 100.0 - b, 1e-4);
    float e = 1.0 / max(gamma.x, 1e-3);
    float3 t = saturate((x - b) / d);
    float3 y = exp2(e * log2(t)); // pow(t, e)。t=0 は log2 が −∞ で 0 になる
    return Premul(lin3(y), p.a);
}
