// 自作の PNG・APNG の書き出しの検査(AC-37 の (2) のチャンクと (6) の APNG、AC-38 の (3) の打ち切り、INV-8 の並列度)。GPU を使わない。
// ファイルはテストに書いた最小のパーサー(MiniPngParser。エンコーダーとコードを共有しない)で読み、画素を zlib の展開とフィルタの復元で取り出して比べる。
// フレームはコードで作る(ExportTestKit)。モーションのあるフレームの位置の検査は ExportRenderAc37Tests にある。
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Exports.ExportTestKit;

namespace GlintLoop.Tests.Exports;

public sealed class PngApngAc37Tests
{
    private static readonly string[] MetaChunks = ["eXIf", "iCCP", "tEXt", "iTXt", "zTXt", "tIME"];

    [Test]
    public void AC37_2_PNGのチャンクと画素()
    {
        const int w = 70, h = 33;
        var frames = Noisy(w, h, 1);
        var png = Encode(new PngExportEncoder(), new ArrayFrames(w, h, frames), Settings("png", w, h));
        var p = MiniPngParser.Parse(png);
        Check.Equal((w, h), (p.Width, p.Height), "IHDR の大きさ");
        Check.Equal(6, p.ColorType, "IHDR の色の種類");
        Check.Equal(8, p.BitDepth, "IHDR のビット深度");
        Check.Equal(0, p.Interlace, "IHDR のインターレース");
        foreach (var t in MetaChunks) Check.True(!p.Types.Contains(t), $"{t} のチャンクがある");
        Check.SequenceEqual(["IHDR", "IDAT", "IEND"], p.Types.Distinct().ToList(), "チャンクの種類");
        Check.Equal(1, p.Frames.Count, "画像の枚数");
        Check.SequenceEqual(frames[0], p.Frames[0], "PNG の画素が元と一致");
    }

    private static void CheckApng(MiniPng p, int w, int h, int n, int fps, long hold, int plays, string what)
    {
        // チャンクの順: IHDR、acTL、(PLTE、tRNS)、fcTL(0)、IDAT、{fcTL、fdAT…}、IEND
        var types = p.Types.ToList();
        Check.Equal("IHDR", types[0], what + ": 最初は IHDR");
        Check.Equal("acTL", types[1], what + ": 2 番目は acTL");
        Check.Equal("IEND", types[^1], what + ": 最後は IEND");
        var i = 2;
        if (p.ColorType == 3)
        {
            Check.Equal("PLTE", types[2], what + ": PLTE");
            Check.Equal("tRNS", types[3], what + ": tRNS");
            i = 4;
        }
        Check.Equal("fcTL", types[i], what + ": 最初のフレームの fcTL");
        Check.Equal("IDAT", types[i + 1], what + ": 最初のフレームは IDAT");
        var rest = types.Skip(i + 2).Take(types.Count - i - 3).ToList();
        Check.True(rest.All(t => t is "fcTL" or "fdAT"), what + ": 2 枚目からは fcTL と fdAT だけ");
        Check.True(!rest.Contains("IDAT") && types.Count(t => t == "IDAT") == 1, what + ": IDAT は最初のフレームだけ");
        foreach (var t in MetaChunks) Check.True(!types.Contains(t), $"{what}: {t} のチャンクがある");

        Check.Equal<int?>(n, p.ActlFrames, what + ": acTL のフレーム数");
        Check.Equal<int?>(plays, p.ActlPlays, what + ": acTL の繰り返し回数");
        Check.Equal(n, p.Controls.Count, what + ": fcTL の数");
        Check.Equal(n, p.Frames.Count, what + ": フレームの数");

        // sequence_number は fcTL と fdAT を通して 0 から連番
        var seqs = p.Chunks.Where(c => c.Type is "fcTL" or "fdAT")
            .Select(c => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.Data)).ToList();
        for (var s = 0; s < seqs.Count; s++) Check.Equal((uint)s, seqs[s], $"{what}: sequence_number の {s} 番目");
        Check.True(p.Chunks.Where(c => c.Type == "fdAT").All(c => c.Data.Length <= 8192), what + ": fdAT が 8192 バイト以下");

        var delays = Delays(n, fps, hold);
        for (var k = 0; k < n; k++)
        {
            var c = p.Controls[k];
            Check.Equal((0, 0, w, h), (c.X, c.Y, c.Width, c.Height), $"{what}: フレーム {k} は全面");
            Check.Equal((0, 0), (c.Dispose, c.Blend), $"{what}: フレーム {k} の dispose_op と blend_op");
            Check.Equal((delays[k], 100), (c.DelayNum, c.DelayDen), $"{what}: フレーム {k} の遅延");
        }
    }

    [Test]
    public void AC37_6_APNGの32ビット版の構造と画素()
    {
        const int w = 96, h = 80, n = 7, fps = 15;
        var frames = Noisy(w, h, n);
        foreach (var (loop, loops, plays) in new[] { (LoopMode.Infinite, 1, 0), (LoopMode.Once, 1, 1), (LoopMode.Once, 4, 4) })
        {
            var s = Settings("apng", w, h, n, fps) with { Loop = loop, ApngLoops = loops, HoldMs = 1200 };
            var bytes = Encode(new ApngEncoder(), new ArrayFrames(w, h, frames), s);
            var p = MiniPngParser.Parse(bytes);
            Check.Equal(6, p.ColorType, "32 ビット版の色の種類");
            CheckApng(p, w, h, n, fps, 1200, plays, $"32 ビット・{loop}・{loops}");
            Check.True(p.Chunks.Count(c => c.Type == "fdAT") > n - 1, "fdAT が 8192 バイトごとに分かれている");
            for (var k = 0; k < n; k++) Check.SequenceEqual(frames[k], p.Frames[k], $"フレーム {k} の画素が元と一致");
        }

        // 1 フレームでも APNG の形で書く(遅延 100)
        var one = MiniPngParser.Parse(Encode(new ApngEncoder(), new ArrayFrames(w, h, [frames[0]]), Settings("apng", w, h)));
        CheckApng(one, w, h, 1, 20, 0, 0, "1 フレーム");
        Check.SequenceEqual(frames[0], one.Frames[0], "1 フレームの画素");
    }

    [Test]
    public void AC37_6_APNGのパレット版の構造と画素()
    {
        // 色の種類が少ない画像(色の差が大きく、Wu 法のセルが色ごとに分かれる)と、透明の画素
        const int w = 64, h = 48, n = 5;
        (int, int, int)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255), (0, 0, 0), (255, 255, 0), (0, 255, 255), (128, 64, 200)];
        var frames = Frames(w, h, n, (x, y, k) =>
        {
            if ((x + 2 * y + k) % 11 == 0) return (10, 20, 30, 0);
            var (r, g, b) = colors[(x / 8 + y / 8 + k) % colors.Length];
            return (r, g, b, 255);
        });
        var s = Settings("apng", w, h, n) with { ApngPalette = true, Dither = GifDither.None, Colors = 256 };
        var p = MiniPngParser.Parse(Encode(new ApngEncoder(), new ArrayFrames(w, h, frames), s));
        Check.Equal(3, p.ColorType, "パレット版の色の種類");
        CheckApng(p, w, h, n, 20, 0, 0, "パレット");
        Check.True(p.Plte is not null && p.Trns is not null, "PLTE と tRNS がある");
        Check.True(p.Plte!.Length / 3 <= 256, "パレットは 256 色以下");
        var zeros = p.Trns!.Select((a, i) => (a, i)).Where(t => t.a != 255).ToList();
        Check.Equal(1, zeros.Count, "tRNS で 255 でない番号は 1 つ(透明の番号)");
        Check.Equal(0, (int)zeros[0].a, "透明の番号の α は 0(二値)");
        for (var k = 0; k < n; k++)
        {
            var src = frames[k];
            var got = p.Frames[k];
            for (var i = 0; i < w * h; i++)
            {
                var o = i * 4;
                if (src[o + 3] < 128)
                {
                    Check.Equal(0, (int)got[o + 3], $"フレーム {k} の画素 {i} は透明");
                    continue;
                }
                Check.Equal((src[o], src[o + 1], src[o + 2], 255), (got[o], got[o + 1], got[o + 2], got[o + 3]), $"フレーム {k} の画素 {i} がパレットの色で元と一致");
            }
        }

        // 色の多い画像でも、どの画素もパレットの色(PLTE と tRNS の色)で、α は 0 か 255
        var noisy = Noisy(48, 40, 3);
        var q = MiniPngParser.Parse(Encode(new ApngEncoder(), new ArrayFrames(48, 40, noisy), Settings("apng", 48, 40, 3) with { ApngPalette = true, Colors = 64 }));
        Check.True(q.Plte!.Length / 3 <= 64, "色数 64 のパレットは 64 色以下(透明の番号を含む)");
        Check.True(q.Frames.All(f => f.Where((_, i) => i % 4 == 3).All(a => a is 0 or 255)), "パレット版の α は二値");
    }

    [Test]
    public void AC37_6_APNGは並列度に依らず同じバイト列()
    {
        const int w = 64, h = 64, n = 9;
        var frames = Smooth(w, h, n);
        foreach (var palette in new[] { false, true })
        {
            var s = Settings("apng", w, h, n) with { ApngPalette = palette };
            var a = Encode(new ApngEncoder(), new ArrayFrames(w, h, frames), s, run: Run(1));
            var b = Encode(new ApngEncoder(), new ArrayFrames(w, h, frames), s, run: Run(8));
            Check.SequenceEqual(a, b, $"パレット {palette}: 並列度 1 と 8 のバイト列(INV-8)");
            var c = Encode(new PngExportEncoder(), new ArrayFrames(w, h, frames), Settings("png", w, h));
            var d = Encode(new PngExportEncoder(), new ArrayFrames(w, h, frames), Settings("png", w, h));
            Check.SequenceEqual(c, d, "PNG の 2 回のバイト列");
        }
    }

    [Test]
    public void AC38_3_本物のエンコーダーも上限で打ち切る()
    {
        const int w = 80, h = 60, n = 6;
        var frames = Noisy(w, h, n);
        (IExportEncoder Enc, ExportSettings S)[] cases =
        [
            (new PngExportEncoder(), Settings("png", w, h)),
            (new ApngEncoder(), Settings("apng", w, h, n)),
            (new ApngEncoder(), Settings("apng", w, h, n) with { ApngPalette = true }),
            (new GifExportEncoder(), Settings("gif", w, h, n)),
        ];
        foreach (var (enc, s) in cases)
        {
            var full = Encode(enc, new ArrayFrames(w, h, frames), s).Length;
            var limit = full / 2;
            using var ms = new System.IO.MemoryStream();
            var r = enc.Encode(new ArrayFrames(w, h, frames), s, ms, limit, Run());
            Check.Equal(true, r.Overflowed, $"{s.Format}(パレット {s.ApngPalette}): 上限 {limit} で打ち切られる");
            Check.True(r.Bytes <= limit && ms.Length <= limit, $"{s.Format}: 打ち切りまでに書いたバイト数 {ms.Length} が上限以下");
            using var ok = new System.IO.MemoryStream();
            var r2 = enc.Encode(new ArrayFrames(w, h, frames), s, ok, full, Run());
            Check.Equal((false, (long)full), (r2.Overflowed, r2.Bytes), $"{s.Format}: 上限がちょうどなら収まる");
        }
    }
}
