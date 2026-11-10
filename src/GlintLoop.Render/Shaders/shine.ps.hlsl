// モーションのキラッ(shine、§9.5 の色と光。波形なし): n=(cos angle, sin angle)、枠の 4 隅の dot(隅, n) の最小 pmin・最大 pmax、hw=px(width)÷2、
// a=sat(u÷(1−pause÷100))、pc=mix(pmin−hw, pmax+hw, S(a))、b=1−smoothstep(hw×(1−soft÷100), hw, |dot(x, n)−pc|)、P'.rgb=P.rgb+intensity÷100×b×lin(color)×不透明度×α。
// x と隅は枠の左上が原点(§9.7 の規則 12 の (b))、S0 は枠の短辺。定数バッファは bind の順に {angle, width, soft, intensity, color, pause, t, cycles, phase, frame}。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 angle;
    float4 width;
    float4 soft;
    float4 intensity;
    float4 color;
    float4 pause;
    float4 t;
    uint4 cycles;
    float4 phase;
    float4 frame;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float u = MotionU(MotionPsi(t.x, cycles.x, phase.x));
    float th = radians(clamp(angle.x, -90.0, 90.0));
    float2 n = float2(cos(th), sin(th));
    // 隅 (0,0)・(W,0)・(0,H)・(W,H) の dot
    float cx = frame.z * n.x;
    float cy = frame.w * n.y;
    float pmin = min(min(0.0, cx), min(cy, cx + cy));
    float pmax = max(max(0.0, cx), max(cy, cx + cy));
    float hw = Px(clamp(width.x, 20.0, 500.0), FrameShort(frame)) * 0.5;
    float a = saturate(u / (1.0 - clamp(pause.x, 0.0, 80.0) / 100.0));
    float pc = lerp(pmin - hw, pmax + hw, S(a));
    float d = abs(dot(ToFrame(pos.xy, frame), n) - pc);
    float b = 1.0 - SmoothStepSpec(hw * (1.0 - clamp(soft.x, 0.0, 100.0) / 100.0), hw, d);
    float k = clamp(intensity.x, 0.0, 300.0) / 100.0 * b * saturate(color.a) * p.a;
    return float4(p.rgb + k * color.rgb, p.a);
}
