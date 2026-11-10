// 形ステージの折りたたみ(§9.7 の規則 3): 回転・切り抜き・余白を 1 つのアフィン変換にまとめ、ソースから 1 回だけ再標本化する。
// ds≤1 はキャッチマル・ロム(4×4)、ds>1 は三角窓(各軸 |d|<ds、正規化)。透明と余白は幾何の覆いの率 covR・covC で決める。
// 出力 P=Pc×min(covR, covC)+F×(1−covC) を切り(RGB 0〜64・α 0〜1・NaN→0)、最も近い half に丸めて書く。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 Lin;      // 出力の画素の位置 x → ソースの位置 p = Lin.xy・x の行列 ((x, y), (z, w)) と Off
    float4 Off;      // xy: p の平行移動。z: ソースの位置からテクスチャの画素への倍率(先に 1/2 に縮めたときは 1/2^m)。w: テクスチャの画素の ds
    float4 SrcRect;  // xy: ソースの幅と高さ(画素)。z・w: p.x・p.y の勾配の長さの逆数(出力の画素の距離に直す)
    float4 CropRect; // 切り抜いたあとのキャンバス(余白を足す前)の出力の画素での l・t・r・b
    float4 Fill;     // 余白の色(線形光・プリマルチプライ)
    uint4 Mode;      // x: 0=キャッチマル・ロム、1=三角窓。y: 1 ならテクスチャはプリマルチプライ済み(RGBA16F)、0 なら straight(sRGB)。z: 1 なら一様なソース
};

float4 Tap(int2 q, int2 dims)
{
    float4 s = Input0.Load(int3(clamp(q, int2(0, 0), dims - int2(1, 1)), 0));
    return Mode.y != 0u ? s : Premul(s.rgb, s.a);
}

// キャッチマル・ロムの 4 つの重み(t は 0 以上 1 未満)。t=0 で (0, 1, 0, 0) になる。
float4 CatmullRom(float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return float4(
        -0.5 * t3 + t2 - 0.5 * t,
        1.5 * t3 - 2.5 * t2 + 1.0,
        -1.5 * t3 + 2.0 * t2 + 0.5 * t,
        0.5 * t3 - 0.5 * t2);
}

float4 SampleCatmullRom(float2 pt, int2 dims)
{
    float2 f = pt - 0.5;
    float2 i0 = floor(f);
    float2 t = f - i0;
    int2 base = int2(i0) - int2(1, 1);
    float4 wx = CatmullRom(t.x);
    float4 wy = CatmullRom(t.y);
    float4 acc = float4(0.0, 0.0, 0.0, 0.0);
    [unroll]
    for (int j = 0; j < 4; j++)
    {
        float4 row = float4(0.0, 0.0, 0.0, 0.0);
        [unroll]
        for (int i = 0; i < 4; i++)
        {
            row += wx[i] * Tap(base + int2(i, j), dims);
        }
        acc += wy[j] * row;
    }
    return max(acc, float4(0.0, 0.0, 0.0, 0.0));
}

float4 SampleTriangle(float2 pt, float ds, int2 dims)
{
    // 画素 i の中心 i+0.5 と pt の差 d が |d|<ds のものだけ。1 軸のタップ数は 2×ceil(ds)+1 以下
    int2 lo = int2(ceil(pt - ds - 0.5));
    int2 hi = int2(floor(pt + ds - 0.5));
    float4 acc = float4(0.0, 0.0, 0.0, 0.0);
    float wsum = 0.0;
    [loop]
    for (int y = lo.y; y <= hi.y; y++)
    {
        float wy = 1.0 - abs(float(y) + 0.5 - pt.y) / ds;
        if (wy <= 0.0) continue;
        [loop]
        for (int x = lo.x; x <= hi.x; x++)
        {
            float wx = 1.0 - abs(float(x) + 0.5 - pt.x) / ds;
            if (wx <= 0.0) continue;
            float w = wx * wy;
            acc += w * Tap(int2(x, y), dims);
            wsum += w;
        }
    }
    return wsum > 0.0 ? acc / wsum : Tap(int2(floor(pt)), dims);
}

float Cov(float4 d)
{
    float4 c = saturate(0.5 + d);
    return c.x * c.y * c.z * c.w;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float2 x = pos.xy;
    float2 p = float2(dot(Lin.xy, x), dot(Lin.zw, x)) + Off.xy;

    uint w, h;
    Input0.GetDimensions(w, h);
    int2 dims = int2(int(w), int(h));

    float4 pc;
    if (Mode.z != 0u)
    {
        pc = Tap(int2(0, 0), dims);
    }
    else if (Mode.x == 0u)
    {
        pc = SampleCatmullRom(p * Off.z, dims);
    }
    else
    {
        pc = SampleTriangle(p * Off.z, Off.w, dims);
    }

    // 回した元画像の矩形: p の各辺までの距離を出力の画素に直す(内側が正)
    float covR = Cov(float4(p.x * SrcRect.z, p.y * SrcRect.w, (SrcRect.x - p.x) * SrcRect.z, (SrcRect.y - p.y) * SrcRect.w));
    // 切り抜いたあとのキャンバス(出力の座標で軸に平行)
    float covC = Cov(float4(x.x - CropRect.x, x.y - CropRect.y, CropRect.z - x.x, CropRect.w - x.y));
    float covI = min(covR, covC);
    float4 o = pc * covI + Fill * (1.0 - covC);
    float3 rgb = min(max(o.rgb, float3(0.0, 0.0, 0.0)), float3(64.0, 64.0, 64.0));
    float a = min(max(o.a, 0.0), 1.0);
    return RoundToHalf(float4(rgb, a));
}
