// ルックのチャンネル入れ替え(channelmix、§9.4 の「色」): order の綴りの順に、元の R・G・B を新しい R・G・B に置く(grb なら新しい R に元の G)。
// 入力 t0 は src。並べ替えだけなので、プリマルチプライの値をそのまま入れ替える(α は変えない)。
// 定数バッファは bind の順に {"order"(番号: rgb 0・rbg 1・grb 2・gbr 3・brg 4・bgr 5), "amount"}。番号が範囲外なら rgb とする。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 order;
    float4 amount;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 c = p.rgb;
    switch (order.x)
    {
        case 1u: c = p.rbg; break;
        case 2u: c = p.grb; break;
        case 3u: c = p.gbr; break;
        case 4u: c = p.brg; break;
        case 5u: c = p.bgr; break;
        default: break;
    }
    return float4(c, p.a);
}
