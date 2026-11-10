// バッチの画像 1 個の文書(FR-87): 画像を 1 枚だけの土台にして、いまの文書の土台のスタックとルックの強さ、全体のスタックとルックの強さ、seed、時間、出力設定を写す。
// 土台の表示と不透明度は使わず、オンと 100 にする。土台以外のレイヤーは使わない。文書が無いとき(ウェルカム画面)は、空のスタックと既定の時間・出力設定にする(仮の決め)。
// BatchSourceImages は、この文書の描画が読む画素(処理中の 1 個だけ)。取り込みのセッションには入れない。
using GlintLoop.Codec;
using GlintLoop.Core.Session;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Batch;

public static class BatchDocument
{
    public static SessionDocument Build(SessionDocument? current, ImportedImage image, string name)
    {
        var layer = new Layer(DocumentRules.ImportName(name), LayerKind.Image, image.Sha256, null, null, true, 100, BlendMode.Normal, Placement.Identity,
            current?.Base.LookStrength ?? 100, current?.Base.Stack ?? Array.Empty<StackEntry>());
        return new SessionDocument(
            current?.Seed ?? 0,
            current?.SaltCounter ?? 0,
            current?.Time ?? TimeSettings.Default,
            Target.OfLayer(0),
            [new AssetEntry(image.Sha256, image.Ext, image.ByteCount)],
            [layer],
            current?.All ?? GlobalStack.Empty,
            current?.Output ?? OutputSettings.Default);
    }
}

public sealed class BatchSourceImages : ISourceImages
{
    private volatile Entry? _current;

    public void Set(ImportedImage image) => _current = new Entry(image.Sha256, new SourceImage(image.Width, image.Height, image.Pixels));

    public void Clear() => _current = null;

    public SourceImage? Find(string sha256) => _current is { } c && c.Sha256 == sha256 ? c.Image : null;

    private sealed record Entry(string Sha256, SourceImage Image);
}
