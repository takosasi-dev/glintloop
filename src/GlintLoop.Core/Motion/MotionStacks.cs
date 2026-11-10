// モーションのプリセットをスタックに入れる規則(FR-64)と、モーションのタイルの絵の文書(§9.7 の規則 14、Q-L)を作る純粋な関数。
// 適用: モーションステージの行を origin を問わずすべて取り除き、プリセットの行を origin=user・新しい salt で、モーションステージ(最後のステージ)へ表の順に入れる。
// タイル: レイヤーは配置どおりに作業面へ置いたその 1 枚(下のレイヤーは重ねない)にプリセットだけを通し、全体は各レイヤーのモーションを通さない合成にプリセットだけを通す。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Motion;

public static class MotionStacks
{
    /// <summary>
    /// stack のモーションステージの行(目のオン・オフ、origin を問わない)を取り除き、effects の行を末尾(モーションステージ)へ表の順に足した並びを返す。
    /// salt は saltCounter の次から 1 ずつ振る(saltCounter を進める)。カタログに無い id・モーション以外のステージの行は入れない。
    /// 取り除いたあとの個数と入れる個数の合計が 64 を超えるときは null(E-22 と同じ。saltCounter は変えない)。effects が空なら取り除くだけ(「モーションなし」)。
    /// </summary>
    public static List<StackEntry>? Apply(IReadOnlyList<StackEntry> stack, IReadOnlyList<MotionPresetEffect> effects, IEffectCatalog catalog, ref long saltCounter)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(catalog);
        var kept = MotionInfo.WithoutMotion(stack, catalog).ToList();
        var defs = Usable(effects, catalog);
        if (kept.Count + defs.Count > EffectSchema.MaxStackEntries) return null;
        foreach (var (e, def) in defs) kept.Add(Row(def, e, ref saltCounter));
        return kept;
    }

    /// <summary>
    /// タイルの絵の文書(規則 14)。effects が空なら「モーションなし」の絵。salt の振り方は Apply と同じ(文書の saltCounter の次から)。
    /// 対象がレイヤー i のとき: そのレイヤーを配置どおりに置き(表示オン・不透明度 100・ブレンド通常)、スタックのモーションの行をプリセットに替える。
    /// 下のレイヤーは重ねないので、土台(i が 0 でないとき)は表示をオフにして作業面の大きさ(土台のネイティブ寸法。規則 1)だけに使い、ほかのレイヤーは除く。
    /// 全体のスタックは空にする。対象が全体のとき: 各レイヤーのモーションを外した合成に、全体のスタックをプリセットの行だけにして通す(Q-L)。
    /// 大きさ(Lout=min(192, 合成の長辺))とフレーム(t=K÷N)は描く側が決める。
    /// </summary>
    public static SessionDocument Preview(SessionDocument doc, Target target, IReadOnlyList<MotionPresetEffect> effects, IEffectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(catalog);
        var counter = doc.SaltCounter;
        var rows = new List<StackEntry>();
        foreach (var (e, def) in Usable(effects, catalog)) rows.Add(Row(def, e, ref counter));
        if (target.IsAll)
        {
            return MotionInfo.WithoutLayerMotion(doc, catalog) with
            {
                SaltCounter = counter,
                All = new GlobalStack(doc.All.LookStrength, rows),
            };
        }
        ArgumentOutOfRangeException.ThrowIfNegative(target.LayerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(target.LayerIndex, doc.Layers.Count);
        var src = doc.Layers[target.LayerIndex];
        var stack = MotionInfo.WithoutMotion(src.Stack, catalog).Concat(rows).ToArray();
        var layer = src with { Visible = true, Opacity = 100, Blend = BlendMode.Normal, Stack = stack };
        Layer[] layers;
        if (target.LayerIndex == 0)
        {
            layers = [layer];
        }
        else
        {
            // 土台は重ねないが、作業面の大きさ(合成のネイティブ寸法)は土台のソースと回転・切り抜き・余白で決まる(規則 1)ので、その行だけ残す
            var b = doc.Base;
            var baseStack = b.Stack.Where(e => EffectSchema.IsFixedShape(e.Id)).ToArray();
            layers = [b with { Visible = false, Stack = baseStack }, layer];
        }
        return doc with
        {
            SaltCounter = counter,
            Selected = Target.OfLayer(layers.Length - 1),
            Layers = layers,
            All = doc.All with { Stack = [] },
        };
    }

    private static StackEntry Row(EffectDef def, MotionPresetEffect e, ref long saltCounter) =>
        new(def.Id, true, EffectOrigin.User, ++saltCounter, 100, Complete(def, e.Params));

    private static List<(MotionPresetEffect Effect, EffectDef Def)> Usable(IReadOnlyList<MotionPresetEffect> effects, IEffectCatalog catalog)
    {
        var list = new List<(MotionPresetEffect, EffectDef)>(effects.Count);
        foreach (var e in effects)
        {
            if (catalog.Find(e.Id) is { Stage: EffectStage.Motion } def) list.Add((e, def));
        }
        return list;
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
