// 形で抜く(mask、§9.4 の「輪郭」): 形の符号付き距離 sd(画素、内側が負)から cov=sat(0.5−sd÷w)(w=max(1, px(feather)))を求め P に掛ける。
// 形は center を中心に R=size÷100×S0÷2 で決める(角丸・円・楕円・スクワークル・ハート・星・六角形)。invert が真なら 1−cov を掛ける。
// 入力 t0 は src。定数バッファは bind の順に {kind, size, radius, inner, feather, center, invert, texel($texel)}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 kind;
    float4 size;
    float4 radius;
    float4 inner;
    float4 feather;
    float4 center;
    uint4 invert;
    float4 texel;
};

// 角丸の矩形(半幅 b、角の半径 r)。
float SdRoundRect(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return length(max(q, float2(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - r;
}

// (|x|÷a)^n+(|y|÷b)^n−1 を勾配の長さで割った近似の距離(n=4 はスクワークルで §9.4 の式、n=2 は楕円で仮に同じ形にした)。
float SdSuperEllipse(float2 p, float2 ab, float n)
{
    float2 u = abs(p) / ab;
    float f = pow(u.x, n) + pow(u.y, n) - 1.0;
    float2 g = n * pow(u, n - 1.0) / ab;
    return f / max(length(g), 1e-6);
}

// §9.4 のハート(45 度に回した正方形と 2 つの円の和集合)。R は画素。
float SdHeart(float2 p, float rr)
{
    float k = rr / 0.85355;
    float2 q = p / k;
    float2 s = float2(q.x, q.y - 0.07322);
    float dDiamond = (abs(s.x) + abs(s.y) - 0.70711) / 1.41421;
    float dC1 = length(s - float2(-0.35355, -0.35355)) - 0.5;
    float dC2 = length(s - float2(0.35355, -0.35355)) - 0.5;
    return min(dDiamond, min(dC1, dC2)) * k;
}

// 頂点 i の位置(角度 −90°+step×i、距離は偶数の頂点が r0、奇数が r1)。
float2 Vertex(int i, float stepDeg, float r0, float r1)
{
    float a = radians(-90.0 + stepDeg * float(i));
    float r = (i & 1) == 0 ? r0 : r1;
    return float2(cos(a), sin(a)) * r;
}

// 多角形の sd: 各辺への線分距離の最小値に、偶奇則で内側なら負の符号を付ける。
float SdPolygon(float2 p, int n, float stepDeg, float r0, float r1)
{
    float d2 = 1e30;
    bool inside = false;
    float2 vj = Vertex(n - 1, stepDeg, r0, r1);
    [loop]
    for (int i = 0; i < n; i++)
    {
        float2 vi = Vertex(i, stepDeg, r0, r1);
        float2 e = vj - vi;
        float2 w = p - vi;
        float2 b = w - e * saturate(dot(w, e) / dot(e, e));
        d2 = min(d2, dot(b, b));
        if ((vi.y > p.y) != (vj.y > p.y) && p.x < (vj.x - vi.x) * (p.y - vi.y) / (vj.y - vi.y) + vi.x)
            inside = !inside;
        vj = vi;
    }
    float d = sqrt(d2);
    return inside ? -d : d;
}

float main_sd(float2 p, float W, float H)
{
    float s0 = min(W, H);
    float rr = size.x / 100.0 * s0 / 2.0;
    float2 half_ = size.x / 100.0 * float2(W, H) / 2.0;
    switch (kind.x)
    {
        case 0u: return SdRoundRect(p, half_, min(Px(radius.x, s0), min(half_.x, half_.y)));
        case 1u: return length(p) - rr;
        case 2u: return SdSuperEllipse(p, half_, 2.0);
        case 3u: return SdSuperEllipse(p, half_, 4.0);
        case 4u: return SdHeart(p, rr);
        case 5u: return SdPolygon(p, 10, 36.0, rr, rr * inner.x / 100.0);
        default: return SdPolygon(p, 6, 60.0, rr, rr);
    }
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float W = texel.z;
    float H = texel.w;
    float2 c = center.xy * float2(W, H);
    float sd = main_sd(pos.xy - c, W, H);
    float w = max(1.0, Px(feather.x, min(W, H)));
    float cov = saturate(0.5 - sd / w);
    if (invert.x != 0u) cov = 1.0 - cov;
    return LoadHere(Input0, pos) * cov;
}
