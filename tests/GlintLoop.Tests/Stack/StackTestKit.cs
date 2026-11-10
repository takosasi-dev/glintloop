// 担当 stack のテストの道具: 製品のカタログ、手で組んだ文書(座標の関数のテスト用)、アプリへの画像の取り込み、StackModule の取り出し。
// 画面のテストは TestApp(画面の外・偽物)の上で行い、UIA はテストのスレッドから呼ぶ。取り込みは ImageImporter と MemoryImportData を使う。
using GlintLoop.App.Stack;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Stack;

internal static class StackTestKit
{
    private static readonly Lazy<EffectCatalog> CatalogLazy = new(() => BuiltInEffects.Load(new MemoryLogSink()));

    public static EffectCatalog Catalog => CatalogLazy.Value;

    public static EffectDef Def(string id) => Catalog.Find(id) ?? throw new InvalidOperationException("カタログに無い: " + id);

    public static StackEntry Entry(string id, long salt = 1, params (string Key, ParamValue Value)[] values)
    {
        var ps = EffectSchema.DefaultParams(Def(id)).Select(kv =>
        {
            foreach (var (k, v) in values) if (k == kv.Key) return new KeyValuePair<string, ParamValue>(k, v);
            return kv;
        }).ToArray();
        return new StackEntry(id, true, EffectOrigin.User, salt, 100, ps);
    }

    public static Layer ImageLayer(string asset, Placement place, params StackEntry[] stack) =>
        new("L", LayerKind.Image, asset, null, null, true, 100, BlendMode.Normal, place, 100, stack);

    public static SessionDocument Doc(params Layer[] layers) => new(
        0, 10, TimeSettings.Default, Target.OfLayer(layers.Length - 1), [], layers, GlobalStack.Empty, OutputSettings.Default);

    public static StackModule Module(TestApp app) => app.Ui(() => app.Shell.Modules.OfType<StackModule>().Single());

    /// <summary>画像 1 枚を取り込む(文書が無ければ土台、あればレイヤー)。UI スレッドの通知が済むまで待つ。</summary>
    public static void Import(TestApp app, int w, int h, byte[] rgba, string name = "a")
    {
        using var dir = new TempDir();
        var importer = new ImageImporter(app.Shell.ImportTarget, new MemoryLogSink());
        var o = importer.Drop(new MemoryImportData().WithFiles(dir.Write(name + ".png", SkiaBytes.Png(w, h, rgba))));
        Framework.Check.Equal(1, o.Added, "取り込めた枚数");
        UiHost.Idle();
    }

    /// <summary>画素 (x, y) の色が (R=x mod 256, G=y mod 256, B=b, α=255) の画像。</summary>
    public static byte[] Gradient(int w, int h, byte b)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            px[i] = (byte)(x % 256);
            px[i + 1] = (byte)(y % 256);
            px[i + 2] = b;
            px[i + 3] = 255;
        }
        return px;
    }

    public static void SetPixel(byte[] px, int w, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = (y * w + x) * 4;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
        px[i + 3] = a;
    }
}
