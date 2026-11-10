// probe-skia: 仕様書 §8.4 の測定 2(SkiaSharp の版・.NET 10 での起動・デコード・エンコード・EXIF の向き・ICC から sRGB・文字の描画)。
// 結果は貼りやすい文字列で標準出力に出す。読み込む画像はすべてこのプログラムがコードで作る(基準画像は使わない)。
// 測定用プログラムは製品に含めない(仕様書 §8.4・AC-7)。
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using GlintLoop.Core.Numerics;
using GlintLoop.Probes.Shared;
using SkiaSharp;

namespace GlintLoop.Probes.Skia;

static class Program
{
    static void W(string s = "") => Console.WriteLine(s);

    static void Section(string title, Action body)
    {
        W($"## {title}");
        try { body(); }
        catch (Exception e) { W($"  例外: {e.GetType().FullName}: {e.Message}"); }
        W();
    }

    static void Item(string label, Func<string> body)
    {
        string r;
        try { r = body(); }
        catch (Exception e) { r = $"例外 {e.GetType().Name}: {e.Message}"; }
        W($"  {label}: {r}");
    }

    static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        W("===== probe-skia (測定 2: SkiaSharp) =====");
        Section("1. 版と起動", Versions);
        Section("2. デコード(PNG・JPEG・WebP・BMP・ICO・GIF)", Decodes);
        Section("3. エンコード(WebP・PNG・JPEG)と読み戻し", Encodes);
        Section("4. EXIF の向き 1〜8", ExifOrientation);
        Section("5. Display P3 の ICC から sRGB への変換", DisplayP3);
        Section("6. 日本語と絵文字の描画", Text);
        W("===== probe-skia ここまで =====");
        return 0;
    }

    // ---------- 1 ----------
    static void Versions()
    {
        var asm = typeof(SKBitmap).Assembly;
        Item("SkiaSharp の管理側アセンブリの版", () => asm.GetName().Version?.ToString() ?? "(不明)");
        Item("SkiaSharp の InformationalVersion", () => asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(なし)");
        Item("SkiaSharpVersion.Native(ネイティブ部品の版)", () => SkiaSharpVersion.Native.ToString());
        Item("SkiaSharpVersion.NativeMinimum", () => SkiaSharpVersion.NativeMinimum.ToString());
        Item("CheckNativeLibraryCompatible(false)", () => SkiaSharpVersion.CheckNativeLibraryCompatible(false).ToString());
        Item(".NET の版(Environment.Version)", () => Environment.Version.ToString());
        Item("FrameworkDescription", () => RuntimeInformation.FrameworkDescription);
        Item("OSDescription", () => RuntimeInformation.OSDescription);
        Item("ProcessArchitecture", () => RuntimeInformation.ProcessArchitecture.ToString());
        Item("ネイティブ部品が動くか(1×1 の SKBitmap を作って塗る)", () =>
        {
            using var b = new SKBitmap(1, 1);
            b.Erase(new SKColor(10, 20, 30, 255));
            var c = b.GetPixel(0, 0);
            return $"動く。読み戻し=({c.Red},{c.Green},{c.Blue},{c.Alpha})";
        });
    }

    // ---------- 共通の部品 ----------
    // 4 つの象限に色を置いた模様。TL=赤 TR=緑 BL=青 BR=白。withAlpha のとき TR の α=128、BR の α=0。
    static byte[] Pattern(int w, int h, bool withAlpha)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool right = x >= w / 2, bottom = y >= h / 2;
                (byte r, byte g, byte b, byte a) c = (right, bottom) switch
                {
                    (false, false) => (255, 0, 0, 255),
                    (true, false) => (0, 255, 0, (byte)(withAlpha ? 128 : 255)),
                    (false, true) => (0, 0, 255, 255),
                    _ => (255, 255, 255, (byte)(withAlpha ? 0 : 255)),
                };
                int i = (y * w + x) * 4;
                px[i] = c.r; px[i + 1] = c.g; px[i + 2] = c.b; px[i + 3] = c.a;
            }
        return px;
    }

    static SKBitmap ToBitmap(int w, int h, byte[] rgba, SKColorSpace? cs = null)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul, cs);
        var bmp = new SKBitmap(info);
        IntPtr p = bmp.GetPixels();
        for (int y = 0; y < h; y++) Marshal.Copy(rgba, y * w * 4, p + y * bmp.RowBytes, w * 4);
        return bmp;
    }

    // コーデックで、変換先の色空間を dst にして straight の RGBA8888 に読む(dst が null なら色空間を変換しない)。
    static (SKCodecResult res, int w, int h, byte[] px) DecodeRgba(byte[] bytes, SKColorSpace? dst, int frame = 0)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes)) ?? throw new InvalidOperationException("SKCodec.Create が null");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, dst);
        var px = new byte[info.BytesSize];
        var h = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            var res = codec.GetPixels(info, h.AddrOfPinnedObject(), info.RowBytes, new SKCodecOptions(frame));
            return (res, info.Width, info.Height, px);
        }
        finally { h.Free(); }
    }

    static string Px(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return $"({px[i]},{px[i + 1]},{px[i + 2]},{px[i + 3]})";
    }

    static string Corners(byte[] px, int w, int h)
    {
        int ix = Math.Max(0, w / 8), iy = Math.Max(0, h / 8);
        return $"TL{Px(px, w, ix, iy)} TR{Px(px, w, w - 1 - ix, iy)} BL{Px(px, w, ix, h - 1 - iy)} BR{Px(px, w, w - 1 - ix, h - 1 - iy)}";
    }

    static int MaxDiff(byte[] a, byte[] b, bool alphaToo)
    {
        int m = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (!alphaToo && i % 4 == 3) continue;
            // 完全に透明な画素の色は意味を持たないので比べない
            if (a[i - i % 4 + 3] == 0 || b[i - i % 4 + 3] == 0) continue;
            m = Math.Max(m, Math.Abs(a[i] - b[i]));
        }
        return m;
    }

    static string CodecSummary(byte[] bytes)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes));
        if (codec is null) return "SKCodec.Create=null(読めない)";
        var i = codec.Info;
        var sb = new StringBuilder();
        sb.Append($"形式={codec.EncodedFormat} {i.Width}×{i.Height} ColorType={i.ColorType} AlphaType={i.AlphaType} ");
        sb.Append($"ColorSpace={(i.ColorSpace is null ? "なし" : (i.ColorSpace.IsSrgb ? "sRGB" : "sRGB以外"))} ");
        sb.Append($"FrameCount={codec.FrameCount} RepetitionCount={codec.RepetitionCount} Origin={codec.EncodedOrigin}");
        if (codec.FrameCount > 1)
        {
            var fi = codec.FrameInfo;
            sb.Append(" フレーム=[" + string.Join(" ", fi.Select((f, k) => $"#{k}:{f.Duration}ms,disp={f.DisposalMethod},req={f.RequiredFrame}")) + "]");
        }
        return sb.ToString();
    }

    // ---------- 2 ----------
    static void Decodes()
    {
        const int w = 16, h = 8;
        var opaque = Pattern(w, h, false);
        var alpha = Pattern(w, h, true);
        W($"  元の模様({w}×{h}): {Corners(opaque, w, h)}(不透明版)/ {Corners(alpha, w, h)}(透過版)");

        byte[]? png = null, jpg = null, webp = null;
        using (var b = ToBitmap(w, h, alpha)) { png = b.Encode(SKEncodedImageFormat.Png, 100)?.ToArray(); webp = b.Encode(SKEncodedImageFormat.Webp, 100)?.ToArray(); }
        using (var b = ToBitmap(w, h, opaque)) { jpg = b.Encode(SKEncodedImageFormat.Jpeg, 95)?.ToArray(); }

        var cases = new List<(string name, Func<byte[]?> make, byte[] expect, bool exact)>
        {
            ("PNG(SkiaSharp で作成、透過あり)", () => png, alpha, true),
            ("JPEG(SkiaSharp で作成、q95)", () => jpg, opaque, false),
            ("WebP(SkiaSharp で作成、q100)", () => webp, alpha, false),
            ("BMP 24 ビット BI_RGB(手組み)", () => ImageBytes.Bmp24(w, h, opaque), opaque, true),
            ("BMP 32 ビット V5 BI_BITFIELDS アルファあり(手組み)", () => ImageBytes.Bmp32V5(w, h, alpha), alpha, true),
            ("ICO(PNG 入り、手組み)", () => png is null ? null : ImageBytes.IcoWithPng(w, h, png), alpha, true),
            ("ICO(32 ビット DIB+AND マスク、手組み)", () => ImageBytes.IcoWithBmp(w, h, alpha), alpha, true),
        };
        foreach (var (name, make, expect, exact) in cases)
        {
            Item(name, () =>
            {
                var bytes = make();
                if (bytes is null) return "作れなかった(エンコードが null)";
                var sum = CodecSummary(bytes);
                var (res, dw, dh, px) = DecodeRgba(bytes, null);
                int d = MaxDiff(expect, px, true);
                return $"{bytes.Length} バイト / {sum} / GetPixels={res} / {Corners(px, dw, dh)} / 元との最大差={d}{(exact ? (d == 0 ? "(一致)" : "(不一致)") : "(非可逆なので参考)")}";
            });
        }

        // GIF: 2 フレーム、遅延 50(=500ms)、ループ 0(無限)。3 番を透明にした版も読む。
        Item("GIF(手組み、2 フレーム、遅延 50、NETSCAPE2.0 ループ 0)", () =>
        {
            var gif = MiniGif.TwoFrameAnimated(16);
            var sum = CodecSummary(gif);
            var f0 = DecodeRgba(gif, null, 0);
            var f1 = DecodeRgba(gif, null, 1);
            return $"{gif.Length} バイト / {sum} / 0 枚目 GetPixels={f0.res} 中央{Px(f0.px, f0.w, 8, 8)} / 1 枚目 GetPixels={f1.res} 中央{Px(f1.px, f1.w, 8, 8)}(期待: 0 枚目=赤系 (224,48,48)、1 枚目=青系 (48,80,224))";
        });
        Item("GIF(手組み、NETSCAPE2.0 なし)", () =>
        {
            uint[] pal = { 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF };
            var a = Enumerable.Repeat((byte)1, 16).ToArray();
            var b = Enumerable.Repeat((byte)2, 16).ToArray();
            var gif = MiniGif.Build(4, 4, pal, new[] { a, b }, 10, null);
            return CodecSummary(gif);
        });
        Item("GIF(手組み、透明インデックス 0・disposal 2)", () =>
        {
            uint[] pal = { 0x000000, 0xFF0000, 0x00FF00, 0x0000FF };
            var a = new byte[16]; a[5] = 1;
            var b = new byte[16]; b[10] = 3;
            var gif = MiniGif.Build(4, 4, pal, new[] { a, b }, 10, 0, transparentIndex: 0, disposal: 2);
            var f1 = DecodeRgba(gif, null, 1);
            return $"{CodecSummary(gif)} / 1 枚目: (1,1)={Px(f1.px, 4, 1, 1)}(期待 α=0) (2,2)={Px(f1.px, 4, 2, 2)}(期待 青 α=255)";
        });
    }

    // ---------- 3 ----------
    static void Encodes()
    {
        const int w = 64, h = 32;
        var src = Pattern(w, h, true);
        using var bmp = ToBitmap(w, h, src);
        using var pm = bmp.PeekPixels();
        var list = new List<(string name, Func<SKData?> enc, bool lossless, bool keepsAlpha)>
        {
            ("PNG(SKBitmap.Encode)", () => bmp.Encode(SKEncodedImageFormat.Png, 100), true, true),
            ("PNG(SKPixmap.Encode と SKPngEncoderOptions.Default)", () => pm.Encode(SKPngEncoderOptions.Default), true, true),
            ("JPEG q90(SKBitmap.Encode)", () => bmp.Encode(SKEncodedImageFormat.Jpeg, 90), false, false),
            ("JPEG q90(SKJpegEncoderOptions(90))", () => pm.Encode(new SKJpegEncoderOptions(90)), false, false),
            ("WebP q80 非可逆(SKBitmap.Encode)", () => bmp.Encode(SKEncodedImageFormat.Webp, 80), false, true),
            ("WebP 可逆(SKWebpEncoderOptions(Lossless,100))", () => pm.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100)), true, true),
        };
        foreach (var (name, enc, lossless, keepsAlpha) in list)
        {
            Item(name, () =>
            {
                using var data = enc();
                if (data is null) return "エンコードが null(失敗)";
                var bytes = data.ToArray();
                var (res, dw, dh, px) = DecodeRgba(bytes, null);
                int d = MaxDiff(src, px, keepsAlpha);
                string head = BitConverter.ToString(bytes, 0, Math.Min(4, bytes.Length));
                return $"{bytes.Length} バイト 先頭={head} / 読み戻し {res} {dw}×{dh} / {Corners(px, dw, dh)} / 元との最大差={d}{(lossless ? (d == 0 ? "(一致)" : "(不一致)") : "(非可逆なので参考)")}";
            });
        }
        Item("BMP・GIF・ICO のエンコード(参考。SkiaSharp で作れるか)", () =>
        {
            string Try(SKEncodedImageFormat f) { try { var d = bmp.Encode(f, 100); return d is null ? "null" : $"{d.Size} バイト"; } catch (Exception e) { return e.GetType().Name; } }
            return $"Bmp={Try(SKEncodedImageFormat.Bmp)} Gif={Try(SKEncodedImageFormat.Gif)} Ico={Try(SKEncodedImageFormat.Ico)}";
        });
    }

    // ---------- 4 ----------
    static void ExifOrientation()
    {
        const int w = 32, h = 16;
        var src = Pattern(w, h, false);
        byte[] baseJpeg;
        using (var b = ToBitmap(w, h, src)) baseJpeg = b.Encode(SKEncodedImageFormat.Jpeg, 95)!.ToArray();
        W($"  元の JPEG: {w}×{h}、TL=赤 TR=緑 BL=青 BR=白(EXIF なし、{baseJpeg.Length} バイト)");
        W("  列: 向き / SKCodec.EncodedOrigin / SKCodec.GetPixels の大きさと四隅 / SKBitmap.Decode / SKImage.FromEncodedData / 正しく表示したときの期待");
        string[] expect =
        {
            "",
            "TL=赤 TR=緑 BL=青 BR=白(32×16)",
            "TL=緑 TR=赤 BL=白 BR=青(32×16)",
            "TL=白 TR=青 BL=緑 BR=赤(32×16)",
            "TL=青 TR=白 BL=赤 BR=緑(32×16)",
            "TL=赤 TR=青 BL=緑 BR=白(16×32)",
            "TL=青 TR=赤 BL=白 BR=緑(16×32)",
            "TL=白 TR=緑 BL=青 BR=赤(16×32)",
            "TL=緑 TR=白 BL=赤 BR=青(16×32)",
        };
        for (int o = 1; o <= 8; o++)
        {
            int ori = o;
            Item($"向き {ori}", () =>
            {
                var jpg = ImageBytes.JpegWithOrientation(baseJpeg, ori);
                string origin;
                using (var codec = SKCodec.Create(SKData.CreateCopy(jpg))) origin = codec?.EncodedOrigin.ToString() ?? "codec=null";
                var (res, dw, dh, px) = DecodeRgba(jpg, null);
                string viaCodec = $"{dw}×{dh} {Named(px, dw, dh)}";
                string viaBitmap;
                using (var bm = SKBitmap.Decode(jpg))
                    viaBitmap = bm is null ? "null" : $"{bm.Width}×{bm.Height} {NamedBitmap(bm)}";
                string viaImage;
                using (var img = SKImage.FromEncodedData(jpg))
                {
                    if (img is null) viaImage = "null";
                    else
                    {
                        using var rb = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                        bool ok = img.ReadPixels(rb.Info, rb.GetPixels(), rb.RowBytes, 0, 0);
                        viaImage = $"{img.Width}×{img.Height} {(ok ? NamedBitmap(rb) : "ReadPixels 失敗")}";
                    }
                }
                return $"Origin={origin} / codec {viaCodec} / Decode {viaBitmap} / FromEncodedData {viaImage} / 期待 {expect[ori]}";
            });
        }
    }

    static string ColorName(byte r, byte g, byte b)
    {
        (string n, int r, int g, int b)[] names = { ("赤", 255, 0, 0), ("緑", 0, 255, 0), ("青", 0, 0, 255), ("白", 255, 255, 255) };
        var best = names.OrderBy(c => (c.r - r) * (c.r - r) + (c.g - g) * (c.g - g) + (c.b - b) * (c.b - b)).First();
        return best.n;
    }

    static string Named(byte[] px, int w, int h)
    {
        int ix = w / 8, iy = h / 8;
        string N(int x, int y) { int i = (y * w + x) * 4; return ColorName(px[i], px[i + 1], px[i + 2]); }
        return $"TL={N(ix, iy)} TR={N(w - 1 - ix, iy)} BL={N(ix, h - 1 - iy)} BR={N(w - 1 - ix, h - 1 - iy)}";
    }

    static string NamedBitmap(SKBitmap bm)
    {
        int w = bm.Width, h = bm.Height, ix = w / 8, iy = h / 8;
        string N(int x, int y) { var c = bm.GetPixel(x, y); return ColorName(c.Red, c.Green, c.Blue); }
        return $"TL={N(ix, iy)} TR={N(w - 1 - ix, iy)} BL={N(ix, h - 1 - iy)} BR={N(w - 1 - ix, h - 1 - iy)}";
    }

    // ---------- 5 ----------
    static void DisplayP3()
    {
        var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        var srgb = SKColorSpace.CreateSrgb();
        W($"  作った色空間: IsSrgb={p3.IsSrgb} GammaIsCloseToSrgb={p3.GammaIsCloseToSrgb}");

        // 代表色 5 点(P3 の 8 ビット値)。純色 2 つは sRGB の色域の外、肌色・灰・白は内側。
        (string name, byte r, byte g, byte b)[] colors =
        {
            ("P3 の純赤", 255, 0, 0), ("P3 の純緑", 0, 255, 0), ("肌色", 200, 120, 60), ("中間の灰", 128, 128, 128), ("白", 255, 255, 255),
        };
        int w = colors.Length, h = 1;
        var rgba = new byte[w * 4];
        for (int i = 0; i < w; i++) { rgba[i * 4] = colors[i].r; rgba[i * 4 + 1] = colors[i].g; rgba[i * 4 + 2] = colors[i].b; rgba[i * 4 + 3] = 255; }

        byte[] png;
        using (var bmp = ToBitmap(w, h, rgba, p3)) png = bmp.Encode(SKEncodedImageFormat.Png, 100)!.ToArray();
        bool encoderWroteIccp = ImageBytes.PngHasChunk(png, "iCCP");
        W($"  SkiaSharp の PNG エンコーダーが iCCP を書いたか: {encoderWroteIccp}");
        if (!encoderWroteIccp)
        {
            using var prof = p3.ToProfile();
            var icc = new byte[(int)prof.Size];
            Marshal.Copy(prof.Buffer, icc, 0, icc.Length);
            png = ImageBytes.PngInsertIccp(png, icc);
            W($"  iCCP を手で差し込んだ(ICC {icc.Length} バイト)。差し込み後に iCCP があるか: {ImageBytes.PngHasChunk(png, "iCCP")}");
        }
        Item("P3 の JPEG(参考): エンコーダーが ICC_PROFILE を書いたか", () =>
        {
            using var bmp = ToBitmap(w, h, rgba, p3);
            var jpg = bmp.Encode(SKEncodedImageFormat.Jpeg, 100)!.ToArray();
            return ImageBytes.Contains(jpg, "ICC_PROFILE"u8).ToString();
        });

        using (var codec = SKCodec.Create(SKData.CreateCopy(png)))
        {
            var cs = codec?.Info.ColorSpace;
            W($"  読んだ PNG の色空間: {(cs is null ? "なし" : $"IsSrgb={cs.IsSrgb}")}");
            if (cs is not null)
            {
                var xyz = cs.ToColorSpaceXyz();
                var ref3 = SKColorSpaceXyz.DisplayP3;
                double maxd = 0;
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) maxd = Math.Max(maxd, Math.Abs(xyz[r, c] - ref3[r, c]));
                W($"  読んだ色空間の XYZ と SKColorSpaceXyz.DisplayP3 の最大差: {maxd:E2}");
            }
        }

        var raw = DecodeRgba(png, null);
        var conv = DecodeRgba(png, srgb);
        var sv = SKColorSpaceXyz.Srgb;
        W($"  SKColorSpaceXyz.Srgb.Values(行優先と書かれている): [{string.Join(", ", sv.Values.Select(v => v.ToString("F5")))}]");
        W($"  SKColorSpaceXyz.Srgb[0,1]={sv[0, 1]:F5} [1,0]={sv[1, 0]:F5}(既知の D50 の sRGB 行列は X 行 = 0.43607, 0.38515, 0.14307)");
        var m = P3ToSrgbMatrix();
        W($"  計算の行列(SKColorSpaceXyz.Srgb の逆行列 × DisplayP3): [{string.Join(", ", m.Select(v => v.ToString("F5")))}]");
        W("  列: 色 / P3 の値 / 変換なしで読んだ値 / sRGB へ変換して読んだ値 / 自前の計算の期待値(線形光で行列を掛け 0〜1 に切る) / 差");
        for (int i = 0; i < w; i++)
        {
            var c = colors[i];
            double lr = Srgb.Lin(c.r / 255.0), lg = Srgb.Lin(c.g / 255.0), lb = Srgb.Lin(c.b / 255.0);
            int E(double v) => MathX.RoundHalfUpToInt(255 * Srgb.Enc(MathX.Sat(v)));
            int er = E(m[0] * lr + m[1] * lg + m[2] * lb), eg = E(m[3] * lr + m[4] * lg + m[5] * lb), eb = E(m[6] * lr + m[7] * lg + m[8] * lb);
            int k = i * 4;
            int d = Math.Max(Math.Abs(conv.px[k] - er), Math.Max(Math.Abs(conv.px[k + 1] - eg), Math.Abs(conv.px[k + 2] - eb)));
            W($"  {c.name}: P3({c.r},{c.g},{c.b}) / なし({raw.px[k]},{raw.px[k + 1]},{raw.px[k + 2]}) / sRGB({conv.px[k]},{conv.px[k + 1]},{conv.px[k + 2]}) / 期待({er},{eg},{eb}) / 差 {d}");
        }
        W($"  GetPixels の結果: 変換なし={raw.res} sRGB へ={conv.res}");
    }

    // 線形光の P3 から線形光の sRGB への行列(行優先 9 要素)。Skia が持つ D50 の XYZ 行列から計算する。
    static double[] P3ToSrgbMatrix()
    {
        var s = SKColorSpaceXyz.Srgb;
        var p = SKColorSpaceXyz.DisplayP3;
        double[,] S = new double[3, 3], P = new double[3, 3];
        // 添字 [x,y] は XML ドキュメントでは「行・列」だが、実物は「列・行」だった(probe の出力で確認)。行優先の Values を使う。
        float[] sv = s.Values, pv = p.Values;
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) { S[r, c] = sv[r * 3 + c]; P[r, c] = pv[r * 3 + c]; }
        double det = S[0, 0] * (S[1, 1] * S[2, 2] - S[1, 2] * S[2, 1]) - S[0, 1] * (S[1, 0] * S[2, 2] - S[1, 2] * S[2, 0]) + S[0, 2] * (S[1, 0] * S[2, 1] - S[1, 1] * S[2, 0]);
        double[,] I = new double[3, 3];
        I[0, 0] = (S[1, 1] * S[2, 2] - S[1, 2] * S[2, 1]) / det; I[0, 1] = (S[0, 2] * S[2, 1] - S[0, 1] * S[2, 2]) / det; I[0, 2] = (S[0, 1] * S[1, 2] - S[0, 2] * S[1, 1]) / det;
        I[1, 0] = (S[1, 2] * S[2, 0] - S[1, 0] * S[2, 2]) / det; I[1, 1] = (S[0, 0] * S[2, 2] - S[0, 2] * S[2, 0]) / det; I[1, 2] = (S[0, 2] * S[1, 0] - S[0, 0] * S[1, 2]) / det;
        I[2, 0] = (S[1, 0] * S[2, 1] - S[1, 1] * S[2, 0]) / det; I[2, 1] = (S[0, 1] * S[2, 0] - S[0, 0] * S[2, 1]) / det; I[2, 2] = (S[0, 0] * S[1, 1] - S[0, 1] * S[1, 0]) / det;
        var m = new double[9];
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) { double v = 0; for (int k = 0; k < 3; k++) v += I[r, k] * P[k, c]; m[r * 3 + c] = v; }
        return m;
    }

    // ---------- 6 ----------
    static void Text()
    {
        const string text = "GlintLoop 日本語のテキスト 😀🎞️";
        const float size = 32f;
        using var fm = SKFontManager.CreateDefault();
        Item("既定の書体(SKTypeface.Default)", () => SKTypeface.Default.FamilyName);
        Item("「日」に合う書体(MatchCharacter)", () => fm.MatchCharacter('日')?.FamilyName ?? "null");
        Item("「😀」(U+1F600)に合う書体(MatchCharacter(int))", () => fm.MatchCharacter(0x1F600)?.FamilyName ?? "null");
        Item("既定の書体だけで描くときの欠けた字形(glyph 0)の数", () =>
        {
            using var f = new SKFont(SKTypeface.Default, size);
            var g = f.GetGlyphs(text);
            return $"{g.Count(x => x == 0)} / {g.Length}";
        });

        // 書体の代替: 1 文字(ルーン)ずつ、既定の書体に字形が無ければ MatchCharacter で探し、同じ書体の続きを 1 つの区間にまとめる。
        var runs = new List<(SKTypeface tf, string s)>();
        foreach (var rune in text.EnumerateRunes())
        {
            SKTypeface tf = SKTypeface.Default;
            // 異体字セレクター(U+FE0E・U+FE0F)と ZWJ(U+200D)は、前の文字と同じ書体の区間に入れる
            bool joiner = rune.Value is 0xFE0E or 0xFE0F or 0x200D;
            if (joiner && runs.Count > 0) { runs[^1] = (runs[^1].tf, runs[^1].s + rune.ToString()); continue; }
            using (var df = new SKFont(tf, size)) if (!df.ContainsGlyph(rune.Value)) tf = fm.MatchCharacter(rune.Value) ?? SKTypeface.Default;
            string s = rune.ToString();
            if (runs.Count > 0 && runs[^1].tf.FamilyName == tf.FamilyName) runs[^1] = (runs[^1].tf, runs[^1].s + s);
            else runs.Add((tf, s));
        }
        W("  区間(書体・文字・欠けた字形の数/字形の数・送り幅): " + string.Join(" | ", runs.Select(r => { using var f = new SKFont(r.tf, size); var g = f.GetGlyphs(r.s); return $"[{r.tf.FamilyName}] {r.s} 欠け{g.Count(x => x == 0)}/{g.Length} {f.MeasureText(r.s):F1}px"; })));

        float total = 0, ascent = 0, descent = 0;
        foreach (var (tf, s) in runs)
        {
            using var f = new SKFont(tf, size);
            total += f.MeasureText(s);
            f.GetFontMetrics(out var met);
            ascent = Math.Min(ascent, met.Ascent);
            descent = Math.Max(descent, met.Descent);
        }
        int bw = (int)Math.Ceiling(total) + 8, bh = (int)Math.Ceiling(descent - ascent) + 8;
        using var bmp = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            float x = 4, y = 4 - ascent;
            foreach (var (tf, s) in runs)
            {
                using var f = new SKFont(tf, size) { Edging = SKFontEdging.Antialias };
                canvas.DrawText(s, x, y, SKTextAlign.Left, f, paint);
                x += f.MeasureText(s);
            }
        }
        W($"  文字の大きさ {size}px、全体の送り幅 {total:F1}px、ascent {ascent:F1} descent {descent:F1}");
        W($"  描いたビットマップの大きさ: {bw}×{bh}");
        int minX = bw, minY = bh, maxX = -1, maxY = -1, colored = 0, inked = 0;
        for (int yy = 0; yy < bh; yy++)
            for (int xx = 0; xx < bw; xx++)
            {
                var c = bmp.GetPixel(xx, yy);
                if (c.Alpha == 0) continue;
                inked++;
                minX = Math.Min(minX, xx); minY = Math.Min(minY, yy); maxX = Math.Max(maxX, xx); maxY = Math.Max(maxY, yy);
                int mx = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), mn = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                if (mx - mn > 40) colored++;
            }
        W($"  インクのある画素: {inked} 個、外接矩形 x={minX}..{maxX} y={minY}..{maxY}(幅 {maxX - minX + 1}×高さ {maxY - minY + 1})");
        W($"  色の付いた画素(黒で塗ったのに彩度がある画素 = カラー絵文字が描けた印): {colored} 個");
    }
}
