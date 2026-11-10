// 形の反転(flip、§9.4 の「寸法」): h が真なら左右を、v が真なら上下を入れ替える。整数の位置の写像で、フィルタをかけない。
// 入力 t0 は src。出力の画素 (i, j) は入力の (h ? W−1−i : i, v ? H−1−j : j) をそのまま写す(α も同じ)。
// 定数バッファは bind の順に {"h": "h", "v": "v"}(どちらも bool → uint4)。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 h;
    uint4 v;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, ht;
    Input0.GetDimensions(w, ht);
    int2 p = int2(pos.xy);
    if (h.x != 0u) p.x = int(w) - 1 - p.x;
    if (v.x != 0u) p.y = int(ht) - 1 - p.y;
    return Input0.Load(int3(p, 0));
}
