// 仕上げの周辺光量(vignette、§9.4 の「光」): ax=mix(W, max(W,H), round÷100)÷2(ay も同様)、d=length((x−W÷2)÷ax, (y−H÷2)÷ay)、
// s0=size÷100×1.2、s1=s0+max(soft÷100×1.2, 0.01)、m=strength÷100×smoothstep(s0, s1, d)×color の不透明度、P'.rgb=P.rgb×(1−m)+lin(color.rgb)×α×m。
// α は変えない。定数バッファは bind の順に {strength, size, soft, round, color, texel($texel)}(strength は scale の実効値)。範囲外は表の範囲に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 strength;
    float4 size;
    float4 soft;
    float4 round_;
    float4 color;
    float4 texel;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float2 wh = texel.zw;
    float m0 = max(wh.x, wh.y);
    float rd = clamp(round_.x, 0.0, 100.0) / 100.0;
    float2 ax = float2(wh.x + (m0 - wh.x) * rd, wh.y + (m0 - wh.y) * rd) * 0.5;
    float d = length((pos.xy - wh * 0.5) / ax);
    float s0 = clamp(size.x, 0.0, 100.0) / 100.0 * 1.2;
    float s1 = s0 + max(clamp(soft.x, 0.0, 100.0) / 100.0 * 1.2, 0.01);
    float m = clamp(strength.x, 0.0, 100.0) / 100.0 * SmoothStepSpec(s0, s1, d) * saturate(color.a);
    return float4(p.rgb * (1.0 - m) + color.rgb * p.a * m, p.a);
}
