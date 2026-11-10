// モーションのプリセット 28 個(§9.5)の検査: AC-35 の (6) のプリセットの部分(キー・名前・構成・タイルの t が表と一致)、AC-13 の (2) のプリセットの部分(存在する id と範囲内の値だけ)、
// AC-35 の (7) の適用の行(モーションの行を全部取り除いてから origin=user で入れる・film は 2 個がこの順・Undo の 1 件・ほかの対象は変わらない・64 個)と、タイルの絵の文書(規則 14)の形。
// move・light・noise の id は統合までカタログに無いので、適用と Build の 28 個は表から作った偽の EffectDef のカタログで調べる。埋め込みのカタログでは、そろっている id だけを調べる。
using System.Globalization;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Motion;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;
using GlintLoop.Tests.Session;

namespace GlintLoop.Tests.Motions;

public sealed class PresetsTests
{
    private static readonly Lazy<EffectCatalog> Embedded = new(() => BuiltInEffects.Load(new MemoryLogSink()));

    /// <summary>§9.5 の表(キー・名前・構成・タイルの t)を、仕様書の書き方のまま写したもの(上から順)。</summary>
    internal static readonly (string Key, string Name, string Recipe, double T)[] Spec =
    [
        ("spin", "くるくる", "spin(cycles=1)", 0.25),
        ("spin_ccw", "逆回転", "spin(dir=ccw, cycles=1)", 0.25),
        ("bounce", "ぴょんぴょん", "bounce(cycles=2, height=80)", 0.25),
        ("hop", "ゆるジャンプ", "bounce(cycles=1, height=120, squash=20)", 0.25),
        ("hover", "ふわふわ", "hover(cycles=1)", 0.25),
        ("pulse", "ぷくぷく", "pulse(shape=sine, cycles=2, amp=10)", 0.125),
        ("heartbeat", "ドキドキ", "pulse(shape=heartbeat, cycles=2, amp=14)", 0.1),
        ("shake", "ブルブル", "shake(cycles=1, rate=12, amp=12)", 0.25),
        ("quake", "大揺れ", "shake(cycles=1, rate=8, amp=40, rot=8, smooth=true)", 0.25),
        ("wobble", "ゆらゆら", "wobble(cycles=1, angle=10)", 0.25),
        ("swing", "ブランコ", "wobble(cycles=1, angle=25, pivot=(0.5, 0.0))", 0.25),
        ("jelly", "ぷるぷる", "jelly(cycles=3)", 0.25),
        ("coin", "コイン", "coinflip(axis=y, cycles=1)", 0.125),
        ("marquee", "流れる", "slide(dir=left, cycles=1)", 0.25),
        ("blink", "ピカピカ", "blink(mode=opacity, shape=square, cycles=3)", 0.05),
        ("flash", "フラッシュ", "blink(mode=flash, shape=square, duty=20, cycles=2)", 0.25),
        ("rainbow", "虹色", "huecycle(mode=full, cycles=1)", 0.25),
        ("neon", "ネオン", "glowpulse(cycles=2)", 0.25),
        ("glint", "キラッ", "shine(cycles=1)", 0.25),
        ("leak", "ライトリーク", "leaks(palette=warm, cycles=1)", 0.25),
        ("glitch", "グリッチ", "glitch(cycles=1)", 0.25),
        ("rgb", "RGBずれ", "rgbsplit(cycles=2)", 0.125),
        ("ripple", "波紋", "ripple(cycles=1)", 0.25),
        ("sparkle", "キラキラ", "sparkle(cycles=2)", 0.25),
        ("snow", "雪", "particles(kind=snow, dir=down, cycles=1)", 0.25),
        ("confetti", "紙吹雪", "particles(kind=confetti, tone=rainbow, count=60, cycles=1)", 0.25),
        ("bubbles", "泡", "particles(kind=bubbles, dir=up, cycles=1)", 0.25),
        ("film", "フィルム", "grainmove(cycles=1, rate=6)、leaks(palette=warm, intensity=40, cycles=1)", 0.25),
    ];

    /// <summary>move・light・noise の担当が作る 18 個の id(統合まで埋め込みのカタログに無くてよいもの)。</summary>
    private static readonly HashSet<string> OtherWorkersIds = new(StringComparer.Ordinal)
    {
        "bounce", "hover", "pulse", "shake", "wobble", "jelly", "coinflip",
        "blink", "huecycle", "glowpulse", "shine", "leaks", "rgbsplit",
        "glitch", "ripple", "sparkle", "particles", "grainmove",
    };

    private static string Text(object v) => v switch
    {
        double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => s,
        PointValue p => string.Format(CultureInfo.InvariantCulture, "({0:0.0##}, {1:0.0##})", p.X, p.Y),
        _ => throw new CheckFailedException("表の値の型が不明: " + v.GetType().Name),
    };

    private static string Recipe(PresetRaw r) =>
        string.Join("、", r.Effects.Select(e => $"{e.Id}({string.Join(", ", e.Params.Select(p => $"{p.Key}={Text(p.Value)}"))})"));

    [Test]
    public void AC35_6_プリセット28個のキーと名前と構成とタイルのtが表と一致する()
    {
        var table = BuiltInMotionPresets.Table;
        Check.Equal(28, table.Count, "プリセットの数");
        Check.SequenceEqual(Spec.Select(s => s.Key).ToList(), table.Select(r => r.Key).ToList(), "キーの順");
        Check.SequenceEqual(Spec.Select(s => s.Name).ToList(), table.Select(r => r.Name).ToList(), "名前の順");
        for (var i = 0; i < Spec.Length; i++)
        {
            Check.Equal(Spec[i].Recipe, Recipe(table[i]), Spec[i].Key + " の構成");
            Check.Equal(Spec[i].T, (double)table[i].TileK / table[i].TileN, Spec[i].Key + " のタイルの t");
        }
        // AC-35 の (7) が例に挙げる値
        Check.Equal((1, 4), (table[0].TileK, table[0].TileN), "spin は 0.25");
        Check.Equal((1, 8), (table[5].TileK, table[5].TileN), "pulse は 0.125");
        Check.Equal((1, 10), (table[6].TileK, table[6].TileN), "heartbeat は 0.1");
        Check.Equal((1, 20), (table[14].TileK, table[14].TileN), "blink は 0.05");
        // 表が使う id は §9.5 のモーション 20 個(20 個とも、どれかのプリセットに出る)
        Check.Equal(20, BuiltInMotionPresets.UsedIds().Count, "表が使う id の数");
        Check.True(BuiltInMotionPresets.UsedIds().All(id => id is "spin" or "slide" || OtherWorkersIds.Contains(id)), "表が §9.5 に無い id を使う");
    }

    [Test]
    public void AC13_2_プリセットの構成は埋め込みのカタログにあるidと範囲内の値だけを使う()
    {
        // 統合の前は move・light・noise の id が無い。無い id は、その 18 個に限る。そろったあとは無い id が 0 個で、28 個のすべてを調べる(統合後に回すテスト)
        var catalog = Embedded.Value;
        var missing = BuiltInMotionPresets.MissingIds(catalog);
        Check.True(missing.All(OtherWorkersIds.Contains), "spin・slide のほかに無い id がある: " + string.Join(",", missing));
        Check.True(!missing.Contains("spin") && !missing.Contains("slide"), "見本の spin・slide が無い");
        var problems = BuiltInMotionPresets.Validate(catalog);
        var real = problems.Where(p => !p.Contains(BuiltInMotionPresets.MissingMark, StringComparison.Ordinal)).ToList();
        Check.True(real.Count == 0, "プリセットの構成の問題: " + string.Join(" / ", real));
        foreach (var p in problems.Except(real))
            Check.True(missing.Any(id => p.Contains("id " + id + " ", StringComparison.Ordinal)), "無い id の文が一覧と合わない: " + p);
        // そろっている id のプリセット(spin・spin_ccw・marquee)は、全エフェクトが入る
        var built = BuiltInMotionPresets.Build(catalog);
        foreach (var key in new[] { "spin", "spin_ccw", "marquee" })
        {
            var p = built.Single(x => x.Key == key);
            Check.Equal(0, p.MissingIds.Count, key + " に無い id");
            Check.Equal(1, p.Effects.Count, key + " のエフェクトの数");
        }
        Check.Equal(new StringValue("ccw"), built.Single(x => x.Key == "spin_ccw").Effects[0].Get("dir"), "spin_ccw の dir");
        Check.Equal(new StringValue("left"), built.Single(x => x.Key == "marquee").Effects[0].Get("dir"), "marquee の dir");
    }

    // ================================================================ 表から作った偽のカタログ

    private static ParamDef Fake(string key, object sample, IEnumerable<string> enumValues) => sample switch
    {
        double => new ParamDef(key, key, ParamType.Float, -1000, 1000, 1, new NumberValue(0), "", null, null, false, true, null, null),
        bool => new ParamDef(key, key, ParamType.Bool, null, null, null, new BoolValue(false), "", null, null, false, true, null, null),
        string => new ParamDef(key, key, ParamType.Enum, null, null, null, new StringValue("dflt"), "",
            enumValues.Prepend("dflt").Distinct().Select(v => new EnumOption(v, v)).ToArray(), null, false, true, null, null),
        PointValue => new ParamDef(key, key, ParamType.Point, null, null, null, new PointValue(0.5, 0.5), "", null, null, false, true, null, null),
        _ => throw new CheckFailedException("表の値の型が不明"),
    };

    /// <summary>表の 20 個の id を、表に出るキーだけを宣言したモーションの EffectDef にしたもの(cycles・phase・wave は EffectSchema が足す)と、SessionTestKit の形・ルック・仕上げ。</summary>
    internal static DictionaryEffectCatalog FakeCatalog()
    {
        var defs = SessionTestKit.Defs.Where(d => d.Stage != EffectStage.Motion).ToList();
        var effects = BuiltInMotionPresets.Table.SelectMany(r => r.Effects).ToList();
        foreach (var id in BuiltInMotionPresets.UsedIds())
        {
            var ps = effects.Where(e => e.Id == id).SelectMany(e => e.Params).Where(p => p.Key != "cycles").ToList();
            var keys = ps.Select(p => p.Key).Distinct().ToList();
            var paramDefs = keys.Select(k => Fake(k, ps.First(p => p.Key == k).Value, ps.Where(p => p.Key == k).Select(p => p.Value).OfType<string>())).ToArray();
            defs.Add(new EffectDef(1, id, id, EffectStage.Motion, "g", Array.Empty<string>(), AmountMode.None, Array.Empty<string>(), true, false,
                WaveKind.Sine, SizeRule.Same, AuxKind.None, paramDefs, Array.Empty<PassDef>()));
        }
        return new DictionaryEffectCatalog(defs);
    }

    private static object Raw(ParamValue v) => v switch
    {
        NumberValue n => n.Value,
        BoolValue b => b.Value,
        StringValue s => s.Value,
        PointValue p => p,
        _ => throw new CheckFailedException("値の型が不明"),
    };

    [Test]
    public void AC35_6_Buildは表の値を変えずに全キーの並びにして検査を通る()
    {
        var catalog = FakeCatalog();
        Check.True(BuiltInMotionPresets.Validate(catalog).Count == 0, "偽のカタログでの問題: " + string.Join(" / ", BuiltInMotionPresets.Validate(catalog)));
        var presets = BuiltInMotionPresets.Build(catalog);
        Check.Equal(28, presets.Count, "プリセットの数");
        for (var i = 0; i < presets.Count; i++)
        {
            var raw = BuiltInMotionPresets.Table[i];
            var p = presets[i];
            Check.Equal(raw.Key, p.Key, "キー");
            Check.Equal(0, p.MissingIds.Count, raw.Key + " に無い id");
            Check.SequenceEqual(raw.Effects.Select(e => e.Id).ToList(), p.Effects.Select(e => e.Id).ToList(), raw.Key + " の id の並び");
            for (var j = 0; j < raw.Effects.Count; j++)
            {
                var all = EffectSchema.AllParams(catalog.Find(raw.Effects[j].Id)!);
                Check.SequenceEqual(all.Select(x => x.Key).ToList(), p.Effects[j].Params.Select(x => x.Key).ToList(), $"{raw.Key}.{raw.Effects[j].Id} のキーの並び");
                foreach (var pd in all)
                {
                    var written = raw.Effects[j].Params.FirstOrDefault(x => x.Key == pd.Key);
                    var expected = written is null ? Raw(pd.Default) : written.Value;
                    Check.Equal(expected, Raw(p.Effects[j].Get(pd.Key)!), $"{raw.Key}.{raw.Effects[j].Id}.{pd.Key}");
                }
                // phase は既定値(0)のまま
                Check.Equal(new NumberValue(0), p.Effects[j].Get("phase"), raw.Key + " の phase");
            }
        }
    }

    [Test]
    public void AC13_2_検査が無いidとモーション以外のステージと範囲外と列挙に無い値を見つける()
    {
        var defs = FakeCatalog().Effects
            .Where(d => d.Id != "hover")
            .Select(d => d.Id switch
            {
                "bounce" => d with { Params = d.Params.Select(p => p.Key == "height" ? p with { Max = 50 } : p).ToArray() },
                "coinflip" => d with { Params = d.Params.Select(p => p.Key == "axis" ? p with { Options = [new EnumOption("x", "x")] } : p).ToArray() },
                "jelly" => d with { Stage = EffectStage.Look },
                "shake" => d with { Params = d.Params.Where(p => p.Key != "rot").ToArray() },
                _ => d,
            }).ToArray();
        var problems = BuiltInMotionPresets.Validate(new DictionaryEffectCatalog(defs));
        bool Has(string prefix) => problems.Any(p => p.StartsWith(prefix, StringComparison.Ordinal));
        Check.True(Has("hover: id hover " + BuiltInMotionPresets.MissingMark), "無い id を見つけない: " + string.Join(" / ", problems));
        Check.True(Has("bounce: bounce.height") && Has("hop: bounce.height"), "範囲外を見つけない");
        Check.True(Has("coin: coinflip.axis"), "列挙に無い値を見つけない");
        Check.True(Has("jelly: jelly のステージ"), "モーション以外のステージを見つけない");
        Check.True(Has("quake: shake にパラメータ rot が無い"), "無いキーを見つけない");
        Check.SequenceEqual(["hover"], BuiltInMotionPresets.MissingIds(new DictionaryEffectCatalog(defs)), "無い id の一覧");
    }

    // ================================================================ 適用(FR-64)

    private static EditorSession TwoLayers(IEffectCatalog catalog)
    {
        var s = new EditorSession(catalog, new FixedSeedSource(12345));
        s.AddImportedImages([SessionTestKit.Image(1, 200, 100), SessionTestKit.Image(2, 80, 60)]);
        return s;
    }

    private static void Add(EditorSession s, Target t, string id) => Check.True(s.AddEffect(t, id).Changed, id + " を足せない");

    [Test]
    public void AC35_7_28個のプリセットでモーションの行を全部取り除いてからoriginuserで入れUndoの1件で戻る()
    {
        var catalog = FakeCatalog();
        foreach (var preset in BuiltInMotionPresets.Build(catalog))
        {
            var s = TwoLayers(catalog);
            var a = Target.OfLayer(1);
            Add(s, a, "flip");
            Add(s, a, "invert");
            Add(s, a, "jelly");
            Add(s, a, "shake");
            Check.True(s.SetEffectEnabled(a, 3, false).Changed, "目をオフにした行も取り除く");
            Add(s, a, "outline");
            Add(s, Target.OfLayer(0), "spin");
            Add(s, Target.All, "wobble");
            var before = s.Document!;
            var json = s.Json;
            var undo = s.UndoCount;
            var o = s.ApplyMotionPreset(a, preset.Effects);
            Check.True(o.Changed, preset.Key + " を適用できない");
            var stack = s.Document!.Layers[1].Stack;
            Check.SequenceEqual(["flip", "invert", "outline"], stack.Take(3).Select(e => e.Id).ToList(), preset.Key + " のモーション以外の行");
            var motion = stack.Skip(3).ToList();
            Check.SequenceEqual(preset.Effects.Select(e => e.Id).ToList(), motion.Select(e => e.Id).ToList(), preset.Key + " のモーションの行(表の順)");
            for (var i = 0; i < motion.Count; i++)
            {
                Check.Equal(EffectOrigin.User, motion[i].Origin, preset.Key + " の origin");
                Check.True(motion[i].Enabled, preset.Key + " の目");
                Check.Equal(100.0, motion[i].Amount, preset.Key + " の amount");
                Check.SequenceEqual(preset.Effects[i].Params, motion[i].Params, $"{preset.Key}.{motion[i].Id} の値");
                Check.Equal(before.SaltCounter + i + 1, motion[i].Salt, preset.Key + " の salt");
            }
            // ほかの対象のスタックは変わらない
            Check.True(ReferenceEquals(before.Layers[0].Stack, s.Document.Layers[0].Stack) || before.Layers[0].Stack.SequenceEqual(s.Document.Layers[0].Stack), preset.Key + " で土台が変わった");
            Check.True(before.All.Stack.SequenceEqual(s.Document.All.Stack), preset.Key + " で全体が変わった");
            // Undo の 1 件
            Check.Equal(undo + 1, s.UndoCount, preset.Key + " の Undo の件数");
            Check.True(s.Undo(), preset.Key + " を Undo できない");
            Check.Equal(json, s.Json, preset.Key + " の Undo で元に戻らない");
        }
    }

    [Test]
    public void AC35_7_filmはgrainmoveとleaksの2個がこの順で入り全体にも適用できる()
    {
        var catalog = FakeCatalog();
        var film = BuiltInMotionPresets.Build(catalog).Single(p => p.Key == "film");
        var s = TwoLayers(catalog);
        Add(s, Target.All, "flip");
        Add(s, Target.All, "spin");
        Add(s, Target.OfLayer(1), "spin");
        Check.True(s.ApplyMotionPreset(Target.All, film.Effects).Changed, "全体に適用できない");
        Check.SequenceEqual(["flip", "grainmove", "leaks"], s.Document!.All.Stack.Select(e => e.Id).ToList(), "全体のスタック");
        Check.SequenceEqual(["spin"], s.Document.Layers[1].Stack.Select(e => e.Id).ToList(), "レイヤーは変わらない");
        Check.True(s.Document.All.Stack.Skip(1).All(e => e.Origin == EffectOrigin.User), "origin=user");
    }

    [Test]
    public void AC35_7_モーションなしはモーションの行だけを取り除き無いときは履歴に積まない()
    {
        var catalog = FakeCatalog();
        var s = TwoLayers(catalog);
        var a = Target.OfLayer(1);
        Add(s, a, "invert");
        var undo = s.UndoCount;
        Check.True(!s.RemoveMotion(a).Changed, "モーションの行が無いのに変わった");
        Check.Equal(undo, s.UndoCount, "何も変えない「モーションなし」を履歴に積んだ");
        Add(s, a, "spin");
        Add(s, a, "hover");
        Check.True(s.RemoveMotion(a).Changed, "モーションの行を取り除かない");
        Check.SequenceEqual(["invert"], s.Document!.Layers[1].Stack.Select(e => e.Id).ToList(), "残る行");
    }

    [Test]
    public void AC35_7_64個を超える適用は何も変えずにstackfullを返す()
    {
        var catalog = FakeCatalog();
        var film = BuiltInMotionPresets.Build(catalog).Single(p => p.Key == "film");
        var a = Target.OfLayer(1);
        // モーションの 1 行(spin)を取り除いて 62 + 2 = 64 は入る
        var s = TwoLayers(catalog);
        for (var i = 0; i < 62; i++) Add(s, a, "invert");
        Add(s, a, "spin");
        Check.True(s.ApplyMotionPreset(a, film.Effects).Changed, "64 個ちょうどが入らない");
        Check.Equal(64, s.Document!.Layers[1].Stack.Count, "64 個");
        // 63 + 2 = 65 は入らない
        s = TwoLayers(catalog);
        for (var i = 0; i < 63; i++) Add(s, a, "invert");
        Add(s, a, "spin");
        Check.Equal(64, s.Document!.Layers[1].Stack.Count, "準備: 63 個の invert と spin");
        var json = s.Json;
        var o = s.ApplyMotionPreset(a, film.Effects);
        Check.True(!o.Changed, "65 個目を入れた");
        Check.Equal(NoticeKeys.StackFull, o.Notice?.Key, "stack.full");
        Check.Equal(json, s.Json, "拒否したのに文書が変わった");
    }

    // ================================================================ AC-34 の (4)(WARP)

    /// <summary>
    /// AC-34 の (4): プリセットを適用したスタック全体で、(1) 閉じ方と (2) 連続(波形が saw のエフェクトを含むときは除く)を満たす。画像は AC-34 の 2 枚、seed=12345、N=40。
    /// 統合の前は、エフェクトがカタログにそろっているプリセット(spin・spin_ccw・marquee)だけを調べる。統合のあとは同じテストが 28 個のすべてを調べる。
    /// </summary>
    [Test(Slow = true)]
    public void AC34_4_プリセットを適用したスタック全体の閉じ方と連続()
    {
        foreach (var img in MotionTestKit.Ac34Images())
        {
            using var kit = new CompositeKit();
            kit.Import(img);
            var presets = BuiltInMotionPresets.Build(kit.Rs.Effects);
            var ready = presets.Where(p => p.MissingIds.Count == 0).ToList();
            foreach (var key in new[] { "spin", "spin_ccw", "marquee" }) Check.True(ready.Any(p => p.Key == key), key + " を調べられない");
            foreach (var p in ready)
            {
                Check.True(kit.Session.ApplyMotionPreset(Target.OfLayer(0), p.Effects).Changed, p.Key + " を適用できない");
                var doc = kit.Doc;
                Check.Equal(12345, doc.Seed, "seed");
                FrameRender render = (k, n) => kit.Renderer.RenderAsync(doc, MotionTestKit.Size, DocumentPixelFormat.StraightRgba8, k, n).GetAwaiter().GetResult().Pixels;
                MotionTestKit.Pass(MotionTestKit.CheckClosure(render), p.Key);
                var saw = doc.Base.Stack.Any(e => e.Get("wave") is StringValue { Value: "saw" });
                if (!saw) MotionTestKit.Pass(MotionTestKit.CheckContinuity(render), p.Key);
            }
        }
    }

    // ================================================================ タイルの絵の文書(規則 14)

    [Test]
    public void AC36_6_レイヤーのタイルの文書は配置どおりのそのレイヤーだけにプリセットを通す()
    {
        var catalog = FakeCatalog();
        var spin = BuiltInMotionPresets.Build(catalog).Single(p => p.Key == "spin");
        var s = new EditorSession(catalog, new FixedSeedSource(12345));
        s.AddImportedImages([SessionTestKit.Image(1, 200, 200), SessionTestKit.Image(2, 100, 50), SessionTestKit.Image(3, 30, 30)]);
        s.SetLayerPlacement(1, 40, -30, 100);
        s.SetLayerOpacity(1, 40);
        s.SetLayerBlend(1, BlendMode.Screen);
        s.SetLayerVisible(1, false);
        Add(s, Target.OfLayer(0), "rotate");
        Add(s, Target.OfLayer(0), "flip");
        Add(s, Target.OfLayer(1), "invert");
        Add(s, Target.OfLayer(1), "jelly");
        Add(s, Target.All, "flip");
        Add(s, Target.All, "hover");
        var doc = s.Document!;
        var view = s.MotionPreview(Target.OfLayer(1), spin.Effects);
        Check.Equal(2, view.Layers.Count, "土台と対象の 2 枚(ほかのレイヤーは除く)");
        Check.True(!view.Layers[0].Visible, "土台は重ねない");
        Check.SequenceEqual(["rotate"], view.Layers[0].Stack.Select(e => e.Id).ToList(), "土台は作業面の大きさに効く行だけ");
        var l = view.Layers[1];
        Check.True(l.Visible, "対象は表示");
        Check.Equal(100, l.Opacity, "不透明度 100");
        Check.Equal(BlendMode.Normal, l.Blend, "ブレンドは通常");
        Check.Equal(doc.Layers[1].Place, l.Place, "配置どおり");
        Check.SequenceEqual(["invert", "spin"], l.Stack.Select(e => e.Id).ToList(), "対象のモーションの行はプリセットだけ");
        Check.Equal(0, view.All.Stack.Count, "全体のスタックは空");
        Check.Equal(doc.SaltCounter + 1, l.Stack[1].Salt, "salt は適用と同じ振り方");
        Check.Equal(doc.SaltCounter, s.Document!.SaltCounter, "文書は変えない");
        // 作業面の大きさは元の合成と同じ(土台のネイティブ寸法)
        Check.Equal(CompositeGeometry.CompositeNative(doc, _ => new SizeD(200, 200)), CompositeGeometry.CompositeNative(view, _ => new SizeD(200, 200)), "作業面の大きさ");

        var baseView = s.MotionPreview(Target.OfLayer(0), spin.Effects);
        Check.Equal(1, baseView.Layers.Count, "土台が対象なら 1 枚");
        Check.SequenceEqual(["rotate", "flip", "spin"], baseView.Layers[0].Stack.Select(e => e.Id).ToList(), "土台のモーションの行はプリセットだけ");
    }

    [Test]
    public void AC36_6_全体のタイルの文書は各レイヤーのモーションを外した合成にプリセットだけを通す()
    {
        var catalog = FakeCatalog();
        var film = BuiltInMotionPresets.Build(catalog).Single(p => p.Key == "film");
        var s = TwoLayers(catalog);
        Add(s, Target.OfLayer(0), "spin");
        Add(s, Target.OfLayer(1), "invert");
        Add(s, Target.OfLayer(1), "hover");
        Add(s, Target.All, "flip");
        Add(s, Target.All, "invert");
        Add(s, Target.All, "jelly");
        Check.True(s.SetLookStrength(Target.All, 30).Changed, "全体のルックの強さ");
        var view = s.MotionPreview(Target.All, film.Effects);
        Check.Equal(2, view.Layers.Count, "レイヤーはそのまま");
        Check.Equal(0, view.Layers[0].Stack.Count, "土台のモーションは通さない");
        Check.SequenceEqual(["invert"], view.Layers[1].Stack.Select(e => e.Id).ToList(), "レイヤーのモーションは通さない");
        Check.SequenceEqual(["grainmove", "leaks"], view.All.Stack.Select(e => e.Id).ToList(), "全体はプリセットのモーションだけ(Q-L)");
        Check.Equal(30, view.All.LookStrength, "全体のルックの強さは変えない");
    }
}
