// 線形光・プリマルチプライの RGBA16F を、straight の sRGB 8 ビット(B8G8R8A8_UNORM)へ変えるピクセルシェーダー。
// 画面の表示の読み戻しの最後のパス(§2 の「読み戻し: 線形 → sRGB 8bit」)。α が 0.0001 以下なら色は 0(§9.4)。
// enc(v) は §9.4 の式。各チャンネルを 0〜1 に切ってから変換する。

Texture2D<float4> Src : register(t0);

float enc1(float v)
{
    return v <= 0.0031308 ? 12.92 * v : 1.055 * pow(v, 1.0 / 2.4) - 0.055;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = Src.Load(int3(int2(pos.xy), 0));
    float a = saturate(p.a);
    float3 c = p.a > 0.0001 ? p.rgb / p.a : float3(0.0, 0.0, 0.0);
    c = saturate(c);
    return float4(enc1(c.r), enc1(c.g), enc1(c.b), a);
}
