// モーションの粒子(particles、§9.5 の粒): 粒 i の初期位置・速さ・大きさ・揺れ・回転数・色番号を H3(k0, 51〜59, i) で決め、
// py=fract(y0±sp×ψ)×(H+2sz)−sz、pxx=x0×W+dr×sin(2π×(df×ψ+dp)) に形(snow・dots・bubbles・stars・confetti)を置いて、番号順に over で重ねる。
// 位置と W・H・S0 は枠が基準で、粒は枠の外にも乗る(§9.7 の規則 12 の (e))。定数バッファは bind の順に {kind, dir, count, size, tone, color, opacity, t, cycles, phase, seed, salt, frame}。
#include "common.hlsli"
#include "motion.hlsli"
#include "star.hlsli"

cbuffer Params : register(b0)
{
    uint4 kind;
    uint4 dir;
    float4 count;
    float4 size;
    uint4 tone;
    float4 color;
    float4 opacity;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 seed;
    uint4 salt;
    float4 frame;
};

// rainbow の 6 色(sRGB の 0〜255。表の順)。
static const float3 Rainbow[6] =
{
    float3(255.0, 89.0, 94.0),
    float3(255.0, 202.0, 58.0),
    float3(138.0, 201.0, 38.0),
    float3(25.0, 130.0, 196.0),
    float3(106.0, 76.0, 147.0),
    float3(255.0, 143.0, 208.0),
};

// 粒の覆い(0〜1)。dd は粒の中心からの差(画素)、sz は粒の大きさ(画素)、theta は confetti の角(ラジアン、時計回りが正)。
float Coverage(uint k, float2 dd, float sz, float theta)
{
    float d = length(dd);
    if (k == 0u)
    {
        float q = d / (sz * 0.5);
        return exp(-1.5 * q * q);
    }
    if (k == 1u) return saturate(0.5 + sz * 0.5 - d);
    if (k == 2u)
    {
        // リング(半径 sz÷2、太さ 0.12×sz)に、内側の塗り 0.15 を足す(内側の縁は dots と同じ 1 画素のなだらかさ)
        float ring = saturate(0.5 - abs(d - sz * 0.5) + 0.06 * sz);
        return saturate(ring + 0.15 * saturate(0.5 + sz * 0.5 - d));
    }
    if (k == 3u) return StarShape(dd, sz);
    // confetti: 縦 sz・横 0.5×sz の長方形を theta 回したものへの符号付き距離 sd(内側が負)
    float2 q2 = abs(Turn(-theta, dd)) - float2(0.25 * sz, 0.5 * sz);
    float sd = length(max(q2, float2(0.0, 0.0))) + min(max(q2.x, q2.y), 0.0);
    return saturate(0.5 - sd);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    uint n = uint(clamp(count.x, 5.0, 120.0) + 0.5);
    float2 wh = frame.zw;
    float szBase = Px(clamp(size.x, 5.0, 60.0), FrameShort(frame));
    float op = clamp(opacity.x, 0.0, 100.0) / 100.0;
    float sgn = dir.x == 1u ? -1.0 : 1.0;
    uint k = min(kind.x, 4u);
    // ψ の整数倍の fract と sin は u=fract(ψ) で求める(sp・df・m は整数なので同じ値)
    float u = MotionU(MotionPsi(t.x, cycles.x, phase.x));
    float2 local = ToFrame(pos.xy, frame);
    float3 fixedColor = tone.x == 2u ? color.rgb : float3(1.0, 1.0, 1.0);
    float fixedAlpha = tone.x == 2u ? saturate(color.a) : 1.0;
    uint k0 = H2(seed.x, salt.x);
    uint h51 = H2(k0, 51u);
    uint h52 = H2(k0, 52u);
    uint h53 = H2(k0, 53u);
    uint h54 = H2(k0, 54u);
    uint h55 = H2(k0, 55u);
    uint h56 = H2(k0, 56u);
    uint h57 = H2(k0, 57u);
    uint h58 = H2(k0, 58u);
    uint h59 = H2(k0, 59u);
    [loop]
    for (uint i = 0u; i < n; i++)
    {
        // H3(k0, c, i)=H2(H2(k0, c), i)
        float x0 = U01(H2(h51, i));
        float y0 = U01(H2(h52, i));
        float sp = float(1u + H2(h53, i) % 3u);
        float sz = max(szBase * (0.5 + U01(H2(h54, i))), 0.0001);
        float dr = 0.04 * wh.x * U01(H2(h55, i));
        float df = float(1u + H2(h56, i) % 2u);
        float dp = U01(H2(h57, i));
        float m = float(1u + H2(h58, i) % 3u);
        uint hi = H2(h59, i) % 6u;
        float py = MotionU(y0 + sgn * sp * u) * (wh.y + 2.0 * sz) - sz;
        float sw, cw;
        SinCos2Pi(df * u + dp, sw, cw);
        float pxx = x0 * wh.x + dr * sw;
        float theta = TurnAngle(m * u + dp);
        float cov = Coverage(k, local - float2(pxx, py), sz, theta);
        float3 c = tone.x == 1u ? lin3(Rainbow[hi] / 255.0) : fixedColor;
        float a = cov * op * (tone.x == 1u ? 1.0 : fixedAlpha);
        p = float4(c * a, a) + p * (1.0 - a);
    }
    return p;
}
