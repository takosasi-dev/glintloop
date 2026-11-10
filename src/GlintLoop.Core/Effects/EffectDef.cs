// EffectDef(仕様書 §9.2)の型。JSON の宣言を読み込んだ結果を、検査と描画とパラメータ欄が共有する。
// 読み込み(埋め込みリソースだけ。INV-3)と 9 項目の検査は EffectDefLoader が行う。
// 値の範囲の直し方(範囲の端、param_clamped)は §9.7 の規則 4 の 5 に従う。
namespace GlintLoop.Core.Effects;

public enum EffectStage { Shape, Look, Finish, Motion }

public enum AmountMode { Mix, Scale, None }

public enum SizeRule { Same, Crop, RotateFit, Pad }

public enum AuxKind { None, Text }

public enum WaveKind { Sine, Tri, Ease, Saw }

public enum ParamType { Float, Int, Bool, Enum, Color, Angle, Point, Rect, Text, Font }

/// <summary>パラメータの値。型ごとに 1 つの派生型を使う。</summary>
public abstract record ParamValue;

/// <summary>float・int・angle の値。</summary>
public sealed record NumberValue(double Value) : ParamValue;

public sealed record BoolValue(bool Value) : ParamValue;

/// <summary>enum の value・color の <c>#RRGGBBAA</c>・text・font の値。</summary>
public sealed record StringValue(string Value) : ParamValue;

/// <summary>point の値。x と y は画像の幅と高さを 0〜1 にした値。</summary>
public sealed record PointValue(double X, double Y) : ParamValue;

/// <summary>rect の値。単位はパラメータの unit に従う(‰辺 または ‰)。</summary>
public sealed record RectValue(double L, double T, double R, double B) : ParamValue;

public sealed record EnumOption(string Value, string Label);

/// <summary>visibleWhen と when の条件。1 つのキーと、成り立つ値の配列。</summary>
public sealed record ParamCondition(string Key, IReadOnlyList<string> Values);

public sealed record ParamDef(
    string Key,
    string Label,
    ParamType Type,
    double? Min,
    double? Max,
    double? Step,
    ParamValue Default,
    string Unit,
    IReadOnlyList<EnumOption>? Options,
    double? Neutral,
    bool Advanced,
    bool Handle,
    ParamCondition? VisibleWhen,
    int? MaxLength);

/// <summary>repeat の指定。整数の回数か、パラメータのキー。</summary>
public sealed record PassRepeat(int Count, string? ParamKey)
{
    public static PassRepeat Once { get; } = new(1, null);
}

/// <summary>bind の 1 組。左辺は HLSL の定数バッファの変数名、右辺は組み込み変数($ つき)かパラメータのキー。</summary>
public sealed record PassBinding(string Variable, string Source);

public sealed record PassDef(
    string Id,
    string Shader,
    IReadOnlyList<string> Inputs,
    double Scale,
    PassRepeat Repeat,
    ParamCondition? When,
    IReadOnlyList<PassBinding> Bind);

public sealed record EffectDef(
    int Schema,
    string Id,
    string Name,
    EffectStage Stage,
    string Group,
    IReadOnlyList<string> Keywords,
    AmountMode AmountMode,
    IReadOnlyList<string> ScaleParams,
    bool TimeDependent,
    bool NoWave,
    WaveKind WaveDefault,
    SizeRule SizeRule,
    AuxKind Aux,
    IReadOnlyList<ParamDef> Params,
    IReadOnlyList<PassDef> Passes)
{
    public ParamDef? FindParam(string key)
    {
        foreach (var p in Params)
        {
            if (p.Key == key)
            {
                return p;
            }
        }
        return null;
    }
}
