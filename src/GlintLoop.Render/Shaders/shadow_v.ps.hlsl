// 影(shadow)の 2 つ目のパス: shadow_h の出力(横にぼかした α)を、同じ σ=px(blur)÷2 のガウスで縦にぼかす。キャンバスの外は透明(0)。
// 横と縦の積で As の元になる Blur(α, px(blur)÷2) になる。σ が 0.05 未満なら入力をそのまま返す。入力 t0 は pass:h。
// 定数バッファは bind の順に {blur, shortSide($shortSide)}。範囲外の blur は 0〜100 に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 blur;
    float4 shortSide;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input0.GetDimensions(w, h);
    int2 p = int2(pos.xy);
    float sigma = Px(clamp(blur.x, 0.0, 100.0), shortSide.x) * 0.5;
    float a = Input0.Load(int3(p, 0)).r;
    if (sigma >= 0.05)
    {
        int r = int(ceil(3.0 * sigma));
        float k = -0.72134752 / (sigma * sigma); // −1÷(2σ²) に log2(e) を掛けたもの
        float sum = 0.0;
        float wsum = 0.0;
        [loop]
        for (int d = -r; d <= r; d++)
        {
            float wt = exp2(k * float(d * d));
            int y = p.y + d;
            if (y >= 0 && y < int(h)) sum += wt * Input0.Load(int3(p.x, y, 0)).r;
            wsum += wt;
        }
        a = sum / wsum;
    }
    return float4(a, a, a, a);
}
