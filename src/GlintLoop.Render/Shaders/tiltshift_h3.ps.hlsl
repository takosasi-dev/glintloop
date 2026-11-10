// ミニチュア(tiltshift)の 3 段目のぼかしの横のパス: σ=TiltLevelSigma(3) の 1 次元ガウス(端を延ばす)。入力 t0 は src。
// 段の σ と定数バッファの並びは tiltshift.hlsli のとおり。σ が 0.05 未満なら入力をそのまま返す。
#include "common.hlsli"
#include "tiltshift.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    return TiltGauss(int2(pos.xy), int2(1, 0), TiltLevelSigma(3));
}
