// 製品に同梱する EffectDef(Render のアセンブリに埋め込んだ Effects/*.json)を読む入口(仕様書 §9.2・INV-3)。
// シェーダーの有無(検査の 6)は、埋め込みの DXBC の一覧(ShaderLibrary)に「名前.ps」があるかで判定する。
// 読み込み元は埋め込みリソースだけで、ファイルは開かない。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;

namespace GlintLoop.Render.Effects;

public static class BuiltInEffects
{
    /// <summary>EffectDef の埋め込みリソースの名前の頭(csproj の LogicalName と同じ)。</summary>
    public const string ResourcePrefix = "GlintLoop.Render.Effects.";

    /// <summary>EffectDef の shader の名前(例 "exposure")のピクセルシェーダーが埋め込まれているか。</summary>
    public static bool ShaderExists(string shader) =>
        ShaderLibrary.Names.Contains(EffectRunner.PixelShaderName(shader), StringComparer.Ordinal);

    /// <summary>埋め込まれた全 EffectDef を読み、検査する(無効なものは log に effectdef_invalid を書いて除く)。</summary>
    public static EffectCatalog Load(ILogSink log) =>
        EffectCatalog.LoadFromAssembly(typeof(BuiltInEffects).Assembly, ResourcePrefix, ShaderExists, log);

    /// <summary>埋め込まれている EffectDef のリソースの数(検査に落ちたものも数える)。</summary>
    public static int ResourceCount => typeof(BuiltInEffects).Assembly.GetManifestResourceNames()
        .Count(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));
}
