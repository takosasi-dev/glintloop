// この PC にインストールされているフォントの一覧と解決(FR-38。同梱のフォントは 0 個: Q-9)。名前は SkiaSharp のファミリ名(OS の表示の言語の名前)。
// 一覧は名前順(大文字と小文字を区別しない序数の比較、同じなら序数)。無いフォントは既定の Yu Gothic UI で描く(E-26)。
// 解決の結果はプロセスの間だけ覚える(どのスレッドから呼んでもよい)。
using System.Collections.Concurrent;
using GlintLoop.Core.Effects;
using SkiaSharp;

namespace GlintLoop.Codec.Text;

public static class InstalledFonts
{
    private static readonly Lazy<IReadOnlyList<string>> FamiliesLazy = new(LoadFamilies, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, SKTypeface?> Matched = new(StringComparer.Ordinal);

    /// <summary>一覧の並びの比較(大文字と小文字を区別しない序数、同じなら序数)。</summary>
    public static int Compare(string? a, string? b)
    {
        var c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    /// <summary>インストールされているフォントのファミリ名(重複なし・名前順)。</summary>
    public static IReadOnlyList<string> Families => FamiliesLazy.Value;

    private static IReadOnlyList<string> LoadFamilies()
    {
        var names = SKFontManager.Default.FontFamilies
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        names.Sort(Compare);
        return names;
    }

    /// <summary>この名前のフォントがこの PC にあるか(SkiaSharp でファミリとして引けるか)。</summary>
    public static bool IsInstalled(string? family) => !string.IsNullOrEmpty(family) && Match(family) is not null;

    /// <summary>描くときの書体。名前のフォントが無ければ既定の Yu Gothic UI、それも無ければ SkiaSharp の既定の書体。</summary>
    public static SKTypeface Resolve(string? family, out bool missing)
    {
        var t = string.IsNullOrEmpty(family) ? null : Match(family);
        missing = t is null;
        return t ?? Match(TextSpec.DefaultFont) ?? SKTypeface.Default;
    }

    // 見つからない名前は null(SKTypeface.FromFamilyName は黙って別の書体に落ちるので、MatchFamily で引く)
    private static SKTypeface? Match(string family) => Matched.GetOrAdd(family, static f => SKFontManager.Default.MatchFamily(f));
}
