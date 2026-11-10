// モーションのライトリーク(leaks、§9.5 の色と光。波形なし): k0=H(seed, salt)、光の塊 i=0,1,2 の中心 ci は枠の W・H と H3(k0, 21〜26, i) から、
// 強さ si=0.6+0.4×sin(2π×(ψ+u01(H3(k0,27,i))))、bi=si×exp(−(distance(x, ci)÷px(size))²)、P'.rgb=P.rgb+α×Σ(lin(色i)×bi)×intensity÷100×0.8(α は変えない)。
// x は枠の左上が原点、S0 は枠の短辺(§9.7 の規則 12)。定数バッファは bind の順に {palette, size, intensity, t, cycles, phase, frame, seed, salt}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    uint4 palette;
    float4 size;
    float4 intensity;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
    uint4 seed;
    uint4 salt;
};

// 色の組(sRGB の 8 ビット)。palette の番号 warm 0・cool 1・sunset 2・rainbow 3 ごとに 3 色。
static const float3 PaletteSrgb8[12] =
{
    float3(255.0, 138.0, 61.0), float3(255.0, 79.0, 107.0), float3(255.0, 209.0, 102.0),   // warm #FF8A3D #FF4F6B #FFD166
    float3(61.0, 165.0, 255.0), float3(122.0, 92.0, 255.0), float3(61.0, 255.0, 212.0),    // cool #3DA5FF #7A5CFF #3DFFD4
    float3(255.0, 94.0, 58.0), float3(255.0, 61.0, 129.0), float3(255.0, 179.0, 71.0),     // sunset #FF5E3A #FF3D81 #FFB347
    float3(255.0, 77.0, 77.0), float3(77.0, 255.0, 136.0), float3(77.0, 123.0, 255.0),     // rainbow #FF4D4D #4DFF88 #4D7BFF
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float psi = MotionPsi(t.x, cycles.x, phase.x);
    uint k0 = H2(seed.x, salt.x);
    float2 x = ToFrame(pos.xy, frame);
    float w = frame.z;
    float h = frame.w;
    float r = Px(clamp(size.x, 100.0, 1000.0), FrameShort(frame));
    uint first = min(palette.x, 3u) * 3u;
    float3 sum = float3(0.0, 0.0, 0.0);
    [unroll]
    for (uint i = 0u; i < 3u; i++)
    {
        float fi = 1.0 + float(H3(k0, 23u, i) % 2u);
        float gi = 1.0 + float(H3(k0, 24u, i) % 2u);
        float phi = U01(H3(k0, 25u, i));
        float chi = U01(H3(k0, 26u, i));
        float sx, cx, sy, cy, ss, cs;
        SinCos2Pi(fi * psi + phi, sx, cx);
        SinCos2Pi(gi * psi + chi, sy, cy);
        float2 c = float2(
            w * (0.15 + 0.7 * U01(H3(k0, 21u, i))) + 0.25 * w * sx,
            h * (0.15 + 0.7 * U01(H3(k0, 22u, i))) + 0.25 * h * cy);
        SinCos2Pi(psi + U01(H3(k0, 27u, i)), ss, cs);
        float si = 0.6 + 0.4 * ss;
        float q = distance(x, c) / r;
        float bi = si * exp(-(q * q));
        sum += lin3(PaletteSrgb8[first + i] / 255.0) * bi;
    }
    float k = clamp(intensity.x, 0.0, 200.0) / 100.0 * 0.8;
    return float4(p.rgb + p.a * sum * k, p.a);
}
