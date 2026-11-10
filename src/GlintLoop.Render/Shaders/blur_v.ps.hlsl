// 仕上げのガウスぼかしの縦のパス(blur_h と対で使う): blur_h の出力を、同じ σ=px(radius)÷2 の 1 次元ガウスで縦に畳み込む。
// 横と縦の積で 2 次元の Blur(X, σ) になる(端を延ばすのも軸ごとに分かれる)。σ が 0.05 未満なら入力をそのまま返す。
// 定数バッファは bind の順に {"radius": ‰ のパラメータ, "shortSide": "$shortSide"}。範囲外の radius は 0〜150 に切る。
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
    float sigma = Px(clamp(radius.x, 0.0, 150.0), shortSide.x) * 0.5;
    if (!(sigma >= 0.05)) return Input0.Load(int3(p, 0));
    int r = int(ceil(3.0 * sigma));
    float k = -0.72134752 / (sigma * sigma); // −1÷(2σ²) に log2(e) を掛けたもの
    int last = int(h) - 1;
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float wsum = 0.0;
    [loop]
    for (int d = -r; d <= r; d++)
    {
        float wt = exp2(k * float(d * d));
        sum += wt * Input0.Load(int3(p.x, clamp(p.y + d, 0, last), 0));
        wsum += wt;
    }
    return sum / wsum;
}
