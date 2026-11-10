// グロー(glow)の 1 つ目のパス: 明部 B=(C×w×α, α×w)(プリマルチプライ)。e=E(C)、w=smoothstep(threshold÷100−0.1, threshold÷100+0.1, e)。
// 入力 t0 は src。出力はぼかしのパス(blur_h・glow_wide_h)が読む。定数バッファは bind の順に {threshold}。範囲外は 0〜100 に切る。
// C は α が 0.0001 以下なら 0 なので、そのときの B の色も 0 になる。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 threshold;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 c = Straight(p);
    float t = clamp(threshold.x, 0.0, 100.0) / 100.0;
    float w = SmoothStepSpec(t - 0.1, t + 0.1, LumaE(c));
    return float4(c * w * p.a, p.a * w);
}
