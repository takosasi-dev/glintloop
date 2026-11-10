// 輪郭の調整(edge)の 1 つ目のパス: 行ごとに、半径 R の中で最も近い A(α≥0.5)の画素の符号付きの x のずれと、B(α<0.5)までの x の距離を求める。
// キャンバスの外はすべて B(左右の縁の外の画素までの距離 x+1 と W−x を B の候補に入れる)。見つからなければ EdgeNone。
// 入力 t0 は src。出力は (A へのずれ, B への距離, 0, 1)。整数で R は小さいので RGBA16F で正確に表せる。
#include "common.hlsli"
#include "edge.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input0.GetDimensions(w, h);
    int2 p = int2(pos.xy);
    int r = EdgeRadius();
    bool hereA = Input0.Load(int3(p, 0)).a >= 0.5;
    float gA = hereA ? 0.0 : EdgeNone;
    float gB = hereA ? min(float(p.x + 1), float(int(w) - p.x)) : 0.0;
    [loop]
    for (int d = 1; d <= r; d++)
    {
        // 左を先に見る(同じ距離なら左を選ぶ)
        int xl = p.x - d;
        int xr = p.x + d;
        bool inL = xl >= 0;
        bool inR = xr < int(w);
        bool aL = inL && Input0.Load(int3(xl, p.y, 0)).a >= 0.5;
        bool aR = inR && Input0.Load(int3(xr, p.y, 0)).a >= 0.5;
        if (gA == EdgeNone)
        {
            if (aL) gA = -float(d);
            else if (aR) gA = float(d);
        }
        if (hereA && gB > float(d) && ((inL && !aL) || (inR && !aR))) gB = float(d);
    }
    return float4(gA, min(gB, EdgeNone), 0.0, 1.0);
}
