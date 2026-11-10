// モーションの虹色(huecycle、§9.5 の色と光。波形なし): Oklab の (a,b) を δ だけ回す((a,b)'=(a×cos δ−b×sin δ, a×sin δ+b×cos δ)。L は変えない)。
// full は δ=360°×ψ(ψ を fract してから)、swing は δ=range×sin(2π×ψ)。fromOk のあと負のチャンネルは 0 に切り、α を掛け直す。
// 定数バッファは bind の順に {mode, range(度), t($t), cycles($cycles), phase($phase)}。amount の混ぜと出力の切りは共通のパスが行う。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    uint4 mode;
    float4 range;
    float4 t;
    uint4 cycles;
    float4 phase;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    float delta;
    if (mode.x == 1u)
    {
        float s, c;
        SinCos2Pi(psi, s, c);
        delta = radians(clamp(range.x, 10.0, 180.0)) * s;
    }
    else
    {
        delta = TurnAngle(psi);
    }
    float3 lab = ToOk(Straight(p));
    float cs = cos(delta);
    float sn = sin(delta);
    float3 r = float3(lab.x, lab.y * cs - lab.z * sn, lab.y * sn + lab.z * cs);
    return Premul(FromOk(r), p.a);
}
