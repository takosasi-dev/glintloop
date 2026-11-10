// 仕上げの色ずれ(aberration、§9.4 の「光」): ずれ o は linear が px(amount)×(cos angle, sin angle)、radial が px(amount)×(x−c)÷(S0÷2)(c は中心)。
// 赤は x+o、緑は x、青は x−o を双線形補間で読み(端を延ばす)、P'=(Pr.r, Pg.g, Pb.b, (Pr.a+Pg.a+Pb.a)÷3)。
// 定数バッファは bind の順に {mode, amount, angle, shortSide($shortSide), texel($texel)}(amount は scale の実効値)。範囲外は表の範囲に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 mode;
    float4 amount;
    float4 angle;
    float4 shortSide;
    float4 texel;
};

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
    int2 last = int2(int(texel.z) - 1, int(texel.w) - 1);
    float k = Px(clamp(amount.x, 0.0, 30.0), shortSide.x);
    float2 o;
    if (mode.x == 1u)
    {
        float th = radians(clamp(angle.x, -180.0, 180.0));
        o = k * float2(cos(th), sin(th));
    }
    else
    {
        o = k * (pos.xy - texel.zw * 0.5) / (shortSide.x * 0.5);
    }
    float4 pr = Bilinear(pos.xy + o, last);
    float4 pg = LoadHere(Input0, pos);
    float4 pb = Bilinear(pos.xy - o, last);
    return float4(pr.r, pg.g, pb.b, (pr.a + pg.a + pb.a) / 3.0);
}
