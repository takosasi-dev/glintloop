// EffectDef の読み込みと検査のテスト(AC-13 の (1)(3)(4) と、埋め込んだ全 EffectDef が有効であること)、実効 amount の式(AC-14 の (3))。
// 無効な宣言は、有効な基の宣言(§9.2 の例 1 の露出)を 1 か所だけ書き換えて作る。GPU は使わない。
// AC-13 の (1) のカタログの個数は Phase 2 の分(形 7・ルック 21)。仕上げとモーションは、作るフェーズで表の id を足す。
using System.IO;
using System.Reflection;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Static;

namespace GlintLoop.Tests.Effects;

public sealed class EffectDefLoaderTests
{
    /// <summary>§9.2 の例 1 に、検査の 8 と 9 を試すための enum のパラメータ kind と visibleWhen を足した有効な宣言。</summary>
    private const string BaseJson = """
        {
          "schema": 1,
          "id": "exposure",
          "name": "露出",
          "stage": "look",
          "group": "基本",
          "keywords": ["明るさ", "ev", "brightness"],
          "amountMode": "mix",
          "timeDependent": false,
          "sizeRule": "same",
          "params": [
            {"key": "ev", "label": "露出", "type": "float", "min": -3, "max": 3, "default": 0, "step": 0.01, "unit": "EV"},
            {"key": "kind", "label": "種類", "type": "enum", "default": "a",
             "options": [{"value": "a", "label": "甲"}, {"value": "b", "label": "乙"}]},
            {"key": "gain", "label": "利得", "type": "float", "min": 0, "max": 2, "default": 1, "visibleWhen": {"kind": ["b"]}}
          ],
          "passes": [
            {"id": "main", "shader": "exposure", "inputs": ["src"], "scale": 1, "bind": {"ev": "ev", "amount": "$amount"}},
            {"id": "post", "shader": "exposure", "inputs": ["pass:main"], "scale": 1, "when": {"kind": ["b"]}, "bind": {"ev": "gain"}}
          ]
        }
        """;

    private static bool OnlyExposure(string shader) => shader == "exposure";

    private static EffectCatalog Load(MemoryLogSink log, params string[] jsons) =>
        EffectCatalog.LoadFromSources(jsons.Select((j, i) => ($"t{i}.json", j)), OnlyExposure, log);

    private static string Replace(string from, string to)
    {
        Check.True(BaseJson.Contains(from, StringComparison.Ordinal), $"基の宣言に「{from}」が無い(テストの誤り)");
        return BaseJson.Replace(from, to, StringComparison.Ordinal);
    }

    [Test]
    public void AC13_基の宣言は有効で全項目が読める()
    {
        var log = new MemoryLogSink();
        var catalog = Load(log, BaseJson);
        Check.Equal(0, log.Events.Count, "ログの件数");
        Check.Equal(1, catalog.Effects.Count, "読めた数");
        var d = catalog.Effects[0];
        Check.Equal("exposure", d.Id, "id");
        Check.Equal(EffectStage.Look, d.Stage, "stage");
        Check.Equal(AmountMode.Mix, d.AmountMode, "amountMode");
        Check.Equal(SizeRule.Same, d.SizeRule, "sizeRule");
        Check.Equal(WaveKind.Sine, d.WaveDefault, "waveDefault の省略時");
        Check.Equal(AuxKind.None, d.Aux, "aux の省略時");
        Check.Equal(3, d.Params.Count, "params の数");
        Check.Equal(0.01, d.Params[0].Step, "ev の step");
        Check.Equal("EV", d.Params[0].Unit, "ev の unit");
        Check.Equal<ParamValue>(new NumberValue(0), d.Params[0].Default, "ev の default");
        Check.Equal<ParamValue>(new StringValue("a"), d.Params[1].Default, "kind の default");
        Check.Equal("kind", d.Params[2].VisibleWhen!.Key, "visibleWhen のキー");
        Check.Equal(2, d.Passes.Count, "passes の数");
        Check.SequenceEqual(new[] { "ev", "amount" }, d.Passes[0].Bind.Select(b => b.Variable).ToArray(), "bind の左辺の順");
        Check.SequenceEqual(new[] { "ev", "$amount" }, d.Passes[0].Bind.Select(b => b.Source).ToArray(), "bind の右辺の順");
        Check.Equal(PassRepeat.Once, d.Passes[0].Repeat, "repeat の省略時");
        Check.SequenceEqual(new[] { "b" }, d.Passes[1].When!.Values.ToArray(), "when の値");
    }

    // 9 項目のそれぞれに 1 つずつ(AC-13 の (3))。1 は同じ id の宣言を 2 つ渡す
    private static readonly (int Reason, string Json)[] OnePerItem =
    [
        (2, Replace("\"min\": -3, \"max\": 3", "\"min\": 3, \"max\": -3")),
        (3, Replace("\"inputs\": [\"pass:main\"]", "\"inputs\": [\"pass:nothere\"]")),
        (4, Replace("\"bind\": {\"ev\": \"gain\"}", "\"bind\": {\"ev\": \"nokey\"}")),
        (5, Replace("\"amountMode\": \"mix\"", "\"amountMode\": \"scale\"")),
        (6, Replace("\"shader\": \"exposure\", \"inputs\": [\"src\"]", "\"shader\": \"missing\", \"inputs\": [\"src\"]")),
        (7, Replace("\"name\": \"露出\"", "\"name\": \"露出露出露出露出露出露出露\"")),
        (8, Replace("\"visibleWhen\": {\"kind\": [\"b\"]}", "\"visibleWhen\": {\"nokey\": [\"b\"]}")),
        (9, Replace("\"sizeRule\": \"same\"", "\"sizeRule\": \"huge\"")),
    ];

    [Test]
    public void AC13_9項目の無効な宣言はどれも読まれずreasonが項目の番号になる()
    {
        // 1: id の重複(両方とも読まない)
        var dupLog = new MemoryLogSink();
        var dup = Load(dupLog, BaseJson, BaseJson);
        Check.Equal(0, dup.Effects.Count, "1: 読めた数");
        Check.Equal(2, dupLog.Events.Count, "1: ログの件数");
        foreach (var e in dupLog.Events) CheckEvent(e, 1, "exposure");

        foreach (var (reason, json) in OnePerItem)
        {
            var log = new MemoryLogSink();
            var catalog = Load(log, json);
            Check.Equal(0, catalog.Effects.Count, $"{reason}: 読めた数");
            Check.Equal(1, log.Events.Count, $"{reason}: ログの件数");
            CheckEvent(log.Events[0], reason, "exposure");
            Check.Equal(reason, catalog.Rejected.Single().Reason, $"{reason}: Rejected の reason({catalog.Rejected.Single().Detail})");
        }
    }

    private static void CheckEvent(LogEvent e, int reason, string id)
    {
        Check.Equal(EffectDefLoader.InvalidEvent, e.Name, $"{reason}: event 名");
        Check.Equal<object?>(reason, e.Get("reason"), $"{reason}: reason");
        Check.Equal<object?>(id, e.Get("id"), $"{reason}: id");
        // INV-15: id と reason だけを書く
        Check.SequenceEqual(new[] { "id", "reason" }, e.Fields.Select(f => f.Key).ToArray(), $"{reason}: 項目");
    }

    // 各項目のほかの当てはまり方(どれも読まれず、reason が項目の番号になる)
    private static readonly (int Reason, string Json, string What)[] MoreCases =
    [
        (2, Replace("\"default\": 0, \"step\": 0.01", "\"default\": 4, \"step\": 0.01"), "default が範囲の外"),
        (2, Replace("\"type\": \"enum\", \"default\": \"a\"", "\"type\": \"enum\", \"default\": \"c\""), "enum の default が options に無い"),
        (2, Replace("{\"key\": \"gain\"", "{\"key\": \"ev\""), "key の重複"),
        (3, Replace("\"inputs\": [\"src\"]", "\"inputs\": [\"pass:post\"]"), "パスの参照の循環"),
        (3, Replace("\"inputs\": [\"src\"]", "\"inputs\": [\"src\", \"src\", \"src\", \"src\", \"src\"]"), "入力が 4 個を超える"),
        (3, Replace("\"scale\": 1, \"bind\": {\"ev\": \"ev\"", "\"scale\": 0.3, \"bind\": {\"ev\": \"ev\""), "scale が 4 つの値のどれでもない"),
        (4, Replace("\"amount\": \"$amount\"", "\"amount\": \"$strength\""), "組み込み変数に無い"),
        (5, Replace("\"amountMode\": \"mix\"", "\"amountMode\": \"scale\", \"scaleParams\": [\"kind\"]"), "scaleParams が数値型でない"),
        (7, Replace("\"id\": \"exposure\"", "\"id\": \"Exposure\""), "id に大文字"),
        (7, Replace("{\"key\": \"gain\"", "{\"key\": \"Gain\""), "key に大文字"),
        (7, Replace("{\"value\": \"b\"", "{\"value\": \"_b\""), "options の value の形式"),
        (7, Replace("\"group\": \"基本\"", "\"group\": \"基本基本基本基本基\""), "group が 8 文字を超える"),
        (7, Replace("\"label\": \"露出\"", "\"label\": \"露出露出露出露出露出露出露出露出露\""), "label が 16 文字を超える"),
        (7, Replace("[\"明るさ\", \"ev\", \"brightness\"]", "[\"a\", \"b\", \"c\", \"d\", \"e\", \"f\", \"g\", \"h\", \"i\"]"), "keywords が 8 個を超える"),
        (8, Replace("\"when\": {\"kind\": [\"b\"]}", "\"when\": {\"kind\": [\"z\"]}"), "when の値が options に無い"),
        (8, Replace("\"amountMode\": \"mix\"", "\"amountMode\": \"scale\", \"scaleParams\": [\"nokey\"]"), "scaleParams の key が無い"),
        (9, Replace("\"timeDependent\": false", "\"timeDependent\": false, \"waveDefault\": \"square\""), "waveDefault の値でない"),
        (9, Replace("\"timeDependent\": false", "\"timeDependent\": false, \"aux\": \"image\""), "aux の値でない"),
        (9, Replace("\"type\": \"enum\", \"default\": \"a\"", "\"type\": \"enum\", \"min\": 0, \"default\": \"a\""), "enum に min"),
        (9, Replace("\"default\": 1, \"visibleWhen\"", "\"default\": 1, \"options\": [], \"visibleWhen\""), "float に options"),
        (9, Replace("\"default\": 1, \"visibleWhen\"", "\"default\": 1, \"maxLength\": 4, \"visibleWhen\""), "float に maxLength"),
    ];

    [Test]
    public void AC13_各項目のほかの当てはまり方も読まれない()
    {
        foreach (var (reason, json, what) in MoreCases)
        {
            var log = new MemoryLogSink();
            var catalog = Load(log, json);
            Check.Equal(0, catalog.Effects.Count, $"{what}: 読めた数");
            Check.Equal(1, log.Events.Count, $"{what}: ログの件数");
            Check.Equal<object?>(reason, log.Events[0].Get("reason"), $"{what}: reason({catalog.Rejected.Single().Detail})");
        }
    }

    [Test]
    public void AC13_無効な宣言があっても有効な宣言は読まれる()
    {
        var log = new MemoryLogSink();
        var other = Replace("\"id\": \"exposure\"", "\"id\": \"other\"");
        var bad = Replace("\"id\": \"exposure\"", "\"id\": \"broken\"").Replace("\"sizeRule\": \"same\"", "\"sizeRule\": 1", StringComparison.Ordinal);
        var catalog = Load(log, BaseJson, bad, other, "{ これは JSON でない");
        Check.SequenceEqual(new[] { "exposure", "other" }, catalog.Effects.Select(e => e.Id).ToArray(), "読めた id");
        Check.SequenceEqual(new object?[] { 9, 7 }, log.Events.Select(e => e.Get("reason")).ToArray(), "reason");
        Check.SequenceEqual(new object?[] { "broken", "" }, log.Events.Select(e => e.Get("id")).ToArray(), "id(読めないものは空)");
        Check.True(catalog.Find("other") is not null && catalog.Find("broken") is null, "Find");
    }

    [Test]
    public void AC13_stepが無いときは9_4の既定の刻みを入れる()
    {
        string P(string type, int min, int max) =>
            $"{{\"key\": \"p\", \"label\": \"p\", \"type\": \"{type}\", \"min\": {min}, \"max\": {max}, \"default\": {min}}}";
        foreach (var (param, expected) in new[]
                 {
                     (P("float", -3, 3), 0.01), (P("float", 0, 10), 0.1), (P("float", -50, 49), 0.1), (P("float", 0, 100), 1.0),
                     (P("int", 0, 5), 1.0), (P("angle", -180, 180), 1.0), (P("angle", 0, 5), 1.0),
                 })
        {
            var json = $$"""
                {"schema": 1, "id": "s", "name": "s", "stage": "look", "group": "g", "keywords": [], "amountMode": "none",
                 "timeDependent": false, "sizeRule": "same", "params": [{{param}}], "passes": []}
                """;
            var log = new MemoryLogSink();
            var catalog = Load(log, json);
            Check.Equal(1, catalog.Effects.Count, $"{param}: 読めた数({string.Join(", ", catalog.Rejected.Select(r => r.Detail))})");
            Check.Equal<double?>(expected, catalog.Effects[0].Params[0].Step, $"{param}: step");
        }
    }

    [Test]
    public void AC13_埋め込まれた全EffectDefが有効()
    {
        var log = new MemoryLogSink();
        var catalog = BuiltInEffects.Load(log);
        Check.True(catalog.Rejected.Count == 0,
            "無効な EffectDef: " + string.Join(" / ", catalog.Rejected.Select(r => $"{r.Id}: 項目 {r.Reason}: {r.Detail}")));
        Check.Equal(0, log.Events.Count, "ログの件数");
        Check.Equal(BuiltInEffects.ResourceCount, catalog.Effects.Count, "読めた数と埋め込みのリソースの数");
        Check.True(catalog.Find("exposure") is not null, "exposure が無い");
    }

    // §9.4 の表の id(Phase 2 で作る形ステージとルックステージ、Phase 3 の仕上げ)。モーション(20 個)は Phase 4 で足す。
    private static readonly string[] ShapeIds = ["rotate", "crop", "pad", "flip", "mask", "chroma", "edge"];
    private static readonly string[] LookIds =
    [
        "exposure", "contrast", "levels", "tone", "fade",
        "wb", "saturation", "hue", "splittone", "gradmap", "mono", "invert", "channelmix",
        "posterize", "threshold", "palette", "pixelate", "bleach", "crossproc",
        "tint", "gradient",
    ];

    // 仕上げの 15 個(text は aux=text)
    private static readonly string[] FinishIds =
    [
        "blur", "zoomblur", "tiltshift", "sharpen",
        "glow", "vignette", "grain", "aberration", "emboss",
        "halftone", "waves", "warp",
        "outline", "shadow", "text",
    ];

    /// <summary>§9.5 のモーション 20 個の id(動き 9・色と光 5・乱れ 3・粒 3)。</summary>
    private static readonly string[] MotionIds =
    [
        "spin", "bounce", "hover", "pulse", "shake", "wobble", "jelly", "coinflip", "slide",
        "blink", "huecycle", "glowpulse", "shine", "leaks",
        "glitch", "rgbsplit", "ripple",
        "sparkle", "particles", "grainmove",
    ];

    [Test]
    public void AC13_1_ステージごとの個数とidが表と過不足なく一致する()
    {
        var catalog = BuiltInEffects.Load(NullLogSink.Instance);
        string[] Ids(EffectStage stage) => catalog.Effects.Where(e => e.Stage == stage).Select(e => e.Id).Order(StringComparer.Ordinal).ToArray();
        Check.SequenceEqual(ShapeIds.Order(StringComparer.Ordinal).ToArray(), Ids(EffectStage.Shape), "形ステージ(7 個)");
        Check.SequenceEqual(LookIds.Order(StringComparer.Ordinal).ToArray(), Ids(EffectStage.Look), "ルックステージ(21 個)");
        Check.SequenceEqual(FinishIds.Order(StringComparer.Ordinal).ToArray(), Ids(EffectStage.Finish), "仕上げステージ(15 個)");
        Check.SequenceEqual(MotionIds.Order(StringComparer.Ordinal).ToArray(), Ids(EffectStage.Motion), "モーションステージ(20 個)");
        Check.Equal(63, catalog.Effects.Count, "合計 63 個");
        Check.Equal(catalog.Effects.Count, catalog.Effects.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count(), "id の重複");
    }

    // ---- AC-13 の (4)・INV-3: 埋め込みリソース以外を開かない ----

    /// <summary>埋め込みリソースの 2 つのメソッドだけに答え、ファイルに関わるメンバーは呼ばれたら失敗するアセンブリ。</summary>
    private sealed class ResourceOnlyAssembly(Dictionary<string, string> resources) : Assembly
    {
        public List<string> Calls { get; } = new();

        public override string[] GetManifestResourceNames()
        {
            Calls.Add("names");
            return resources.Keys.ToArray();
        }

        public override Stream? GetManifestResourceStream(string name)
        {
            Calls.Add("stream:" + name);
            return resources.TryGetValue(name, out var s) ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s), writable: false) : null;
        }

        public override Stream? GetManifestResourceStream(Type type, string name) => throw Forbidden();
        public override string Location => throw Forbidden();
        public override FileStream? GetFile(string name) => throw Forbidden();
        public override FileStream[] GetFiles() => throw Forbidden();
        public override FileStream[] GetFiles(bool getResourceModules) => throw Forbidden();
        public override ManifestResourceInfo? GetManifestResourceInfo(string resourceName) => throw Forbidden();

        private static CheckFailedException Forbidden() => new("ローダーが埋め込みリソースの 2 つのメソッド以外を使った");
    }

    [Test]
    public void AC13_INV3_ローダーは埋め込みリソースだけを読む()
    {
        var asm = new ResourceOnlyAssembly(new Dictionary<string, string>
        {
            ["X.Effects.exposure.json"] = BaseJson,
            ["X.Effects.note.txt"] = "読まない",
            ["Y.other.json"] = "読まない",
        });
        var log = new MemoryLogSink();
        var catalog = EffectCatalog.LoadFromAssembly(asm, "X.Effects.", OnlyExposure, log);
        Check.Equal(1, catalog.Effects.Count, "読めた数");
        Check.SequenceEqual(new[] { "names", "stream:X.Effects.exposure.json" }, asm.Calls, "アセンブリへの呼び出し");
    }

    [Test]
    public void AC13_INV3_ローダーの公開の入口はアセンブリだけを受け取る()
    {
        var forbidden = new[] { typeof(Stream), typeof(FileInfo), typeof(DirectoryInfo), typeof(Uri), typeof(TextReader) };
        foreach (var type in new[] { typeof(EffectCatalog), typeof(EffectDefLoader), typeof(BuiltInEffects) })
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var p in m.GetParameters())
                {
                    Check.True(!forbidden.Any(f => f.IsAssignableFrom(p.ParameterType)), $"{type.Name}.{m.Name} が {p.ParameterType.Name} を受け取る");
                    Check.True(!(p.ParameterType == typeof(string) && p.Name is not ("resourcePrefix" or "id" or "shader")),
                        $"{type.Name}.{m.Name} が文字列 {p.Name} を受け取る(パスや JSON を渡せる入口を作らない)");
                }
            }
        }
        // 読み込みのソースに、ファイルを開く API が無い
        var terms = new[] { "File.", "FileStream", "FileInfo", "Directory.", "OpenRead", "ReadAllText", "ReadAllBytes" };
        foreach (var rel in new[] { "src/GlintLoop.Core/Effects/EffectCatalog.cs", "src/GlintLoop.Core/Effects/EffectDefLoader.cs", "src/GlintLoop.Render/Effects/BuiltInEffects.cs" })
        {
            var text = RepoFiles.ReadText(Path.Combine(RepoFiles.Root, rel));
            var hits = SourceScan.FindTerms(rel, text, terms);
            Check.True(hits.Count == 0, $"ファイルを開く API: {string.Join(", ", hits)}");
        }
    }

    // ---- AC-14 の (3): 実効 amount ----

    [Test]
    public void AC14_実効amountの式の4例()
    {
        Check.Near(0.40, EffectAmount.Effective(80, 50, fromLook: true, AmountMode.Mix), 1e-12, "(80, 50, look, mix)");
        Check.Near(0.80, EffectAmount.Effective(80, 50, fromLook: false, AmountMode.Mix), 1e-12, "(80, 50, user, mix)");
        Check.Near(0.0, EffectAmount.Effective(100, 0, fromLook: true, AmountMode.Mix), 1e-12, "(100, 0, look, mix)");
        Check.Near(0.0, EffectAmount.Effective(0, 100, fromLook: true, AmountMode.Scale), 1e-12, "(0, 100, look, scale)");
        // none はルックの強さを掛けない
        Check.Near(0.80, EffectAmount.Effective(80, 50, fromLook: true, AmountMode.None), 1e-12, "(80, 50, look, none)");
    }

    [Test]
    public void AC14_scaleモードの実効値はneutralからamountの割合()
    {
        // sharpen の strength=100(neutral=0)に amount=50% を掛けると 50
        var strength = new ParamDef("strength", "強さ", ParamType.Float, 0, 200, 1, new NumberValue(100), "%", null, 0, false, true, null, null);
        var a = EffectAmount.Effective(50, 100, fromLook: false, AmountMode.Scale);
        Check.Near(50, EffectAmount.Scale(100, EffectAmount.Neutral(strength), a), 1e-12, "sharpen の strength");
        // neutral の省略時: 範囲に 0 を含めば 0、含まなければ既定値
        var noNeutral = strength with { Neutral = null };
        Check.Equal(0.0, EffectAmount.Neutral(noNeutral), "0 を含む範囲");
        var positive = noNeutral with { Min = 1, Max = 5, Default = new NumberValue(2) };
        Check.Equal(2.0, EffectAmount.Neutral(positive), "0 を含まない範囲");
        Check.Near(3.5, EffectAmount.Scale(5, 2, 0.5), 1e-12, "neutral 2 から 5 の半分");
    }
}
