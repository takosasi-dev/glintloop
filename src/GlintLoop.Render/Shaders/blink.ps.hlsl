// モーションのピカピカ(blink、§9.5 の色と光。波形なし): v は square が u<duty÷100 なら 1、sine と tri は W01(ψ)。m=1−depth÷100×(1−v)。
// opacity は P'=P×m、dark は P'.rgb=P.rgb×m、flash は P'.rgb=P.rgb+depth÷100×v×α(dark と flash は α を変えない)。位置に依らないので枠は使わない。
// 定数バッファは bind の順に {mode, shape, duty, depth, t($t), cycles($cycles), phase($phase)}(depth は scale の実効値。範囲外は表の範囲に切る)。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    uint4 mode;
    uint4 shape;
    float4 duty;
    float4 depth;
    float4 t;
    uint4 cycles;
    float4 phase;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    // shape: square 0・sine 1・tri 2
    float v;
    if (shape.x == 1u) v = Wave01(psi, 0u);
    else if (shape.x == 2u) v = Wave01(psi, 1u);
    else v = MotionU(psi) < clamp(duty.x, 10.0, 90.0) / 100.0 ? 1.0 : 0.0;
    float d = clamp(depth.x, 0.0, 100.0) / 100.0;
    // mode: opacity 0・dark 1・flash 2
    if (mode.x == 2u) return float4(p.rgb + d * v * p.a, p.a);
    float m = 1.0 - d * (1.0 - v);
    if (mode.x == 1u) return float4(p.rgb * m, p.a);
    return p * m;
}
