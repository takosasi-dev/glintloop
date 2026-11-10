// AC-26 の (2)(3) のうち、画面を使わずに EditorSession と描画で確かめるもの: 適用(FR-34)の行の並び・Undo・64 個・対象だけの変更、
// 強さ(FR-35)の対象ごとの値と、vivid の強さ 50% が origin=user の amount 50% と完全に一致すること(WARP)。カタログは埋め込みの EffectDef。
// ギャラリーのタイル・Enter での適用・保存のダイアログは LooksUiAc26Tests.cs、ファイルの保存は UserLookStoreAc26Tests.cs に置く。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Looks;

public sealed class LookSessionAc26Tests
{
    private static readonly Lazy<EffectCatalog> Catalog = new(() => BuiltInEffects.Load(new MemoryLogSink()));

    private static IReadOnlyList<LookRecipe> Looks => BuiltInLooks.Build(Catalog.Value);

    private static LookRecipe Look(string key) => Looks.Single(l => l.Key == key);

    /// <summary>画像 2 枚(土台とレイヤー A)の文書。</summary>
    private static EditorSession TwoLayers()
    {
        var s = new EditorSession(Catalog.Value, new FixedSeedSource(12345));
        s.AddImportedImages([SessionTestKit.Image(1, 200, 100), SessionTestKit.Image(2, 80, 60)]);
        return s;
    }

    private static int Add(EditorSession s, Target t, string id)
    {
        var o = s.AddEffect(t, id);
        Check.True(o.Changed, id + " を足せない");
        return o.Row!.Value;
    }

    private static int KeyOf(StackEntry e) => EffectSchema.OrderKey(Catalog.Value.Find(e.Id)!);

    [Test]
    public void AC26_2_24個のルックで適用後のスタックがレシピと一致しuserの行は後ろに残る()
    {
        foreach (var look in Looks)
        {
            var s = TwoLayers();
            var a = Target.OfLayer(1);
            // user の行: 形(flip)・ルック(invert)・仕上げ(outline)
            Add(s, a, "flip");
            Add(s, a, "invert");
            Add(s, a, "outline");
            var before = s.Document!.SaltCounter;
            Check.True(s.ApplyLook(a, look.Effects).Changed, look.Key + " を適用できない");
            var stack = s.Document!.Layers[1].Stack;
            var looks = stack.Where(e => e.Origin == EffectOrigin.Look).ToList();
            Check.SequenceEqual(look.Effects.Select(e => e.Id).ToList(), looks.Select(e => e.Id).ToList(), look.Key + " の origin=look の id(レシピの順)");
            for (var i = 0; i < looks.Count; i++)
            {
                Check.Equal(100.0, looks[i].Amount, look.Key + " の amount");
                Check.True(looks[i].Enabled, look.Key + " の行が有効");
                Check.SequenceEqual(look.Effects[i].Params, looks[i].Params, $"{look.Key}.{looks[i].Id} の値");
                Check.Equal(before + i + 1, looks[i].Salt, look.Key + " の salt");
            }
            Check.Equal(3 + look.Effects.Count, stack.Count, look.Key + " の行の数");
            // ステージの順(鍵の単調)と、各ステージの中で look の行が user の行より先
            for (var i = 1; i < stack.Count; i++) Check.True(KeyOf(stack[i - 1]) <= KeyOf(stack[i]), look.Key + " のステージの順");
            foreach (var stage in new[] { EffectStage.Look, EffectStage.Finish })
            {
                var rows = stack.Where(e => Catalog.Value.Find(e.Id)!.Stage == stage).Select(e => e.Origin).ToList();
                var firstUser = rows.IndexOf(EffectOrigin.User);
                Check.True(firstUser < 0 || rows.Skip(firstUser).All(o => o == EffectOrigin.User), $"{look.Key} の {stage} で look の行が先頭にない");
            }
            Check.Equal("flip", stack[0].Id, look.Key + " の形ステージの user の行");
            Check.True(stack.Any(e => e.Id == "invert" && e.Origin == EffectOrigin.User), look.Key + " の user のルックの行");
            Check.Equal("outline", stack[^1].Id, look.Key + " の最後は user の仕上げの行");
        }
    }

    [Test]
    public void AC26_2_適用はUndoの1件で適用前の完全な状態に戻り対象のスタックだけを変える()
    {
        var s = TwoLayers();
        var a = Target.OfLayer(1);
        Add(s, a, "invert");
        Add(s, Target.All, "vignette");
        Add(s, Target.OfLayer(0), "exposure");
        Check.True(s.ApplyLook(a, Look("dream").Effects).Changed, "dream");
        var json = s.Json;
        var undo = s.UndoCount;
        var doc = s.Document!;
        Check.True(s.ApplyLook(a, Look("film").Effects).Changed, "film で置き換え");
        Check.Equal(undo + 1, s.UndoCount, "適用は Undo の 1 件");
        var after = s.Document!;
        Check.True(ReferenceEquals(doc.Layers[0].Stack, after.Layers[0].Stack) || doc.Layers[0].Stack.SequenceEqual(after.Layers[0].Stack), "ほかのレイヤーのスタック");
        Check.True(doc.All.Stack.SequenceEqual(after.All.Stack), "全体のスタック");
        Check.True(!(after.Layers[1].Stack.Any(e => e.Id == "glow")), "前のルック(dream の glow)が残っている");
        Check.SequenceEqual(Look("film").Effects.Select(e => e.Id).ToList(),
            after.Layers[1].Stack.Where(e => e.Origin == EffectOrigin.Look).Select(e => e.Id).ToList(), "film の行");
        Check.True(s.Undo(), "Undo");
        Check.Equal(json, s.Json, "Undo 1 回で適用前の JSON(行の並び・origin・パラメータ)");
    }

    [Test]
    public void AC26_2_ルックなしは取り除くだけでuserの行は残る()
    {
        var s = TwoLayers();
        var a = Target.OfLayer(1);
        Add(s, a, "invert");
        s.ApplyLook(a, Look("vivid").Effects);
        Check.True(s.RemoveLook(a).Changed, "ルックなし");
        var stack = s.Document!.Layers[1].Stack;
        Check.Equal(1, stack.Count, "行の数");
        Check.Equal("invert", stack[0].Id, "user の行");
        var undo = s.UndoCount;
        Check.True(!(s.RemoveLook(a).Changed), "ルックの行が無いときのルックなしは何も変えない");
        Check.Equal(undo, s.UndoCount, "履歴に積まない");
    }

    [Test]
    public void AC26_2_E22_合計が64を超える適用は取り除く分を引いて数え全体を取りやめる()
    {
        var s = TwoLayers();
        var a = Target.OfLayer(1);
        s.ApplyLook(a, Look("vivid").Effects); // look の行 3
        for (var i = 0; i < 59; i++) Add(s, a, "invert"); // user の行 59、合計 62
        // film(6 行): 59 + 6 = 65 > 64 で取りやめ
        var json = s.Json;
        var o = s.ApplyLook(a, Look("film").Effects);
        Check.True(!(o.Changed), "65 個になる適用");
        Check.Equal(NoticeKeys.StackFull, o.Notice?.Key, "stack.full");
        Check.Equal(json, s.Json, "スタックが変わらない(取り除きも行わない)");
        // clear(4 行): 59 + 4 = 63 は入る(取り除く 3 を引いて数える)
        Check.True(s.ApplyLook(a, Look("clear").Effects).Changed, "63 個になる適用");
        Check.Equal(63, s.Document!.Layers[1].Stack.Count, "行の数");
        Add(s, a, "invert");
        Check.Equal(64, s.Document!.Layers[1].Stack.Count, "64 個まで入る");
        Check.Equal(NoticeKeys.StackFull, s.AddEffect(a, "invert").Notice?.Key, "65 個目は stack.full");
    }

    [Test]
    public void AC26_3_強さは対象ごとに持ちレイヤーAを50にしてもほかは100()
    {
        var s = TwoLayers();
        Check.True(s.SetLookStrength(Target.OfLayer(1), 50).Changed, "強さ 50");
        Check.Equal(50, s.LookStrengthOf(Target.OfLayer(1)), "レイヤー A");
        Check.Equal(100, s.LookStrengthOf(Target.OfLayer(0)), "土台");
        Check.Equal(100, s.LookStrengthOf(Target.All), "全体");
        Check.True(s.SetLookStrength(Target.All, 30).Changed, "全体を 30");
        Check.Equal(50, s.LookStrengthOf(Target.OfLayer(1)), "レイヤー A は 50 のまま");
    }

    [Test]
    public void AC26_4_保存する中身はルックと仕上げのステージの行のすべてでoriginを問わない()
    {
        var s = TwoLayers();
        var a = Target.OfLayer(1);
        Add(s, a, "flip");
        var inv = Add(s, a, "invert");
        s.SetEffectAmount(a, inv, 40);
        s.ApplyLook(a, Look("vivid").Effects);
        var captured = s.CaptureLook(a);
        Check.SequenceEqual(["saturation", "contrast", "invert", "sharpen"], captured.Select(e => e.Id).ToList(), "保存する行(形ステージの flip を含まない)");
        Check.Equal(40.0, captured[2].Amount, "user の行の amount");
        // 保存した中身を適用すると、すべて origin=look で入る
        var t0 = Target.OfLayer(0);
        Check.True(s.ApplyLook(t0, captured).Changed, "保存した中身の適用");
        Check.True(s.Document!.Layers[0].Stack.All(e => e.Origin == EffectOrigin.Look), "origin=look で入る");
        Check.Equal(40.0, s.Document!.Layers[0].Stack.Single(e => e.Id == "invert").Amount, "amount を持ち越す");
    }

    // ================================================================ 描画(WARP)

    private static (CompositeKit Kit, Target Target) Base()
    {
        var kit = new CompositeKit();
        kit.Import(LookTestImages.Gradient());
        return (kit, Target.OfLayer(0));
    }

    [Test]
    public void AC26_3_vividの強さ50の出力がuserのamount50と完全に一致し強さ0はルックなしと1以内()
    {
        var (kit, t) = Base();
        using (kit)
        {
            var vivid = Look("vivid");
            var none = kit.Render(kit.Doc, 192).Pixels;
            kit.Session.ApplyLook(t, vivid.Effects);
            kit.Session.SetLookStrength(t, 50);
            var half = kit.Render(kit.Doc, 192).Pixels;
            kit.Session.SetLookStrength(t, 0);
            var zero = kit.Render(kit.Doc, 192).Pixels;
            var maxZero = 0;
            for (var i = 0; i < zero.Length; i++) maxZero = Math.Max(maxZero, Math.Abs(zero[i] - none[i]));
            Check.True(maxZero <= 1, $"強さ 0% とルックなしの差 {maxZero}");

            // 同じエフェクトを origin=user・amount 50 で入れた文書(salt も同じにする)
            var looks = kit.Doc.Layers[0].Stack;
            var userDoc = kit.Doc with
            {
                Layers = [kit.Doc.Layers[0] with { LookStrength = 100, Stack = looks.Select(e => e with { Origin = EffectOrigin.User, Amount = 50 }).ToArray() }],
            };
            var user = kit.Render(userDoc, 192).Pixels;
            Check.True(half.AsSpan().SequenceEqual(user), "強さ 50% と origin=user の amount 50% のバイト列");
        }
    }

    [Test]
    public void AC26_3_userの行は強さを変えても結果が変わらない()
    {
        var (kit, t) = Base();
        using (kit)
        {
            kit.AddEffect(t, "contrast", ("c", new NumberValue(40)));
            kit.AddEffect(t, "vignette");
            var a = kit.Render(kit.Doc, 192).Pixels;
            kit.Session.SetLookStrength(t, 20);
            var b = kit.Render(kit.Doc, 192).Pixels;
            Check.True(a.AsSpan().SequenceEqual(b), "origin=user の行の出力が強さで変わった");
        }
    }

    [Test]
    public void AC26_1_サムネイルの文書は形ステージだけを残しそのルックの行だけを足す()
    {
        var s = TwoLayers();
        var a = Target.OfLayer(1);
        Add(s, a, "flip");
        Add(s, a, "invert");
        Add(s, Target.All, "vignette");
        s.SetLayerOpacity(1, 30);
        s.SetLayerBlend(1, BlendMode.Multiply);
        s.SetLookStrength(a, 40);
        var vivid = Look("vivid");
        var p = s.LookPreview(a, vivid.Effects);
        Check.Equal(1, p.Layers.Count, "レイヤー 1 枚の文書");
        var l = p.Layers[0];
        Check.Equal(SessionTestKit.Sha(2), l.Asset, "そのレイヤーのソース");
        Check.True(l.Opacity == 100 && l.Blend == BlendMode.Normal && l.Place == Placement.Identity && l.Visible, "配置・不透明度・ブレンドは使わない");
        Check.Equal(100, l.LookStrength, "ルックの強さは 100 とみなす");
        Check.SequenceEqual(["flip", "saturation", "contrast", "sharpen"], l.Stack.Select(e => e.Id).ToList(), "形ステージとルックの行だけ");
        Check.Equal(0, p.All.Stack.Count, "全体のスタックは空");
        var none = s.LookPreview(a, []);
        Check.SequenceEqual(["flip"], none.Layers[0].Stack.Select(e => e.Id).ToList(), "ルックなしは形ステージだけ");

        // 全体のとき: レイヤーはそのまま、全体のスタックは全体の形ステージとルックの行
        Add(s, Target.All, "flip");
        var all = s.LookPreview(Target.All, vivid.Effects);
        Check.Equal(2, all.Layers.Count, "全体のときはレイヤーを重ねる");
        Check.True(all.Layers[1].Stack.SequenceEqual(s.Document!.Layers[1].Stack), "レイヤーのスタックはそのまま");
        Check.SequenceEqual(["flip", "saturation", "contrast", "sharpen"], all.All.Stack.Select(e => e.Id).ToList(), "全体の形ステージとルックの行");
        Check.Equal(100, all.All.LookStrength, "全体の強さは 100 とみなす");
    }
}
