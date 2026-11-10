// モーションのネオン(glowpulse、§9.5 の色と光。波形あり): 仕上げの glow と同じ計算で intensity に W01(ψ) を掛ける。前段の 5 つのパスは glow のシェーダーをそのまま使う。
// g=0.6×Blur(B, σ1)+0.4×Blur(B, 3σ1)、k=intensity÷100×W01(ψ)×color の不透明度、P'.rgb=P.rgb+g.rgb×lin(color.rgb)×k、α'=sat(α+g.a×k×(1−α))。
// 入力 t0 は src、t1 は pass:v1、t2 は pass:v3。定数バッファは bind の順に {intensity, color, t($t), cycles($cycles), phase($phase), wave($wave)}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 intensity;
    float4 color;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 wave;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float4 g = 0.6 * LoadHere(Input1, pos) + 0.4 * LoadHere(Input2, pos);
    float w01 = Wave01(MotionPsi(t.x, cycles.x, phase.x), wave.x);
    float k = clamp(intensity.x, 0.0, 300.0) / 100.0 * w01 * saturate(color.a);
    return float4(p.rgb + g.rgb * color.rgb * k, saturate(p.a + g.a * k * (1.0 - p.a)));
}
