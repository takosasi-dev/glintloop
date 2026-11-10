// アセンブリに埋め込んだシェーダーのバイトコード(.cso)を読む(仕様書 D-7・INV-3)。
// 読み込み元は埋め込みリソースだけで、ファイル・ネットワーク・クリップボードからは読まない。
// 名前は Shaders フォルダのファイル名から .hlsl を除いたもの(例: "fill.ps")。
using System.Reflection;

namespace GlintLoop.Render;

public static class ShaderLibrary
{
    private const string Prefix = "GlintLoop.Render.Shaders.";
    private const string Suffix = ".cso";

    /// <summary>埋め込まれているシェーダーの名前の一覧(並びは序数順)。</summary>
    public static IReadOnlyList<string> Names { get; } = typeof(ShaderLibrary).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..^Suffix.Length])
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    /// <summary>名前のシェーダーの DXBC を返す。無ければ KeyNotFoundException。</summary>
    public static byte[] Load(string name)
    {
        using var stream = typeof(ShaderLibrary).Assembly.GetManifestResourceStream(Prefix + name + Suffix)
            ?? throw new KeyNotFoundException($"埋め込みのシェーダー {name} が無い");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
