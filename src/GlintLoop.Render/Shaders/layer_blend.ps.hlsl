// レイヤーの重ね(§9.7 の規則 13): 下の結果 B(t0)に、作業面に置いたレイヤー S(t1)を不透明度とブレンドで重ねた R を書く。
// normal・add は線形光のプリマルチプライで、multiply・screen・overlay・softlight は Mb を「sRGB で」(§9.4 のブレンドの関数)求める。
// R は RGB を 0〜64・α を 0〜1 に切り、NaN を 0 にして、最も近い half に丸めて書く。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 Opacity; // x: o=不透明度÷100
    uint4 Mode;     // x: 0=normal 1=multiply 2=screen 3=overlay 4=softlight 5=add
};

float SoftD(float x)
{
    return x <= 0.25 ? ((16.0 * x - 12.0) * x + 4.0) * x : sqrt(x);
}

float BlendF(uint mode, float cb, float cs)
{
    if (mode == 1u) return cb * cs;
    if (mode == 2u) return cb + cs - cb * cs;
    if (mode == 3u) return cb <= 0.5 ? 2.0 * cb * cs : 1.0 - 2.0 * (1.0 - cb) * (1.0 - cs);
    // softlight
    return cs <= 0.5 ? cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb) : cb + (2.0 * cs - 1.0) * (SoftD(cb) - cb);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 b = LoadHere(Input0, pos);
    float4 s = LoadHere(Input1, pos) * Opacity.x;
    float as = s.a;
    float ab = b.a;
    uint mode = Mode.x;
    float4 r;
    if (mode == 0u)
    {
        r = s + (1.0 - as) * b;
    }
    else if (mode == 5u)
    {
        r = float4(s.rgb + b.rgb, min(1.0, as + ab));
    }
    else
    {
        float3 cb = enc3(Straight(b));
        float3 cs = enc3(Straight(s));
        float3 mb = lin3(float3(BlendF(mode, cb.r, cs.r), BlendF(mode, cb.g, cs.g), BlendF(mode, cb.b, cs.b)));
        r = float4((1.0 - ab) * s.rgb + (1.0 - as) * b.rgb + as * ab * mb, as + ab * (1.0 - as));
    }
    float3 rgb = min(max(r.rgb, float3(0.0, 0.0, 0.0)), float3(64.0, 64.0, 64.0));
    float a = min(max(r.a, 0.0), 1.0);
    return RoundToHalf(float4(rgb, a));
}
