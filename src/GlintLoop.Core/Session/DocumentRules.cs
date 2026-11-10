// レイヤーと文書の値の範囲・丸め・名前の規則(§6.4 の FR-41・FR-42・FR-45・FR-54、§9.11 の 3 の表)。
// 編集の操作と JSON の読み込みが同じ規則を使う。丸めは round(x)=floor(x+0.5)(§6 の冒頭)。
// Normalize は土台の配置とブレンドの強制、assets の並べ直し、selected の範囲の直しを行う。
using GlintLoop.Core.Numerics;

namespace GlintLoop.Core.Session;

public static class DocumentRules
{
    public const int MaxLayers = 32;
    public const int MaxNameLength = 40;
    public const int MinEmptySide = 1;
    public const int MaxEmptySide = 4096;
    public const int MinCanvasSide = 16;
    public const double PlaceLimit = 100000;
    public const double MinScale = 5;
    public const double MaxScale = 400;
    public const int MaxSeed = 999999;

    public const string DefaultImageName = "画像";
    public const string DefaultEmptyName = "空のレイヤー";
    public const string CanvasName = "キャンバス";
    public const string PasteName = "貼り付け";
    public const string SampleName = "サンプル";
    public const string CopySuffix = "のコピー";

    /// <summary>empty のレイヤーの size が欠けたときの大きさ(仕様に既定が無いので FR-44 の既定 1024×768 を使う。仮の決め)。</summary>
    public static EmptySize DefaultEmptySize { get; } = new(1024, 768);

    /// <summary>合成の大きさの上限の合計(FR-57)。</summary>
    public const long MaxDocumentBytes = 300_000_000;
    public const long MaxDocumentPixels = 100_000_000;

    /// <summary>x を小数第 digits 位に round する(floor(x×10^d+0.5)÷10^d)。</summary>
    public static double RoundTo(double x, int digits)
    {
        var f = digits == 2 ? 100.0 : digits == 1 ? 10.0 : Math.Pow(10, digits);
        return MathX.RoundHalfUp(x * f) / f;
    }

    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

    public static double RoundPlace(double v) => Clamp(RoundTo(v, 2), -PlaceLimit, PlaceLimit);

    public static double RoundScale(double v) => Clamp(RoundTo(v, 1), MinScale, MaxScale);

    /// <summary>0〜100 の整数(不透明度・ルックの強さ)。</summary>
    public static int RoundPercent(double v) => (int)Clamp(MathX.RoundHalfUp(v), 0, 100);

    /// <summary>名前の既定(空と空白だけのとき)。</summary>
    public static string DefaultName(LayerKind kind) => kind == LayerKind.Image ? DefaultImageName : DefaultEmptyName;

    /// <summary>名前を 40 文字に切る(string.Length で数える)。</summary>
    public static string Truncate(string name) => name.Length > MaxNameLength ? name[..MaxNameLength] : name;

    /// <summary>取り込んだ画像の名前(FR-42)。空と空白だけのときは「画像」、41 文字以上は先頭の 40 文字。</summary>
    public static string ImportName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? DefaultImageName : Truncate(name);

    /// <summary>ファイル名から拡張子を除いた名前(FR-42)。</summary>
    public static string NameFromFileName(string fileName) => ImportName(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>複製の名前(FR-53)。元の名前に「のコピー」を付けて 40 文字に切る。</summary>
    public static string CopyName(string name) => Truncate(name + CopySuffix);

    public static string BlendName(BlendMode b) => b switch
    {
        BlendMode.Normal => "normal",
        BlendMode.Multiply => "multiply",
        BlendMode.Screen => "screen",
        BlendMode.Overlay => "overlay",
        BlendMode.SoftLight => "softlight",
        _ => "add",
    };

    public static bool TryParseBlend(string s, out BlendMode b)
    {
        switch (s)
        {
            case "normal": b = BlendMode.Normal; return true;
            case "multiply": b = BlendMode.Multiply; return true;
            case "screen": b = BlendMode.Screen; return true;
            case "overlay": b = BlendMode.Overlay; return true;
            case "softlight": b = BlendMode.SoftLight; return true;
            case "add": b = BlendMode.Add; return true;
            default: b = BlendMode.Normal; return false;
        }
    }

    /// <summary>
    /// FR-42 の取り込みの scale。r=min(Wc÷w, Hc÷h) が 1 未満なら max(5, floor(1000×r)÷10)、それ以外は 100。
    /// </summary>
    public static double ImportScale(double wc, double hc, int w, int h)
    {
        // 1000×r を 1000×Wc÷w の形で求める(1000×(Wc÷w) より丸めの誤差が小さく、整数の比はちょうどになる)。
        var r1000 = Math.Min(1000 * wc / w, 1000 * hc / h);
        if (r1000 < 1000) return Math.Max(MinScale, Math.Floor(r1000) / 10);
        return 100;
    }

    /// <summary>
    /// 文書の形をそろえる。土台の配置を (0, 0, 100)・ブレンドを normal にし、assets を layers の最初に使われた順に並べ直し
    /// (knownAssets にある ext・bytes を使う)、selected が範囲外なら最前面にする。
    /// </summary>
    public static SessionDocument Normalize(SessionDocument doc, IReadOnlyDictionary<string, AssetEntry> knownAssets)
    {
        var layers = doc.Layers;
        var b = layers[0];
        if (b.Place.X != 0 || b.Place.Y != 0 || b.Place.Scale != 100 || b.Blend != BlendMode.Normal)
        {
            var copy = layers.ToArray();
            copy[0] = b with { Place = Placement.Identity, Blend = BlendMode.Normal };
            layers = copy;
        }
        var assets = new List<AssetEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in layers)
        {
            if (l.Kind != LayerKind.Image || l.Asset is null || !seen.Add(l.Asset)) continue;
            if (knownAssets.TryGetValue(l.Asset, out var a)) assets.Add(a);
            else throw new InvalidOperationException("レイヤーが参照する画像が assets に無い");
        }
        var selected = doc.Selected;
        if (!selected.IsAll && (selected.LayerIndex < 0 || selected.LayerIndex >= layers.Count))
            selected = Target.OfLayer(layers.Count - 1);
        var same = ReferenceEquals(layers, doc.Layers) && selected == doc.Selected && assets.SequenceEqual(doc.Assets);
        return same ? doc : doc with { Layers = layers, Assets = assets, Selected = selected };
    }
}
