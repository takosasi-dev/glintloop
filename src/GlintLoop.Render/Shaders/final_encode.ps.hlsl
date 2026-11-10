// 最後の変換(§9.7 の規則 7): 線形光・プリマルチプライの RGBA16F を、青色雑音で量子化した 8 ビットへ変える(R8G8B8A8_UINT に書く)。
// Mode.x が 0 なら書き出し用の straight RGBA、1 なら表示用の Pbgra32(straight の結果の各色に α÷255 を掛けて round、B・G・R・A の並び)。
// q=floor(255×enc(sat(c))+0.5+n)、n=((bn(i mod 64, j mod 64)+0.5)÷4096−0.5)×0.9、α は floor(255α+0.5)、α≤0.0001 は全部 0。
#include "common.hlsli"

Texture2D<uint> BlueNoiseTile : register(t4);

cbuffer Params : register(b0)
{
    float4 Mode;
};

uint4 main(float4 pos : SV_Position) : SV_Target
{
    int2 ij = int2(pos.xy);
    float4 p = Input0.Load(int3(ij, 0));
    if (!(p.a > 0.0001))
    {
        return uint4(0, 0, 0, 0);
    }
    float a = saturate(p.a);
    float3 c = saturate(p.rgb / p.a);
    float bn = float(BlueNoiseTile.Load(int3(ij & 63, 0)));
    float n = ((bn + 0.5) / 4096.0 - 0.5) * 0.9;
    float3 qf = clamp(floor(255.0 * enc3(c) + 0.5 + n), 0.0, 255.0);
    uint3 q = uint3(qf);
    uint qa = uint(clamp(floor(255.0 * a + 0.5), 0.0, 255.0));
    if (Mode.x < 0.5)
    {
        return uint4(q, qa);
    }
    // round(q×qa÷255) を整数で: floor((2×q×qa + 255) ÷ 510)
    uint3 pm = (2u * q * qa + 255u) / 510u;
    return uint4(pm.b, pm.g, pm.r, qa);
}
