// 仕上げのテキスト(text、§9.4 の「縁と文字」): CPU(SkiaSharp)で描いた文字のビットマップ T(aux。lin と不透明度にしたプリマルチプライ)を画像の上に重ねる。
// P'=T+(1−T.a)×P。入力 t0 は src、t1 は aux(同じ大きさ)。パラメータ(文字・位置・回転など)はビットマップを描く側で使うので、定数バッファは無い。
// 文字列が空のときは T が全画素 0 なので、P'=P になる。
#include "common.hlsli"

float4 main(float4 pos : SV_Position) : SV_Target
{
    float4 p = LoadHere(Input0, pos);
    float4 t = LoadHere(Input1, pos);
    return t + (1.0 - t.a) * p;
}
