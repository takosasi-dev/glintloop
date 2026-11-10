// 決定論の乱数の材料になる整数ハッシュ(仕様書 §9.4 の hash32・H・H3・u01)。
// hash32 は MurmurHash3 の最終混合関数(fmix32)と同じ演算で、ほかのコードは取り込んでいない。
// 実時間・Random クラス・スレッドの番号を使わない(INV-8)。
namespace GlintLoop.Core.Numerics;

public static class Hash
{
    public static uint Hash32(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            x *= 0xC2B2AE35u;
            x ^= x >> 16;
            return x;
        }
    }

    /// <summary>H(a,b)=hash32(a + hash32(b + 0x9E3779B9))。</summary>
    public static uint H(uint a, uint b)
    {
        unchecked { return Hash32(a + Hash32(b + 0x9E3779B9u)); }
    }

    /// <summary>H3(a,b,c)=H(H(a,b),c)。</summary>
    public static uint H3(uint a, uint b, uint c) => H(H(a, b), c);

    /// <summary>u01(h)=(h>>8)÷16777216。0 以上 1 未満。</summary>
    public static double U01(uint h) => (h >> 8) / 16777216.0;
}
