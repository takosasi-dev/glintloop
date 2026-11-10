// SkiaSharp で書く JPEG と WebP の検査(AC-37 の (3) と (5)、FR-69・FR-71)。パッケージの実物の出力を、テストの側でマーカーとチャンクを読んで確かめる。
// JPEG: SOF0 のベースライン・標本化(品質 90 は 4:2:0、91 以上は 4:4:4)・SOF2 と APP1・APP2・COM が無い・透明の over 合成。
// WebP: RIFF のチャンクに EXIF・ICCP・XMP・ANIM が無い・ロスレスの往復で α=255 の画素が一致。デコードは製品の ImageDecoder を使う。
using System.Buffers.Binary;
using System.Text;
using GlintLoop.Codec;
using GlintLoop.Codec.Export;
using GlintLoop.Core.Export;
using GlintLoop.Core.Gif;
using GlintLoop.Tests.Framework;
using static GlintLoop.Tests.Exports.ExportTestKit;

namespace GlintLoop.Tests.Exports;

public sealed class JpegWebpAc37Tests
{
    /// <summary>JPEG のマーカーの列(SOS まで)。SOF のときは成分ごとの (番号, 標本化) も返す。</summary>
    internal static (List<byte> Markers, List<(int Id, int Sampling)> Components) JpegMarkers(byte[] jpg)
    {
        Check.True(jpg[0] == 0xFF && jpg[1] == 0xD8, "JPEG の SOI");
        var markers = new List<byte>();
        var comps = new List<(int, int)>();
        var p = 2;
        while (p + 4 <= jpg.Length)
        {
            Check.Equal(0xFF, (int)jpg[p], $"位置 {p} がマーカーでない");
            var m = jpg[p + 1];
            if (m == 0xFF)
            {
                p++;
                continue;
            }
            markers.Add(m);
            var len = BinaryPrimitives.ReadUInt16BigEndian(jpg.AsSpan(p + 2));
            if (m is 0xC0 or 0xC1 or 0xC2)
            {
                var nf = jpg[p + 9];
                for (var i = 0; i < nf; i++) comps.Add((jpg[p + 10 + i * 3], jpg[p + 11 + i * 3]));
            }
            if (m == 0xDA) break;
            p += 2 + len;
        }
        // SOS の後(エントロピー符号のデータの中)に、別の SOF・APP・COM が無いことを、マーカーの並びで調べる(RST と EOI だけを許す)
        for (var i = p + 2; i + 1 < jpg.Length; i++)
        {
            if (jpg[i] != 0xFF) continue;
            var m = jpg[i + 1];
            if (m is 0x00 or 0xFF || m is >= 0xD0 and <= 0xD7) continue;
            markers.Add(m);
        }
        return (markers, comps);
    }

    private static byte[] Jpeg(int w, int h, List<byte[]> frames, int quality, string? bg = null)
    {
        var s = Settings("jpeg", w, h) with { Quality = quality };
        if (bg is not null) s = s with { JpegBackground = bg };
        return Encode(new JpegExportEncoder(), new ArrayFrames(w, h, frames), s);
    }

    [Test]
    public void AC37_3_JPEGのマーカーと標本化()
    {
        const int w = 64, h = 48;
        var frames = Smooth(w, h, 1);
        foreach (var q in new[] { 1, 50, 90, 91, 100 })
        {
            var (markers, comps) = JpegMarkers(Jpeg(w, h, frames, q));
            Check.True(markers.Contains(0xC0), $"品質 {q}: SOF0(ベースライン)がある");
            Check.True(!markers.Contains(0xC2) && !markers.Contains(0xC1), $"品質 {q}: SOF2(プログレッシブ)・SOF1 が無い");
            Check.True(!markers.Contains(0xE1), $"品質 {q}: APP1 が無い");
            Check.True(!markers.Contains(0xE2), $"品質 {q}: APP2 が無い");
            Check.True(!markers.Contains(0xFE), $"品質 {q}: COM が無い");
            Check.Equal(3, comps.Count, $"品質 {q}: 成分の数");
            var expect = q >= 91 ? new[] { 0x11, 0x11, 0x11 } : new[] { 0x22, 0x11, 0x11 };
            Check.SequenceEqual(expect, comps.Select(c => c.Sampling).ToList(), $"品質 {q}: Y・Cb・Cr の標本化");
            Check.Equal(markers.Last(), (byte)0xD9, $"品質 {q}: 最後は EOI");
        }
    }

    [Test]
    public void AC37_3_JPEGの透明は背景色の上に線形光でover合成する()
    {
        // 合成の式そのもの(8 ビットの値で厳密に)
        var px = new byte[] { 0, 0, 0, 0, 0, 0, 0, 128, 10, 200, 30, 255, 255, 0, 0, 128 };
        var o = JpegExportEncoder.OverBackground(px, new GifColor(255, 255, 255));
        Check.SequenceEqual(new byte[] { 255, 255, 255, 255 }, o[0..4], "α=0 の画素は #FFFFFF");
        Check.True(Math.Abs(o[4] - 188) <= 2 && o[4] == o[5] && o[5] == o[6], $"α=50% の黒は 188±2(実際 {o[4]})");
        Check.SequenceEqual(new byte[] { 10, 200, 30, 255 }, o[8..12], "不透明の画素はそのまま");
        Check.Equal(255, (int)o[15], "α は 255");

        // 実際の JPEG をデコードして、平らな領域で確かめる(既定の背景色 #FFFFFF と、背景色の指定)
        const int w = 64, h = 32;
        var frames = Frames(w, h, 1, (x, _, _) => x < 32 ? (0, 0, 0, 0) : (0, 0, 0, 128));
        var def = OutputPlan.Resolve(ExportPlanAc37Tests.Doc((64, 32)).Doc, Session.SessionTestKit.Catalog, _ => new(64, 32), 0).R0;
        Check.Equal("#FFFFFF", def.JpegBackground, "背景色の既定");
        var dec = ImageDecoder.Decode(Jpeg(w, h, frames, 90));
        foreach (var (x, y, v) in new[] { (8, 16, 255), (48, 16, 188) })
        {
            var i = (y * w + x) * 4;
            for (var c = 0; c < 3; c++) Check.True(Math.Abs(dec.Rgba[i + c] - v) <= 2, $"({x},{y}) の成分 {c}: 期待 {v}±2、実際 {dec.Rgba[i + c]}");
        }
        var red = ImageDecoder.Decode(Jpeg(w, h, frames, 90, "#FF0000"));
        var r0 = (8 * w + 8) * 4;
        Check.True(red.Rgba[r0] >= 250 && red.Rgba[r0 + 1] <= 5 && red.Rgba[r0 + 2] <= 5, "背景色 #FF0000 の上の α=0");
    }

    /// <summary>RIFF の中のチャンクの FourCC の列。</summary>
    internal static List<string> WebpChunks(byte[] webp)
    {
        Check.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4), "RIFF");
        Check.Equal("WEBP", Encoding.ASCII.GetString(webp, 8, 4), "WEBP");
        Check.Equal((uint)webp.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4)), "RIFF の大きさ");
        var list = new List<string>();
        var p = 12;
        while (p + 8 <= webp.Length)
        {
            list.Add(Encoding.ASCII.GetString(webp, p, 4));
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(p + 4));
            p += 8 + len + (len & 1);
        }
        Check.Equal(webp.Length, p, "チャンクの終わりがファイルの終わり");
        return list;
    }

    [Test]
    public void AC37_5_WebPのチャンクとロスレスの往復()
    {
        const int w = 50, h = 40;
        var frames = Noisy(w, h, 1);
        foreach (var (lossless, q) in new[] { (true, 85), (false, 85), (false, 10), (true, 100) })
        {
            var s = Settings("webp", w, h) with { Lossless = lossless, Quality = q };
            var bytes = Encode(new WebpExportEncoder(), new ArrayFrames(w, h, frames), s);
            var chunks = WebpChunks(bytes);
            foreach (var bad in new[] { "EXIF", "ICCP", "XMP ", "ANIM", "ANMF" })
                Check.True(!chunks.Contains(bad), $"ロスレス {lossless}・品質 {q}: {bad} のチャンクがある({string.Join(",", chunks)})");
            Check.True(chunks.Contains(lossless ? "VP8L" : "VP8 "), $"ロスレス {lossless}: 画像のチャンク({string.Join(",", chunks)})");
            var dec = ImageDecoder.Decode(bytes);
            Check.Equal((w, h, false), (dec.Width, dec.Height, dec.Animated), "デコードの大きさと静止画");
            if (!lossless) continue;
            for (var i = 0; i < w * h; i++)
            {
                var o = i * 4;
                if (frames[0][o + 3] != 255) continue;
                Check.Equal((frames[0][o], frames[0][o + 1], frames[0][o + 2], (byte)255), (dec.Rgba[o], dec.Rgba[o + 1], dec.Rgba[o + 2], dec.Rgba[o + 3]),
                    $"ロスレスの画素 {i} が入力と一致");
            }
        }
    }
}
