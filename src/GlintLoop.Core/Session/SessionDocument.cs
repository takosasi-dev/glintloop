// 文書(EditorSession が持つ編集の全体。仕様書 §6.4 の FR-41、§9.11 の 3)の不変の型。
// レイヤーの列(番号 0 が土台)・全体・seed・saltCounter・時間・選んでいる対象・assets・出力設定を持つ。
// 値の直し(範囲・round・土台の強制)は DocumentRules と DocumentJson が行い、この型は検査をしない。
using GlintLoop.Core.Effects;

namespace GlintLoop.Core.Session;

public enum LayerKind { Image, Empty }

/// <summary>ブレンド(FR-49)。JSON では normal・multiply・screen・overlay・softlight・add。</summary>
public enum BlendMode { Normal, Multiply, Screen, Overlay, SoftLight, Add }

/// <summary>スタックの行の印(§6 の用語の origin)。</summary>
public enum EffectOrigin { Look, User }

public enum LoopMode { Infinite, Once }

/// <summary>選んでいる対象。LayerIndex が 0 以上ならレイヤーの番号、負なら全体。</summary>
public readonly record struct Target(int LayerIndex)
{
    public static Target All => new(-1);

    public static Target OfLayer(int index) => new(index);

    public bool IsAll => LayerIndex < 0;

    public override string ToString() => IsAll ? "all" : LayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>配置(位置 x・y は合成のネイティブ寸法の画素、scale は %)。</summary>
public sealed record Placement(double X, double Y, double Scale)
{
    public static Placement Identity { get; } = new(0, 0, 100);
}

/// <summary>empty のレイヤーの幅と高さ(各 1〜4096 の整数)。</summary>
public sealed record EmptySize(int W, int H);

/// <summary>
/// スタックの 1 行。Params は、その EffectDef のパラメータの並び(共通パラメータ cycles・phase・wave が先)で、
/// すべてのキーを持つ(欠けは既定値で埋めてある)。
/// </summary>
public sealed record StackEntry(
    string Id,
    bool Enabled,
    EffectOrigin Origin,
    long Salt,
    double Amount,
    IReadOnlyList<KeyValuePair<string, ParamValue>> Params)
{
    public ParamValue? Get(string key)
    {
        foreach (var kv in Params)
        {
            if (kv.Key == key) return kv.Value;
        }
        return null;
    }
}

/// <summary>
/// レイヤー。Kind が Image のとき Asset(ソースの SHA-256。小文字 16 進 64 桁)を、Empty のとき Size と Fill(<c>#RRGGBBAA</c>)を持つ。
/// </summary>
public sealed record Layer(
    string Name,
    LayerKind Kind,
    string? Asset,
    EmptySize? Size,
    string? Fill,
    bool Visible,
    int Opacity,
    BlendMode Blend,
    Placement Place,
    int LookStrength,
    IReadOnlyList<StackEntry> Stack);

/// <summary>全体(合成に掛けるスタック)。JSON の all。</summary>
public sealed record GlobalStack(int LookStrength, IReadOnlyList<StackEntry> Stack)
{
    public static GlobalStack Empty { get; } = new(100, Array.Empty<StackEntry>());
}

/// <summary>assets の 1 項目。ext は png・jpg・webp・bmp・ico・gif・dib のどれか。bytes は 1〜100,000,000。</summary>
public sealed record AssetEntry(string Sha256, string Ext, long Bytes);

/// <summary>時間の設定(FR-58)。ループの長さは 10 倍の整数 L10(5〜100)で持つ。Phase 4 で使う。</summary>
public sealed record TimeSettings(int Length10, int Fps, LoopMode Loop, long HoldMs)
{
    public static TimeSettings Default { get; } = new(20, 20, LoopMode.Infinite, 0);
}

/// <summary>出力の大きさ。mode が auto のとき Value を持たない。</summary>
public sealed record OutputSize(string Mode, double? Value);

/// <summary>容量上限。kind が preset のとき Key、bytes のとき Bytes を持つ。</summary>
public sealed record OutputLimit(string Kind, string? Key, long? Bytes);

public sealed record GifOptions(long Colors, string Dither, string? Matte, bool Diff);

public sealed record JpegOptions(long Quality, string Background);

public sealed record WebpOptions(long Quality, bool Lossless);

/// <summary>APNG のオプション。Loops は 1〜4 で持ち、time.loop が infinite のときの JSON には 0 を書く。</summary>
public sealed record ApngOptions(bool Palette, long Loops);

/// <summary>出力設定(§9.11 の 3 の output)。Phase 2 では既定値で持ち、JSON に往復できるだけ。</summary>
public sealed record OutputSettings(
    string Format,
    OutputSize Size,
    OutputLimit Limit,
    GifOptions Gif,
    JpegOptions Jpeg,
    WebpOptions Webp,
    ApngOptions Apng)
{
    public static OutputSettings Default { get; } = new(
        "gif",
        new OutputSize("auto", null),
        new OutputLimit("none", null, null),
        new GifOptions(256, "blue", null, true),
        new JpegOptions(90, ColorText.Rgb(0xFFFFFF)),
        new WebpOptions(85, false),
        new ApngOptions(false, 1));
}

/// <summary>文書の全体。Layers は下から上の順で、先頭が土台。</summary>
public sealed record SessionDocument(
    int Seed,
    long SaltCounter,
    TimeSettings Time,
    Target Selected,
    IReadOnlyList<AssetEntry> Assets,
    IReadOnlyList<Layer> Layers,
    GlobalStack All,
    OutputSettings Output)
{
    public Layer Base => Layers[0];

    public int TopIndex => Layers.Count - 1;

    /// <summary>対象のスタック(全体なら All.Stack)。</summary>
    public IReadOnlyList<StackEntry> StackOf(Target t) => t.IsAll ? All.Stack : Layers[t.LayerIndex].Stack;
}
