// 辞書から作るエフェクトのカタログと、スタックの並びの規則(§6.3 の FR-25、§9.2、§9.7 の規則 4 の 1)。
// EffectDef の読み込みは別の担当(EffectDefLoader)が行い、ここは読み込み済みの EffectDef を受け取るだけ。
// 時間依存のエフェクトの共通パラメータ cycles・phase・wave は、EffectDef の params に無ければここで補う。
using System.Runtime.CompilerServices;
using GlintLoop.Core.Effects;

namespace GlintLoop.Core.Session;

/// <summary>辞書か EffectDef の列から作るカタログ。</summary>
public sealed class DictionaryEffectCatalog : IEffectCatalog
{
    private readonly IReadOnlyDictionary<string, EffectDef> _defs;

    public DictionaryEffectCatalog(IReadOnlyDictionary<string, EffectDef> defs)
    {
        _defs = defs;
        Effects = defs.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    public DictionaryEffectCatalog(IEnumerable<EffectDef> defs)
    {
        var map = new Dictionary<string, EffectDef>(StringComparer.Ordinal);
        var order = new List<EffectDef>();
        foreach (var d in defs)
        {
            if (map.TryGetValue(d.Id, out var old)) order.Remove(old);
            map[d.Id] = d;
            order.Add(d);
        }
        _defs = map;
        Effects = order;
    }

    public EffectDef? Find(string id) => _defs.TryGetValue(id, out var d) ? d : null;

    /// <summary>全エフェクト(列から作ったときは列の順、辞書から作ったときは id の序数順)。</summary>
    public IReadOnlyList<EffectDef> Effects { get; }
}

/// <summary>スタックの並びと、EffectDef のパラメータの並び。</summary>
public static class EffectSchema
{
    public const string Rotate = "rotate";
    public const string Crop = "crop";
    public const string Pad = "pad";
    public const string Levels = "levels";

    /// <summary>1 つの対象のスタックに入れられるエフェクトの数(FR-22)。</summary>
    public const int MaxStackEntries = 64;

    /// <summary>形ステージの先頭に固定する 3 つ(回転・切り抜き・余白)か。</summary>
    public static bool IsFixedShape(string id) => id is Rotate or Crop or Pad;

    /// <summary>ステージの順(形 0・ルック 1・仕上げ 2・モーション 3)。</summary>
    public static int StageOrder(EffectStage stage) => stage switch
    {
        EffectStage.Shape => 0,
        EffectStage.Look => 1,
        EffectStage.Finish => 2,
        _ => 3,
    };

    /// <summary>
    /// 並びの鍵。ステージの順×4 に、形ステージの固定の 3 つは 0・1・2、それ以外は 3 を足す(ほかのステージは 0)。
    /// スタックはこの鍵の小さい順(同じ鍵の中は行の順)に並ぶ。
    /// </summary>
    public static int OrderKey(EffectDef def)
    {
        var stage = StageOrder(def.Stage);
        if (def.Stage != EffectStage.Shape) return stage * 4;
        return def.Id switch
        {
            Rotate => 0,
            Crop => 1,
            Pad => 2,
            _ => 3,
        };
    }

    private static readonly ConditionalWeakTable<EffectDef, IReadOnlyList<ParamDef>> AllParamsCache = new();

    /// <summary>
    /// パラメータの並び(§9.2 の UI の並び: cycles、phase、wave、そのほか)。timeDependent のときの共通パラメータが
    /// EffectDef の params に無ければ補う(cycles: 整数 1〜8 既定 1、phase: 0〜100% 既定 0、wave: waveDefault)。
    /// </summary>
    public static IReadOnlyList<ParamDef> AllParams(EffectDef def) =>
        AllParamsCache.GetValue(def, static d => BuildAllParams(d));

    public static ParamDef? FindParam(EffectDef def, string key)
    {
        foreach (var p in AllParams(def))
        {
            if (p.Key == key) return p;
        }
        return null;
    }

    /// <summary>
    /// パラメータが visibleWhen の条件(§9.2)で見えているか(行の値で判定する)。条件が無ければ見える。条件のキーの値(enum の value、bool は
    /// "true"/"false")が条件の値の配列に入るときだけ見える。パラメータ欄の表示とキャンバスのハンドルの判定・配置は、どれもこれを使う。
    /// </summary>
    public static bool IsVisible(ParamDef p, StackEntry entry)
    {
        if (p.VisibleWhen is not { } w) return true;
        var v = entry.Get(w.Key) switch
        {
            StringValue s => s.Value,
            BoolValue b => b.Value ? "true" : "false",
            _ => null,
        };
        return v is not null && w.Values.Contains(v);
    }

    /// <summary>すべてのパラメータを既定値にした並び。</summary>
    public static IReadOnlyList<KeyValuePair<string, ParamValue>> DefaultParams(EffectDef def)
    {
        var all = AllParams(def);
        var list = new KeyValuePair<string, ParamValue>[all.Count];
        for (var i = 0; i < all.Count; i++) list[i] = new(all[i].Key, all[i].Default);
        return list;
    }

    public static string WaveName(WaveKind w) => w switch
    {
        WaveKind.Sine => "sine",
        WaveKind.Tri => "tri",
        WaveKind.Ease => "ease",
        _ => "saw",
    };

    private static IReadOnlyList<ParamDef> BuildAllParams(EffectDef def)
    {
        if (!def.TimeDependent) return def.Params;
        var list = new List<ParamDef>();
        list.Add(def.FindParam("cycles") ?? new ParamDef(
            "cycles", "回数", ParamType.Int, 1, 8, 1, new NumberValue(1), "", null, null, false, true, null, null));
        list.Add(def.FindParam("phase") ?? new ParamDef(
            "phase", "位相", ParamType.Float, 0, 100, 1, new NumberValue(0), "%", null, null, false, true, null, null));
        if (!def.NoWave)
        {
            list.Add(def.FindParam("wave") ?? new ParamDef(
                "wave", "波形", ParamType.Enum, null, null, null, new StringValue(WaveName(def.WaveDefault)), "",
                [
                    new EnumOption("sine", "sine"), new EnumOption("tri", "tri"),
                    new EnumOption("ease", "ease"), new EnumOption("saw", "saw"),
                ],
                null, false, true, null, null));
        }
        foreach (var p in def.Params)
        {
            if (p.Key is "cycles" or "phase" || (p.Key == "wave" && !def.NoWave)) continue;
            list.Add(p);
        }
        return list;
    }
}
