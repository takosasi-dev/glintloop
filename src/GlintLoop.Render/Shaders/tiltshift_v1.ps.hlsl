// ミニチュア(tiltshift)の 1 段目のぼかしの縦のパス: σ=TiltLevelSigma(1) の 1 次元ガウス(端を延ばす)。入力 t0 は pass:h1。
// 段の σ と定数バッファの並びは tiltshift.hlsli のとおり。σ が 0.05 未満なら入力をそのまま返す。
#include "common.hlsli"
#include "tiltshift.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    return TiltGauss(int2(pos.xy), int2(0, 1), TiltLevelSigma(1));
}
