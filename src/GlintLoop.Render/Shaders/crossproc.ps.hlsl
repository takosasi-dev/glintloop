// ルックのクロスプロセス(crossproc、§9.4 の「表現」): sRGB で R'=S(R)、G'=mix(G, S(G), 0.5)、B'=0.12+0.76×B。α は変えない。
// 入力 t0 は src。パラメータが無いので定数バッファを持たない(JSON に bind を書かない)。
// amount の混ぜと出力の切りは共通のパス(effect_finish.ps)が行う。
#include "common.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    float3 y = float3(S(x.r), lerp(x.g, S(x.g), 0.5), 0.12 + 0.76 * x.b);
    return Premul(lin3(y), p.a);
}
