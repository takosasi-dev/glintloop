// モーションの流れる(slide、§9.5 の動き): 1 サイクルで枠の 1 枚分動き、反対側から繰り返して読む(枠の内側で wrap。§9.7 の規則 12 の (d))。
// left は xs=(x+ψ×W) mod W、right は (x−ψ×W) mod W、up・down は y で同じ(W・H は枠の Wl・Hl、原点は枠の左上)。双線形補間。枠の外の画素は変えない。
// 定数バッファは bind の順に {dir, t($t), cycles($cycles), phase($phase), frame($frame)}。ψ×W は u=fract(ψ) で求める(周期 1 なので同じ位置)。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    uint4 dir;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    if (!InFrame(int2(pos.xy), frame)) return LoadHere(Input0, pos);
    float u = MotionU(MotionPsi(t.x, cycles.x, phase.x));
    float2 local = ToFrame(pos.xy, frame);
    float2 size = max(frame.zw, float2(1.0, 1.0));
    // dir: left 0・right 1・up 2・down 3
    if (dir.x == 0u) local.x = ModPos(local.x + u * size.x, size.x);
    else if (dir.x == 1u) local.x = ModPos(local.x - u * size.x, size.x);
    else if (dir.x == 2u) local.y = ModPos(local.y + u * size.y, size.y);
    else local.y = ModPos(local.y - u * size.y, size.y);
    return SampleWrapInFrame(Input0, local, frame);
}
