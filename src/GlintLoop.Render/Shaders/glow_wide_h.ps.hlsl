// グロー(glow)の広いぼかしの横のパス: σ=3×σ1(σ1=px(radius)÷2)の 1 次元ガウス(半幅 ceil(3σ)、合計 1 に正規化、端を延ばす)。
// 入力 t0 は pass:bright。σ が 0.05 未満なら入力をそのまま返す。縦は glow_wide_v。
// 定数バッファは bind の順に {radius, shortSide($shortSide)}。範囲外の radius は 0〜150 に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 radius;
    float4 shortSide;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input0.GetDimensions(w, h);
    int2 p = int2(pos.xy);
    float sigma = 3.0 * Px(clamp(radius.x, 0.0, 150.0), shortSide.x) * 0.5;
    if (!(sigma >= 0.05)) return Input0.Load(int3(p, 0));
    int r = int(ceil(3.0 * sigma));
    float k = -0.72134752 / (sigma * sigma); // −1÷(2σ²) に log2(e) を掛けたもの
    int last = int(w) - 1;
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float wsum = 0.0;
    [loop]
    for (int d = -r; d <= r; d++)
    {
        float wt = exp2(k * float(d * d));
        sum += wt * Input0.Load(int3(clamp(p.x + d, 0, last), p.y, 0));
        wsum += wt;
    }
    return sum / wsum;
}
