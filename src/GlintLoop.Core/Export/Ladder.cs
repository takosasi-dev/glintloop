// 容量上限のはしご(§9.9 の 3)と、調整の内容の文字(§9.9 の 4 の最後)。r_0 から段 r_1〜r_R を、前の段の変更の上に積み重ねて作る。
// fps×0.75 は選択肢のうち floor(0.75×F) 以下で最大のもの(無ければ飛ばす)、長辺×0.85 は max(16, round(Lout×0.85))、色数・品質は今の値以下なら飛ばす。
// 長辺が 16 に達したら、それより後の段は足さない。
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

/// <summary>はしごの段の種類。</summary>
public enum LadderStepKind
{
    /// <summary>APNG の 32 ビット → パレット(256 色、alpha は二値)。</summary>
    Palette,
    Colors,
    Fps,
    LongSide,
    Quality,
}

/// <summary>はしごの 1 段。Value は段のあとの値(色数・fps・長辺・品質。パレットは色数 256)、Settings はその段までを積み重ねた設定 r_i。</summary>
public sealed record LadderStep(LadderStepKind Kind, int Value, ExportSettings Settings);

public static class Ladder
{
    private static readonly int[] GifColors = [128, 64, 48, 32, 24, 16];

    /// <summary>
    /// r_0 から段 r_1〜r_R を作る(r_0 は含めない)。composite は合成のネイティブ寸法(長辺の段の幅と高さを決める)。
    /// </summary>
    public static IReadOnlyList<LadderStep> Build(ExportSettings r0, SizeD composite)
    {
        ArgumentNullException.ThrowIfNull(r0);
        var steps = new List<LadderStep>();
        var cur = r0;

        // 段を 1 つ試す(飛ばす段は足さない)。
        void Add(LadderStepKind kind, int value)
        {
            ExportSettings? next = kind switch
            {
                LadderStepKind.Palette => cur.ApngPalette ? null : cur with { ApngPalette = true, Colors = 256 },
                LadderStepKind.Colors => cur.Colors <= value ? null : cur with { Colors = value },
                LadderStepKind.Quality => cur.Quality <= value ? null : cur with { Quality = value },
                LadderStepKind.Fps => LowerFps(cur.Fps) is int f ? OutputPlan.WithFps(cur, f) : null,
                LadderStepKind.LongSide => OutputPlan.WithLongSide(cur, composite, Math.Max(OutputSizing.MinLongSide, MathX.RoundHalfUpToInt(cur.LongSide * 0.85))),
                _ => null,
            };
            if (next is null) return;
            var v = kind switch
            {
                LadderStepKind.Palette or LadderStepKind.Colors => next.Colors,
                LadderStepKind.Fps => next.Fps,
                LadderStepKind.LongSide => next.LongSide,
                _ => next.Quality,
            };
            steps.Add(new LadderStep(kind, v, next));
            cur = next;
        }

        // 長辺が 16 に達したら(r_0 が 16 のときを含む)、それより後の段は足さない。
        bool Stopped() => cur.LongSide <= OutputSizing.MinLongSide;

        void Run(IEnumerable<(LadderStepKind Kind, int Value)> list)
        {
            foreach (var (k, v) in list)
            {
                if (Stopped()) return;
                Add(k, v);
            }
        }

        void LongSides(int count)
        {
            for (var i = 0; i < count && !Stopped(); i++) Add(LadderStepKind.LongSide, 0);
        }

        switch (r0.Format)
        {
            case ExportFormat.Gif:
                Run(GifList(paletteFirst: false));
                break;
            case ExportFormat.Apng:
                Run(GifList(paletteFirst: true));
                break;
            case ExportFormat.Jpeg:
                Run(new[] { 85, 80, 75, 70, 60, 50, 40 }.Select(q => (LadderStepKind.Quality, q)));
                LongSides(12);
                break;
            case ExportFormat.Webp when !r0.Lossless:
                Run(new[] { 75, 65, 55, 45, 35 }.Select(q => (LadderStepKind.Quality, q)));
                LongSides(12);
                break;
            default:
                // WebP(ロスレス)・PNG
                LongSides(16);
                break;
        }
        return steps;
    }

    // GIF: 色数 128、色数 64、fps×0.75、長辺×0.85、色数 48、fps×0.75、長辺×0.85、色数 32、長辺×0.85、fps×0.75、長辺×0.85、色数 24、長辺×0.85、色数 16
    // APNG: パレット、色数 128、色数 64、fps×0.75、長辺×0.85、色数 48、fps×0.75、長辺×0.85、色数 32、長辺×0.85、fps×0.75、長辺×0.85、色数 16
    private static IEnumerable<(LadderStepKind, int)> GifList(bool paletteFirst)
    {
        const LadderStepKind C = LadderStepKind.Colors, F = LadderStepKind.Fps, L = LadderStepKind.LongSide;
        if (paletteFirst) yield return (LadderStepKind.Palette, 256);
        yield return (C, 128);
        yield return (C, 64);
        yield return (F, 0);
        yield return (L, 0);
        yield return (C, 48);
        yield return (F, 0);
        yield return (L, 0);
        yield return (C, 32);
        yield return (L, 0);
        yield return (F, 0);
        yield return (L, 0);
        if (!paletteFirst)
        {
            yield return (C, 24);
            yield return (L, 0);
        }
        yield return (C, 16);
    }

    /// <summary>fps×0.75 の下げ先: 選択肢のうち floor(0.75×F) 以下で最大のもの。無ければ null(F=5 は飛ばす)。</summary>
    public static int? LowerFps(int fps)
    {
        var cap = fps * 3 / 4; // floor(0.75×F)(F は正の整数)
        int? best = null;
        foreach (var f in TimeModel.FpsChoices)
        {
            if (f <= cap) best = f;
        }
        return best;
    }

    /// <summary>
    /// 調整の内容の文字(§9.9 の 4): ユーザーの設定 user と結果 chosen で変わった項目だけを、パレット化・品質・色数・fps・長辺の順に「 / 」でつなぐ。
    /// 変わった項目が無ければ null(表示しない)。
    /// </summary>
    public static string? Describe(ExportSettings user, ExportSettings chosen)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(chosen);
        var parts = new List<string>();
        var paletteUsed = user.Format == ExportFormat.Gif || chosen.ApngPalette;
        if (user.Format == ExportFormat.Apng && !user.ApngPalette && chosen.ApngPalette) parts.Add("パレット化");
        if (user.Format is ExportFormat.Jpeg or ExportFormat.Webp && user.Quality != chosen.Quality)
            parts.Add($"品質 {user.Quality}→{chosen.Quality}");
        if (user.Format is ExportFormat.Gif or ExportFormat.Apng && paletteUsed && user.Colors != chosen.Colors)
            parts.Add($"色数 {user.Colors}→{chosen.Colors}");
        if (user.Fps != chosen.Fps) parts.Add($"{user.Fps}→{chosen.Fps} fps");
        if (user.LongSide != chosen.LongSide) parts.Add($"長辺 {user.LongSide}→{chosen.LongSide} px");
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }
}
