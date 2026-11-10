// モーションのグリッチ(glitch、§9.5 の乱れ): 段 j=floor(ψ×steps) mod (C×steps) ごとに、帯 b の有効・上端・高さ・横ずれを H3(k0, 31〜34, j×16+b) で決め、
// 帯の行の赤・緑・青を x−dx×(1+rgb÷100)・x−dx・x−dx×(1−rgb÷100) から横の線形補間で読み(枠の内側で wrap。§9.7 の規則 12 の (d))、α は 3 つの平均。
// 枠の外の画素は変えない。定数バッファは bind の順に {intensity, steps, bands, rgb, t, cycles, phase, seed, salt, frame}(intensity は scale の実効値)。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 intensity;
    float4 steps;
    float4 bands;
    float4 rgb;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 seed;
    uint4 salt;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 here = LoadHere(Input0, pos);
    if (!InFrame(int2(pos.xy), frame)) return here;
    float inten = clamp(intensity.x, 0.0, 100.0) / 100.0;
    uint k = uint(clamp(steps.x, 2.0, 24.0) + 0.5);
    uint nb = uint(clamp(bands.x, 1.0, 8.0) + 0.5);
    float split = clamp(rgb.x, 0.0, 100.0) / 100.0;
    float2 size = max(frame.zw, float2(1.0, 1.0));
    float2 local = ToFrame(pos.xy, frame);
    uint j = LatticeStep(MotionPsi(t.x, cycles.x, phase.x), k, cycles.x);
    uint k0 = H2(seed.x, salt.x);
    bool hit = false;
    float dx = 0.0;
    [loop]
    for (uint b = 0u; b < nb; b++)
    {
        uint id = j * 16u + b;
        if (U01(H3(k0, 31u, id)) >= 0.25 + 0.5 * inten) continue;
        float y0 = size.y * U01(H3(k0, 32u, id));
        float hh = size.y * (0.02 + 0.12 * U01(H3(k0, 33u, id)));
        if (local.y >= y0 && local.y < y0 + hh)
        {
            // 番号の大きい帯が勝つ(後から見つけた帯で上書きする)
            hit = true;
            dx = (2.0 * U01(H3(k0, 34u, id)) - 1.0) * 0.2 * size.x * inten;
        }
    }
    if (!hit) return here;
    float4 pr = SampleWrapInFrame(Input0, float2(local.x - dx * (1.0 + split), local.y), frame);
    float4 pg = SampleWrapInFrame(Input0, float2(local.x - dx, local.y), frame);
    float4 pb = SampleWrapInFrame(Input0, float2(local.x - dx * (1.0 - split), local.y), frame);
    return float4(pr.r, pg.g, pb.b, (pr.a + pg.a + pb.a) / 3.0);
}
