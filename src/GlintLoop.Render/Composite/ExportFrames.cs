// 書き出しのフレームの列(IExportFrames)の実装: DocumentRenderer.RenderExportAsync(優先度 2)で、書き出しの長辺の straight RGBA を 1 枚ずつ描く。
// アニメの形式は index k を t=k÷N(N は設定の TimelineFrames)で、静止形式は設定の FrameK を描く。全フレームを持たず、呼ばれるたびに描く(GIF の 2 回の供給も描き直す。Q-I)。
// 描いた大きさが設定の幅と高さと違えば例外にする(出力の大きさの式は描画と同じ CompositeGeometry)。
using GlintLoop.Core.Export;
using GlintLoop.Core.Session;

namespace GlintLoop.Render.Composite;

public sealed class ExportFrames : IExportFrames
{
    private readonly DocumentRenderer _renderer;
    private readonly SessionDocument _doc;
    private readonly ExportSettings _settings;
    private readonly HashSet<int> _shrunk = [];
    private int _renders;

    /// <summary>doc を settings(はしごの段 r_i)で描くフレームの列。</summary>
    public ExportFrames(DocumentRenderer renderer, SessionDocument doc, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(settings);
        _renderer = renderer;
        _doc = doc;
        _settings = settings;
    }

    public int Width => _settings.Width;

    public int Height => _settings.Height;

    public int FrameCount => _settings.FrameCount;

    /// <summary>描いた回数(テストと測定用)。</summary>
    public int Renders => Volatile.Read(ref _renders);

    public ExportSettings Settings => _settings;

    /// <summary>ここまでに描いたフレームで、描画の上限で縮めたレイヤーの番号(昇順。E-19)。</summary>
    public IReadOnlyList<int> ShrunkLayers
    {
        get
        {
            lock (_shrunk) return _shrunk.Order().ToArray();
        }
    }

    public byte[] GetFrame(int index, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ct.ThrowIfCancellationRequested();
        var animated = ExportFormat.IsAnimated(_settings.Format);
        var k = animated ? index : _settings.FrameK;
        var n = Math.Max(1, _settings.TimelineFrames);
        var task = _renderer.RenderExportAsync(_doc, _settings.LongSide, k, n);
        try
        {
            task.Wait(ct);
        }
        catch (AggregateException)
        {
            // 描画の失敗は次の行で元の型のまま投げる(ログの reason はその型で分ける)
        }
        var im = task.GetAwaiter().GetResult();
        Interlocked.Increment(ref _renders);
        lock (_shrunk) _shrunk.UnionWith(im.ShrunkLayers);
        if (im.Width != Width || im.Height != Height)
            throw new InvalidOperationException($"描いた大きさ {im.Width}×{im.Height} が書き出しの設定の {Width}×{Height} と違う");
        return im.Pixels;
    }

    /// <summary>OutputPlan.Resolve に渡すソースの寸法の関数(描画が使う ISourceImages から引く)。</summary>
    public static Func<string, SizeD> SourceSize(ISourceImages sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sha =>
        {
            var im = sources.Find(sha) ?? throw new KeyNotFoundException("文書が参照する画像が ISourceImages に無い");
            return new SizeD(im.Width, im.Height);
        };
    }

    /// <summary>段の設定からフレームの列を作る関数(ExportJob.Run に渡す形)。</summary>
    public static Func<ExportSettings, IExportFrames> Factory(DocumentRenderer renderer, SessionDocument doc) =>
        s => new ExportFrames(renderer, doc, s);
}
