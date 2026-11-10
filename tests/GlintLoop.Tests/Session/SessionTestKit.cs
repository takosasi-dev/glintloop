// 文書と EditorSession のテストの道具。EffectDef をコードで組み立てたカタログ、偽の確認ダイアログ、取り込みの画像を作る。
// EffectDef の読み込み(JSON)は別の担当の範囲なので、ここでは §9.4・§9.11 の例に出る id とパラメータだけを手で書く。
// SHA-256 は内容の代わりに番号から作った 64 桁の小文字 16 進を使う。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.Tests.Session;

internal static class SessionTestKit
{
    private static ParamDef F(string key, double min, double max, double def, string unit = "") =>
        new(key, key, ParamType.Float, min, max, 1, new NumberValue(def), unit, null, null, false, true, null, null);

    private static ParamDef I(string key, double min, double max, double def) =>
        new(key, key, ParamType.Int, min, max, 1, new NumberValue(def), "", null, null, false, true, null, null);

    private static ParamDef A(string key, double min, double max, double def) =>
        new(key, key, ParamType.Angle, min, max, 0.1, new NumberValue(def), "deg", null, null, false, true, null, null);

    private static ParamDef B(string key, bool def) =>
        new(key, key, ParamType.Bool, null, null, null, new BoolValue(def), "", null, null, false, true, null, null);

    private static ParamDef E(string key, string def, params string[] values) =>
        new(key, key, ParamType.Enum, null, null, null, new StringValue(def), "",
            values.Select(v => new EnumOption(v, v)).ToArray(), null, false, true, null, null);

    private static ParamDef C(string key, string def) =>
        new(key, key, ParamType.Color, null, null, null, new StringValue(def), "", null, null, false, true, null, null);

    private static ParamDef P(string key, double x, double y) =>
        new(key, key, ParamType.Point, null, null, null, new PointValue(x, y), "", null, null, false, true, null, null);

    private static ParamDef R(string key, double min, double max, string unit) =>
        new(key, key, ParamType.Rect, min, max, 1, new RectValue(0, 0, 0, 0), unit, null, null, false, true, null, null);

    private static ParamDef T(string key, int maxLength) =>
        new(key, key, ParamType.Text, null, null, null, new StringValue(""), "", null, null, false, true, null, maxLength);

    private static EffectDef Def(string id, EffectStage stage, AmountMode mode, params ParamDef[] ps) =>
        Def(id, stage, mode, false, false, SizeRule.Same, ps);

    private static EffectDef Def(string id, EffectStage stage, AmountMode mode, bool time, bool noWave, SizeRule size, params ParamDef[] ps) =>
        new(1, id, id, stage, "g", Array.Empty<string>(), mode, Array.Empty<string>(), time, noWave, WaveKind.Sine, size,
            AuxKind.None, ps, Array.Empty<PassDef>());

    public static IReadOnlyList<EffectDef> Defs { get; } =
    [
        Def("rotate", EffectStage.Shape, AmountMode.None, false, false, SizeRule.RotateFit,
            A("angle", -180, 180, 0), E("fit", "expand", "expand", "inside")),
        Def("crop", EffectStage.Shape, AmountMode.None, false, false, SizeRule.Crop,
            R("rect", 0, 900, "‰辺"), E("aspect", "free", "free", "orig", "1_1")),
        Def("pad", EffectStage.Shape, AmountMode.None, false, false, SizeRule.Pad,
            R("rect", 0, 3000, "‰"), C("fill", "#00000000")),
        Def("flip", EffectStage.Shape, AmountMode.None, B("h", false), B("v", false)),
        Def("edge", EffectStage.Shape, AmountMode.None, F("shift", -50, 50, 0, "‰"), F("feather", 0, 30, 0, "‰")),
        Def("exposure", EffectStage.Look, AmountMode.Mix, F("ev", -3, 3, 0, "EV")),
        Def("levels", EffectStage.Look, AmountMode.Mix, F("black", 0, 90, 0, "%"), F("white", 10, 100, 100, "%"), F("gamma", 0.2, 5, 1)),
        Def("invert", EffectStage.Look, AmountMode.Mix),
        Def("posterize", EffectStage.Look, AmountMode.Mix, I("levels", 2, 16, 5)),
        Def("outline", EffectStage.Finish, AmountMode.Mix,
            F("width", 0, 100, 10, "‰"), E("pos", "outside", "outside", "inside", "center"), C("color", "#FFFFFFFF"), F("soft", 0, 100, 0, "‰")),
        Def("text", EffectStage.Finish, AmountMode.Mix, T("text", 200), P("pos", 0.5, 0.5)),
        Def("spin", EffectStage.Motion, AmountMode.Mix, true, true, SizeRule.Same,
            E("dir", "cw", "cw", "ccw"), P("pivot", 0.5, 0.5)),
        Def("shake", EffectStage.Motion, AmountMode.Mix, true, false, SizeRule.Same, F("strength", 0, 100, 20, "‰")),
    ];

    public static DictionaryEffectCatalog Catalog { get; } = new(Defs);

    /// <summary>番号 n から作った SHA-256 の代わりの文字列(小文字 16 進 64 桁)。</summary>
    public static string Sha(int n) => n.ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0');

    public static ImportedSource Image(int n, int w, int h, long bytes = 1000, string? name = null) =>
        new(Sha(n), "png", bytes, w, h, name ?? ("img" + n));

    public static EditorSession NewSession(int seed = 421337, ILogSink? log = null) =>
        new(Catalog, new FixedSeedSource(seed), log);

    /// <summary>文書を 1 枚の画像で始めたセッション。</summary>
    public static EditorSession WithBase(int w = 100, int h = 100)
    {
        var s = NewSession();
        s.AddImportedImages([Image(1, w, h)]);
        return s;
    }
}

/// <summary>偽の確認ダイアログ。呼ばれた回数・受け取ったボタン・既定のボタンを記録し、決めた答えを返す。</summary>
internal sealed class FakeConfirm(ReplaceChoice answer) : IReplaceConfirmation
{
    public int Calls { get; private set; }
    public IReadOnlyList<ReplaceChoice>? Buttons { get; private set; }
    public ReplaceChoice? DefaultButton { get; private set; }
    public Action? DuringAsk { get; init; }

    public ReplaceChoice Ask(IReadOnlyList<ReplaceChoice> buttons, ReplaceChoice defaultButton)
    {
        Calls++;
        Buttons = buttons;
        DefaultButton = defaultButton;
        DuringAsk?.Invoke();
        return answer;
    }
}
