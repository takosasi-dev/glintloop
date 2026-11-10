// 仕上げの縁取り(outline、§9.4 の「縁と文字」): sdα を edge.ps と同じ手順で半径 R の中だけ正確に求め、帯 [lo, hi] の被覆 cov を作る。
// cov=sat((sd−lo)÷aa+0.5)×sat((hi−sd)÷aa+0.5)、aa=max(1, px(soft)×2)、L=(lin(color)×a, a)、a=color の不透明度×cov。outside は P+(1−α)L、inside・center は L+(1−L.a)P。
// 入力 t0 は src、t1 は pass:rows(edge_rows を shift=width・feather=soft で共用)。bind の順は {width, pos, color, soft, shortSide}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 width;
    uint4 pos_;
    float4 color;
    float4 soft;
    float4 shortSide;
};

// edge.hlsli の EdgeNone と同じ値(edge_rows の「見つからない」)。
static const float OutlineNone = 30000.0;

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input0.GetDimensions(w, h);
    int2 p = int2(pos.xy);
    float r = Px(clamp(width.x, 0.0, 80.0), shortSide.x);
    float aa = max(1.0, Px(clamp(soft.x, 0.0, 30.0), shortSide.x) * 2.0);
    // edge.hlsli の EdgeRadius と同じ式(edge_rows に shift=width・feather=soft を渡すので、行の探索と同じ半径になる)。
    // |sd| が r+aa÷2 を超えると cov は 0 なので、R の外の距離は求めなくてよい
    int rr = int(ceil(abs(r) + aa * 0.5)) + 1;
    float4 here = Input0.Load(int3(p, 0));
    bool hereA = here.a >= 0.5;
    float bestA = OutlineNone * OutlineNone;
    float bestB = OutlineNone * OutlineNone;
    [loop]
    for (int dy = -rr; dy <= rr; dy++)
    {
        int y = p.y + dy;
        float dy2 = float(dy * dy);
        if (y < 0 || y >= int(h))
        {
            bestB = min(bestB, dy2); // キャンバスの外の行は、どの画素も B
            continue;
        }
        float2 g = Input1.Load(int3(p.x, y, 0)).xy;
        if (g.x != OutlineNone) bestA = min(bestA, g.x * g.x + dy2);
        if (g.y != OutlineNone) bestB = min(bestB, g.y * g.y + dy2);
    }
    float sd;
    if (hereA) sd = bestB < OutlineNone * OutlineNone ? -sqrt(bestB) : -1e4;
    else sd = bestA < OutlineNone * OutlineNone ? sqrt(bestA) : 1e4;

    float lo = 0.0;
    float hi = r;
    if (pos_.x == 1u)
    {
        lo = -r;
        hi = 0.0;
    }
    else if (pos_.x == 2u)
    {
        lo = -r * 0.5;
        hi = r * 0.5;
    }
    float cov = saturate((sd - lo) / aa + 0.5) * saturate((hi - sd) / aa + 0.5);
    float a = saturate(color.a) * cov;
    float4 ring = float4(color.rgb * a, a);
    if (pos_.x == 0u) return here + (1.0 - here.a) * ring;
    return ring + (1.0 - ring.a) * here;
}
