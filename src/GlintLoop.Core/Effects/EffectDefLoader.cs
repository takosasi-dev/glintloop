// EffectDef の JSON 1 つを解釈し、§9.2 の「読み込み時の検査」の 2〜9 を行う(1 の id の重複は EffectCatalog がまとめて調べる)。
// 当てはまった項目の番号を reason に返す(複数あれば、解釈の途中で最初に見つけたもの)。表に無い形の誤りは、近い項目に寄せる。
// step が無い数値のパラメータには、§9.4 の冒頭の既定の刻みを入れる。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlintLoop.Core.Effects;

public static partial class EffectDefLoader
{
    /// <summary>無効な宣言のログの event 名。</summary>
    public const string InvalidEvent = "effectdef_invalid";

    /// <summary>bind の右辺に使える組み込み変数(§9.2。$frame は 2026-10-29 の Q-A で足した)。</summary>
    public static IReadOnlyList<string> BuiltinVariables { get; } =
    [
        "$width", "$height", "$shortSide", "$texel", "$amount", "$t", "$k", "$n", "$cycles", "$phase", "$wave", "$seed", "$salt", "$frame",
    ];

    /// <summary>パスの scale に使える値。</summary>
    public static IReadOnlyList<double> PassScales { get; } = [1, 0.5, 0.25, 0.125];

    /// <summary>パラメータの unit に使える値。</summary>
    public static IReadOnlyList<string> Units { get; } = ["‰", "‰辺", "%", "deg", "EV", "s", ""];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,23}$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_]{0,15}$")]
    private static partial Regex OptionValueRegex();

    [GeneratedRegex("^#[0-9A-Fa-f]{8}$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex HlslNameRegex();

    private static readonly string[] EffectFields =
    [
        "schema", "id", "name", "stage", "group", "keywords", "amountMode", "scaleParams", "timeDependent", "noWave", "waveDefault",
        "sizeRule", "aux", "params", "passes",
    ];

    private static readonly string[] ParamFields =
    [
        "key", "label", "type", "min", "max", "step", "default", "unit", "options", "neutral", "advanced", "handle", "visibleWhen", "maxLength",
    ];

    private static readonly string[] OptionFields = ["value", "label"];

    private static readonly string[] PassFields = ["id", "shader", "inputs", "scale", "repeat", "when", "bind"];

    /// <summary>解釈の結果。Def が null のときは Reason(1〜9)と Detail に理由が入る。Id は読めた id(形式に合わなければ空)。</summary>
    internal readonly record struct Result(EffectDef? Def, string Id, int Reason, string Detail);

    private sealed class Invalid(int reason, string detail) : Exception(detail)
    {
        public int Reason { get; } = reason;
    }

    internal static Result Parse(string json, Func<string, bool> shaderExists)
    {
        var id = "";
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String &&
                IdRegex().IsMatch(idElement.GetString()!))
            {
                id = idElement.GetString()!;
            }
            var def = ParseEffect(root);
            Validate(def, shaderExists);
            return new Result(def, def.Id, 0, "");
        }
        catch (JsonException e)
        {
            return new Result(null, id, 7, "JSON として読めない: " + e.Message);
        }
        catch (Invalid e)
        {
            return new Result(null, id, e.Reason, e.Message);
        }
    }

    // ---- 解釈(形式の誤りは 7、列挙の値でないものと型に合わない項目は 9、値の範囲は 2、パスの形は 3、bind は 4) ----

    private static EffectDef ParseEffect(JsonElement root)
    {
        var o = Obj(root, EffectFields, "宣言");
        var schema = Number(Required(o, "schema", 7), "schema", 7);
        if (schema != 1) throw new Invalid(7, $"schema が 1 でない: {schema}");
        var id = Text(Required(o, "id", 7), "id", 7);
        if (!IdRegex().IsMatch(id)) throw new Invalid(7, $"id の形式が合わない: {id}");
        var name = Text(Required(o, "name", 7), "name", 7);
        CheckLength(name, 1, 12, "name");
        var stage = Text(Required(o, "stage", 9), "stage", 9) switch
        {
            "shape" => EffectStage.Shape,
            "look" => EffectStage.Look,
            "finish" => EffectStage.Finish,
            "motion" => EffectStage.Motion,
            var s => throw new Invalid(9, $"stage の値でない: {s}"),
        };
        var group = Text(Required(o, "group", 7), "group", 7);
        CheckLength(group, 1, 8, "group");
        var keywords = StringArray(Required(o, "keywords", 7), "keywords", 7);
        if (keywords.Count > 8) throw new Invalid(7, $"keywords が 8 個を超える: {keywords.Count}");
        foreach (var k in keywords) CheckLength(k, 1, 16, "keywords の語");
        var amountMode = Text(Required(o, "amountMode", 9), "amountMode", 9) switch
        {
            "mix" => AmountMode.Mix,
            "scale" => AmountMode.Scale,
            "none" => AmountMode.None,
            var s => throw new Invalid(9, $"amountMode の値でない: {s}"),
        };
        var scaleParams = o.TryGetValue("scaleParams", out var sp) ? StringArray(sp, "scaleParams", 7) : [];
        var timeDependent = Bool(Required(o, "timeDependent", 7), "timeDependent", 7);
        var noWave = o.TryGetValue("noWave", out var nw) && Bool(nw, "noWave", 7);
        var waveDefault = WaveKind.Sine;
        if (o.TryGetValue("waveDefault", out var wd))
        {
            waveDefault = (wd.ValueKind == JsonValueKind.String ? wd.GetString() : null) switch
            {
                "sine" => WaveKind.Sine,
                "tri" => WaveKind.Tri,
                "ease" => WaveKind.Ease,
                "saw" => WaveKind.Saw,
                _ => throw new Invalid(9, "waveDefault の値でない"),
            };
        }
        var sizeRule = (Required(o, "sizeRule", 9) is { ValueKind: JsonValueKind.String } sr ? sr.GetString() : null) switch
        {
            "same" => SizeRule.Same,
            "crop" => SizeRule.Crop,
            "rotateFit" => SizeRule.RotateFit,
            "pad" => SizeRule.Pad,
            _ => throw new Invalid(9, "sizeRule の値でない"),
        };
        var aux = AuxKind.None;
        if (o.TryGetValue("aux", out var ax))
        {
            if (ax.ValueKind != JsonValueKind.String || ax.GetString() != "text") throw new Invalid(9, "aux の値でない");
            aux = AuxKind.Text;
        }
        var paramsElement = Required(o, "params", 7);
        if (paramsElement.ValueKind != JsonValueKind.Array) throw new Invalid(7, "params が配列でない");
        var parameters = paramsElement.EnumerateArray().Select(ParseParam).ToList();
        var passesElement = Required(o, "passes", 7);
        if (passesElement.ValueKind != JsonValueKind.Array) throw new Invalid(7, "passes が配列でない");
        var passes = passesElement.EnumerateArray().Select(ParsePass).ToList();
        return new EffectDef(1, id, name, stage, group, keywords, amountMode, scaleParams, timeDependent, noWave, waveDefault,
            sizeRule, aux, parameters, passes);
    }

    private static ParamDef ParseParam(JsonElement element)
    {
        var o = Obj(element, ParamFields, "パラメータ");
        var key = Text(Required(o, "key", 7), "key", 7);
        if (!KeyRegex().IsMatch(key)) throw new Invalid(7, $"key の形式が合わない: {key}");
        var label = Text(Required(o, "label", 7), "label", 7);
        CheckLength(label, 1, 16, $"{key} の label");
        var type = Text(Required(o, "type", 9), "type", 9) switch
        {
            "float" => ParamType.Float,
            "int" => ParamType.Int,
            "bool" => ParamType.Bool,
            "enum" => ParamType.Enum,
            "color" => ParamType.Color,
            "angle" => ParamType.Angle,
            "point" => ParamType.Point,
            "rect" => ParamType.Rect,
            "text" => ParamType.Text,
            "font" => ParamType.Font,
            var s => throw new Invalid(9, $"{key}: type の値でない: {s}"),
        };
        var numeric = type is ParamType.Float or ParamType.Int or ParamType.Angle;
        var ranged = numeric || type == ParamType.Rect;
        foreach (var f in new[] { "min", "max", "step" })
        {
            if (!ranged && o.ContainsKey(f)) throw new Invalid(9, $"{key}: {f} は {type} に付けられない");
        }
        if (type != ParamType.Enum && o.ContainsKey("options")) throw new Invalid(9, $"{key}: options は enum 以外に付けられない");
        if (type != ParamType.Text && o.ContainsKey("maxLength")) throw new Invalid(9, $"{key}: maxLength は text 以外に付けられない");
        if (!numeric && o.ContainsKey("neutral")) throw new Invalid(9, $"{key}: neutral は数値の型にだけ付けられる");

        var unit = "";
        if (o.TryGetValue("unit", out var u))
        {
            unit = u.ValueKind == JsonValueKind.String ? u.GetString()! : throw new Invalid(9, $"{key}: unit の値でない");
            if (!Units.Contains(unit)) throw new Invalid(9, $"{key}: unit の値でない: {unit}");
        }

        double? min = null, max = null, step = null;
        if (ranged)
        {
            min = Number(Required(o, "min", 7), $"{key} の min", 7);
            max = Number(Required(o, "max", 7), $"{key} の max", 7);
            if (min >= max) throw new Invalid(2, $"{key}: min が max 以上");
            if (type == ParamType.Int && (!IsInteger(min.Value) || !IsInteger(max.Value)))
                throw new Invalid(2, $"{key}: int の min・max が整数でない");
            if (o.TryGetValue("step", out var st))
            {
                step = Number(st, $"{key} の step", 7);
                if (step <= 0) throw new Invalid(2, $"{key}: step が正でない");
            }
            else
            {
                step = DefaultStep(type, min.Value, max.Value);
            }
        }

        List<EnumOption>? options = null;
        if (type == ParamType.Enum)
        {
            if (!o.TryGetValue("options", out var opts) || opts.ValueKind != JsonValueKind.Array || opts.GetArrayLength() == 0)
                throw new Invalid(2, $"{key}: enum に options が無い");
            options = new List<EnumOption>();
            foreach (var opt in opts.EnumerateArray())
            {
                var oo = Obj(opt, OptionFields, $"{key} の選択肢");
                var value = Text(Required(oo, "value", 7), $"{key} の選択肢の value", 7);
                if (!OptionValueRegex().IsMatch(value)) throw new Invalid(7, $"{key}: 選択肢の value の形式が合わない: {value}");
                var optLabel = Text(Required(oo, "label", 7), $"{key} の選択肢の label", 7);
                CheckLength(optLabel, 1, 16, $"{key} の選択肢の label");
                if (options.Any(x => x.Value == value)) throw new Invalid(2, $"{key}: 選択肢の value {value} が重複している");
                options.Add(new EnumOption(value, optLabel));
            }
        }

        int? maxLength = null;
        if (o.TryGetValue("maxLength", out var ml))
        {
            var n = Number(ml, $"{key} の maxLength", 7);
            if (!IsInteger(n) || n < 1) throw new Invalid(7, $"{key}: maxLength が 1 以上の整数でない");
            maxLength = (int)n;
        }

        var def = ParseDefault(key, type, Required(o, "default", 7), min, max, options, maxLength);
        double? neutral = o.TryGetValue("neutral", out var ne) ? Number(ne, $"{key} の neutral", 7) : null;
        var advanced = o.TryGetValue("advanced", out var adv) && Bool(adv, $"{key} の advanced", 7);
        var handle = !o.TryGetValue("handle", out var hd) || Bool(hd, $"{key} の handle", 7);
        var visibleWhen = o.TryGetValue("visibleWhen", out var vw) ? Condition(vw, $"{key} の visibleWhen", allowBool: true) : null;
        return new ParamDef(key, label, type, min, max, step, def, unit, options, neutral, advanced, handle, visibleWhen, maxLength);
    }

    /// <summary>§9.4 の冒頭: 整数型と角度型は 1、実数型は範囲の幅が 100 以上なら 1、10 以上なら 0.1、10 未満なら 0.01(rect も実数型と同じ)。</summary>
    internal static double DefaultStep(ParamType type, double min, double max)
    {
        if (type is ParamType.Int or ParamType.Angle) return 1;
        var width = max - min;
        return width >= 100 ? 1 : width >= 10 ? 0.1 : 0.01;
    }

    private static ParamValue ParseDefault(string key, ParamType type, JsonElement d, double? min, double? max,
        List<EnumOption>? options, int? maxLength)
    {
        string what = $"{key} の default";
        switch (type)
        {
            case ParamType.Float or ParamType.Angle or ParamType.Int:
                var n = Number(d, what, 2);
                if (n < min || n > max) throw new Invalid(2, $"{what} {n} が範囲の外");
                if (type == ParamType.Int && !IsInteger(n)) throw new Invalid(2, $"{what} が整数でない");
                return new NumberValue(n);
            case ParamType.Bool:
                return new BoolValue(Bool(d, what, 2));
            case ParamType.Enum:
                var s = Text(d, what, 2);
                if (!options!.Any(x => x.Value == s)) throw new Invalid(2, $"{what} {s} が options に無い");
                return new StringValue(s);
            case ParamType.Color:
                var c = Text(d, what, 2);
                if (!ColorRegex().IsMatch(c)) throw new Invalid(2, $"{what} が #RRGGBBAA でない");
                return new StringValue(c);
            case ParamType.Point:
                var p = Obj(d, ["x", "y"], what, reason: 2);
                var x = Number(Required(p, "x", 2), what, 2);
                var y = Number(Required(p, "y", 2), what, 2);
                if (x < 0 || x > 1 || y < 0 || y > 1) throw new Invalid(2, $"{what} が 0〜1 の外");
                return new PointValue(x, y);
            case ParamType.Rect:
                var r = Obj(d, ["l", "t", "r", "b"], what, reason: 2);
                var sides = new[] { "l", "t", "r", "b" }.Select(k => Number(Required(r, k, 2), what, 2)).ToArray();
                if (sides.Any(v => v < min || v > max)) throw new Invalid(2, $"{what} が範囲の外");
                return new RectValue(sides[0], sides[1], sides[2], sides[3]);
            case ParamType.Text:
                var t = Text(d, what, 2);
                if (maxLength is { } m && TextLimits.CountChars(t) > m) throw new Invalid(2, $"{what} が maxLength を超える");
                return new StringValue(t);
            default:
                return new StringValue(Text(d, what, 2));
        }
    }

    private static PassDef ParsePass(JsonElement element)
    {
        var o = Obj(element, PassFields, "パス");
        var id = Text(Required(o, "id", 7), "パスの id", 7);
        if (id.Length == 0) throw new Invalid(7, "パスの id が空");
        var shader = Text(Required(o, "shader", 7), $"パス {id} の shader", 7);
        if (shader.Length == 0) throw new Invalid(7, $"パス {id} の shader が空");
        var inputs = StringArray(Required(o, "inputs", 3), $"パス {id} の inputs", 3);
        if (inputs.Count > 4) throw new Invalid(3, $"パス {id} の inputs が 4 個を超える");
        foreach (var input in inputs)
        {
            if (input is not ("src" or "orig" or "aux") && !(input.StartsWith("pass:", StringComparison.Ordinal) && input.Length > 5))
                throw new Invalid(3, $"パス {id} の入力 {input} の形が合わない");
        }
        var scale = 1.0;
        if (o.TryGetValue("scale", out var sc))
        {
            scale = Number(sc, $"パス {id} の scale", 3);
            if (!PassScales.Contains(scale)) throw new Invalid(3, $"パス {id} の scale {scale} は 1・0.5・0.25・0.125 のどれでもない");
        }
        var repeat = PassRepeat.Once;
        if (o.TryGetValue("repeat", out var rp))
        {
            if (rp.ValueKind == JsonValueKind.Number)
            {
                var n = Number(rp, $"パス {id} の repeat", 3);
                if (!IsInteger(n) || n < 1 || n > 64) throw new Invalid(3, $"パス {id} の repeat が 1〜64 の整数でない");
                repeat = new PassRepeat((int)n, null);
            }
            else
            {
                var ro = Obj(rp, ["param"], $"パス {id} の repeat", reason: 3);
                repeat = new PassRepeat(0, Text(Required(ro, "param", 3), $"パス {id} の repeat の param", 3));
            }
        }
        var when = o.TryGetValue("when", out var wh) ? Condition(wh, $"パス {id} の when", allowBool: false) : null;
        var bind = new List<PassBinding>();
        if (o.TryGetValue("bind", out var bd))
        {
            if (bd.ValueKind != JsonValueKind.Object) throw new Invalid(4, $"パス {id} の bind がオブジェクトでない");
            foreach (var prop in bd.EnumerateObject())
            {
                if (!HlslNameRegex().IsMatch(prop.Name)) throw new Invalid(4, $"パス {id} の bind の左辺 {prop.Name} が HLSL の名前でない");
                if (bind.Any(b => b.Variable == prop.Name)) throw new Invalid(4, $"パス {id} の bind の左辺 {prop.Name} が重複している");
                if (prop.Value.ValueKind != JsonValueKind.String) throw new Invalid(4, $"パス {id} の bind の右辺が文字列でない");
                bind.Add(new PassBinding(prop.Name, prop.Value.GetString()!));
            }
        }
        return new PassDef(id, shader, inputs, scale, repeat, when, bind);
    }

    private static ParamCondition Condition(JsonElement element, string what, bool allowBool)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new Invalid(7, $"{what} がオブジェクトでない");
        var props = element.EnumerateObject().ToArray();
        if (props.Length != 1) throw new Invalid(7, $"{what} のキーが 1 つでない");
        var values = props[0].Value;
        if (values.ValueKind != JsonValueKind.Array) throw new Invalid(7, $"{what} の値が配列でない");
        var list = new List<string>();
        foreach (var v in values.EnumerateArray())
        {
            list.Add(v.ValueKind switch
            {
                JsonValueKind.String => v.GetString()!,
                JsonValueKind.True when allowBool => "true",
                JsonValueKind.False when allowBool => "false",
                _ => throw new Invalid(7, $"{what} の値の形が合わない"),
            });
        }
        return new ParamCondition(props[0].Name, list);
    }

    // ---- 宣言の中の参照の検査(2・3・4・5・6・8) ----

    private static void Validate(EffectDef def, Func<string, bool> shaderExists)
    {
        // 2: key の重複
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in def.Params)
        {
            if (!keys.Add(p.Key)) throw new Invalid(2, $"key {p.Key} が重複している");
        }

        // 3: パスの id の重複、存在しない pass: の参照、循環
        var passIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pass in def.Passes)
        {
            if (!passIds.Add(pass.Id)) throw new Invalid(3, $"パスの id {pass.Id} が重複している");
        }
        foreach (var pass in def.Passes)
        {
            foreach (var input in pass.Inputs)
            {
                if (input.StartsWith("pass:", StringComparison.Ordinal) && !passIds.Contains(input[5..]))
                    throw new Invalid(3, $"パス {pass.Id} の入力 {input} のパスが無い");
                if (input == "aux" && def.Aux != AuxKind.Text)
                    throw new Invalid(3, $"パス {pass.Id} が aux を読むが、宣言に aux が無い");
            }
        }
        if (HasCycle(def.Passes)) throw new Invalid(3, "パスの参照が循環している");
        foreach (var pass in def.Passes)
        {
            if (pass.Repeat.ParamKey is { } rk && def.FindParam(rk) is { } rpParam && rpParam.Type != ParamType.Int)
                throw new Invalid(3, $"パス {pass.Id} の repeat のキー {rk} が int でない");
        }

        // 4: bind の右辺
        foreach (var pass in def.Passes)
        {
            foreach (var b in pass.Bind)
            {
                if (b.Source.StartsWith('$'))
                {
                    if (!BuiltinVariables.Contains(b.Source)) throw new Invalid(4, $"パス {pass.Id} の bind の右辺 {b.Source} は組み込み変数に無い");
                }
                else if (def.FindParam(b.Source) is null)
                {
                    throw new Invalid(4, $"パス {pass.Id} の bind の右辺 {b.Source} はパラメータの key に無い");
                }
            }
        }

        // 5: scale モードの scaleParams
        if (def.AmountMode == AmountMode.Scale && def.ScaleParams.Count == 0) throw new Invalid(5, "scale モードなのに scaleParams が空");
        foreach (var k in def.ScaleParams)
        {
            if (def.FindParam(k) is { } p && p.Type is not (ParamType.Float or ParamType.Int or ParamType.Angle))
                throw new Invalid(5, $"scaleParams の {k} が数値の型でない");
        }

        // 6: シェーダーの埋め込み
        foreach (var pass in def.Passes)
        {
            if (!shaderExists(pass.Shader)) throw new Invalid(6, $"パス {pass.Id} のシェーダー {pass.Shader} が埋め込まれていない");
        }

        // 8: scaleParams・visibleWhen・when(と repeat の param)の参照
        foreach (var k in def.ScaleParams)
        {
            if (def.FindParam(k) is null) throw new Invalid(8, $"scaleParams の {k} が params に無い");
        }
        foreach (var p in def.Params)
        {
            if (p.VisibleWhen is { } vw) CheckCondition(def, vw, $"{p.Key} の visibleWhen", allowBool: true);
        }
        foreach (var pass in def.Passes)
        {
            if (pass.When is { } w) CheckCondition(def, w, $"パス {pass.Id} の when", allowBool: false);
            if (pass.Repeat.ParamKey is { } rk && def.FindParam(rk) is null)
                throw new Invalid(8, $"パス {pass.Id} の repeat のキー {rk} が params に無い");
        }
    }

    private static void CheckCondition(EffectDef def, ParamCondition c, string what, bool allowBool)
    {
        var p = def.FindParam(c.Key) ?? throw new Invalid(8, $"{what} のキー {c.Key} が params に無い");
        if (p.Type == ParamType.Enum)
        {
            foreach (var v in c.Values)
            {
                if (!p.Options!.Any(o => o.Value == v)) throw new Invalid(8, $"{what} の値 {v} が {c.Key} の options に無い");
            }
        }
        else if (p.Type == ParamType.Bool && allowBool)
        {
            foreach (var v in c.Values)
            {
                if (v is not ("true" or "false")) throw new Invalid(8, $"{what} の値 {v} が真偽でない");
            }
        }
        else
        {
            throw new Invalid(8, $"{what} のキー {c.Key} が{(allowBool ? " enum か bool" : " enum")} でない");
        }
    }

    private static bool HasCycle(IReadOnlyList<PassDef> passes)
    {
        var byId = passes.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1: 調べている途中、2: 済み
        bool Visit(PassDef p)
        {
            if (state.TryGetValue(p.Id, out var s)) return s == 1;
            state[p.Id] = 1;
            foreach (var input in p.Inputs)
            {
                if (input.StartsWith("pass:", StringComparison.Ordinal) && byId.TryGetValue(input[5..], out var dep) && Visit(dep)) return true;
            }
            state[p.Id] = 2;
            return false;
        }
        return passes.Any(Visit);
    }

    // ---- JSON の小さな道具 ----

    private static Dictionary<string, JsonElement> Obj(JsonElement e, IReadOnlyCollection<string> allowed, string what, int reason = 7)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new Invalid(reason, $"{what} がオブジェクトでない");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in e.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name)) throw new Invalid(reason, $"{what} に知らない項目 {prop.Name} がある");
            if (!result.TryAdd(prop.Name, prop.Value)) throw new Invalid(reason, $"{what} の項目 {prop.Name} が重複している");
        }
        return result;
    }

    private static JsonElement Required(Dictionary<string, JsonElement> o, string name, int reason) =>
        o.TryGetValue(name, out var e) ? e : throw new Invalid(reason, $"必須の項目 {name} が無い");

    private static string Text(JsonElement e, string what, int reason) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new Invalid(reason, $"{what} が文字列でない");

    private static bool Bool(JsonElement e, string what, int reason) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new Invalid(reason, $"{what} が真偽でない"),
    };

    private static double Number(JsonElement e, string what, int reason)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var v) || !double.IsFinite(v))
            throw new Invalid(reason, $"{what} が数でない");
        return v;
    }

    private static List<string> StringArray(JsonElement e, string what, int reason)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new Invalid(reason, $"{what} が配列でない");
        return e.EnumerateArray().Select(x => Text(x, what, reason)).ToList();
    }

    private static bool IsInteger(double v) => Math.Floor(v) == v;

    private static void CheckLength(string s, int min, int max, string what)
    {
        var n = new StringInfo(s).LengthInTextElements;
        if (n < min || n > max) throw new Invalid(7, $"{what} の長さ {n} が {min}〜{max} の外");
    }
}
