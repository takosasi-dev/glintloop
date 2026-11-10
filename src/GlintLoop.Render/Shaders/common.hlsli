// エフェクトのパスのシェーダーが共有するインクルード(入力・サンプラー・定数バッファの規則と、§9.4 の記法の関数)。
// 使い方: 各 name.ps.hlsl の先頭で #include "common.hlsli" とし、下の「規則」に従って Params を宣言する。
// ここには main を置かない。ビルドは .hlsli をコンパイルの入力にせず、変わったときの作り直しの依存にだけ使う。

// ==== 規則(EffectConstants.cs と同じ内容。片方を変えたら両方を直す) ====
//
// 1. 入口は float4 main(float4 pos : SV_Position) : SV_Target。pos.xy は出力の画素の中心 (i+0.5, j+0.5)。
//    出力は線形光・プリマルチプライの RGBA(RGBA16F に書く)。最後の切り(RGB 0〜64・α 0〜1・NaN→0)と
//    mix の amount の適用は、共通のパス(effect_finish.ps)が行うので、エフェクトのシェーダーは行わない。
// 2. 入力: EffectDef の passes[].inputs の i 番目(0〜3)が t{i}(下の Input0〜Input3)。
//    src・orig・pass:id はどれも線形光・プリマルチプライの RGBA。aux は補助のビットマップ(同じ形式)。
//    サンプラーは s0 が点(端を延ばす)、s1 が双線形(端を延ばす)。画素を読むだけなら Load か LoadClamp を使う。
// 3. 定数バッファ: cbuffer Params : register(b0) に、passes[].bind の左辺を、JSON に書いた順に 1 つずつ宣言する。
//    どの変数も 4 成分の型(float4 か uint4)にし、1 つが 16 バイトの 1 枠を占める(詰め物は要らない)。
//    名前は bind の左辺と同じにする(C# 側は順番だけで詰めるので、名前は読み手のため)。型と中身は次のとおり。
//      右辺がパラメータのとき(値は定義の単位のまま: ‰ は ‰、% は 0〜100、deg は度、EV は EV。scale モードでは実効値)
//        float・int・angle → float4(値, 0, 0, 0)
//        bool              → uint4(偽 0 / 真 1, 0, 0, 0)
//        enum              → uint4(options の中の番号(0 から), 0, 0, 0)
//        color             → float4(lin(R), lin(G), lin(B), AA÷255)。straight の線形 RGB と不透明度
//        point             → float4(x, y, 0, 0)。画像の幅と高さを 0〜1 にした値
//        rect              → float4(l, t, r, b)。定義の単位のまま
//        text・font        → 渡せない(実行時に例外)
//      右辺が組み込み変数のとき
//        $width $height $shortSide → float4(画素数, 0, 0, 0)。このパスの最初の入力(入力が無いパスはエフェクトの入力)の大きさ。
//                                    ただしモーションステージのパスでは、枠の Wl・Hl・min(Wl, Hl)(§9.7 の規則 12 の (a)。Q-A)
//        $texel                    → float4(1÷幅, 1÷高さ, 幅, 高さ)。同じ入力の 1 画素の大きさ(モーションステージでも入力のまま)
//        $frame                    → float4(qx, qy, Wl, Hl)。レイヤーのモーションステージでは枠 Q(作業面の画素)、
//                                    それ以外のステージと全体のモーションでは (0, 0, 入力の幅, 入力の高さ)。枠の座標と標本化は motion.hlsli
//                                    (scale が 1 でないパスでは、枠をパスの入力÷エフェクトの入力の比で縮めた値。$width なども同じ)
//        $amount $t $phase         → float4(値, 0, 0, 0)。amount は 0〜1 の実効値、t は k÷N、phase は 0〜100(%)
//        $k $n $cycles $wave $seed $salt → uint4(値, 0, 0, 0)。wave は sine 0・tri 1・ease 2・saw 3
//    例(exposure): "bind": {"ev": "ev", "amount": "$amount"} → cbuffer Params : register(b0) { float4 ev; float4 amount; };
// 4. 出力の大きさは、エフェクトの入力の幅と高さに passes[].scale を掛けて切り上げたもの(1 以上)。

Texture2D<float4> Input0 : register(t0);
Texture2D<float4> Input1 : register(t1);
Texture2D<float4> Input2 : register(t2);
Texture2D<float4> Input3 : register(t3);
SamplerState PointClamp : register(s0);
SamplerState LinearClamp : register(s1);

// ==== 画素の読み出し ====

// 画素 (i, j) を読む。範囲の外は最も近い画素(端を延ばす)。
float4 LoadClamp(Texture2D<float4> tex, int2 p)
{
    uint w, h;
    tex.GetDimensions(w, h);
    int2 q = clamp(p, int2(0, 0), int2(int(w) - 1, int(h) - 1));
    return tex.Load(int3(q, 0));
}

// 出力の画素と同じ位置の画素を読む(scale が 1 のパス用)。
float4 LoadHere(Texture2D<float4> tex, float4 pos)
{
    return tex.Load(int3(int2(pos.xy), 0));
}

// ==== RGBA16F への書き込みの直前の丸め ====

// 各成分を最も近い half(同じ距離なら偶数の側)へ丸めた値を返す。WARP は RGBA16F への書き込みで 0 の向きに切り捨てるので、
// 先に half で表せる値にしておき、書き込みで値が変わらないようにする(AC-12 の往復の一致)。
// RGBA16F に直接書く土台のパス(ingest・effect_finish・shape_fold・box_half・layer_place・layer_blend)だけで使う。エフェクトのシェーダーでは使わない(例外は halftone_h・halftone_v)。
// f16tof32(f32tof16(x)) は使えない: WARP の f32tof16 も 0 の向きに切り捨てる(実測)。そのためビットの演算で丸める。
// 前提は |x| が 65504 以下の有限の値(呼ぶ側は切ったあとの値を渡す)。±Inf と NaN はそのまま返す。
float4 RoundToHalf(float4 p)
{
    uint4 u = asuint(p);
    uint4 sign = u & 0x80000000u;
    uint4 a = u & 0x7FFFFFFFu;
    // half の正規数の範囲(2^-14 以上): float の仮数の下 13 ビットを、偶数への丸めで落とす(繰り上がりは指数へ進む)
    uint4 normal = (a + 0x0FFFu + ((a >> 13) & 1u)) & ~0x1FFFu;
    // half の非正規数の範囲(2^-14 未満): 2^-24 の倍数へ丸める。round は偶数への丸め(round_ne)。2 の累乗の掛け算は誤差が無い
    uint4 tiny = asuint(round(asfloat(a) * 16777216.0) * (1.0 / 16777216.0));
    uint4 r = a >= 0x38800000u ? normal : tiny;
    r = a >= 0x7F800000u ? a : r;
    return asfloat(r | sign);
}

// ==== §9.4 の記法 ====

// lin(c)・enc(v)。enc は 0〜1 に切ってから変換する(「sRGB で」の約束)。
float lin1(float c)
{
    return c <= 0.04045 ? c / 12.92 : pow((max(c, 0.0) + 0.055) / 1.055, 2.4);
}

float enc1(float v)
{
    float x = saturate(v);
    return x <= 0.0031308 ? 12.92 * x : 1.055 * pow(x, 1.0 / 2.4) - 0.055;
}

float3 lin3(float3 c) { return float3(lin1(c.r), lin1(c.g), lin1(c.b)); }
float3 enc3(float3 v) { return float3(enc1(v.r), enc1(v.g), enc1(v.b)); }

// P=(C×α, α) から straight の C を取り出す。α が 0.0001 以下なら 0。
float3 Straight(float4 p)
{
    return p.a > 0.0001 ? p.rgb / p.a : float3(0.0, 0.0, 0.0);
}

// straight の C と α からプリマルチプライの P を作る。
float4 Premul(float3 c, float a)
{
    return float4(c * a, a);
}

// Y(C) と E(C)=enc(sat(Y(C)))。
float LumaY(float3 c) { return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b; }
float LumaE(float3 c) { return enc1(saturate(LumaY(c))); }

// S(x)=x×x×(3−2x)。SmoothStepSpec は §9.4 の smoothstep(e0≥e1 のときは x≥e1 で 1、それ以外は 0)。
float S(float x) { return x * x * (3.0 - 2.0 * x); }

float SmoothStepSpec(float e0, float e1, float x)
{
    if (e0 >= e1) return x >= e1 ? 1.0 : 0.0;
    float t = saturate((x - e0) / (e1 - e0));
    return t * t * (3.0 - 2.0 * t);
}

// px(v)=v×S0÷1000(‰ を画素に)。shortSide は $shortSide の x。
float Px(float permille, float shortSide) { return permille * shortSide / 1000.0; }

// turn(θ, v)。θ はラジアン。y が下向きなので θ が正なら画面の上で時計回り。
float2 Turn(float theta, float2 v)
{
    float c = cos(theta);
    float s = sin(theta);
    return float2(v.x * c - v.y * s, v.x * s + v.y * c);
}

// hash32・H・H3・u01(§9.4。32 ビット符号なしの演算で、桁あふれは捨てる)。
uint Hash32(uint x)
{
    x ^= x >> 16;
    x *= 0x85EBCA6Bu;
    x ^= x >> 13;
    x *= 0xC2B2AE35u;
    x ^= x >> 16;
    return x;
}

uint H2(uint a, uint b) { return Hash32(a + Hash32(b + 0x9E3779B9u)); }
uint H3(uint a, uint b, uint c) { return H2(H2(a, b), c); }
float U01(uint h) { return float(h >> 8) / 16777216.0; }

// Oklab(§9.4)。fromOk のあと負になったチャンネルは 0 に切る(上は切らない)。
// 負の値も符号を保って立方根を取る。三項演算子で pow の負の底を書くと fxc が X3571 を出してビルドが止まるので、abs と sign で書く。
float Cbrt(float x) { return sign(x) * pow(abs(x), 1.0 / 3.0); }

float3 ToOk(float3 c)
{
    float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
    float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
    float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
    float l1 = Cbrt(l);
    float m1 = Cbrt(m);
    float s1 = Cbrt(s);
    return float3(
        0.2104542553 * l1 + 0.7936177850 * m1 - 0.0040720468 * s1,
        1.9779984951 * l1 - 2.4285922050 * m1 + 0.4505937099 * s1,
        0.0259040371 * l1 + 0.7827717662 * m1 - 0.8086757660 * s1);
}

float3 FromOk(float3 lab)
{
    float l1 = lab.x + 0.3963377774 * lab.y + 0.2158037573 * lab.z;
    float m1 = lab.x - 0.1055613458 * lab.y - 0.0638541728 * lab.z;
    float s1 = lab.x - 0.0894841775 * lab.y - 1.2914855480 * lab.z;
    float l = l1 * l1 * l1;
    float m = m1 * m1 * m1;
    float s = s1 * s1 * s1;
    float3 c = float3(
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    return max(c, float3(0.0, 0.0, 0.0));
}
