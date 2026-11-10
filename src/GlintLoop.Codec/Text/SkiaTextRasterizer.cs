// テキストのエフェクトのビットマップを SkiaSharp の CPU の描画で作る(§9.4 の text の定義、D-35)。キャンバスと同じ大きさに描き、sRGB の straight の RGBA 8 ビットで返す。
// 字形は輪郭(パス)にして描く: 先に線(角と端は丸め、片側 px(stroke))、その上に塗り。アンチエイリアスは有効、LCD のサブピクセル描画とヒンティングは無効。
// 書体に無い文字は、OS の代替フォント(SKFontManager.MatchCharacter)の書体の区間に分けて描く。同じ入力からは同じ画素になる(乱数と時刻を使わない)。
using System.Collections.Concurrent;
using System.Globalization;
using GlintLoop.Core.Effects;
using SkiaSharp;

namespace GlintLoop.Codec.Text;

public sealed class SkiaTextRasterizer : ITextRasterizer
{
    // (書体のファミリ名と字形の太さ・幅・傾き, コードポイント) → 代替の書体(無ければ null)
    private static readonly ConcurrentDictionary<(string, int, int, int, int), SKTypeface?> Fallbacks = new();

    /// <summary>1 行の中の、同じ書体で描く区間。</summary>
    private sealed record Run(SKTypeface Typeface, string Text, float Advance);

    private sealed record LineLayout(IReadOnlyList<Run> Runs, float Width);

    private sealed record Layout(TextBlockLayout Block, double Px, double LineHeight, double Baseline, IReadOnlyList<LineLayout> Lines);

    public TextBlockLayout Measure(TextSpec spec, int width, int height) => Lay(spec, width, height).Block;

    public byte[]? Rasterize(TextSpec spec, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Text.Length == 0) return null;
        var layout = Lay(spec, width, height);
        var block = layout.Block;
        using var builder = new SKPathBuilder();
        for (var i = 0; i < layout.Lines.Count; i++)
        {
            var line = layout.Lines[i];
            var x = block.Left + spec.Align switch
            {
                TextAlign.Left => 0,
                TextAlign.Right => block.Width - line.Width,
                _ => (block.Width - line.Width) / 2,
            };
            var baseline = block.Top + i * layout.LineHeight + layout.Baseline;
            foreach (var run in line.Runs)
            {
                using var font = Font(run.Typeface, layout.Px);
                using var p = font.GetTextPath(run.Text, new SKPoint((float)x, (float)baseline));
                builder.AddPath(p, SKPathAddMode.Append);
                x += run.Advance;
            }
        }

        using var path = builder.Detach();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate((float)block.AnchorX, (float)block.AnchorY);
            canvas.RotateDegrees((float)block.RotationDeg);
            var strokeHalf = spec.Stroke * Math.Min(width, height) / 1000.0; // 片側の太さ px(stroke)
            if (strokeHalf > 0)
            {
                using var stroke = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)(2 * strokeHalf),
                    StrokeJoin = SKStrokeJoin.Round,
                    StrokeCap = SKStrokeCap.Round,
                    Color = ParseColor(spec.StrokeColor, SKColors.Black),
                };
                canvas.DrawPath(path, stroke);
            }
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ParseColor(spec.Color, SKColors.White) };
            canvas.DrawPath(path, fill);
        }
        return Unpremultiply(bitmap.GetPixelSpan(), bitmap.RowBytes, width, height);
    }

    /// <summary>行を区間に分け、文字の塊(§9.4)と各行の幅・基線を決める。</summary>
    private static Layout Lay(TextSpec spec, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var s0 = Math.Min(width, height);
        var px = spec.Size * s0 / 1000.0; // em の高さ px(size)
        var lineHeight = px * spec.Line / 100.0;
        var primary = InstalledFonts.Resolve(spec.Font, out _);
        var lines = new List<LineLayout>();
        foreach (var text in spec.Lines)
        {
            var runs = new List<Run>();
            var w = 0f;
            foreach (var (tf, part) in SplitRuns(primary, text))
            {
                using var font = Font(tf, px);
                var adv = font.MeasureText(part);
                runs.Add(new Run(tf, part, adv));
                w += adv;
            }
            lines.Add(new LineLayout(runs, w));
        }
        var blockW = lines.Count == 0 ? 0 : lines.Max(l => (double)l.Width);
        var blockH = lines.Count * lineHeight;
        var left = spec.Align switch
        {
            TextAlign.Left => 0,
            TextAlign.Right => -blockW,
            _ => -blockW / 2,
        };
        // 行の箱(高さ px(size)×line÷100)の上下の中央に em の箱(高さ px(size))を置き、em の箱の中の基線は書体の ascent と descent の比で決める
        using var pf = Font(primary, px);
        var m = pf.Metrics;
        var asc = -m.Ascent;
        var desc = m.Descent;
        var ratio = asc + desc > 0 ? asc / (asc + desc) : 0.8;
        var baseline = (lineHeight - px) / 2 + px * ratio;
        var block = new TextBlockLayout(spec.PosX * width, spec.PosY * height, left, -blockH / 2, blockW, blockH, spec.Rotation, lines.Count);
        return new Layout(block, px, lineHeight, baseline, lines);
    }

    /// <summary>1 行を、同じ書体の区間に分ける。書体に字形の無い文字は OS の代替フォントの書体にする(代替も無ければ元の書体)。</summary>
    private static IEnumerable<(SKTypeface, string)> SplitRuns(SKTypeface primary, string line)
    {
        if (line.Length == 0) yield break;
        using var probe = new SKFont(primary);
        SKTypeface? current = null;
        var start = 0;
        for (var i = 0; i < line.Length;)
        {
            var cp = char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])
                ? char.ConvertToUtf32(line[i], line[i + 1])
                : line[i];
            var len = cp > 0xFFFF ? 2 : 1;
            var tf = probe.ContainsGlyph(cp) ? primary : Fallback(primary, cp) ?? primary;
            if (current is null)
            {
                current = tf;
            }
            else if (!ReferenceEquals(tf, current))
            {
                yield return (current, line[start..i]);
                current = tf;
                start = i;
            }
            i += len;
        }
        yield return (current!, line[start..]);
    }

    private static SKTypeface? Fallback(SKTypeface primary, int cp) =>
        Fallbacks.GetOrAdd((primary.FamilyName, primary.FontWeight, primary.FontWidth, (int)primary.FontSlant, cp), static k =>
        {
            var t = SKFontManager.Default.MatchCharacter(k.Item1, k.Item2, k.Item3, (SKFontStyleSlant)k.Item4, null, k.Item5);
            using var f = t is null ? null : new SKFont(t);
            return f is not null && f.ContainsGlyph(k.Item5) ? t : null;
        });

    /// <summary>描く設定の SKFont(アンチエイリアス、LCD でない、ヒンティングなし、位置と幅は丸めない)。</summary>
    private static SKFont Font(SKTypeface tf, double px) => new(tf, (float)px)
    {
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None,
        Subpixel = true,
        LinearMetrics = true,
    };

    /// <summary>#RRGGBBAA(sRGB の straight)。読めなければ fallback。</summary>
    private static SKColor ParseColor(string s, SKColor fallback)
    {
        if (s.Length != 9 || s[0] != '#' ||
            !uint.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return fallback;
        return new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>Skia のプリマルチプライの RGBA 8 ビットを、straight(c×255÷a を整数で丸める)の詰めた行にする。</summary>
    private static byte[] Unpremultiply(ReadOnlySpan<byte> src, int rowBytes, int width, int height)
    {
        var dst = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = src.Slice(y * rowBytes, width * 4);
            var o = y * width * 4;
            for (var x = 0; x < width * 4; x += 4)
            {
                int a = row[x + 3];
                if (a == 0) continue;
                dst[o + x + 3] = (byte)a;
                for (var c = 0; c < 3; c++)
                    dst[o + x + c] = (byte)Math.Min(255, (row[x + c] * 255 + a / 2) / a);
            }
        }
        return dst;
    }
}
