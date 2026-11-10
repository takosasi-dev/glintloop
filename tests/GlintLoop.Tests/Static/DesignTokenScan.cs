// INV-14 の数値の検査の道具。XAML とコードから余白・角丸・時間・イージングの直書きを拾い、許す値の一覧と比べる。
// 許す値(§9.1 のトークン表の数字)はここに書かない。StaticInvariantTests.cs が TokenValues として渡す。
// grep と同じく文字列で探すので、コメントの中も数え、変数に入れてから渡した値は見つけない。
using System.Globalization;
using System.Text.RegularExpressions;

namespace GlintLoop.Tests.Static;

public static partial class DesignTokenScan
{
    /// <summary>拾った値の種類。OtherEasing は KeySpline 以外のイージング(§9.1 は KeySpline の 4 本だけを使う)。</summary>
    public enum Kind { Space, Radius, Time, Easing, OtherEasing }

    /// <summary>拾った 1 件(ファイル・行・種類・属性や書き方の名前・値の文字列)。</summary>
    public readonly record struct Use(string File, int Line, Kind Kind, string Name, string Raw)
    {
        public override string ToString() => $"{File}:{Line} {Name}=\"{Raw}\"";
    }

    /// <summary>許す値の一覧。余白と角丸は DIP、時間はミリ秒、イージングは KeySpline の 4 つの数。</summary>
    public sealed record TokenValues(
        IReadOnlyCollection<double> Space,
        IReadOnlyCollection<double> Radius,
        IReadOnlyCollection<long> TimeMs,
        IReadOnlyCollection<double[]> KeySplines);

    /// <summary>読めなかった値に入れる印(Problem が「読めない」として違反にする)。</summary>
    private const string Unreadable = "(読めない)";

    private static readonly Dictionary<string, Kind> XamlNames = new(StringComparer.Ordinal)
    {
        ["Margin"] = Kind.Space, ["Padding"] = Kind.Space,
        ["CornerRadius"] = Kind.Radius, ["RadiusX"] = Kind.Radius, ["RadiusY"] = Kind.Radius,
        ["Duration"] = Kind.Time, ["BeginTime"] = Kind.Time, ["KeyTime"] = Kind.Time,
        ["InitialShowDelay"] = Kind.Time, ["BetweenShowDelay"] = Kind.Time,
        ["KeySpline"] = Kind.Easing,
    };

    private const string XamlNameAlt = "Margin|Padding|CornerRadius|RadiusX|RadiusY|Duration|BeginTime|KeyTime|InitialShowDelay|BetweenShowDelay|KeySpline";

    // 属性の値(二重引用符と単一引用符のどちらも)
    private const string QuotedVal = "(?:\"(?<val>[^\"]*)\"|'(?<val>[^']*)')";

    // 属性(Margin="8"、Margin='8'、ToolTipService.InitialShowDelay="500" のような添付の形も)。ShowDuration などの長い名前は前が英字なので拾わない。
    [GeneratedRegex("(?<=[\\s.])(?<name>" + XamlNameAlt + ")\\s*=\\s*" + QuotedVal)]
    private static partial Regex XamlAttributeRegex();

    // <Setter Property="Margin" Value="8"/>(Property と Value の順はどちらでもよい)
    [GeneratedRegex("<(?:\\w+:)?Setter\\b[^>]*>")]
    private static partial Regex SetterTagRegex();

    [GeneratedRegex("\\bProperty\\s*=\\s*[\"'](?:[\\w]+\\.)?(?<p>\\w+)[\"']")]
    private static partial Regex SetterPropertyRegex();

    [GeneratedRegex("\\bValue\\s*=\\s*" + QuotedVal)]
    private static partial Regex SetterValueRegex();

    // 要素の中身に書く形(<Thickness>8</Thickness>、<sys:TimeSpan>0:0:0.12</sys:TimeSpan> など)
    [GeneratedRegex("<(?:\\w+:)?(?<el>Thickness|CornerRadius|Duration|KeyTime|KeySpline|TimeSpan)\\b[^>/]*>(?<val>[^<]*)</")]
    private static partial Regex XamlElementRegex();

    // プロパティ要素の中身に書く形(<Border.Margin>5</Border.Margin>、<DoubleAnimation.Duration>0:0:0.13</DoubleAnimation.Duration>)
    [GeneratedRegex("<(?:\\w+:)?\\w+\\.(?<name>" + XamlNameAlt + ")\\s*>(?<val>[^<]*)</")]
    private static partial Regex XamlPropertyElementRegex();

    // 属性で値を書く要素(<Thickness Left="5"/>、<CornerRadius TopLeft="6"/>、<KeySpline ControlPoint1="0.23,1" ControlPoint2="0.32,1"/>)
    [GeneratedRegex("<(?:\\w+:)?(?<el>Thickness|CornerRadius|KeySpline)\\b(?<attrs>[^>]*)>")]
    private static partial Regex XamlObjectElementRegex();

    [GeneratedRegex("(?<![\\w:.])(?<name>Left|Top|Right|Bottom|TopLeft|TopRight|BottomRight|BottomLeft|ControlPoint1|ControlPoint2)\\s*=\\s*" + QuotedVal)]
    private static partial Regex XamlMemberAttributeRegex();

    // KeySpline 以外のイージング: <CubicEase/> などの要素、EasingFunction="..." の属性、<X.EasingFunction> のプロパティ要素
    [GeneratedRegex("<(?:\\w+:)?(?<el>\\w*Ease)(?=[\\s/>])")]
    private static partial Regex XamlEaseElementRegex();

    [GeneratedRegex("(?<=[\\s.])EasingFunction\\s*=\\s*" + QuotedVal)]
    private static partial Regex XamlEasingAttributeRegex();

    [GeneratedRegex("<(?:\\w+:)?\\w+\\.EasingFunction\\s*>")]
    private static partial Regex XamlEasingPropertyElementRegex();

    /// <summary>XAML の文字列から、余白・角丸・時間・イージングの値を拾う。{...}(リソースの参照など)の値は拾わない。</summary>
    public static List<Use> ExtractXaml(string file, string text)
    {
        var uses = new List<Use>();
        foreach (Match m in XamlAttributeRegex().Matches(text))
            Add(uses, file, text, m.Index, XamlNames[m.Groups["name"].Value], m.Groups["name"].Value, m.Groups["val"].Value);
        foreach (Match tag in SetterTagRegex().Matches(text))
        {
            var p = SetterPropertyRegex().Match(tag.Value);
            var v = SetterValueRegex().Match(tag.Value);
            if (!p.Success || !v.Success || !XamlNames.TryGetValue(p.Groups["p"].Value, out var kind)) continue;
            Add(uses, file, text, tag.Index, kind, "Setter " + p.Groups["p"].Value, v.Groups["val"].Value);
        }
        foreach (Match m in XamlElementRegex().Matches(text))
        {
            var el = m.Groups["el"].Value;
            var kind = el switch
            {
                "Thickness" => Kind.Space,
                "CornerRadius" => Kind.Radius,
                "KeySpline" => Kind.Easing,
                _ => Kind.Time,
            };
            if (string.IsNullOrWhiteSpace(m.Groups["val"].Value)) continue; // 中身が無い(<KeySpline ControlPoint1="…"></KeySpline> は下で拾う)
            Add(uses, file, text, m.Index, kind, "<" + el + ">", m.Groups["val"].Value);
        }
        foreach (Match m in XamlPropertyElementRegex().Matches(text))
        {
            if (string.IsNullOrWhiteSpace(m.Groups["val"].Value)) continue; // 中身が子の要素(<Thickness> などは上で拾う)
            var name = m.Groups["name"].Value;
            Add(uses, file, text, m.Index, XamlNames[name], "<." + name + ">", m.Groups["val"].Value);
        }
        foreach (Match m in XamlObjectElementRegex().Matches(text))
        {
            var el = m.Groups["el"].Value;
            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match a in XamlMemberAttributeRegex().Matches(m.Groups["attrs"].Value))
                members[a.Groups["name"].Value] = a.Groups["val"].Value;
            if (members.Count == 0) continue; // 中身に書く形(上で拾う)
            if (el == "KeySpline")
            {
                members.TryGetValue("ControlPoint1", out var c1);
                members.TryGetValue("ControlPoint2", out var c2);
                if (c1?.TrimStart().StartsWith('{') == true || c2?.TrimStart().StartsWith('{') == true) continue;
                Add(uses, file, text, m.Index, Kind.Easing, "<KeySpline ControlPoint>",
                    c1 is null || c2 is null ? Unreadable : c1 + "," + c2);
                continue;
            }
            var kind = el == "Thickness" ? Kind.Space : Kind.Radius;
            foreach (var (name, val) in members)
                Add(uses, file, text, m.Index, kind, "<" + el + " " + name + ">", val);
        }
        foreach (Match m in XamlEaseElementRegex().Matches(text))
            AddOtherEasing(uses, file, text, m.Index, "<" + m.Groups["el"].Value + ">");
        foreach (Match m in XamlEasingAttributeRegex().Matches(text))
        {
            if (m.Groups["val"].Value.Trim() == "{x:Null}") continue;
            AddOtherEasing(uses, file, text, m.Index, "EasingFunction");
        }
        foreach (Match m in XamlEasingPropertyElementRegex().Matches(text))
            AddOtherEasing(uses, file, text, m.Index, "<.EasingFunction>");
        uses.Sort((a, b) => a.Line.CompareTo(b.Line));
        return uses;
    }

    private static void Add(List<Use> uses, string file, string text, int index, Kind kind, string name, string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith('{')) return; // StaticResource・DynamicResource・Binding・x:Null
        uses.Add(new Use(file, LineOf(text, index), kind, name, value));
    }

    /// <summary>KeySpline 以外のイージングを 1 件足す。同じ行に何度出ても 1 件にする(EasingFunction = new CubicEase() など)。</summary>
    private static void AddOtherEasing(List<Use> uses, string file, string text, int index, string name)
    {
        var line = LineOf(text, index);
        if (uses.Any(u => u.Kind == Kind.OtherEasing && u.Line == line)) return;
        uses.Add(new Use(file, line, Kind.OtherEasing, name, name));
    }

    private static int LineOf(string text, int index) => 1 + text.AsSpan(0, index).Count('\n');

    // ---- コード(.cs)----

    // 数のリテラル(1、0.5、-4、8d、0.25f、130L、1_000)。引数がこれだけでできているときだけ拾う。
    private const string Num = "-?(?:\\d[\\d_]*(?:\\.\\d+)?|\\.\\d+)[dDfFmMlLuU]{0,2}";

    // 型の名前の前の名前空間(System.Windows.Thickness など)
    private const string Ns = "(?:\\w+\\s*\\.\\s*)*";

    // new Thickness(8)・new System.Windows.Thickness(5)。文に Margin・Padding がある(Margin =、PaddingProperty)ときだけ余白とみなす。
    [GeneratedRegex("\\bnew\\s+" + Ns + "Thickness\\s*\\((?<args>[^()]*)\\)")]
    private static partial Regex CodeThicknessRegex();

    // Margin = new(8)・Padding = new(8, 4)(型を書かない new)
    [GeneratedRegex("\\b(?<name>Margin|Padding)\\s*=\\s*new\\s*\\((?<args>[^()]*)\\)")]
    private static partial Regex CodeSpaceTargetTypedRegex();

    [GeneratedRegex("Margin|Padding")]
    private static partial Regex SpaceContextRegex();

    // new CornerRadius(4)・new System.Windows.CornerRadius(4)・CornerRadius = new(4)
    [GeneratedRegex("(?:\\bnew\\s+" + Ns + "CornerRadius\\s*|\\bCornerRadius\\s*=\\s*new\\s*)\\((?<args>[^()]*)\\)")]
    private static partial Regex CodeRadiusRegex();

    // RadiusX = 4
    [GeneratedRegex("\\b(?<name>RadiusX|RadiusY)\\s*=\\s*(?<args>" + Num + ")\\s*[;,}]")]
    private static partial Regex CodeRadiusXyRegex();

    // オブジェクト初期化子(new Thickness { Left = 5 }・new CornerRadius() { TopLeft = 6 }・Margin = new() { Left = 5 })
    [GeneratedRegex("(?:\\bnew\\s+" + Ns + "(?<type>Thickness|CornerRadius)|\\b(?<type>Margin|Padding|CornerRadius)\\s*=\\s*new)\\s*(?:\\(\\s*\\))?\\s*\\{(?<body>[^{}]*)\\}")]
    private static partial Regex CodeInitializerRegex();

    [GeneratedRegex("\\b(?<m>Left|Top|Right|Bottom|TopLeft|TopRight|BottomRight|BottomLeft)\\s*=\\s*(?<v>[^,}]*)")]
    private static partial Regex CodeInitializerMemberRegex();

    // new KeySpline(0.23, 1, 0.32, 1)
    [GeneratedRegex("\\bnew\\s+" + Ns + "KeySpline\\s*\\((?<args>[^()]*)\\)")]
    private static partial Regex CodeKeySplineRegex();

    // new KeySpline(new Point(0.23, 1), new Point(0.32, 1))
    [GeneratedRegex("\\bnew\\s+" + Ns + "KeySpline\\s*\\(\\s*new\\s*(?:" + Ns + "Point)?\\s*\\((?<a>[^()]*)\\)\\s*,\\s*new\\s*(?:" + Ns + "Point)?\\s*\\((?<b>[^()]*)\\)\\s*\\)")]
    private static partial Regex CodeKeySplinePointsRegex();

    // new KeySpline { ControlPoint1 = new Point(0.23, 1), ControlPoint2 = new Point(0.32, 1) }
    [GeneratedRegex("\\bnew\\s+" + Ns + "KeySpline\\s*(?:\\(\\s*\\))?\\s*\\{(?<body>(?:[^{}()]|\\([^()]*\\))*)\\}")]
    private static partial Regex CodeKeySplineInitializerRegex();

    [GeneratedRegex("\\bControlPoint(?<i>[12])\\s*=\\s*new\\s*(?:" + Ns + "Point)?\\s*\\((?<p>[^()]*)\\)")]
    private static partial Regex CodeControlPointRegex();

    // TimeSpan.FromMilliseconds(120)・TimeSpan.FromSeconds(0.12)・TimeSpan.FromTicks(1_300_000)・TimeSpan.FromSeconds(0, 130)
    [GeneratedRegex("\\bTimeSpan\\s*\\.\\s*From(?<unit>Days|Hours|Minutes|Seconds|Milliseconds|Microseconds|Ticks)\\s*\\((?<args>[^()]*)\\)")]
    private static partial Regex CodeTimeSpanRegex();

    // new TimeSpan(0, 0, 0, 0, 130)・new System.TimeSpan(1_300_000)
    [GeneratedRegex("\\bnew\\s+" + Ns + "TimeSpan\\s*\\((?<args>[^()]*)\\)")]
    private static partial Regex CodeNewTimeSpanRegex();

    // UI の時間を表す書き方。文(前後の ; { } の間。複数行に分けた呼び出しも 1 つ)にこれがあるときだけ、TimeSpan を UI の時間とみなす(GIF の遅延などは数えない)。
    [GeneratedRegex("\\b(?:Duration|BeginTime|KeyTime|InitialShowDelay|BetweenShowDelay)\\s*=(?!=)|\\b(?:Duration|BeginTime|KeyTime|InitialShowDelay|BetweenShowDelay)Property\\b|\\bnew\\s+" + Ns + "Duration\\s*\\(|\\bKeyTime\\s*\\.\\s*FromTimeSpan\\s*\\(|\\bnew\\s+" + Ns + "\\w*(?:Animation|KeyFrame)\\w*\\s*[({]")]
    private static partial Regex CodeTimeContextRegex();

    // ToolTipService.SetInitialShowDelay(x, 500)・InitialShowDelay = 500
    [GeneratedRegex("(?:\\bSet(?<name>InitialShowDelay|BetweenShowDelay)\\s*\\([^,()]*,\\s*(?:\\w+\\s*:\\s*)?|\\b(?<name>InitialShowDelay|BetweenShowDelay)\\s*=\\s*)(?<args>" + Num + ")\\s*[);,}]")]
    private static partial Regex CodeDelayRegex();

    // x.SetValue(Rectangle.RadiusXProperty, 3)・x.SetValue(ToolTipService.InitialShowDelayProperty, 500)
    [GeneratedRegex("\\bSetValue\\s*\\(\\s*" + Ns + "(?<name>RadiusX|RadiusY|InitialShowDelay|BetweenShowDelay)Property\\s*,\\s*(?:\\w+\\s*:\\s*)?(?<args>" + Num + ")\\s*\\)")]
    private static partial Regex CodeSetValueNumberRegex();

    // KeySpline 以外のイージング(new CubicEase()・EasingFunction = …・EasingFunctionProperty・IEasingFunction を実装した自作のもの)
    [GeneratedRegex("\\bnew\\s+" + Ns + "(?<el>\\w*Ease)\\s*[({]|\\bEasingFunction\\s*=(?!=)\\s*(?!null\\b)\\S|\\bEasingFunctionProperty\\b|\\bIEasingFunction\\b|\\bEasingFunctionBase\\b")]
    private static partial Regex CodeOtherEasingRegex();

    // 名前付き引数の名前(topLeft: 6 の topLeft:)
    [GeneratedRegex("^@?(?<n>\\w+)\\s*:(?!:)\\s*")]
    private static partial Regex ArgNameRegex();

    /// <summary>C# の文字列から、余白・角丸・時間・イージングの数のリテラルを拾う。引数に変数や式があるものは拾わない。</summary>
    public static List<Use> ExtractCode(string file, string text)
    {
        var uses = new List<Use>();
        foreach (Match m in CodeThicknessRegex().Matches(text))
        {
            if (SpaceContextRegex().IsMatch(Statement(text, m))) AddLiteralArgs(uses, file, text, m, Kind.Space, "Thickness");
        }
        foreach (Match m in CodeSpaceTargetTypedRegex().Matches(text))
            AddLiteralArgs(uses, file, text, m, Kind.Space, m.Groups["name"].Value);
        foreach (Match m in CodeRadiusRegex().Matches(text))
            AddLiteralArgs(uses, file, text, m, Kind.Radius, "CornerRadius");
        foreach (Match m in CodeRadiusXyRegex().Matches(text))
            AddLiteralArgs(uses, file, text, m, Kind.Radius, m.Groups["name"].Value);
        foreach (Match m in CodeInitializerRegex().Matches(text))
        {
            var type = m.Groups["type"].Value;
            var kind = type == "CornerRadius" ? Kind.Radius : Kind.Space;
            if (type == "Thickness" && !SpaceContextRegex().IsMatch(Statement(text, m))) continue;
            foreach (Match member in CodeInitializerMemberRegex().Matches(m.Groups["body"].Value))
            {
                var v = Literal(member.Groups["v"].Value);
                if (v is not null) uses.Add(new Use(file, LineOf(text, m.Index), kind, type + " { " + member.Groups["m"].Value + " }", v));
            }
        }
        foreach (Match m in CodeKeySplineRegex().Matches(text))
            AddKeySpline(uses, file, text, m.Index, m.Groups["args"].Value);
        foreach (Match m in CodeKeySplinePointsRegex().Matches(text))
            AddKeySplinePoints(uses, file, text, m.Index, m.Groups["a"].Value, m.Groups["b"].Value);
        foreach (Match m in CodeKeySplineInitializerRegex().Matches(text))
        {
            string? a = null, b = null;
            foreach (Match cp in CodeControlPointRegex().Matches(m.Groups["body"].Value))
            {
                if (cp.Groups["i"].Value == "1") a = cp.Groups["p"].Value; else b = cp.Groups["p"].Value;
            }
            if (a is null || b is null) uses.Add(new Use(file, LineOf(text, m.Index), Kind.Easing, "KeySpline { }", Unreadable));
            else AddKeySplinePoints(uses, file, text, m.Index, a, b);
        }
        foreach (Match m in CodeDelayRegex().Matches(text))
            AddLiteralArgs(uses, file, text, m, Kind.Time, m.Groups["name"].Value, "ms");
        foreach (Match m in CodeSetValueNumberRegex().Matches(text))
        {
            var name = m.Groups["name"].Value;
            if (name.StartsWith("Radius", StringComparison.Ordinal)) AddLiteralArgs(uses, file, text, m, Kind.Radius, "SetValue " + name);
            else AddLiteralArgs(uses, file, text, m, Kind.Time, "SetValue " + name, "ms");
        }
        foreach (Match m in CodeTimeSpanRegex().Matches(text))
        {
            if (!CodeTimeContextRegex().IsMatch(Statement(text, m))) continue;
            var unit = m.Groups["unit"].Value;
            AddTime(uses, file, text, m, "TimeSpan.From" + unit, TimeSpanFromMs(unit, m.Groups["args"].Value));
        }
        foreach (Match m in CodeNewTimeSpanRegex().Matches(text))
        {
            if (!CodeTimeContextRegex().IsMatch(Statement(text, m))) continue;
            AddTime(uses, file, text, m, "new TimeSpan", NewTimeSpanMs(m.Groups["args"].Value));
        }
        foreach (Match m in CodeOtherEasingRegex().Matches(text))
            AddOtherEasing(uses, file, text, m.Index, m.Groups["el"].Success ? "new " + m.Groups["el"].Value : m.Value.Trim());
        uses.Sort((a, b) => a.Line.CompareTo(b.Line));
        return uses;
    }

    /// <summary>一致を含む文(前の ; { } の次から、後ろの ; { } の手前まで)。複数行に分けた呼び出しも 1 つの文として見る。</summary>
    private static string Statement(string text, Match m)
    {
        var start = m.Index == 0 ? 0 : text.LastIndexOfAny([';', '{', '}'], m.Index - 1) + 1;
        var after = m.Index + m.Length;
        var end = after >= text.Length ? -1 : text.IndexOfAny([';', '{', '}'], after);
        if (end < 0) end = text.Length;
        return text[start..end];
    }

    private static readonly Regex NumOnly = new("^" + Num + "$", RegexOptions.CultureInvariant);

    /// <summary>1 つの引数が数のリテラルなら、名前・桁区切り・接尾辞を除いた文字列を返す。そうでなければ null。</summary>
    private static string? Literal(string arg)
    {
        var p = ArgNameRegex().Replace(arg.Trim(), "").Trim();
        if (!NumOnly.IsMatch(p)) return null;
        return p.Replace("_", "").TrimEnd('d', 'D', 'f', 'F', 'm', 'M', 'l', 'L', 'u', 'U');
    }

    /// <summary>引数を (名前か null, 数の文字列) に分ける。1 つでも数のリテラルでなければ null(変数・式・定数の名前を含む)。</summary>
    private static List<(string? Name, string Value)>? LiteralArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        var result = new List<(string?, string)>();
        foreach (var part in args.Split(','))
        {
            var t = part.Trim();
            var v = Literal(t);
            if (v is null) return null;
            var nm = ArgNameRegex().Match(t);
            result.Add((nm.Success ? nm.Groups["n"].Value : null, v));
        }
        return result;
    }

    private static void AddLiteralArgs(List<Use> uses, string file, string text, Match m, Kind kind, string name, string? unit = null)
    {
        var args = LiteralArgs(m.Groups["args"].Value);
        if (args is null) return;
        var raw = string.Join(",", args.Select(a => a.Value));
        uses.Add(new Use(file, LineOf(text, m.Index), kind, name, unit is null ? raw : raw + unit));
    }

    // KeySpline(x1, y1, x2, y2)。名前付き引数はこの名前の順に並べ直し、ほかの名前なら読めないとする。
    private static readonly string[] KeySplineParams = ["x1", "y1", "x2", "y2"];

    private static void AddKeySpline(List<Use> uses, string file, string text, int index, string argsText)
    {
        var args = LiteralArgs(argsText);
        if (args is null) return;
        string raw;
        if (args.All(a => a.Name is null)) raw = string.Join(",", args.Select(a => a.Value));
        else if (args.Count == 4 && args.All(a => a.Name is not null && KeySplineParams.Contains(a.Name)) && args.Select(a => a.Name).Distinct().Count() == 4)
            raw = string.Join(",", KeySplineParams.Select(p => args.First(a => a.Name == p).Value));
        else raw = Unreadable;
        uses.Add(new Use(file, LineOf(text, index), Kind.Easing, "KeySpline", raw));
    }

    private static void AddKeySplinePoints(List<Use> uses, string file, string text, int index, string a, string b)
    {
        var pa = LiteralArgs(a);
        var pb = LiteralArgs(b);
        if (pa is null || pb is null) return;
        var raw = pa.Count == 2 && pb.Count == 2 && pa.Concat(pb).All(x => x.Name is null)
            ? string.Join(",", pa.Concat(pb).Select(x => x.Value))
            : Unreadable;
        uses.Add(new Use(file, LineOf(text, index), Kind.Easing, "KeySpline(Point, Point)", raw));
    }

    private static void AddTime(List<Use> uses, string file, string text, Match m, string name, decimal? ms)
    {
        if (LiteralArgs(m.Groups["args"].Value) is null) return; // 変数・式を含む
        var raw = ms is null ? Unreadable : ms.Value.ToString(CultureInfo.InvariantCulture) + "ms";
        uses.Add(new Use(file, LineOf(text, m.Index), Kind.Time, name, raw));
    }

    // TimeSpan の単位(ミリ秒)。From… の 2 つ目からの引数は、1 つずつ下の単位になる(FromSeconds(1, 500) は 1 秒 500 ミリ秒)。
    private static readonly (string Name, decimal Ms)[] TimeUnits =
    [
        ("days", 86_400_000m), ("hours", 3_600_000m), ("minutes", 60_000m), ("seconds", 1_000m),
        ("milliseconds", 1m), ("microseconds", 0.001m),
    ];

    private const decimal TickMs = 0.0001m;

    /// <summary>引数をミリ秒にして足す。firstUnit は 1 つ目の引数の単位(TimeUnits の番号)、-1 なら ticks が 1 つだけ。</summary>
    private static decimal? SumUnits(List<(string? Name, string Value)> args, int firstUnit)
    {
        decimal ms = 0;
        for (var i = 0; i < args.Count; i++)
        {
            if (!decimal.TryParse(args[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            decimal unitMs;
            if (args[i].Name is { } name)
            {
                if (name == "ticks") unitMs = TickMs;
                else
                {
                    var idx = Array.FindIndex(TimeUnits, u => u.Name == name);
                    if (idx < 0) return null;
                    unitMs = TimeUnits[idx].Ms;
                }
            }
            else if (firstUnit < 0)
            {
                if (i > 0) return null;
                unitMs = TickMs;
            }
            else
            {
                if (firstUnit + i >= TimeUnits.Length) return null;
                unitMs = TimeUnits[firstUnit + i].Ms;
            }
            ms += v * unitMs;
        }
        return ms;
    }

    private static decimal? TimeSpanFromMs(string unit, string argsText)
    {
        var args = LiteralArgs(argsText);
        if (args is null) return null;
        if (unit == "Ticks") return SumUnits(args, -1);
        var first = Array.FindIndex(TimeUnits, u => u.Name.Equals(unit, StringComparison.OrdinalIgnoreCase));
        return first < 0 ? null : SumUnits(args, first);
    }

    // new TimeSpan の引数の数と単位: 1 は ticks、3 は時・分・秒、4 は日・時・分・秒、5 は + ミリ秒、6 は + マイクロ秒。
    private static decimal? NewTimeSpanMs(string argsText)
    {
        var args = LiteralArgs(argsText);
        if (args is null) return null;
        if (args.Any(a => a.Name is not null)) return args.All(a => a.Name is not null) ? SumUnits(args, 0) : null;
        return args.Count switch
        {
            1 => SumUnits(args, -1),
            3 => SumUnits(args, 1),
            4 or 5 or 6 => SumUnits(args, 0),
            _ => null,
        };
    }

    // ---- 判定 ----

    private static readonly string[] TimeWords = ["Automatic", "Forever", "Uniform", "Paced"];

    /// <summary>許されない値なら理由を、許される値と数でない書き方(Automatic など)なら null を返す。</summary>
    public static string? Problem(Use use, TokenValues allowed)
    {
        switch (use.Kind)
        {
            case Kind.Space:
            case Kind.Radius:
            {
                var set = use.Kind == Kind.Space ? allowed.Space : allowed.Radius;
                var nums = ParseNumbers(use.Raw);
                if (nums is null || nums.Length is not (1 or 2 or 4)) return $"{use}: 数として読めない";
                var bad = nums.Where(n => n != 0 && !set.Contains(n)).ToList();
                return bad.Count == 0 ? null : $"{use}: {string.Join("・", bad)} はトークン表に無い";
            }
            case Kind.Time:
            {
                if (TimeWords.Contains(use.Raw, StringComparer.Ordinal) || use.Raw.EndsWith('%')) return null;
                var ms = ParseMilliseconds(use.Raw, intIsMs: use.Name.Contains("ShowDelay", StringComparison.Ordinal));
                if (ms is null) return $"{use}: 時間として読めない(1 ms 未満の端数も含む)";
                return ms == 0 || allowed.TimeMs.Contains(ms.Value) ? null : $"{use}: {ms} ms はトークン表に無い";
            }
            case Kind.Easing:
            {
                var nums = ParseNumbers(use.Raw);
                if (nums is null || nums.Length != 4) return $"{use}: KeySpline として読めない";
                return allowed.KeySplines.Any(k => k.SequenceEqual(nums)) ? null : $"{use}: トークン表に無い KeySpline";
            }
            case Kind.OtherEasing:
                return $"{use}: KeySpline 以外のイージング(§9.1 は KeySpline の 4 本だけを使う)";
            default:
                return $"{use}: 種類が分からない";
        }
    }

    /// <summary>コンマか空白で区切った数(XAML の Thickness・CornerRadius・KeySpline の書き方)。読めなければ null。</summary>
    private static double[]? ParseNumbers(string raw)
    {
        var parts = raw.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])) return null;
        }
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// 時間の文字列をミリ秒にする。"120ms"・"0.12s"(コードから拾ったもの)、"0:0:0.12"(XAML の TimeSpan)。
    /// intIsMs のとき(InitialShowDelay などの int の属性)だけ、"500" をミリ秒と読む(TimeSpan の "500" は 500 日)。
    /// </summary>
    private static long? ParseMilliseconds(string raw, bool intIsMs)
    {
        decimal ms;
        if (raw.EndsWith("ms", StringComparison.Ordinal))
        {
            if (!decimal.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out ms)) return null;
        }
        else if (raw.EndsWith('s'))
        {
            if (!decimal.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return null;
            ms = s * 1000m;
        }
        else if (intIsMs && decimal.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            ms = whole; // InitialShowDelay などの int(ミリ秒)
        }
        else
        {
            if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts)) return null;
            if (ts.Ticks % TimeSpan.TicksPerMillisecond != 0) return null;
            return ts.Ticks / TimeSpan.TicksPerMillisecond;
        }
        return ms == decimal.Truncate(ms) ? (long)ms : null;
    }
}
