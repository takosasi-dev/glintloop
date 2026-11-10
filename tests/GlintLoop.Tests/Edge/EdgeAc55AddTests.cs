// AC-55 の (8)〜(12): 取り込めるエッジケース(E-10・E-11・E-14・E-15・E-16)。アニメーション・GPU の上限の縮小・複数のファイル・小さいソース・すべて透明。
// (9) は製品の取り込みの最大寸法が定数(16384)で、機能レベル 10_x のデバイスは拒否される(NFR-11)ので、ImageImporter の maxTextureSide で 8192 を与える(AC-55 の「偽のデバイス」の読み替え。E11 の検査は IngestAc11Tests にもある)。
// (11)(12) は WARP の本物の書き出しを通す(Slow)。
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Gif;
using GlintLoop.Tests.Session;
using SkiaSharp;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc55AddTests
{
    // ================================================================ (8) アニメーション(E-10)

    private static readonly byte[] Red = Img.Solid(8, 6, 220, 40, 40);
    private static readonly byte[] Blue = Img.Solid(8, 6, 40, 60, 220);

    [Test]
    public void AC55_8_E10_2フレームのGIFとWebPは先頭のフレームでimport_animated()
    {
        using var r = EdgeRig.Start();
        var gif = r.Dir.Write("two.gif", OtherBytes.Gif(8, 6, [Red, Blue]));
        var webp = r.Dir.Write("two.webp", OtherBytes.AnimatedWebp(8, 6, [Red, Blue]));
        foreach (var (name, path, tolerance) in new[] { ("GIF", gif, 2), ("WebP", webp, 0) })
        {
            r.ClearNotice();
            var o = r.Open(path)!;
            Check.Equal(1, o.Added, name + " の足せた枚数");
            var img = o.AddedLayers[0].Image;
            Check.True(img.Animated, name + " の印");
            Check.True(Img.MaxDiff(Red, img.Pixels) <= tolerance, $"{name} は先頭のフレームの画素(差 {Img.MaxDiff(Red, img.Pixels)})");
            r.ExpectNotice(NoticeKeys.ImportAnimated, NoticeLevel.Info, name);
            r.CloseDocument();
        }
    }

    [Test]
    public void AC55_8_E10_1フレームのGIFと静止画のWebPと2フレームのAPNGでは出ない()
    {
        using var r = EdgeRig.Start();
        var gif = r.Dir.Write("one.gif", OtherBytes.Gif(8, 6, [Red]));
        var webp = r.Dir.Write("still.webp", SkiaBytes.WebpLossless(8, 6, Red));
        var apng = r.Dir.Write("two.png", PngBytes.Apng(8, 6, Red, Blue));
        foreach (var (name, path) in new[] { ("1 フレームの GIF", gif), ("静止画の WebP", webp), ("2 フレームの APNG", apng) })
        {
            r.ClearNotice();
            var o = r.Open(path)!;
            Check.Equal(1, o.Added, name + " の足せた枚数");
            Check.True(!o.AddedLayers[0].Image.Animated, name + " に印が無い");
            Check.True(Img.MaxDiff(Red, o.AddedLayers[0].Image.Pixels) <= 2, name + " は先頭の画像の画素");
            Check.Equal(null, r.NoticeNow, name + ": 新しい文書の 1 枚では何も出ない");
            r.CloseDocument();
        }
    }

    [Test]
    public void AC55_8_FR4_文書があるとき2フレームのGIFはimport_animated_1フレームのGIFはlayer_added()
    {
        using var r = EdgeRig.Start();
        r.LoadTwoLayers();
        r.ClearNotice();
        Check.Equal(1, r.Open(r.Dir.Write("two.gif", OtherBytes.Gif(8, 6, [Red, Blue])))?.Added, "2 フレームの GIF");
        r.ExpectNotice(NoticeKeys.ImportAnimated, NoticeLevel.Info, "2 フレームの GIF(layer.added は出ない)");
        r.ClearNotice();
        Check.Equal(1, r.Open(r.Dir.Write("one.gif", OtherBytes.Gif(8, 6, [Red])))?.Added, "1 フレームの GIF");
        r.ExpectNotice(NoticeKeys.LayerAdded, NoticeLevel.Info, "1 フレームの GIF");
    }

    // ================================================================ (9) GPU の上限(E-11・INV-7)

    private static ImportOutcome Paste(byte[] png, int maxTextureSide, IImportTarget? target = null) =>
        new ImageImporter(target ?? new FakeTarget(), new MemoryLogSink(), maxTextureSide: maxTextureSide)
            .Paste(new MemoryImportData().With(ImportFormatNames.Png, png));

    // 黒と白が 1 画素ごとに並ぶ縦の縞
    private static byte[] Stripes(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var v = (byte)(x % 2 == 0 ? 0 : 255);
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (v, v, v, (byte)255);
            }
        }
        return px;
    }

    [Test]
    public void AC55_9_E11_INV7_最大寸法8192では12000x300が6000x150に縮みsRGB188で通知が出る()
    {
        var png = SkiaBytes.Png(12000, 300, Stripes(12000, 300));
        var o = Paste(png, 8192);
        var img = o.AddedLayers[0].Image;
        Check.Equal((6000, 150), (img.Width, img.Height), "縮めた大きさ");
        Check.True(img.GpuShrunk, "縮めた印");
        Check.Equal(NoticeKeys.ImportGpuShrunk, o.Notice?.Key, "通知");
        Check.Equal(NoticeLevel.Info, o.Notice!.Level, "通知の種別");
        for (var i = 0; i < img.Pixels.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                if (Math.Abs(img.Pixels[i + c] - 188) > 2) throw new CheckFailedException($"画素 {i / 4} の成分 {c} が {img.Pixels[i + c]}(188±2 でない)");
            }
            if (img.Pixels[i + 3] != 255) throw new CheckFailedException($"画素 {i / 4} の α が {img.Pixels[i + 3]}");
        }

        // 最大寸法が 16384 なら、そのままで通知なし
        var o2 = Paste(png, 16384);
        Check.Equal((12000, 300), (o2.AddedLayers[0].Image.Width, o2.AddedLayers[0].Image.Height), "16384 では縮めない");
        Check.True(!o2.AddedLayers[0].Image.GpuShrunk, "縮めた印が無い");
        Check.Equal(null, o2.Notice, "16384 では通知なし");
    }

    [Test]
    public void AC55_9_E11_16384x64は8192x32_辺が奇数の9001x300は端の列を複製して4501x150()
    {
        var o = Paste(SkiaBytes.Png(16384, 64, Img.Solid(16384, 64, 10, 20, 30)), 8192);
        Check.Equal((8192, 32), (o.AddedLayers[0].Image.Width, o.AddedLayers[0].Image.Height), "16384×64");

        const int w = 9001, h = 300;
        var px = Img.Solid(w, h, 0, 0, 0);
        for (var y = 0; y < h; y++) px[(y * w + 9000) * 4] = 255;
        var img = Paste(SkiaBytes.Png(w, h, px), 8192).AddedLayers[0].Image;
        Check.Equal((4501, 150), (img.Width, img.Height), "9001×300");
        for (var y = 0; y < 150; y++)
        {
            Check.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Img.At(img.Pixels, 4501, 4500, y), $"最後の列 ({y})");
            Check.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Img.At(img.Pixels, 4501, 4499, y), $"その左の列 ({y})");
        }
    }

    [Test]
    public void AC55_9_FR42_土台が400x300の文書に足した12000x300はscale6_6でgpu_shrunkが出る()
    {
        var session = SessionTestKit.NewSession();
        var target = new SessionImportTarget(session);
        var importer = new ImageImporter(target, new MemoryLogSink(), maxTextureSide: 8192);
        Check.Equal(1, importer.Paste(new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(400, 300, Img.Pattern(400, 300, alpha: false)))).Tap(o => o.CompleteDisplay()).Added, "土台");
        var o = importer.Paste(new MemoryImportData().With(ImportFormatNames.Png, SkiaBytes.Png(12000, 300, Stripes(12000, 300))));
        o.CompleteDisplay();
        Check.Equal(1, o.Added, "足せた枚数");
        Check.Equal((6000, 150), (o.AddedLayers[0].Image.Width, o.AddedLayers[0].Image.Height), "縮めた大きさ");
        var layer = session.Document!.Layers[1];
        Check.Near(6.6, layer.Place.Scale, 1e-9, "scale");
        Check.Equal(NoticeKeys.ImportGpuShrunk, o.Notice?.Key, "gpu_shrunk が出て layer.added は出ない");
    }

    // ================================================================ (10) 複数のファイル(E-14・FR-4)

    private sealed record Files(string A, string B, string X, string Y, string G, string Text);

    private static Files MakeFiles(EdgeRig r) => new(
        r.Dir.Write("a.png", EdgeBytes.Solid(250, 10, 10)),
        r.Dir.Write("b.png", EdgeBytes.Solid(10, 250, 10)),
        r.Dir.Write("x.png", EdgeBytes.CutPng(1)),
        r.Dir.Write("y.png", EdgeBytes.CutPng(2)),
        r.Dir.Write("g.glint", EdgeBytes.ZipHead),
        r.Dir.Write("note.png", "テキストのファイル"u8.ToArray()));

    [Test]
    public void AC55_10_E14_文書が無いとき切れたPNGとAとBのドロップはAが土台でBが選ばれ履歴は空()
    {
        using var r = EdgeRig.Start();
        var f = MakeFiles(r);
        var logBefore = r.ImportLog().Length;
        var o = r.DropFiles(f.X, f.A, f.B)!;
        Check.Equal(2, o.Added, "足せた枚数");
        Check.SequenceEqual(["a", "b"], r.LayerNames, "A が土台、B が最前面");
        Check.Equal(Target.OfLayer(1), r.App.Ui(() => r.Session.Selected), "B が選ばれる");
        r.ExpectMulti(3, 2, "[X, A, B]");
        r.ExpectLog(logBefore, ["import_fail:decode_error", "import_ok", "import_ok"], "[X, A, B]");
        var ok = r.LogLines().Where(l => l.GetProperty("event").GetString() == "import_ok").ToArray();
        Check.True(ok.All(l => l.GetProperty("layers").GetInt32() == 2), "import_ok の layers はどちらも 2");
        Check.Equal(0, r.UndoCount, "文書を新しく作る取り込みの履歴は空");
    }

    [Test]
    public void AC55_10_E14_2枚の文書に足すときの通知と履歴()
    {
        using var r = EdgeRig.Start();
        r.LoadTwoLayers();
        var f = MakeFiles(r);

        // [A, B]: 渡された順に足され、layer.added。Ctrl+Z の 1 回で 2 枚とも消える
        r.ClearNotice();
        var undo = r.UndoCount;
        var names = r.LayerNames;
        Check.Equal(2, r.DropFiles(f.A, f.B)?.Added, "[A, B]");
        Check.SequenceEqual([.. names, "a", "b"], r.LayerNames, "渡された順");
        r.ExpectNotice(NoticeKeys.LayerAdded, NoticeLevel.Info, "[A, B]");
        Check.Equal(undo + 1, r.UndoCount, "全体で Undo の 1 件");
        Check.True(r.Key(Key.Z, ModifierKeys.Control), "Ctrl+Z が処理されない");
        Check.SequenceEqual(names, r.LayerNames, "Ctrl+Z の 1 回で 2 枚とも消える");

        // [X, A, Y, B]: A と B が足され import.multi
        r.ClearNotice();
        Check.Equal(2, r.DropFiles(f.X, f.A, f.Y, f.B)?.Added, "[X, A, Y, B]");
        Check.SequenceEqual([.. names, "a", "b"], r.LayerNames, "[X, A, Y, B] の並び");
        r.ExpectMulti(4, 2, "[X, A, Y, B]");

        // [X, Y]: 何も足されず、2 つ目の失敗の通知
        r.Reject("[X, Y]", () => r.DropFiles(f.X, f.Y), NoticeKeys.ImportDecodeError, NoticeLevel.Error,
            ["import_fail:decode_error", "import_fail:decode_error"], decodes: 2);

        // [G, A]: A だけが足され、G は開かれない
        var logBefore = r.ImportLog().Length;
        r.ClearNotice();
        var before = r.LayerNames;
        var o = r.DropFiles(f.G, f.A)!;
        Check.Equal(ImportStatus.Completed, o.Status, "[G, A] は開く操作にならない");
        Check.SequenceEqual([.. before, "a"], r.LayerNames, "[G, A]");
        r.ExpectMulti(2, 1, "[G, A]");
        r.ExpectLog(logBefore, ["import_fail:unsupported", "import_ok"], "[G, A]");

        // [G, G]: 何も足されず import.unsupported
        r.Reject("[G, G]", () => r.DropFiles(f.G, f.G), NoticeKeys.ImportUnsupported, NoticeLevel.Error,
            ["import_fail:unsupported", "import_fail:unsupported"]);
    }

    [Test]
    public void AC55_10_E14_1個のドロップではimport_multiが出ない()
    {
        using var r = EdgeRig.Start();
        var f = MakeFiles(r);
        Check.Equal(1, r.DropFiles(f.A)?.Added, "文書が無いときの 1 個");
        Check.Equal(null, r.NoticeNow, "文書が無いときの 1 個は何も出ない");
        Check.Equal(1, r.DropFiles(f.B)?.Added, "文書があるときの 1 個");
        r.ExpectNotice(NoticeKeys.LayerAdded, NoticeLevel.Info, "文書があるときの 1 個");
    }

    [Test]
    public void AC55_10_E14_貼り付けのFileDropも同じに扱う()
    {
        using var r = EdgeRig.Start();
        r.LoadTwoLayers();
        var f = MakeFiles(r);
        var names = r.LayerNames;

        // [A, テキスト, B]: テキストは数えない
        r.ClearNotice();
        Check.Equal(2, r.PasteFiles(f.A, f.Text, f.B)?.Added, "[A, テキスト, B]");
        Check.SequenceEqual([.. names, "a", "b"], r.LayerNames, "[A, テキスト, B] の並び");
        r.ExpectNotice(NoticeKeys.LayerAdded, NoticeLevel.Info, "[A, テキスト, B]");

        // [A, X]: A だけ
        r.ClearNotice();
        Check.Equal(1, r.PasteFiles(f.A, f.X)?.Added, "[A, X]");
        r.ExpectMulti(2, 1, "[A, X]");

        // [X, Y]: 足せず、ほかの形式も無い
        r.Reject("[X, Y]", () => r.PasteFiles(f.X, f.Y), NoticeKeys.ImportDecodeError, NoticeLevel.Error,
            ["import_fail:decode_error", "import_fail:decode_error"], decodes: 2);
    }

    // ================================================================ (11) 小さいソース(E-15)

    private static SKBitmap Decode(string path) =>
        SKBitmap.Decode(path, new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul).WithSize(Size(path)));

    private static SKSizeI Size(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec.Info.Size;
    }

    [Test(Slow = true)]
    public void AC55_11_E15_1x1は16x16で全画素が同じ色_8x4は16x8で四隅が不透明()
    {
        using var r = EdgeRig.Start(gpu: true);
        var band = r.WaitBand();

        Check.Equal(1, r.PastePng(SkiaBytes.Png(1, 1, Img.Solid(1, 1, 255, 0, 0)))?.Added, "1×1");
        Check.Equal(null, r.NoticeNow, "1×1 でエラーが出ない");
        r.App.Ui(() => r.Session.SetOutputFormat(ExportFormat.Png));
        var o = r.ExportDone(band);
        using (var bmp = Decode(o.Path))
        {
            Check.Equal((16, 16), (bmp.Width, bmp.Height), "1×1 の書き出しの大きさ");
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                    Check.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(x, y), $"({x},{y})");
            }
        }
        r.CloseDocument();

        Check.Equal(1, r.PastePng(SkiaBytes.Png(8, 4, Img.Pattern(8, 4, alpha: false)))?.Added, "8×4");
        r.App.Ui(() => r.Session.SetOutputFormat(ExportFormat.Png));
        var o2 = r.ExportDone(band);
        using (var bmp = Decode(o2.Path))
        {
            Check.Equal((16, 8), (bmp.Width, bmp.Height), "8×4 の書き出しの大きさ");
            foreach (var (x, y) in new[] { (0, 0), (15, 0), (0, 7), (15, 7) })
                Check.Equal((byte)255, bmp.GetPixel(x, y).Alpha, $"四隅 ({x},{y}) の α");
        }
    }

    // ================================================================ (12) すべて透明(E-16)

    [Test(Slow = true)]
    public void AC55_12_E16_すべて透明のソースはGIFで透明インデックスだけのフレームになりPNGで全画素が0()
    {
        using var r = EdgeRig.Start(gpu: true);
        var band = r.WaitBand();
        var o = r.PastePng(SkiaBytes.Png(8, 8, Img.Solid(8, 8, 10, 20, 30, 0)))!;
        Check.Equal(1, o.Added, "取り込める");
        Check.Equal(null, r.NoticeNow, "エラーが出ない");
        r.App.Ui(() =>
        {
            var s = r.Session;
            Check.True(s.AddEffect(Target.OfLayer(0), "spin").Changed, "spin を足せない");
            s.SetLength10(5);
            s.SetFps(20);
            s.SetOutputFormat(ExportFormat.Gif);
        });
        var outcome = r.ExportDone(band);
        var gif = File.ReadAllBytes(outcome.Path);

        var wpf = new GifBitmapDecoder(new MemoryStream(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check.Equal(outcome.Frames, wpf.Frames.Count, "GifBitmapDecoder のフレーム数");
        Check.True(outcome.Frames >= 2, "spin でフレームが複数");

        var f = MiniGifDecoder.Parse(gif);
        Check.Equal(2, f.GlobalTableEntries, "カラーテーブルが 2 枠");
        Check.Equal(((byte)0, (byte)0, (byte)0), f.TableColor(0), "枠 0 は #000000");
        Check.Equal(((byte)0, (byte)0, (byte)0), f.TableColor(1), "枠 1 は #000000");
        Check.Equal(outcome.Frames, f.Frames.Count, "別実装のデコーダーのフレーム数");
        for (var i = 0; i < f.Frames.Count; i++)
        {
            var fr = f.Frames[i];
            Check.True(fr.TransparentFlag, $"フレーム {i} の透明フラグ");
            Check.Equal(1, fr.TransparentIndexField, $"フレーム {i} の透明インデックス");
            Check.Equal(2, fr.Disposal, $"フレーム {i} の disposal");
            Check.Equal(fr.Width * fr.Height, fr.Indices.Length, $"フレーム {i} の画素の数");
            Check.True(fr.Indices.Length > 0 && fr.Indices.All(v => v == 1), $"フレーム {i} の全画素が透明インデックス");
        }

        r.App.Ui(() => r.Session.SetOutputFormat(ExportFormat.Png));
        var png = r.ExportDone(band);
        using var bmp = Decode(png.Path);
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
                Check.Equal(new SKColor(0, 0, 0, 0), bmp.GetPixel(x, y), $"PNG の ({x},{y})");
        }
    }
}

internal static class EdgeOutcomeExtensions
{
    public static T Tap<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
