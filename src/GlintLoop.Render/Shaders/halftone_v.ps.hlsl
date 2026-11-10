// ハーフトーン(halftone)の 2 つ目のパス: halftone_h の出力(横に平均した C)を、同じ幅 s の区間で縦に箱形平均する(端を延ばす)。
// 結果は、画素の中心を中心とする一辺 s の軸に沿った正方形の C の平均。halftone.ps が格子点 q で双線形補間して A にする(§9.4 の近似)。
// 入力 t0 は pass:h。定数バッファは bind の順に {size, shortSide($shortSide)}。範囲外の size は 5〜100 に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 size;
    float4 shortSide;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input0.GetDimensions(w, h);
    float s = max(2.0, Px(clamp(size.x, 5.0, 100.0), shortSide.x));
    float lo = pos.y - s * 0.5;
    float hi = pos.y + s * 0.5;
    int k0 = int(floor(lo));
    int k1 = int(ceil(hi)) - 1;
    int last = int(h) - 1;
    int x = int(pos.x);
    float3 sum = float3(0.0, 0.0, 0.0);
    [loop]
    for (int k = k0; k <= k1; k++)
    {
        float wt = min(hi, float(k + 1)) - max(lo, float(k));
        sum += max(wt, 0.0) * Input0.Load(int3(x, clamp(k, 0, last), 0)).rgb;
    }
    // WARP の 0 の向きへの切り捨てで A が下がると、暗い側の cov の差に広がるので、half へ丸めて書く(common.hlsli の RoundToHalf の例外)
    return RoundToHalf(float4(sum / s, 1.0));
}
