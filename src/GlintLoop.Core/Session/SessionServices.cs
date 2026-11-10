// EditorSession が外から受け取るものと、外へ返す小さな型(seed の出所、置き換えの確認、取り込みの画像、操作の結果)。
// seed を決める乱数は FR-66 が許す場所(文書を始めるとき)だけで使い、テストは固定の値を渡す(INV-8)。
// 確認ダイアログは interface にして、画面の側が実装し、テストは偽物を使う(FR-13)。
using GlintLoop.Core.Notices;

namespace GlintLoop.Core.Session;

/// <summary>新しい文書の seed(0〜999999)の出所(FR-66)。</summary>
public interface ISeedSource
{
    int NextSeed();
}

/// <summary>システムの乱数で seed を決める(FR-66 が許す、文書を始めるときだけに使う)。</summary>
public sealed class SystemSeedSource : ISeedSource
{
    public static SystemSeedSource Instance { get; } = new();

    public int NextSeed() => Random.Shared.Next(0, DocumentRules.MaxSeed + 1);
}

/// <summary>いつも同じ seed を返す(テストと再現用)。</summary>
public sealed class FixedSeedSource(int seed) : ISeedSource
{
    public int NextSeed() => seed;
}

/// <summary>置き換えの確認ダイアログのボタン(FR-13)。</summary>
public enum ReplaceChoice { SaveAndReplace, ReplaceWithoutSaving, Cancel }

/// <summary>置き換えの確認ダイアログ。buttons の並びで出し、defaultButton を既定のボタンにする。</summary>
public interface IReplaceConfirmation
{
    ReplaceChoice Ask(IReadOnlyList<ReplaceChoice> buttons, ReplaceChoice defaultButton);
}

/// <summary>新規・開くの要求の結果。</summary>
public enum ReplaceResult
{
    /// <summary>文書が無い(ウェルカム画面)ので何もしなかった(新規のとき)、または確認ダイアログの表示中で無視した。</summary>
    Ignored,
    Replaced,
    Cancelled,
}

/// <summary>読めた 1 枚の画像(取り込みの担当が渡す)。Name はファイル名から拡張子を除いたもの・「貼り付け」・「サンプル」。</summary>
public sealed record ImportedSource(string Sha256, string Ext, long Bytes, int Width, int Height, string Name);

/// <summary>セッションが保持するソースの情報(内容は SHA-256 で同じものを 1 つにまとめる。D-16)。</summary>
public sealed record SourceInfo(string Sha256, string Ext, long Bytes, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>編集の操作の結果。Changed が false のときは文書も履歴も変わっていない。Row は足した行の番号など。</summary>
public readonly record struct EditOutcome(bool Changed, Notice? Notice, int? Row)
{
    public static EditOutcome Unchanged { get; } = new(false, null, null);

    public static EditOutcome Rejected(string noticeKey) => new(false, Notice.Of(noticeKey, NoticeLevel.Warn), null);
}

/// <summary>取り込みで足した結果。Notice は FR-42 の通知の候補(layer.added・layer.full)。取り込み 1 回の通知は FR-4 で呼び出し側が決める。</summary>
public sealed record ImportAddResult(int AddedCount, bool StartedNewDocument, Notice? Notice);

/// <summary>取り込みの前の判定(FR-57。デコードを始める前に、バイト数とヘッダーの画素数で決める)。</summary>
public enum ImportVerdict { Ok, LayerFull, OverBudget }

/// <summary>
/// 1 回の取り込み(複数のファイル)の上限の判定。いまの文書の枚数と、異なる画像のバイト数・画素数の合計から始め、
/// TryAdmit が Ok を返すたびに足した分を数える。同じ SHA-256 の画像は合計を増やさない(E-13)。
/// </summary>
public sealed class ImportBudget
{
    private readonly HashSet<string> _known;
    private int _layers;
    private long _bytes;
    private long _pixels;

    internal ImportBudget(int layers, long bytes, long pixels, IEnumerable<string> known)
    {
        _layers = layers;
        _bytes = bytes;
        _pixels = pixels;
        _known = new HashSet<string>(known, StringComparer.Ordinal);
    }

    public int Layers => _layers;
    public long Bytes => _bytes;
    public long Pixels => _pixels;

    /// <summary>32 枚を超えるなら LayerFull、合計を超えるなら OverBudget(この順に調べる。E-12・E-13)。</summary>
    public ImportVerdict TryAdmit(string sha256, long bytes, long pixels)
    {
        if (_layers >= DocumentRules.MaxLayers) return ImportVerdict.LayerFull;
        if (!_known.Contains(sha256))
        {
            if (_bytes + bytes > DocumentRules.MaxDocumentBytes || _pixels + pixels > DocumentRules.MaxDocumentPixels)
                return ImportVerdict.OverBudget;
            _known.Add(sha256);
            _bytes += bytes;
            _pixels += pixels;
        }
        _layers++;
        return ImportVerdict.Ok;
    }
}
