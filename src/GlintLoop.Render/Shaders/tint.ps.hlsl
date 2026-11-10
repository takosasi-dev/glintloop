// ルックの色の重ね(tint、§9.4 の「重ね」): sRGB で s=color の sRGB、y=mix(x, blend(mode, x, s), opacity÷100×color の不透明度)。
// blend は §9.4 の「ブレンド」(cb=x、cs=s、チャンネルごと)。mode の番号は normal 0・multiply 1・screen 2・overlay 3・softlight 4。
// α は変えない。範囲外の opacity は 0〜100 に切る。bind の順は {"color": "color", "mode": "mode", "opacity": "opacity"}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 color;
    uint4 mode;
    float4 opacity;
};

float SoftD(float x)
{
    return x <= 0.25 ? ((16.0 * x - 12.0) * x + 4.0) * x : sqrt(x);
}

float BlendOne(uint m, float cb, float cs)
{
    if (m == 1u) return cb * cs;
    if (m == 2u) return cb + cs - cb * cs;
    if (m == 3u) return cb <= 0.5 ? 2.0 * cb * cs : 1.0 - 2.0 * (1.0 - cb) * (1.0 - cs);
    if (m == 4u) return cs <= 0.5 ? cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb) : cb + (2.0 * cs - 1.0) * (SoftD(cb) - cb);
    return cs;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 x = enc3(Straight(p));
    float3 s = enc3(color.rgb);
    float3 b = float3(BlendOne(mode.x, x.r, s.r), BlendOne(mode.x, x.g, s.g), BlendOne(mode.x, x.b, s.b));
    float k = clamp(opacity.x, 0.0, 100.0) / 100.0 * saturate(color.a);
    return Premul(lin3(lerp(x, b, k)), p.a);
}
