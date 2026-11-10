// 輪郭の調整(edge、§9.4 の「輪郭」)の 2 つ目のパス: 行の結果を列に沿ってまとめ、sdα(内側が負)を半径 R の中で正確に求める。
// α'=sat(0.5−(sdα−px(shift))÷max(1, px(feather)×2))。元が B(α<0.5)の画素の色は最も近い A の画素の色、A の画素は元の色にする。
// 入力 t0 は src、t1 は pass:rows。キャンバスの外の行はすべて B として扱う。A が見つからない B は sdα=1e4、B が見つからない A は −1e4。
#include "common.hlsli"
#include "edge.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input0.GetDimensions(w, h);
    int2 p = int2(pos.xy);
    int r = EdgeRadius();
    float4 here = Input0.Load(int3(p, 0));
    bool hereA = here.a >= 0.5;
    float bestA = EdgeNone * EdgeNone;
    int2 nearA = p;
    float bestB = EdgeNone * EdgeNone;
    [loop]
    for (int dy = -r; dy <= r; dy++)
    {
        int y = p.y + dy;
        float dy2 = float(dy * dy);
        if (y < 0 || y >= int(h))
        {
            // キャンバスの外の行は、どの画素も B
            bestB = min(bestB, dy2);
            continue;
        }
        float2 g = Input1.Load(int3(p.x, y, 0)).xy;
        if (g.x != EdgeNone)
        {
            float d2 = g.x * g.x + dy2;
            if (d2 < bestA)
            {
                bestA = d2;
                nearA = int2(p.x + int(g.x), y);
            }
        }
        if (g.y != EdgeNone) bestB = min(bestB, g.y * g.y + dy2);
    }
    float sd;
    if (hereA) sd = bestB < EdgeNone * EdgeNone ? -sqrt(bestB) : -1e4;
    else sd = bestA < EdgeNone * EdgeNone ? sqrt(bestA) : 1e4;
    float a1 = saturate(0.5 - (sd - Px(shift.x, shortSide.x)) / EdgeWidth());
    float3 c = hereA ? Straight(here) : Straight(Input0.Load(int3(nearA, 0)));
    return Premul(c, a1);
}
