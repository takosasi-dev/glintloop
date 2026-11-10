// ミニチュア(tiltshift)の 10 個のパス(tiltshift_h1〜h4・v1〜v4・lo・tiltshift)が共有する定数バッファと、σ の式と 1 次元ガウス。
// 4 段のぼかしの σ は σmax÷8・σmax÷4・σmax÷2・σmax(σmax=px(blur)÷2)。σ(x)=σmax×smoothstep(0, f, |v|−wh)(§9.4 の「ぼかし」)。
// 定数バッファは全パスとも bind の順に {angle, pos, width, blur, shortSide($shortSide), texel($texel)}。範囲外の値は表の範囲に切る。

cbuffer Params : register(b0)
{
    float4 angle;
    float4 pos;
    float4 width;
    float4 blur;
    float4 shortSide;
    float4 texel;
};

// σmax=px(blur)÷2。
float TiltSigmaMax()
{
    return Px(clamp(blur.x, 0.0, 100.0), shortSide.x) * 0.5;
}

// 段 level(0〜4)の σ。0 は入力そのもの。
float TiltLevelSigma(int level)
{
    float m = TiltSigmaMax();
    if (level <= 0) return 0.0;
    if (level == 1) return m * 0.125;
    if (level == 2) return m * 0.25;
    if (level == 3) return m * 0.5;
    return m;
}

// 画素の中心 x の σ(x)。W・H は texel.zw。
float TiltSigmaAt(float2 x)
{
    float th = radians(clamp(angle.x, -90.0, 90.0));
    float sn = sin(th);
    float cs = cos(th);
    float2 n = float2(-sn, cs);
    float en = texel.z * abs(sn) + texel.w * abs(cs);
    float2 base = texel.zw * 0.5 + n * ((clamp(pos.x, 0.0, 100.0) - 50.0) / 100.0 * en);
    float v = dot(x - base, n);
    float wh = clamp(width.x, 0.0, 80.0) / 100.0 * en * 0.5;
    float f = 0.25 * en;
    return TiltSigmaMax() * SmoothStepSpec(0.0, f, abs(v) - wh);
}

// σ の 1 次元ガウス(半幅 ceil(3σ)、合計 1 に正規化、端を延ばす)。axis は (1, 0) か (0, 1)。σ が 0.05 未満なら入力を返す。
float4 TiltGauss(int2 p, int2 axis, float sigma)
{
    float4 result = Input0.Load(int3(p, 0));
    if (sigma >= 0.05)
    {
        int2 last = int2(int(texel.z) - 1, int(texel.w) - 1);
        int r = int(ceil(3.0 * sigma));
        float k = -0.72134752 / (sigma * sigma); // −1÷(2σ²) に log2(e) を掛けたもの
        float4 sum = float4(0.0, 0.0, 0.0, 0.0);
        float wsum = 0.0;
        [loop]
        for (int d = -r; d <= r; d++)
        {
            float wt = exp2(k * float(d * d));
            sum += wt * Input0.Load(int3(clamp(p + axis * d, int2(0, 0), last), 0));
            wsum += wt;
        }
        result = sum / wsum;
    }
    return result;
}

// 隣り合う 2 段 a<b の重み w=sat((σ(x)−σa)÷max(σb−σa, 1e-6))。
float TiltWeight(float s, float sa, float sb)
{
    return saturate((s - sa) / max(sb - sa, 1e-6));
}
