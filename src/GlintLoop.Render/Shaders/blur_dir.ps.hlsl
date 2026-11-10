// 仕上げのぼかし(blur)の方向ぼかし: 方向 (cos angle, sin angle)(y は下向きが正)に沿った 1 次元ガウス(σ=px(radius)÷2、半幅 ceil(3σ))。
// 各タップは画素の中心から d 画素ずつ離れた位置を、線形・プリマルチプライの双線形補間で読む(キャンバスの外は端を延ばす)。
// σ が 0.05 未満なら入力をそのまま返す。定数バッファは bind の順に {radius, angle, shortSide($shortSide)}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 radius;
    float4 angle;
    float4 shortSide;
};

// 位置 u(画素の中心が i+0.5 の座標)の双線形補間。タップは端の画素に切る。
float4 Bilinear(float2 u, int2 last)
{
    float2 t = u - 0.5;
    float2 f = floor(t);
    float2 a = t - f;
    int2 i0 = int2(f);
    float4 p00 = Input0.Load(int3(clamp(i0, int2(0, 0), last), 0));
    float4 p10 = Input0.Load(int3(clamp(i0 + int2(1, 0), int2(0, 0), last), 0));
    float4 p01 = Input0.Load(int3(clamp(i0 + int2(0, 1), int2(0, 0), last), 0));
    float4 p11 = Input0.Load(int3(clamp(i0 + int2(1, 1), int2(0, 0), last), 0));
    float4 top = p00 + (p10 - p00) * a.x;
    float4 bottom = p01 + (p11 - p01) * a.x;
    return top + (bottom - top) * a.y;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input0.GetDimensions(w, h);
    float sigma = Px(clamp(radius.x, 0.0, 100.0), shortSide.x) * 0.5;
    if (!(sigma >= 0.05)) return Input0.Load(int3(int2(pos.xy), 0));
    float th = radians(clamp(angle.x, -180.0, 180.0));
    float2 dir = float2(cos(th), sin(th));
    int2 last = int2(int(w) - 1, int(h) - 1);
    int r = int(ceil(3.0 * sigma));
    float k = -0.72134752 / (sigma * sigma); // −1÷(2σ²) に log2(e) を掛けたもの
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float wsum = 0.0;
    [loop]
    for (int d = -r; d <= r; d++)
    {
        float wt = exp2(k * float(d * d));
        sum += wt * Bilinear(pos.xy + dir * float(d), last);
        wsum += wt;
    }
    return sum / wsum;
}
