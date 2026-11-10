// EffectDef の読み込み(仕様書 §9.2)。読み込み元はアセンブリの埋め込みリソースだけで、ファイルやストリームを外から受け取らない(INV-3)。
// 「読み込み時の検査」の 9 項目に当てはまった宣言は読み込まず、ログに effectdef_invalid(id と reason=項目の番号)を書く(AC-13)。
// JSON の解釈と検査の中身は EffectDefLoader が行う。
using System.Reflection;
using System.Text;
using GlintLoop.Core.Diagnostics;

namespace GlintLoop.Core.Effects;

/// <summary>読み込まなかった宣言 1 つ。Detail は開発者向けの説明で、ログには書かない(INV-15)。</summary>
public sealed record EffectDefRejection(string Id, int Reason, string Detail);

public sealed class EffectCatalog : IEffectCatalog
{
    private readonly Dictionary<string, EffectDef> _byId;

    private EffectCatalog(IReadOnlyList<EffectDef> effects, IReadOnlyList<EffectDefRejection> rejected)
    {
        Effects = effects;
        Rejected = rejected;
        _byId = effects.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>検査に通った宣言(リソース名の序数順)。</summary>
    public IReadOnlyList<EffectDef> Effects { get; }

    /// <summary>検査に落ちて読み込まなかった宣言(同じ内容をログにも書いた)。</summary>
    public IReadOnlyList<EffectDefRejection> Rejected { get; }

    public EffectDef? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// assembly の埋め込みリソースのうち、名前が resourcePrefix で始まり .json で終わるものを、名前の序数順にすべて読む。
    /// shaderExists は、EffectDef の shader の名前(例 "exposure")のバイトコードが埋め込まれているか(検査の 6)。
    /// </summary>
    public static EffectCatalog LoadFromAssembly(Assembly assembly, string resourcePrefix, Func<string, bool> shaderExists, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(resourcePrefix);
        ArgumentNullException.ThrowIfNull(shaderExists);
        ArgumentNullException.ThrowIfNull(log);
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(resourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var sources = new List<(string Name, string Json)>(names.Length);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                sources.Add((name, ""));
                continue;
            }
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            sources.Add((name, reader.ReadToEnd()));
        }
        return LoadFromSources(sources, shaderExists, log);
    }

    /// <summary>JSON の文字列から読む(テストの入口。製品のコードは LoadFromAssembly だけを使う)。</summary>
    internal static EffectCatalog LoadFromSources(IEnumerable<(string Name, string Json)> sources, Func<string, bool> shaderExists, ILogSink log)
    {
        var parsed = new List<EffectDef>();
        var rejected = new List<EffectDefRejection>();
        foreach (var (_, json) in sources)
        {
            var r = EffectDefLoader.Parse(json, shaderExists);
            if (r.Def is not null) parsed.Add(r.Def);
            else rejected.Add(new EffectDefRejection(r.Id, r.Reason, r.Detail));
        }

        // 検査の 1: id の重複(全ステージを通して)。重複した id の宣言は、どれも読み込まない
        var duplicated = parsed.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var accepted = new List<EffectDef>();
        foreach (var def in parsed)
        {
            if (duplicated.Contains(def.Id)) rejected.Add(new EffectDefRejection(def.Id, 1, $"id {def.Id} が重複している"));
            else accepted.Add(def);
        }

        foreach (var r in rejected)
            log.Write(LogEvent.Of(EffectDefLoader.InvalidEvent, ("id", r.Id), ("reason", r.Reason)));
        return new EffectCatalog(accepted, rejected);
    }
}
