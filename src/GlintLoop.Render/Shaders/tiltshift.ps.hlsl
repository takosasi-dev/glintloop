// ミニチュア(tiltshift、§9.4 の「ぼかし」)の合成の後半: σ(x) が σmax÷4 以下の画素は前半(tiltshift_lo)の結果をそのまま使い、
// それより大きい画素は 2〜3 段目か 3〜4 段目の挟む 2 枚を w=sat((σ(x)−σa)÷max(σb−σa, 1e-6)) で mix する。
// 入力 t0 は pass:lo、t1〜t3 は pass:v2・v3・v4。σmax が 0.05 未満なら前半(=入力)を返す。定数バッファは tiltshift.hlsli のとおり。
#include "common.hlsli"
#include "tiltshift.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy);
    float4 lo = Input0.Load(int3(p, 0));
    if (!(TiltSigmaMax() >= 0.05)) return lo;
    float s = TiltSigmaAt(pos.xy);
    float s2 = TiltLevelSigma(2);
    float s3 = TiltLevelSigma(3);
    float s4 = TiltLevelSigma(4);
    if (s <= s2) return lo;
    if (s <= s3)
    {
        float4 a = Input1.Load(int3(p, 0));
        float4 b = Input2.Load(int3(p, 0));
        return a + (b - a) * TiltWeight(s, s2, s3);
    }
    float4 c = Input2.Load(int3(p, 0));
    float4 d = Input3.Load(int3(p, 0));
    return c + (d - c) * TiltWeight(s, s3, s4);
}
