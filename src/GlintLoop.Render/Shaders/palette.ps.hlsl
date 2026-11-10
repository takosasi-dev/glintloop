// ルックのパレット減色(palette、§9.4 の「表現」): x=enc(C) にベイヤー行列 bm(i mod 4, j mod 4) のディザを足して 0〜1 に切り、
// パレットの色から Oklab の二乗距離が最も近い色(同じ距離なら番号の小さい色)を選んで lin で返す。α は変えない。
// パレットの色は §9.4 の表の sRGB の 8 ビットの値を定数で持ち、lin で線形にする。bind の順は {"kind": "kind", "dither": "dither"}。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    uint4 kind;
    float4 dither;
};

// 色の並び(番号 0 から)。p16 は 0〜15、g4 は 16〜19、bw は 20〜21、sepia4 は 22〜25。
static const float3 PaletteSrgb8[26] =
{
    float3(0x14, 0x12, 0x1F), float3(0x3A, 0x3A, 0x5C), float3(0x8C, 0x8F, 0xAE), float3(0xF2, 0xF0, 0xE6),
    float3(0x7A, 0x1F, 0x3D), float3(0xD6, 0x33, 0x4A), float3(0xF2, 0x8C, 0x28), float3(0xF7, 0xD1, 0x54),
    float3(0x7C, 0xBF, 0x3F), float3(0x1F, 0x8A, 0x5B), float3(0x2B, 0xB5, 0xB0), float3(0x3F, 0x8F, 0xE0),
    float3(0x2C, 0x3E, 0xA8), float3(0x7C, 0x4D, 0xD6), float3(0xE0, 0x6B, 0xC0), float3(0x8A, 0x5A, 0x3C),
    float3(0x0B, 0x1F, 0x10), float3(0x2F, 0x5D, 0x3A), float3(0x86, 0xB3, 0x6B), float3(0xE4, 0xF2, 0xC8),
    float3(0x00, 0x00, 0x00), float3(0xFF, 0xFF, 0xFF),
    float3(0x1E, 0x14, 0x10), float3(0x5C, 0x40, 0x30), float3(0xB0, 0x8A, 0x66), float3(0xF1, 0xE2, 0xC6),
};

// kind の番号(p16・g4・bw・sepia4)ごとの、先頭の番号と色の数。
static const uint PaletteOffset[4] = { 0, 16, 20, 22 };
static const uint PaletteCount[4] = { 16, 4, 2, 4 };

// ベイヤー行列 bm。[j×4+i] が bm(i, j)(i が列、j が行)。
static const float Bayer4[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    int2 ij = int2(pos.xy);
    float bm = Bayer4[(ij.y & 3) * 4 + (ij.x & 3)];
    float amp = clamp(dither.x, 0.0, 100.0) / 100.0 * 0.25;
    float3 x = saturate(enc3(Straight(p)) + (bm / 16.0 + 1.0 / 32.0 - 0.5) * amp);
    float3 lab = ToOk(lin3(x));

    uint k = min(kind.x, 3u);
    uint first = PaletteOffset[k];
    uint count = PaletteCount[k];
    float best = 3.402823466e38;
    float3 chosen = float3(0.0, 0.0, 0.0);
    [loop]
    for (uint i = 0; i < count; i++)
    {
        float3 c = lin3(PaletteSrgb8[first + i] / 255.0);
        float3 d = ToOk(c) - lab;
        float dist = dot(d, d);
        if (dist < best)
        {
            best = dist;
            chosen = c;
        }
    }
    return Premul(chosen, p.a);
}
