// モーション(§9.5)のシェーダーが共有するインクルード: 時間の式 ψ・u、波形 W・W01、枠(§9.7 の規則 12)の座標、逆写像 M⁻¹、
// 外を透明にするキャッチマル・ロムと双線形、枠の内側で繰り返す双線形、乱数の格子の段。common.hlsli の後に #include する。
// 定数は bind の $t・$cycles・$phase・$wave・$frame・$seed・$salt で受ける(並びと型は common.hlsli の規則 3)。main は置かない。

// ==== 時間の式(§9.5) ====

// ψ=C×t+p(p=phase÷100)。t は $t.x、cycles は $cycles.x、phase は $phase.x(0〜100)。
float MotionPsi(float t, uint cycles, float phasePercent)
{
    return float(cycles) * t + phasePercent / 100.0;
}

// u=fract(ψ)。ψ は 0 以上。
float MotionU(float psi)
{
    return psi - floor(psi);
}

// sin(2πψ) と cos(2πψ)。三角関数の前に ψ を fract して小さくする(周期 1 なので同じ値で、cycles=8 の t=1 のような大きな角でも誤差が増えない)。
void SinCos2Pi(float psi, out float s, out float c)
{
    float a = 6.28318530718 * MotionU(psi);
    s = sin(a);
    c = cos(a);
}

// 角度 360°×ψ をラジアンにしたもの(fract してから。回転の角 θ に使う。cw が正)。
float TurnAngle(float psi)
{
    return 6.28318530718 * MotionU(psi);
}

// 波形 W(ψ)(−1〜1、周期 1)。wave は $wave.x(sine 0・tri 1・ease 2・saw 3)。
float WaveTri(float psi)
{
    return 4.0 * abs(MotionU(psi - 0.25) - 0.5) - 1.0;
}

float Wave(float psi, uint wave)
{
    if (wave == 1u) return WaveTri(psi);
    if (wave == 2u) return 2.0 * S((WaveTri(psi) + 1.0) * 0.5) - 1.0;
    if (wave == 3u) return 2.0 * MotionU(psi) - 1.0;
    float s, c;
    SinCos2Pi(psi, s, c);
    return s;
}

// W01=(W+1)÷2。
float Wave01(float psi, uint wave)
{
    return (Wave(psi, wave) + 1.0) * 0.5;
}

// ==== 乱数の格子(§9.5 の時間の式の 2) ====

// j=floor(ψ×K) mod (C×K)。K は 1 サイクルあたりの段の数。
uint LatticeStep(float psi, uint k, uint cycles)
{
    uint period = max(1u, k * cycles);
    return uint(floor(psi * float(k))) % period;
}

// なめらかな格子の補間の段: i=floor(ψ×K) mod (C×K)、next=(i+1) mod (C×K)、f=S(fract(ψ×K))。mix(v_i, v_next, f) で使う。
void LatticeSmooth(float psi, uint k, uint cycles, out uint i, out uint next, out float f)
{
    uint period = max(1u, k * cycles);
    float x = psi * float(k);
    float fl = floor(x);
    i = uint(fl) % period;
    next = (i + 1u) % period;
    f = S(x - fl);
}

// ==== 枠(§9.7 の規則 12)の座標 ====
// frame は $frame(qx, qy, Wl, Hl)。作業面の画素の中心は (i+0.5, j+0.5)。

// 作業面の位置 → 枠の左上を原点とする位置。
float2 ToFrame(float2 surface, float4 frame)
{
    return surface - frame.xy;
}

// 枠の位置 → 作業面の位置。
float2 FromFrame(float2 local, float4 frame)
{
    return local + frame.xy;
}

// 点のパラメータ(枠の幅と高さに対する 0〜1)の作業面の位置。
float2 FramePoint(float2 point01, float4 frame)
{
    return frame.xy + point01 * frame.zw;
}

// 作業面の画素 p が枠の内側か。
bool InFrame(int2 p, float4 frame)
{
    int2 lo = int2(frame.xy);
    int2 hi = lo + int2(frame.zw);
    return p.x >= lo.x && p.y >= lo.y && p.x < hi.x && p.y < hi.y;
}

// 枠の短辺 S0=min(Wl, Hl)。
float FrameShort(float4 frame)
{
    return min(frame.z, frame.w);
}

// ==== 逆写像(§9.5 の移動・回転・拡大の共通の規則) ====

// M=T(pivot+offset)×R(θ)×Sh×Sc(sx, sy)×T(−pivot) の逆写像 xs=M⁻¹(x)。pivot・offset・x は作業面の位置、θ はラジアン(時計回りが正)、
// shear は x 方向のせん断 x'=x+shear×(pivot.y−y)(jelly)。sx・sy は 0 でないこと。
float2 MotionInverse(float2 x, float2 pivot, float2 offset, float theta, float shear, float sx, float sy)
{
    float2 v = Turn(-theta, x - pivot - offset);
    v.x = v.x + shear * v.y;
    return pivot + float2(v.x / sx, v.y / sy);
}

// ==== 標本化(線形・プリマルチプライ) ====

// 画素 q を読む。作業面の外は透明(clamp しない)。
float4 LoadTransparent(Texture2D<float4> tex, int2 q)
{
    uint w, h;
    tex.GetDimensions(w, h);
    if (q.x < 0 || q.y < 0 || q.x >= int(w) || q.y >= int(h)) return float4(0.0, 0.0, 0.0, 0.0);
    return tex.Load(int3(q, 0));
}

// キャッチマル・ロムの 4 つの重み(t は 0 以上 1 未満)。t=0 で (0, 1, 0, 0)。
float4 CatRomWeights(float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return float4(
        -0.5 * t3 + t2 - 0.5 * t,
        1.5 * t3 - 2.5 * t2 + 1.0,
        -1.5 * t3 + 2.0 * t2 + 0.5 * t,
        0.5 * t3 - 0.5 * t2);
}

// 作業面の位置 pt(画素の中心が i+0.5)をキャッチマル・ロム双三次で読む。作業面の外のタップは透明。負の値は 0 に切る。
float4 SampleCatRomTransparent(Texture2D<float4> tex, float2 pt)
{
    float2 f = pt - 0.5;
    float2 i0 = floor(f);
    float2 t = f - i0;
    int2 base = int2(i0) - int2(1, 1);
    float4 wx = CatRomWeights(t.x);
    float4 wy = CatRomWeights(t.y);
    float4 acc = float4(0.0, 0.0, 0.0, 0.0);
    [unroll]
    for (int j = 0; j < 4; j++)
    {
        float4 row = float4(0.0, 0.0, 0.0, 0.0);
        [unroll]
        for (int i = 0; i < 4; i++)
        {
            row += wx[i] * LoadTransparent(tex, base + int2(i, j));
        }
        acc += wy[j] * row;
    }
    return max(acc, float4(0.0, 0.0, 0.0, 0.0));
}

// 作業面の位置 pt を双線形で読む。外は透明。
float4 SampleBilinearTransparent(Texture2D<float4> tex, float2 pt)
{
    float2 f = pt - 0.5;
    float2 i0 = floor(f);
    float2 a = f - i0;
    int2 b = int2(i0);
    float4 top = lerp(LoadTransparent(tex, b), LoadTransparent(tex, b + int2(1, 0)), a.x);
    float4 bottom = lerp(LoadTransparent(tex, b + int2(0, 1)), LoadTransparent(tex, b + int2(1, 1)), a.x);
    return lerp(top, bottom, a.y);
}

// 作業面の位置 pt を双線形で読む。外は最も近い画素(clamp。rgbsplit・ripple の規則 12 の (c))。
float4 SampleBilinearClamp(Texture2D<float4> tex, float2 pt)
{
    float2 f = pt - 0.5;
    float2 i0 = floor(f);
    float2 a = f - i0;
    int2 b = int2(i0);
    float4 top = lerp(LoadClamp(tex, b), LoadClamp(tex, b + int2(1, 0)), a.x);
    float4 bottom = lerp(LoadClamp(tex, b + int2(0, 1)), LoadClamp(tex, b + int2(1, 1)), a.x);
    return lerp(top, bottom, a.y);
}

// a mod m=a−floor(a÷m)×m(常に 0 以上 m 未満。m は正)。
float ModPos(float a, float m)
{
    float r = a - floor(a / m) * m;
    return r >= m ? r - m : r;
}

// 整数の a mod m(0 以上 m 未満)。int の % は fxc が遅いと警告する(X3556)ので、floor の除算で求める(画素の番号は float で正確に表せる大きさ)。
int ModPosInt(int a, int m)
{
    int r = a - int(floor(float(a) / float(m))) * m;
    return r < 0 ? r + m : (r >= m ? r - m : r);
}

// 枠の位置 local(枠の左上が原点、画素の中心が i+0.5)を、枠の内側で繰り返して(wrap。slide・glitch の規則 12 の (d))双線形で読む。
float4 SampleWrapInFrame(Texture2D<float4> tex, float2 local, float4 frame)
{
    int2 size = max(int2(frame.zw), int2(1, 1));
    int2 origin = int2(frame.xy);
    float2 f = local - 0.5;
    float2 i0 = floor(f);
    float2 a = f - i0;
    int2 b = int2(i0);
    int x0 = ModPosInt(b.x, size.x);
    int x1 = ModPosInt(b.x + 1, size.x);
    int y0 = ModPosInt(b.y, size.y);
    int y1 = ModPosInt(b.y + 1, size.y);
    float4 p00 = LoadTransparent(tex, origin + int2(x0, y0));
    float4 p10 = LoadTransparent(tex, origin + int2(x1, y0));
    float4 p01 = LoadTransparent(tex, origin + int2(x0, y1));
    float4 p11 = LoadTransparent(tex, origin + int2(x1, y1));
    float4 top = lerp(p00, p10, a.x);
    float4 bottom = lerp(p01, p11, a.x);
    return lerp(top, bottom, a.y);
}
