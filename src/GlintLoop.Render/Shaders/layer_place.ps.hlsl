// レイヤーの静的な結果を合成の作業面に置く(§9.7 の規則 11)。枠 Q=(qx, qy, Wl, Hl) の内側だけに写し、外は (0, 0, 0, 0)。
// 縮めていない(n=0)ときは画素をそのまま写す(拡大縮小も補間もしない)。n≥1 のときは wr×hr を Wl×Hl へ双線形補間で拡大する
// (画素の中心 (i+0.5, j+0.5) の対応、端を延ばす)。値はプリマルチプライのまま、最も近い half に丸めて書く。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    int4 Frame;   // qx, qy, Wl, Hl
    uint4 Mode;   // x: 1 なら縮めた画像(双線形で拡大)
};

float4 main(float4 pos : SV_Position) : SV_Target
{
    int2 a = int2(pos.xy) - Frame.xy;
    if (a.x < 0 || a.y < 0 || a.x >= Frame.z || a.y >= Frame.w)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }
    if (Mode.x == 0u)
    {
        return Input0.Load(int3(a, 0));
    }
    uint w, h;
    Input0.GetDimensions(w, h);
    float2 u = (float2(a) + 0.5) * float2(float(w), float(h)) / float2(Frame.zw) - 0.5;
    float2 f = floor(u);
    float2 t = u - f;
    int2 i0 = int2(f);
    float4 p00 = LoadClamp(Input0, i0);
    float4 p10 = LoadClamp(Input0, i0 + int2(1, 0));
    float4 p01 = LoadClamp(Input0, i0 + int2(0, 1));
    float4 p11 = LoadClamp(Input0, i0 + int2(1, 1));
    float4 top = p00 + (p10 - p00) * t.x;
    float4 bottom = p01 + (p11 - p01) * t.x;
    return RoundToHalf(top + (bottom - top) * t.y);
}
