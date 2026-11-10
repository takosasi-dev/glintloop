// ルックの色温度(wb、§9.4 の「色」): t=temp÷100、g=tint÷100、R'=R×2^(0.4t+0.1g)、G'=G×2^(−0.3g)、B'=B×2^(−0.4t+0.1g)。
// 入力 t0 は src。チャンネルごとの掛け算なので、プリマルチプライの値にそのまま掛ける(α は変えない)。
// 定数バッファは bind の順に {"temp", "tint", "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 temp;
    float4 tint;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float t = temp.x / 100.0;
    float g = tint.x / 100.0;
    float3 k = exp2(float3(0.4 * t + 0.1 * g, -0.3 * g, -0.4 * t + 0.1 * g));
    return float4(p.rgb * k, p.a);
}
