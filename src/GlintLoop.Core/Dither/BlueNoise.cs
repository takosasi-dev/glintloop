// 青色雑音のタイル bn(64×64、0〜4095 の順位。仕様書 §9.7 の 8)を、埋め込みリソースから読んで渡す。
// 表は tools/make-bluenoise が作ってリポジトリにコミットしたもので、実行時には作らない(AC-3 の (5))。
// bn(i, j) は j 行目・i 列目の値(i が x、j が y)。GIF の青色雑音ディザ(§9.8 の 5)も同じ表を使う。
using System.Buffers.Binary;

namespace GlintLoop.Core.Dither;

public static class BlueNoise
{
    public const int Size = 64;
    public const int Count = Size * Size;

    internal const string ResourceName = "GlintLoop.Core.Assets.bluenoise64.bin";

    private static readonly ushort[] s_tile = Load();

    /// <summary>行優先の 4096 個。[j×64+i] が bn(i, j)。</summary>
    public static ReadOnlySpan<ushort> Tile => s_tile;

    /// <summary>bn(i mod 64, j mod 64)。i が列(x)、j が行(y)。負の座標でも 0〜63 に入れる。</summary>
    public static int At(int i, int j) => s_tile[((j & (Size - 1)) * Size) + (i & (Size - 1))];

    /// <summary>埋め込みリソースの生のバイト列(テストで大きさと並びを確かめるため)。</summary>
    internal static byte[] ReadResourceBytes()
    {
        using var stream = typeof(BlueNoise).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"埋め込みリソース {ResourceName} が無い");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static ushort[] Load()
    {
        var bytes = ReadResourceBytes();
        if (bytes.Length != Count * 2)
            throw new InvalidDataException($"{ResourceName} の大きさが {Count * 2} バイトではない: {bytes.Length}");
        var tile = new ushort[Count];
        for (var k = 0; k < Count; k++)
        {
            tile[k] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 * k, 2));
        }
        return tile;
    }
}
