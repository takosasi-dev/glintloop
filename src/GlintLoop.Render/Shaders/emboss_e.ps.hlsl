// エンボス(emboss)の 1 つ目のパス: 各画素の E(C)=enc(sat(Y(C)))(C は straight。α が 0.0001 以下なら 0)を 4 成分に入れる。
// 出力は blur_h・blur_v(σ=px(soft)÷2)でぼかして h(x) にする。入力 t0 は src。定数バッファは無い。
#include "common.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    float e = LumaE(Straight(LoadHere(Input0, pos)));
    return float4(e, e, e, e);
}
