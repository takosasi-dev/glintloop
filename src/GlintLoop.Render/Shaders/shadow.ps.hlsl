// 仕上げの影(shadow、§9.4 の「縁と文字」): As(x) は Blur(α, px(blur)÷2)(t1)を位置 x−(px(dx), px(dy)) から双線形補間で読んだもの
// (ここだけキャンバスの外は透明)。a=color の不透明度×As、L=(lin(color.rgb)×a, a)、画像の下に敷く: P'=P+(1−α)×L。
// 入力 t0 は src、t1 は pass:v。定数バッファは bind の順に {dx, dy, color, shortSide($shortSide)}。範囲外は表の範囲に切る。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 dx;
    float4 dy;
    float4 color;
    float4 shortSide;
};

// t1 の位置 u の双線形補間。キャンバスの外のタップは 0。
float BlurredAlphaAt(float2 u, int2 dims)
{
    float2 t = u - 0.5;
    float2 f = floor(t);
    float2 a = t - f;
    int2 i0 = int2(f);
    float v[4];
    [unroll]
    for (int k = 0; k < 4; k++)
    {
        int2 q = i0 + int2(k & 1, k >> 1);
        bool inside = q.x >= 0 && q.y >= 0 && q.x < dims.x && q.y < dims.y;
        v[k] = inside ? Input1.Load(int3(q, 0)).r : 0.0;
    }
    float top = v[0] + (v[1] - v[0]) * a.x;
    float bottom = v[2] + (v[3] - v[2]) * a.x;
    return top + (bottom - top) * a.y;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    Input1.GetDimensions(w, h);
    float4 p = LoadHere(Input0, pos);
    float2 off = float2(Px(clamp(dx.x, -100.0, 100.0), shortSide.x), Px(clamp(dy.x, -100.0, 100.0), shortSide.x));
    float as = BlurredAlphaAt(pos.xy - off, int2(int(w), int(h)));
    float a = saturate(color.a) * as;
    return p + (1.0 - p.a) * float4(color.rgb * a, a);
}
