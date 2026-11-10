// モーションのバウンス(bounce、§9.5 の動き): 波形なし。h=4×u×(1−u)、持ち上げ offset=(0, −px(height)×h)、q=squash÷100×(1−smoothstep(0, 0.2, h))、
// sx=1+0.5×q、sy=max(1−q, 0.02)。pivot は枠の下端中央 (0.5, 1.0) で固定。xs=M⁻¹(x) をキャッチマル・ロムで読み、作業面の外は透明(規則 12 の (c))。
// 定数バッファは bind の順に {height, squash, t($t), cycles($cycles), phase($phase), frame($frame)}。px の S0 は枠の短辺(規則 12 の (a))。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 height;
    float4 squash;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float u = MotionU(MotionPsi(t.x, cycles.x, phase.x));
    float h = 4.0 * u * (1.0 - u);
    float lift = Px(height.x, FrameShort(frame)) * h;
    float q = squash.x / 100.0 * (1.0 - SmoothStepSpec(0.0, 0.2, h));
    float sx = 1.0 + 0.5 * q;
    float sy = max(1.0 - q, 0.02);
    float2 pivot = FramePoint(float2(0.5, 1.0), frame);
    float2 xs = MotionInverse(pos.xy, pivot, float2(0.0, -lift), 0.0, 0.0, sx, sy);
    return SampleCatRomTransparent(Input0, xs);
}
