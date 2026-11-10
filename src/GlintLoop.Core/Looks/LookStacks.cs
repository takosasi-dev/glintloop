// ルックをスタックに入れる規則(FR-34)と、ギャラリーのサムネイルの文書(FR-33・§9.7 の規則 14)を作る純粋な関数。
// 適用: origin=look の行をすべて取り除き、ルックの行を origin=look・新しい salt で、ルックステージと仕上げステージのそれぞれの先頭へレシピの順に入れる。
// サムネイル: 対象(レイヤー 1 枚、または全体)の形ステージの行だけを残し、そのルックの行だけを足した文書。ルックの強さは 100 とみなし、各レイヤーのモーションは通さない(規則 14)。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Looks;

public static class LookStacks
{
    /// <summary>ルックに入れられるステージ(ルックと仕上げ。§9.6)か。</summary>
    public static bool IsLookStage(EffectDef def) => def.Stage is EffectStage.Look or EffectStage.Finish;

    /// <summary>
    /// stack の origin=look の行を取り除き、effects を各ステージの先頭へ入れた並びを返す。salt は saltCounter の次から
    /// レシピの順に 1 ずつ振る(saltCounter を進める)。カタログに無い id・ルックと仕上げ以外のステージの行は入れない。
    /// 取り除いたあとの個数と入れる個数の合計が 64 を超えるときは null(E-22。saltCounter は変えない)。
    /// </summary>
    public static List<StackEntry>? Apply(IReadOnlyList<StackEntry> stack, IReadOnlyList<LookEffect> effects, IEffectCatalog catalog, ref long saltCounter)
    {
        var kept = stack.Where(e => e.Origin != EffectOrigin.Look).ToList();
        var defs = Usable(effects, catalog);
        if (kept.Count + defs.Count > EffectSchema.MaxStackEntries) return null;
        return Insert(kept, defs, catalog, ref saltCounter);
    }

    /// <summary>
    /// 対象のスタックのルックステージと仕上げステージのエフェクトのすべて(origin を問わない。FR-36)を、ルックの行にする。
    /// 有効と無効の区別は持たない(ファイルの形に enabled が無いため)。
    /// </summary>
    public static IReadOnlyList<LookEffect> Capture(IReadOnlyList<StackEntry> stack, IEffectCatalog catalog)
    {
        var list = new List<LookEffect>();
        foreach (var e in stack)
        {
            var def = catalog.Find(e.Id);
            if (def is null || !IsLookStage(def)) continue;
            list.Add(new LookEffect(e.Id, e.Amount, e.Params));
        }
        return list;
    }

    /// <summary>
    /// ギャラリーのタイルのサムネイルの文書(規則 14)。対象がレイヤーのとき、そのレイヤー 1 枚を土台にした文書(SingleLayerView)の
    /// スタックを形ステージの行だけにして effects を足す。対象が全体のとき、レイヤーはそのままにして、全体のスタックを
    /// 全体の形ステージの行と effects にする。effects が空なら「ルックなし」(形ステージだけ)。salt の振り方は Apply と同じ。
    /// 大きさ(Lout=min(192, 元の長辺))は描く側が決める。
    /// </summary>
    public static SessionDocument Preview(SessionDocument doc, Target target, IReadOnlyList<LookEffect> effects, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var counter = doc.SaltCounter;
        var defs = Usable(effects, catalog);
        if (target.IsAll)
        {
            // 規則 14: 全体のギャラリーは、各レイヤーのモーションステージを通さない合成に、全体の形ステージとルックを通す
            var stack = Insert(ShapeOnly(doc.All.Stack, catalog), defs, catalog, ref counter);
            return MotionInfo.WithoutLayerMotion(doc, catalog) with { SaltCounter = counter, All = new GlobalStack(100, stack) };
        }
        var single = SingleLayerView.Of(doc, target.LayerIndex, catalog);
        var layer = single.Layers[0];
        var layerStack = Insert(ShapeOnly(layer.Stack, catalog), defs, catalog, ref counter);
        return single with { SaltCounter = counter, Layers = [layer with { LookStrength = 100, Stack = layerStack }] };
    }

    /// <summary>形ステージの行だけ(行の順のまま)。カタログに無い id の行は除く(描画も通さない)。</summary>
    public static List<StackEntry> ShapeOnly(IReadOnlyList<StackEntry> stack, IEffectCatalog catalog) =>
        stack.Where(e => catalog.Find(e.Id) is { Stage: EffectStage.Shape }).ToList();

    private static List<(LookEffect Effect, EffectDef Def)> Usable(IReadOnlyList<LookEffect> effects, IEffectCatalog catalog)
    {
        var list = new List<(LookEffect, EffectDef)>(effects.Count);
        foreach (var e in effects)
        {
            if (catalog.Find(e.Id) is { } def && IsLookStage(def)) list.Add((e, def));
        }
        return list;
    }

    private static List<StackEntry> Insert(List<StackEntry> kept, List<(LookEffect Effect, EffectDef Def)> defs, IEffectCatalog catalog, ref long saltCounter)
    {
        int KeyOf(StackEntry e) => catalog.Find(e.Id) is { } d ? EffectSchema.OrderKey(d) : int.MaxValue;
        var lookKey = EffectSchema.StageOrder(EffectStage.Look) * 4;
        var finishKey = EffectSchema.StageOrder(EffectStage.Finish) * 4;
        var looks = new List<StackEntry>();
        var finishes = new List<StackEntry>();
        foreach (var (e, def) in defs)
        {
            var entry = new StackEntry(def.Id, true, EffectOrigin.Look, ++saltCounter, DocumentRules.Clamp(e.Amount, 0, 100), Complete(def, e.Params));
            (def.Stage == EffectStage.Look ? looks : finishes).Add(entry);
        }
        var result = new List<StackEntry>(kept.Count + looks.Count + finishes.Count);
        var i = 0;
        while (i < kept.Count && KeyOf(kept[i]) < lookKey) result.Add(kept[i++]);
        result.AddRange(looks);
        while (i < kept.Count && KeyOf(kept[i]) < finishKey) result.Add(kept[i++]);
        result.AddRange(finishes);
        while (i < kept.Count) result.Add(kept[i++]);
        return result;
    }

    /// <summary>値の並びを、その EffectDef の全キーの並びにそろえる(欠けは既定値)。</summary>
    private static IReadOnlyList<KeyValuePair<string, ParamValue>> Complete(EffectDef def, IReadOnlyList<KeyValuePair<string, ParamValue>> values)
    {
        var all = EffectSchema.AllParams(def);
        var list = new KeyValuePair<string, ParamValue>[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            var key = all[i].Key;
            ParamValue? v = null;
            foreach (var kv in values)
            {
                if (kv.Key == key)
                {
                    v = kv.Value;
                    break;
                }
            }
            list[i] = new(key, v ?? all[i].Default);
        }
        return list;
    }
}
