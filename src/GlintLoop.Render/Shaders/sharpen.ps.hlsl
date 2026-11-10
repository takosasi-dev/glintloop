// 仕上げのシャープ(sharpen、§9.4 の「ぼかし」): sRGB で、b=enc(Blur(P, px(radius)÷2) の straight の C)、Δ=x−b、
// Δ'=Δ×smoothstep(threshold÷255, 2×threshold÷255, |Δ|)(threshold が 0 なら Δ'=Δ)、y=x+strength÷100×Δ'。α は変えない。
// 入力 t0 は src、t1 は pass:v(blur_h・blur_v の結果)。定数バッファは bind の順に {strength, threshold}(strength は scale の実効値)。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 strength;
    float4 threshold;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    float3 b = enc3(Straight(LoadHere(Input1, pos)));
    float3 d = x - b;
    float th = clamp(threshold.x, 0.0, 50.0) / 255.0;
    if (th > 0.0)
    {
        float3 ad = abs(d);
        d *= float3(SmoothStepSpec(th, 2.0 * th, ad.r), SmoothStepSpec(th, 2.0 * th, ad.g), SmoothStepSpec(th, 2.0 * th, ad.b));
    }
    float3 y = x + clamp(strength.x, 0.0, 300.0) / 100.0 * d;
    return Premul(lin3(y), p.a);
}
