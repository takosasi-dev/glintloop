// 影(shadow)の 1 つ目のパス: α を σ=px(blur)÷2 の 1 次元ガウス(半幅 ceil(3σ)、合計 1 に正規化)で横にぼかす。ここだけキャンバスの外は透明(α=0)。
// σ が 0.05 未満なら α をそのまま返す。入力 t0 は src。出力は 4 成分とも同じ値。縦は shadow_v。
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
    float a = Input0.Load(int3(p, 0)).a;
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
            int x = p.x + d;
            if (x >= 0 && x < int(w)) sum += wt * Input0.Load(int3(x, p.y, 0)).a;
            wsum += wt;
        }
        a = sum / wsum;
    }
    return float4(a, a, a, a);
}
