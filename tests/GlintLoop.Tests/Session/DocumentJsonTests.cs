// 文書の JSON(§9.11 の 3)の検査。例の往復とキーの順、表の直しの各行、project.invalid の各条件、param_clamped の内容(E-25)。
// AC-47 の (3)〜(5) の読み込みの項目のうち、ZIP を使わずに JSON だけで確かめられるものをここで確かめる。
// EffectDef はコードで組み立てたカタログ(SessionTestKit)を使う。
using System.Text.Json;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Session.SessionTestKit;

namespace GlintLoop.Tests.Session;

public sealed class DocumentJsonTests
{
    /// <summary>§9.11 の 3 の例(そのまま)。</summary>
    internal const string SpecExample = """
{
  "schema": 1,
  "seed": 421337,
  "saltCounter": 9,
  "time": {"length": 2.0, "fps": 20, "loop": "infinite", "holdMs": 0},
  "selected": 1,
  "assets": [
    {"sha256": "c8bf4103116a8ca2947a36c669f6e072c5c217a07e45af15a8e82879bb657539", "ext": "jpg", "bytes": 2345678},
    {"sha256": "1d1b20453379820a2ee36a7e6c626c027f87e633ce373c2c23220dfbf98aa75f", "ext": "png", "bytes": 123456}
  ],
  "layers": [
    {"name": "写真", "kind": "image",
     "asset": "c8bf4103116a8ca2947a36c669f6e072c5c217a07e45af15a8e82879bb657539",
     "visible": true, "opacity": 100, "blend": "normal",
     "place": {"x": 0, "y": 0, "scale": 100}, "lookStrength": 100,
     "stack": [
       {"id": "exposure", "enabled": true, "origin": "look", "salt": 3, "amount": 100, "params": {"ev": 0.2}}
     ]},
    {"name": "ステッカー", "kind": "image",
     "asset": "1d1b20453379820a2ee36a7e6c626c027f87e633ce373c2c23220dfbf98aa75f",
     "visible": true, "opacity": 90, "blend": "multiply",
     "place": {"x": 120.5, "y": -40, "scale": 35.5}, "lookStrength": 100,
     "stack": [
       {"id": "outline", "enabled": true, "origin": "user", "salt": 6, "amount": 100,
        "params": {"width": 12, "pos": "outside", "color": "#FFFFFFFF", "soft": 0}},
       {"id": "spin", "enabled": true, "origin": "user", "salt": 7, "amount": 100,
        "params": {"cycles": 1, "phase": 0, "dir": "cw", "pivot": [0.5, 0.5]}}
     ]}
  ],
  "all": {
    "lookStrength": 100,
    "stack": [
      {"id": "exposure", "enabled": true, "origin": "user", "salt": 8, "amount": 100, "params": {"ev": -0.1}}
    ]
  },
  "output": {
    "format": "gif",
    "size": {"mode": "long", "value": 480},
    "limit": {"kind": "preset", "key": "chat_small"},
    "options": {
      "gif": {"colors": 256, "dither": "blue", "matte": null, "diff": true},
      "png": {},
      "jpeg": {"quality": 90, "background": "#FFFFFF"},
      "webp": {"quality": 85, "lossless": false},
      "apng": {"palette": false, "loops": 0}
    }
  }
}
""";

    // ------------------------------------------------------------ 道具

    private static readonly string A1 = Sha(1);
    private static readonly string A2 = Sha(2);

    private static string Img(string name = "a", string asset = "", string extra = "") =>
        $$"""{"name": "{{name}}", "kind": "image", "asset": "{{(asset == "" ? A1 : asset)}}"{{extra}}}""";

    private static string Emp(string extra = "") => $$"""{"name": "e", "kind": "empty"{{extra}}}""";

    private static string Doc(string layers, string? assets = null, string rest = "") =>
        $$"""{"schema": 1, "seed": 5, "saltCounter": 0, "assets": [{{assets ?? AssetJson(A1)}}], "layers": [{{layers}}]{{rest}}}""";

    private static string AssetJson(string sha, string ext = "png", string bytes = "1000") =>
        $$"""{"sha256": "{{sha}}", "ext": "{{ext}}", "bytes": {{bytes}}}""";

    private static (SessionDocument Doc, MemoryLogSink Log, IReadOnlyList<Notice> Notices) Load(string json)
    {
        var log = new MemoryLogSink();
        var r = DocumentJson.Read(json, Catalog, log);
        Check.True(r.IsValid, $"読めない: {r.InvalidReason}");
        return (r.Document!, log, r.Notices);
    }

    private static List<(string Effect, string Key)> Clamps(MemoryLogSink log) =>
        log.Events.Where(e => e.Name == "param_clamped").Select(e => ((string)e.Get("effect")!, (string)e.Get("key")!)).ToList();

    private static void OneClamp(MemoryLogSink log, string effect, string key)
    {
        var c = Clamps(log);
        Check.Equal(1, c.Count, $"param_clamped の行数({effect}.{key})");
        Check.Equal((effect, key), c[0], "param_clamped の effect と key");
    }

    private static void NoClamp(MemoryLogSink log, string what) => Check.Equal(0, Clamps(log).Count, $"{what} で param_clamped が書かれた");

    /// <summary>JSON の木を深さ優先にたどって、(パス, 値の文字列) の列にする。</summary>
    private static List<(string Path, string Value)> Walk(string json)
    {
        using var d = JsonDocument.Parse(json);
        var list = new List<(string, string)>();
        Walk(d.RootElement, "", list);
        return list;
    }

    private static void Walk(JsonElement e, string path, List<(string, string)> list)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    list.Add((path + "/" + p.Name, "{key}"));
                    Walk(p.Value, path + "/" + p.Name, list);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var x in e.EnumerateArray()) Walk(x, path + "[" + i++ + "]", list);
                break;
            case JsonValueKind.Number:
                list.Add((path, e.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                break;
            default:
                list.Add((path, e.ValueKind + ":" + e.ToString()));
                break;
        }
    }

    // ------------------------------------------------------------ 往復

    [Test]
    public void AC47_2_JSONの例を読んで書くとキーの順と値が同じ()
    {
        var (doc, log, notices) = Load(SpecExample);
        NoClamp(log, "例");
        Check.Equal(0, notices.Count, "例の通知");
        Check.Equal(Target.OfLayer(1), doc.Selected, "selected");
        var written = DocumentJson.Write(doc);
        var expected = Walk(SpecExample);
        var actual = Walk(written);
        Check.SequenceEqual(expected.Select(x => x.Path).ToList(), actual.Select(x => x.Path).ToList(), "キーの順");
        Check.SequenceEqual(expected, actual, "値");
        Check.Equal("0", actual.Last(x => x.Path == "/output/options/apng/loops").Value, "infinite のときの apng.loops");
        Check.True(written.Contains("\"length\":2.0", StringComparison.Ordinal), "time.length が 2.0 で書かれていない");
        Check.True(written.Contains("写真", StringComparison.Ordinal), "日本語の名前がそのまま書かれていない");
    }

    [Test]
    public void AC47_2_書いて読んで書くと完全に一致する()
    {
        var first = DocumentJson.Write(Load(SpecExample).Doc);
        var second = DocumentJson.Write(Load(first).Doc);
        Check.Equal(first, second, "2 回目の JSON");
        var s = WithBase();
        s.AddEmptyLayer();
        s.SetLayerFill(1, "#FF000080");
        s.AddEffect(Target.OfLayer(0), "crop");
        s.SetEffectParam(Target.OfLayer(0), 0, "rect", new RectValue(10, 20, 30, 40));
        s.AddEffect(Target.All, "invert");
        s.Select(Target.All);
        var j = s.Json!;
        Check.Equal(j, DocumentJson.Write(Load(j).Doc), "セッションの JSON の往復");
    }

    [Test]
    public void AC47_1_レイヤーのキーの順はimageとemptyで決まっている()
    {
        var (doc, _, _) = Load(Doc(Img() + "," + Emp(""" , "size": {"w": 64, "h": 48}, "fill": "#FF000080" """)));
        var json = DocumentJson.Write(doc);
        using var d = JsonDocument.Parse(json);
        var layers = d.RootElement.GetProperty("layers");
        Check.SequenceEqual(
            ["name", "kind", "asset", "visible", "opacity", "blend", "place", "lookStrength", "stack"],
            layers[0].EnumerateObject().Select(p => p.Name).ToList(), "image のキー");
        Check.SequenceEqual(
            ["name", "kind", "size", "fill", "visible", "opacity", "blend", "place", "lookStrength", "stack"],
            layers[1].EnumerateObject().Select(p => p.Name).ToList(), "empty のキー");
        Check.Equal("{\"w\":64,\"h\":48}", layers[1].GetProperty("size").GetRawText(), "size");
        Check.Equal("#FF000080", layers[1].GetProperty("fill").GetString(), "fill");
        Check.SequenceEqual(
            ["schema", "seed", "saltCounter", "time", "selected", "assets", "layers", "all", "output"],
            d.RootElement.EnumerateObject().Select(p => p.Name).ToList(), "文書のキー");
    }

    // ------------------------------------------------------------ レイヤーの直し(表の行と AC-47 (4))

    [Test]
    public void E25_opacityの範囲外は端に直してparam_clampedを書く()
    {
        foreach (var (raw, want) in new[] { ("150", 100), ("-5", 0) })
        {
            var (doc, log, _) = Load(Doc(Img() + "," + Img(extra: $", \"opacity\": {raw}")));
            Check.Equal(want, doc.Layers[1].Opacity, $"opacity {raw}");
            OneClamp(log, "layer", "opacity");
        }
    }

    [Test]
    public void E25_opacityの小数はroundしてログを書かない()
    {
        var (doc, log, _) = Load(Doc(Img(extra: ", \"opacity\": 49.6")));
        Check.Equal(50, doc.Layers[0].Opacity, "49.6");
        NoClamp(log, "49.6");
        var (d2, _, _) = Load(Doc(Img(extra: ", \"opacity\": 49.5")));
        Check.Equal(50, d2.Layers[0].Opacity, "49.5 は大きい方へ");
    }

    [Test]
    public void E25_placeのxとscaleの範囲外とround()
    {
        var (doc, log, _) = Load(Doc(Img() + "," + Img(extra: ", \"place\": {\"x\": 100001, \"y\": 0, \"scale\": 100}")));
        Check.Equal(100000.0, doc.Layers[1].Place.X, "x");
        OneClamp(log, "layer", "place.x");
        foreach (var (raw, want) in new[] { ("4", 5.0), ("401", 400.0) })
        {
            var (d, l, _) = Load(Doc(Img() + "," + Img(extra: $", \"place\": {{\"x\": 0, \"y\": 0, \"scale\": {raw}}}")));
            Check.Equal(want, d.Layers[1].Place.Scale, $"scale {raw}");
            OneClamp(l, "layer", "place.scale");
        }
        var (d3, l3, _) = Load(Doc(Img() + "," + Img(extra: ", \"place\": {\"x\": 1.234, \"y\": -100000.5, \"scale\": 35.55}")));
        Check.Equal(1.23, d3.Layers[1].Place.X, "x 1.234");
        Check.Equal(-100000.0, d3.Layers[1].Place.Y, "y");
        Check.Equal(35.6, d3.Layers[1].Place.Scale, "scale 35.55");
        OneClamp(l3, "layer", "place.y");
    }

    [Test]
    public void E25_lookStrengthの範囲外はlayerとallで書き分ける()
    {
        var (doc, log, _) = Load(Doc(Img(extra: ", \"lookStrength\": 120")));
        Check.Equal(100, doc.Layers[0].LookStrength, "layer");
        OneClamp(log, "layer", "lookStrength");
        var (d2, l2, _) = Load(Doc(Img(), rest: ", \"all\": {\"lookStrength\": 120, \"stack\": []}"));
        Check.Equal(100, d2.All.LookStrength, "all");
        OneClamp(l2, "all", "lookStrength");
    }

    [Test]
    public void E25_blendの列挙に無い文字列はnormalに直す()
    {
        var (doc, log, _) = Load(Doc(Img() + "," + Img(extra: ", \"blend\": \"dissolve\"")));
        Check.Equal(BlendMode.Normal, doc.Layers[1].Blend, "dissolve");
        OneClamp(log, "layer", "blend");
        var (d2, _, _) = Load(Doc(Img() + "," + Img(extra: ", \"blend\": \"softlight\"")));
        Check.Equal(BlendMode.SoftLight, d2.Layers[1].Blend, "softlight");
    }

    [Test]
    public void E25_nameの41文字と空と空白()
    {
        var name41 = new string('あ', 41);
        var (doc, log, _) = Load(Doc(Img(name: name41)));
        Check.Equal(new string('あ', 40), doc.Layers[0].Name, "41 文字");
        OneClamp(log, "layer", "name");
        var (d2, l2, _) = Load(Doc(Img(name: "")));
        Check.Equal("画像", d2.Layers[0].Name, "空の image");
        OneClamp(l2, "layer", "name");
        var (d3, l3, _) = Load(Doc(Img() + "," + Emp().Replace("\"e\"", "\"  \"")));
        Check.Equal("空のレイヤー", d3.Layers[1].Name, "空白の empty");
        OneClamp(l3, "layer", "name");
    }

    [Test]
    public void E25_emptyのsizeとfill()
    {
        var (doc, log, _) = Load(Doc(Img() + "," + Emp(", \"size\": {\"w\": 5000, \"h\": 0}, \"fill\": \"#ff000080\"")));
        Check.Equal(new EmptySize(4096, 1), doc.Layers[1].Size, "size");
        Check.Equal("#FF000080", doc.Layers[1].Fill, "fill は大文字にそろえる");
        Check.SequenceEqual([("layer", "size.w"), ("layer", "size.h")], Clamps(log), "param_clamped");
        var (d2, l2, _) = Load(Doc(Img() + "," + Emp(", \"size\": {\"w\": 64, \"h\": 48}, \"fill\": \"red\"")));
        Check.Equal(ColorText.Transparent, d2.Layers[1].Fill, "fill の形でない値は既定");
        NoClamp(l2, "fill の形違い");
        var (d3, _, _) = Load(Doc(Img(extra: ", \"size\": {\"w\": 5, \"h\": 5}, \"fill\": \"#FF000080\"")));
        Check.Equal(null, d3.Layers[0].Size, "image の size は捨てる");
        Check.Equal(null, d3.Layers[0].Fill, "image の fill は捨てる");
    }

    [Test]
    public void E25_selectedの範囲外と全体と欠け()
    {
        var two = Img() + "," + Img();
        var (doc, log, _) = Load(Doc(two, rest: ", \"selected\": 9"));
        Check.Equal(Target.OfLayer(1), doc.Selected, "9 は最前面");
        OneClamp(log, "layer", "selected");
        var (d2, l2, _) = Load(Doc(two, rest: ", \"selected\": \"all\""));
        Check.Equal(Target.All, d2.Selected, "all");
        NoClamp(l2, "all");
        var (d3, l3, _) = Load(Doc(two));
        Check.Equal(Target.OfLayer(1), d3.Selected, "欠けは最前面");
        NoClamp(l3, "欠け");
        var (d4, _, _) = Load(Doc(two, rest: ", \"selected\": 0"));
        Check.Equal(Target.OfLayer(0), d4.Selected, "0");
    }

    [Test]
    public void E25_欠けたキーは既定値でログを書かず未知のキーは捨てる()
    {
        var (doc, log, _) = Load(Doc(Img(extra: ", \"mystery\": 3, \"opacity\": \"x\"")));
        var l = doc.Layers[0];
        Check.Equal(100, l.Opacity, "opacity の既定");
        Check.Equal(BlendMode.Normal, l.Blend, "blend の既定");
        Check.Equal(true, l.Visible, "visible の既定");
        Check.Equal(100, l.LookStrength, "lookStrength の既定");
        NoClamp(log, "欠け");
        var json = DocumentJson.Write(doc);
        Check.True(!json.Contains("mystery", StringComparison.Ordinal), "未知のキーが残った");
        Check.Equal(TimeSettings.Default, doc.Time, "time の既定");
        Check.Equal(OutputSettings.Default, doc.Output, "output の既定");
    }

    [Test]
    public void AC47_3_土台のブレンドと配置は書かれた値にかかわらず既定でログを書かない()
    {
        var (doc, log, _) = Load(Doc(Img(extra: ", \"blend\": \"multiply\", \"place\": {\"x\": 5, \"y\": 6, \"scale\": 50}")));
        Check.Equal(BlendMode.Normal, doc.Base.Blend, "blend");
        Check.Equal(Placement.Identity, doc.Base.Place, "place");
        NoClamp(log, "土台の強制");
        var json = DocumentJson.Write(doc);
        Check.True(json.Contains("\"blend\":\"normal\",\"place\":{\"x\":0,\"y\":0,\"scale\":100}", StringComparison.Ordinal), "書き直した値: " + json);
    }

    [Test]
    public void AC47_4_saltCounterは読んだsaltの最大値に直す()
    {
        var stack = ", \"stack\": [{\"id\": \"exposure\", \"salt\": 9}, {\"id\": \"invert\", \"salt\": 2}]";
        var json = Doc(Img(extra: stack)).Replace("\"saltCounter\": 0", "\"saltCounter\": 3");
        var (doc, _, _) = Load(json);
        Check.Equal(9L, doc.SaltCounter, "saltCounter");
        var (d2, _, _) = Load(json.Replace("\"saltCounter\": 3", "\"saltCounter\": 20"));
        Check.Equal(20L, d2.SaltCounter, "大きいときはそのまま");
    }

    [Test]
    public void 表_assetsはlayersの最初に使われた順に並べ使われないものを捨てる()
    {
        var a3 = Sha(3);
        var assets = AssetJson(a3, "gif") + "," + AssetJson(A2, "jpg", "77") + "," + AssetJson(A1);
        var (doc, _, _) = Load(Doc(Img(asset: A2) + "," + Img(asset: A1) + "," + Img(asset: A2), assets));
        Check.SequenceEqual(
            [new AssetEntry(A2, "jpg", 77), new AssetEntry(A1, "png", 1000)], doc.Assets.ToList(), "assets");
    }

    // ------------------------------------------------------------ スタックの直し

    [Test]
    public void 表_stackはステージ順に並べ直し形の固定の3つを先頭に置く()
    {
        var stack = ", \"stack\": [" + string.Join(",",
            "{\"id\": \"spin\"}", "{\"id\": \"outline\"}", "{\"id\": \"exposure\"}", "{\"id\": \"flip\"}",
            "{\"id\": \"pad\"}", "{\"id\": \"invert\"}", "{\"id\": \"rotate\"}", "{\"id\": \"crop\"}") + "]";
        var (doc, _, notices) = Load(Doc(Img(extra: stack)));
        Check.SequenceEqual(
            ["rotate", "crop", "pad", "flip", "exposure", "invert", "outline", "spin"],
            doc.Layers[0].Stack.Select(e => e.Id).ToList(), "並び");
        Check.Equal(0, notices.Count, "通知");
        // salt が欠けた行には saltCounter の続きを振る(仮の決め)。どの salt も重ならない。
        Check.Equal(8, doc.Layers[0].Stack.Select(e => e.Salt).Distinct().Count(), "salt の重なり");
    }

    [Test]
    public void E24_カタログに無いidと全体の回転切り抜き余白は外してstack_unknown_effect()
    {
        var layerStack = ", \"stack\": [{\"id\": \"nosuch\"}, {\"id\": \"exposure\"}]";
        var all = ", \"all\": {\"lookStrength\": 100, \"stack\": [{\"id\": \"rotate\"}, {\"id\": \"crop\"}, {\"id\": \"pad\"}, {\"id\": \"invert\"}]}";
        var (doc, _, notices) = Load(Doc(Img(extra: layerStack), rest: all));
        Check.SequenceEqual(["exposure"], doc.Layers[0].Stack.Select(e => e.Id).ToList(), "レイヤー");
        Check.SequenceEqual(["invert"], doc.All.Stack.Select(e => e.Id).ToList(), "全体");
        Check.SequenceEqual([NoticeKeys.StackUnknownEffect], notices.Select(n => n.Key).ToList(), "通知");
    }

    [Test]
    public void E23_stackは並べ直した先頭の64個だけ読んでstack_full()
    {
        var rows = Enumerable.Range(0, 70).Select(i => i == 69 ? "{\"id\": \"flip\"}" : "{\"id\": \"invert\"}");
        var (doc, _, notices) = Load(Doc(Img(extra: ", \"stack\": [" + string.Join(",", rows) + "]")));
        var st = doc.Layers[0].Stack;
        Check.Equal(64, st.Count, "行の数");
        Check.Equal("flip", st[0].Id, "並べ直したあとの先頭(配列の最後の flip が形ステージで先頭に来る)");
        Check.SequenceEqual([NoticeKeys.StackFull], notices.Select(n => n.Key).ToList(), "通知");
    }

    [Test]
    public void E25_パラメータの範囲外は端で型違いと欠けは既定で未知のキーは捨てる()
    {
        var stack = ", \"stack\": [" +
            "{\"id\": \"exposure\", \"amount\": 150, \"params\": {\"ev\": 5, \"zzz\": 1}}," +
            "{\"id\": \"outline\", \"params\": {\"width\": \"wide\", \"pos\": \"nowhere\", \"color\": \"#ffffff80\"}}," +
            "{\"id\": \"posterize\", \"params\": {\"levels\": 3.4}}," +
            "{\"id\": \"spin\", \"params\": {\"cycles\": 9, \"pivot\": [1.5, -1]}}]";
        var (doc, log, _) = Load(Doc(Img(extra: stack)));
        var st = doc.Layers[0].Stack;
        var ex = st.Single(e => e.Id == "exposure");
        Check.Equal(new NumberValue(3), ex.Get("ev"), "ev 5 は 3");
        Check.Equal(100.0, ex.Amount, "amount 150 は 100");
        Check.Equal(1, ex.Params.Count, "未知のキーが残った");
        var ol = st.Single(e => e.Id == "outline");
        Check.Equal(new NumberValue(10), ol.Get("width"), "型違いは既定");
        Check.Equal(new StringValue("outside"), ol.Get("pos"), "列挙に無い値は既定");
        Check.Equal(new StringValue("#FFFFFF80"), ol.Get("color"), "色は大文字にそろえる");
        Check.Equal(new NumberValue(0), ol.Get("soft"), "欠けは既定");
        Check.SequenceEqual(["width", "pos", "color", "soft"], ol.Params.Select(p => p.Key).ToList(), "宣言の順");
        Check.Equal(new NumberValue(3), st.Single(e => e.Id == "posterize").Get("levels"), "整数は round");
        var sp = st.Single(e => e.Id == "spin");
        Check.SequenceEqual(["cycles", "phase", "dir", "pivot"], sp.Params.Select(p => p.Key).ToList(), "共通パラメータの順");
        Check.Equal(new NumberValue(8), sp.Get("cycles"), "cycles 9 は 8");
        Check.Equal(new PointValue(1, 0), sp.Get("pivot"), "point は 0〜1");
        Check.SequenceEqual(
            [("exposure", "amount"), ("exposure", "ev"), ("outline", "pos"), ("spin", "cycles"), ("spin", "pivot")],
            Clamps(log).OrderBy(c => c.Effect, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal).ToList(),
            "param_clamped(型違いの width と欠けの soft は書かない)");
    }

    [Test]
    public void E25_時間依存のエフェクトにwaveを補う()
    {
        var (doc, _, _) = Load(Doc(Img(extra: ", \"stack\": [{\"id\": \"shake\", \"params\": {\"wave\": \"tri\"}}]")));
        var sh = doc.Layers[0].Stack[0];
        Check.SequenceEqual(["cycles", "phase", "wave", "strength"], sh.Params.Select(p => p.Key).ToList(), "並び");
        Check.Equal(new StringValue("tri"), sh.Get("wave"), "wave");
    }

    [Test]
    public void 表_切り抜きのlとrの和が980を超えるときはr_980引くl()
    {
        var stack = ", \"stack\": [{\"id\": \"crop\", \"params\": {\"rect\": {\"l\": 600, \"t\": 990, \"r\": 500, \"b\": 950}}}]";
        var (doc, log, _) = Load(Doc(Img(extra: stack)));
        Check.Equal(new RectValue(600, 900, 380, 80), doc.Layers[0].Stack[0].Get("rect"), "rect(t は 900 に切ってから b=980−t)");
        OneClamp(log, "crop", "rect");
    }

    [Test]
    public void 表_levelsのwhite引くblackが5未満はblack_white引く5()
    {
        var stack = ", \"stack\": [{\"id\": \"levels\", \"params\": {\"black\": 50, \"white\": 52}}]";
        var (doc, _, _) = Load(Doc(Img(extra: stack)));
        var lv = doc.Layers[0].Stack[0];
        Check.Equal(new NumberValue(47), lv.Get("black"), "black");
        Check.Equal(new NumberValue(52), lv.Get("white"), "white");
    }

    // ------------------------------------------------------------ 時間と出力(表の行)

    [Test]
    public void 表_timeとoutputの列挙の直し()
    {
        var rest = ", \"time\": {\"length\": 2.25, \"fps\": 21, \"loop\": \"twice\", \"holdMs\": 300}" +
            ", \"output\": {\"format\": \"png\", \"size\": {\"mode\": \"huge\", \"value\": 3}, \"limit\": {\"kind\": \"user\", \"key\": \"x\"}," +
            " \"options\": {\"gif\": {\"colors\": 64, \"dither\": \"fancy\", \"matte\": \"#000000FF\", \"diff\": false}, \"apng\": {\"palette\": true, \"loops\": 9}}}";
        var (doc, _, _) = Load(Doc(Img(), rest: rest));
        Check.Equal(new TimeSettings(23, 20, LoopMode.Infinite, 300), doc.Time, "time(2.25 は 2.3、fps 21 は既定)");
        Check.Equal("png", doc.Output.Format, "format");
        Check.Equal(new OutputSize("auto", null), doc.Output.Size, "size.mode");
        Check.Equal(new OutputLimit("none", null, null), doc.Output.Limit, "limit.kind の user");
        Check.Equal(new GifOptions(64, "blue", "#000000FF", false), doc.Output.Gif, "gif");
        Check.Equal(new ApngOptions(true, 4), doc.Output.Apng, "apng.loops 9 は 4");
        var once = Load(Doc(Img(), rest: ", \"time\": {\"loop\": \"once\"}, \"output\": {\"options\": {\"apng\": {\"loops\": 3}}}")).Doc;
        Check.True(DocumentJson.Write(once).Contains("\"loops\":3", StringComparison.Ordinal), "once のときは loops を書く");
        var bytes = Load(Doc(Img(), rest: ", \"output\": {\"limit\": {\"kind\": \"bytes\", \"bytes\": 500000}}")).Doc;
        Check.Equal(new OutputLimit("bytes", null, 500000), bytes.Output.Limit, "limit bytes");
        var unknown = Load(Doc(Img(), rest: ", \"output\": {\"format\": \"bmp\"}")).Doc;
        Check.Equal(OutputSettings.Default.Format, unknown.Output.Format, "知らない format は既定");
    }

    // ------------------------------------------------------------ project.invalid(E-45 と AC-47 (5))

    [Test]
    public void AC47_5_開かない文書はproject_invalidとして読み込みを失敗にする()
    {
        var thirtyThree = string.Join(",", Enumerable.Repeat(Img(), 33));
        var cases = new (string What, string Json)[]
        {
            ("schema が 2", Doc(Img()).Replace("\"schema\": 1", "\"schema\": 2")),
            ("schema が無い", Doc(Img()).Replace("\"schema\": 1, ", "")),
            ("layers が配列でない", "{\"schema\": 1, \"assets\": [], \"layers\": {}}"),
            ("layers が空", Doc("")),
            ("layers が 33 個", Doc(thirtyThree)),
            ("kind が video", Doc(Img().Replace("\"image\"", "\"video\""))),
            ("image の asset が無い", Doc($$"""{"name": "a", "kind": "image"}""")),
            ("asset が assets に無い", Doc(Img(asset: A2))),
            ("sha256 が大文字", Doc(Img(asset: Sha(0xabc).ToUpperInvariant()), AssetJson(Sha(0xabc).ToUpperInvariant()))),
            ("sha256 が 63 桁", Doc(Img(asset: A1[1..]), AssetJson(A1[1..]))),
            ("同じ sha256 が 2 回", Doc(Img(), AssetJson(A1) + "," + AssetJson(A1, "jpg"))),
            ("ext が tiff", Doc(Img(), AssetJson(A1, "tiff"))),
            ("bytes が 0", Doc(Img(), AssetJson(A1, "png", "0"))),
            ("bytes が 100,000,001", Doc(Img(), AssetJson(A1, "png", "100000001"))),
            ("bytes が小数", Doc(Img(), AssetJson(A1, "png", "1.5"))),
            ("使われない assets の項目が壊れている", Doc(Img(), AssetJson(A1) + "," + AssetJson(A2, "tiff"))),
            ("JSON として読めない", "{\"schema\": 1,"),
            ("文書が配列", "[]"),
        };
        foreach (var (what, json) in cases)
        {
            var r = DocumentJson.Read(json, Catalog);
            Check.True(!r.IsValid, $"{what} で読めてしまった");
            Check.True(r.InvalidReason is { Length: > 0 }, $"{what} の理由が無い");
        }
        // 境界の内側は読める
        Check.True(DocumentJson.Read(Doc(Img(), AssetJson(A1, "dib", "100000000")), Catalog).IsValid, "bytes 100,000,000 の dib");
        Check.True(DocumentJson.Read(Doc(string.Join(",", Enumerable.Repeat(Img(), 32))), Catalog).IsValid, "32 枚");
    }
}
