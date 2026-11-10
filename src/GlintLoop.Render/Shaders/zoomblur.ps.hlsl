// 仕上げの放射ぼかし(zoomblur、§9.4 の「ぼかし」): 32 個の標本 i=0〜31(t=i÷31)の同じ重みの平均。c=center×(W, H)。
// zoom は c+(x−c)×(1−strength÷100×0.5×t)、spin は (x−c) を (t−0.5)×strength÷100×(π÷3) だけ回した位置を、双線形補間で読む(端を延ばす)。
// 定数バッファは bind の順に {kind, center, strength, texel($texel)}。範囲外の strength は 0〜100 に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 kind;
    float4 center;
    float4 strength;
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
    float2 c = saturate(center.xy) * texel.zw;
    float2 v = pos.xy - c;
    float s = clamp(strength.x, 0.0, 100.0) / 100.0;
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    [loop]
    for (int i = 0; i < 32; i++)
    {
        float t = float(i) / 31.0;
        float2 q = kind.x == 1u ? c + Turn((t - 0.5) * s * (3.14159265 / 3.0), v) : c + v * (1.0 - s * 0.5 * t);
        sum += Bilinear(q, last);
    }
    return sum / 32.0;
}
