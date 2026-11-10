// ルックのグラデーション重ね(gradient、§9.4 の「重ね」): キャンバスの 4 隅 (0,0)(W,0)(0,H)(W,H) を (cos angle, sin angle) に射影した
// 最小を 0・最大を 1 として画素の中心の射影を t とし、c0 と c1(不透明度を含む)を sRGB で t について補間した色で tint と同じ式を行う。
// α は変えない。範囲外の angle・opacity は範囲の端に切る。bind の順は {c0, c1, angle, mode, opacity, texel($texel)}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 c0;
    float4 c1;
    float4 angle;
    uint4 mode;
    float4 opacity;
    float4 texel;
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
    float th = radians(clamp(angle.x, -180.0, 180.0));
    float2 dir = float2(cos(th), sin(th));
    float pw = texel.z * dir.x;
    float ph = texel.w * dir.y;
    float lo = min(min(0.0, pw), min(ph, pw + ph));
    float hi = max(max(0.0, pw), max(ph, pw + ph));
    float t = hi - lo > 0.0 ? saturate((dot(pos.xy, dir) - lo) / (hi - lo)) : 0.0;

    float3 sc = lerp(enc3(c0.rgb), enc3(c1.rgb), t);
    float sa = lerp(saturate(c0.a), saturate(c1.a), t);
    float3 x = enc3(Straight(p));
    float3 b = float3(BlendOne(mode.x, x.r, sc.r), BlendOne(mode.x, x.g, sc.g), BlendOne(mode.x, x.b, sc.b));
    float k = clamp(opacity.x, 0.0, 100.0) / 100.0 * sa;
    return Premul(lin3(lerp(x, b, k)), p.a);
}
