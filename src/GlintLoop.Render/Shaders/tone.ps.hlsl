// ルックの影とハイライト(tone、§9.4 の「基本」): e=E(C)、ws=(1−e)²、wh=e²、C'=C×2^(1.5×(shadows×ws+highlights×wh)÷100)。
// 入力 t0 は src。e は straight の C から求め、掛け算はプリマルチプライの値にそのまま掛ける(α は変えない)。
// 定数バッファは bind の順に {"shadows", "highlights", "amount": "$amount"}。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 shadows;
    float4 highlights;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float e = LumaE(Straight(p));
    float ws = (1.0 - e) * (1.0 - e);
    float wh = e * e;
    float k = exp2(1.5 * (shadows.x * ws + highlights.x * wh) / 100.0);
    return float4(p.rgb * k, p.a);
}
