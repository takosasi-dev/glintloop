// ハーフトーン(halftone)の 1 つ目のパス: 横の箱形平均。画素の中心を中心とする幅 s=max(2, px(size)) の区間 [x−s÷2, x+s÷2] の straight の C の平均
// (画素 k は区間 [k, k+1] を占め、重なった長さで重みを付ける。キャンバスの外は端の画素で延ばす)。縦の halftone_v と合わせて、升目の A の近似に使う。
// 入力 t0 は src。出力は (平均の C, 1)。定数バッファは bind の順に {size, shortSide($shortSide)}。範囲外の size は 5〜100 に切る。
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
    float lo = pos.x - s * 0.5;
    float hi = pos.x + s * 0.5;
    int k0 = int(floor(lo));
    int k1 = int(ceil(hi)) - 1;
    int last = int(w) - 1;
    int y = int(pos.y);
    float3 sum = float3(0.0, 0.0, 0.0);
    [loop]
    for (int k = k0; k <= k1; k++)
    {
        float wt = min(hi, float(k + 1)) - max(lo, float(k));
        sum += max(wt, 0.0) * Straight(Input0.Load(int3(clamp(k, 0, last), y, 0)));
    }
    // WARP の 0 の向きへの切り捨てで A が下がると、暗い側の cov の差に広がるので、half へ丸めて書く(common.hlsli の RoundToHalf の例外)
    return RoundToHalf(float4(sum / s, 1.0));
}
