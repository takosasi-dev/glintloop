// 各エフェクトの出力に共通で掛けるパス(§9.7 の規則 4 の 3 と 4)。mix の amount を適用し、RGB を 0〜64・α を 0〜1 に切り、NaN を 0 にする。
// t0 がエフェクトへの入力 P_in、t1 がエフェクトの出力 P_fx(同じ大きさ)。Mode.x が実効 amount、Mode.y が 1 なら mix、0 なら切るだけ。
// max(x, 0) で NaN が 0 になる(D3D11 の max・min は NaN でない方を返す)。切ったあと RoundToHalf で最も近い half に丸めて書く。
#include "common.hlsli"

cbuffer Params : register(b0)
{
    float4 Mode;
};

float4 ClampOut(float4 p)
{
    float3 rgb = min(max(p.rgb, float3(0.0, 0.0, 0.0)), float3(64.0, 64.0, 64.0));
    float a = min(max(p.a, 0.0), 1.0);
    return float4(rgb, a);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 fx = ClampOut(LoadHere(Input1, pos));
    if (Mode.y < 0.5)
    {
        return RoundToHalf(fx);
    }
    float4 pin = ClampOut(LoadHere(Input0, pos));
    float a = Mode.x;
    // P_out = P_in + a×(P_fx − P_in) を、a=0 と a=1 で端の値に正確に一致する形で計算する
    // 0・1・64 は half で表せるので、切ったあとに丸めても切りの範囲を外れない
    return RoundToHalf(ClampOut(pin * (1.0 - a) + fx * a));
}
