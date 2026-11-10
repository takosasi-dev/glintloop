// モーションの RGB ずれ(rgbsplit、§9.5 の乱れ。波形あり): o=px(amount)×W(ψ)×(cos angle, sin angle)。赤を x+o、緑を x、青を x−o から双線形で読み、
// 仕上げの aberration の linear と同じ合成 P'=(Pr.r, Pg.g, Pb.b, (Pr.a+Pg.a+Pb.a)÷3)。作業面の外は最も近い画素(clamp。§9.7 の規則 12 の (c))、S0 は枠の短辺。
// 定数バッファは bind の順に {amount, angle, shortSide($shortSide), t($t), cycles($cycles), phase($phase), wave($wave)}(amount は scale の実効値)。
#include "common.hlsli"
#include "motion.hlsli"

cbuffer Params : register(b0)
{
    float4 amount;
    float4 angle;
    float4 shortSide;
    float4 t;
    uint4 cycles;
    float4 phase;
    uint4 wave;
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    float w = Wave(MotionPsi(t.x, cycles.x, phase.x), wave.x);
    float th = radians(clamp(angle.x, -180.0, 180.0));
    float2 o = Px(clamp(amount.x, 0.0, 60.0), shortSide.x) * w * float2(cos(th), sin(th));
    float4 pr = SampleBilinearClamp(Input0, pos.xy + o);
    float4 pg = LoadHere(Input0, pos);
    float4 pb = SampleBilinearClamp(Input0, pos.xy - o);
    return float4(pr.r, pg.g, pb.b, (pr.a + pg.a + pb.a) / 3.0);
}
