// ルックの露出(exposure、§9.4 の「基本」): C'=C×2^ev。プリマルチプライの値でも同じ式になる(α は変えない)。
// 入力 t0 は src。amount の混ぜと出力の切りは共通のパス(effect_finish.ps)が行うので、ここでは使わない。
// 定数バッファは bind の順に {"ev": "ev", "amount": "$amount"}(common.hlsli の規則 3)。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 ev;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    return float4(p.rgb * exp2(ev.x), p.a);
}
