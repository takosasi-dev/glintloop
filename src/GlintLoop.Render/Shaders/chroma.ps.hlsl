// 色抜き(chroma、§9.4 の「輪郭」): Oklab の (a,b) で key との距離 d を測り、m=smoothstep(tol, tol+max(soft,0.01), d) を α に掛ける。
// despill は key の色相の向きの成分 s を (a,b) から引く(L は変えない)。α' と変えた色で P を作り直す。明るさ L は距離に使わない。
// 入力 t0 は src。定数バッファは bind の順に {key(color), tol, soft, despill}(% は 0〜100 のまま)。key の不透明度 AA は使わない。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 key;
    float4 tol;
    float4 soft;
    float4 despill;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float3 lab = ToOk(Straight(p));
    float2 kab = ToOk(key.rgb).yz;
    float d = length(lab.yz - kab) / 0.3;
    float t0 = tol.x / 100.0;
    float m = SmoothStepSpec(t0, t0 + max(soft.x / 100.0, 0.01), d);
    float a1 = p.a * m;
    float chk = length(kab);
    float2 u = kab / max(chk, 1e-6);
    float s = max(dot(lab.yz, u), 0.0);
    float2 ab = lab.yz - despill.x / 100.0 * s * u;
    float3 c = FromOk(float3(lab.x, ab));
    return Premul(c, a1);
}
