// AC-13 の (2) のうちルック 24 個(§9.6)の構成の検査: 埋め込みの EffectDef のカタログにある id(ルックか仕上げのステージ)と、宣言にあるキー・型・範囲内の値だけを使う。
// あわせて、表の順・キー・名前が §9.6 の表と一致し、Build が表の値を変えずに全キーの並びにすることを確かめる。モーションのプリセット 28 個は Phase 4。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Looks;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Looks;

public sealed class LookCatalogAc13Tests
{
    private static readonly Lazy<EffectCatalog> Catalog = new(() => BuiltInEffects.Load(new MemoryLogSink()));

    /// <summary>§9.6 の表のキーと名前(上から順)。</summary>
    internal static readonly (string Key, string Name)[] SpecOrder =
    [
        ("vivid", "ビビッド"), ("clear", "クリア"), ("dream", "ドリーミー"), ("film", "フィルム"), ("retro", "レトロ"), ("mono", "モノクロ"),
        ("noir", "ノワール"), ("sepia", "セピア"), ("cine", "シネマ"), ("neon", "ネオン"), ("pastel", "パステル"), ("pop", "ポップ"),
        ("matte", "マット"), ("sunset", "夕焼け"), ("midnight", "ミッドナイト"), ("pixel", "ドット絵"), ("gb4", "4階調の緑"), ("comic", "コミック"),
        ("sticker", "ステッカー"), ("miniature", "ミニチュア"), ("classic", "クラシック"), ("sakura", "桜"), ("crossproc", "クロスプロセス"), ("bleach", "銀残し"),
    ];

    [Test]
    public void AC13_2_ルック24個の構成が存在するidと範囲内の値だけを使う()
    {
        var problems = BuiltInLooks.Validate(Catalog.Value);
        Check.True(problems.Count == 0, "ルックの構成の問題: " + string.Join(" / ", problems));
    }

    [Test]
    public void AC13_2_ルックの表の順とキーと名前が9_6の表と一致する()
    {
        Check.SequenceEqual(SpecOrder.Select(s => s.Key).ToList(), BuiltInLooks.Table.Select(r => r.Key).ToList(), "キーの順");
        Check.SequenceEqual(SpecOrder.Select(s => s.Name).ToList(), BuiltInLooks.Table.Select(r => r.Name).ToList(), "名前の順");
    }

    [Test]
    public void AC13_2_検査が無いidと範囲外の値と列挙に無い値を見つける()
    {
        // 検査自身の確認: カタログから saturation を外すと vivid などが「無い id」になり、sharpen の範囲を狭めると「範囲外」になる
        var defs = Catalog.Value.Effects.Where(d => d.Id != "saturation").Select(d => d.Id == "sharpen"
            ? d with { Params = d.Params.Select(p => p.Key == "strength" ? p with { Max = 50 } : p).ToArray() }
            : d.Id == "halftone"
                ? d with { Params = d.Params.Select(p => p.Key == "mode" ? p with { Options = [new EnumOption("mono", "mono")] } : p).ToArray() }
                : d).ToArray();
        var problems = BuiltInLooks.Validate(new GlintLoop.Core.Session.DictionaryEffectCatalog(defs));
        Check.True(problems.Any(p => p.StartsWith("vivid: id saturation", StringComparison.Ordinal)), "無い id を見つけない: " + string.Join(" / ", problems));
        Check.True(problems.Any(p => p.StartsWith("vivid: sharpen.strength", StringComparison.Ordinal)), "範囲外を見つけない");
        Check.True(problems.Any(p => p.StartsWith("comic: halftone.mode", StringComparison.Ordinal)), "列挙に無い値を見つけない");
    }

    [Test]
    public void AC13_2_Buildは表の値を変えずに全キーの並びにする()
    {
        var catalog = Catalog.Value;
        var looks = BuiltInLooks.Build(catalog);
        Check.Equal(24, looks.Count, "ルックの数");
        for (var i = 0; i < looks.Count; i++)
        {
            var raw = BuiltInLooks.Table[i];
            var look = looks[i];
            Check.Equal(raw.Key, look.Key, "キー");
            Check.SequenceEqual(raw.Effects.Select(e => e.Id).ToList(), look.Effects.Select(e => e.Id).ToList(), raw.Key + " の id の並び");
            for (var j = 0; j < raw.Effects.Count; j++)
            {
                var def = catalog.Find(raw.Effects[j].Id)!;
                var e = look.Effects[j];
                Check.Equal(100.0, e.Amount, raw.Key + " の amount");
                var all = GlintLoop.Core.Session.EffectSchema.AllParams(def);
                Check.SequenceEqual(all.Select(p => p.Key).ToList(), e.Params.Select(p => p.Key).ToList(), $"{raw.Key}.{def.Id} のキーの並び");
                foreach (var p in all)
                {
                    var written = raw.Effects[j].Params.FirstOrDefault(x => x.Key == p.Key);
                    ParamValue expected = written is null ? p.Default : written.Value switch
                    {
                        double d => new NumberValue(d),
                        bool b => new BoolValue(b),
                        string s => new StringValue(s),
                        _ => throw new CheckFailedException("表の値の型が不明"),
                    };
                    Check.Equal(expected, e.Get(p.Key), $"{raw.Key}.{def.Id}.{p.Key}");
                }
            }
        }
    }
}
