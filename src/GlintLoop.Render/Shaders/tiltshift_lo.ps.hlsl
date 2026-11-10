// ミニチュア(tiltshift)の合成の前半: σ(x) が σmax÷4 以下の画素を、入力(σ=0)・1 段目・2 段目のうち挟む 2 枚の mix で求める。
// 入力 t0 は src、t1 は pass:v1、t2 は pass:v2。σ(x) が σmax÷4 より大きい画素は後半(tiltshift.ps)が求めるので 0 を返す。
// σmax が 0.05 未満なら入力をそのまま返す。定数バッファは tiltshift.hlsli のとおり。
#include "common.hlsli"
#include "tiltshift.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy);
    float4 l0 = Input0.Load(int3(p, 0));
    if (!(TiltSigmaMax() >= 0.05)) return l0;
    float s = TiltSigmaAt(pos.xy);
    float s1 = TiltLevelSigma(1);
    float s2 = TiltLevelSigma(2);
    if (s > s2) return float4(0.0, 0.0, 0.0, 0.0);
    if (s <= s1)
    {
        float4 l1 = Input1.Load(int3(p, 0));
        return l0 + (l1 - l0) * TiltWeight(s, 0.0, s1);
    }
    float4 a = Input1.Load(int3(p, 0));
    float4 b = Input2.Load(int3(p, 0));
    return a + (b - a) * TiltWeight(s, s1, s2);
}
