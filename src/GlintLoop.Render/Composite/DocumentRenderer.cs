// 文書(SessionDocument)の描画(§9.7 の文書の描画の流れ: 規則 1〜7・9・11〜13)。表示(プロキシ)・サムネイル・書き出しが同じ経路を通る(INV-6)。
// レイヤーごとに ソース → 折りたたみ → 形の残り → ルック → 仕上げ の静的な結果を作り、作業面に置いてモーション(規則 12)を通して重ね、全体のスタックと最後の変換を通す。
// 仕事は RenderService の専用スレッドの上で行い、外からは Task を返す依頼だけで使う(INV-5)。ノードの結果は NodeCache で再利用する(規則 5)。
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render.Effects;

namespace GlintLoop.Render.Composite;

/// <summary>読み戻しの形(規則 7)。書き出しは straight の RGBA、表示とサムネイルは Pbgra32。</summary>
public enum DocumentPixelFormat { StraightRgba8, Pbgra32 }

/// <summary>
/// 文書の描画の結果。Pixels は Width×Height×4 バイト(行を詰める)。Layout は描いた大きさの配置(規則 1・11)。
/// ShrunkLayers は、描画の上限で縮めて(n≥1)描いたレイヤーの番号(layer.render_shrunk の候補。RenderShrinkNotices に渡す)。
/// </summary>
public sealed record DocumentImage(
    int Width,
    int Height,
    DocumentPixelFormat Format,
    byte[] Pixels,
    CompositeLayout Layout,
    IReadOnlyList<int> ShrunkLayers);

/// <summary>DocumentRenderer の設定。MaxTextureDimension は描画の上限の判定(規則 11)に使う最大寸法(null はアダプターの値)。</summary>
public sealed record DocumentRendererOptions
{
    /// <summary>ノードのキャッシュの上限の既定(規則 5: GPU メモリで 512 MB)。</summary>
    public const long DefaultCacheBytes = 512L * 1024 * 1024;

    public int? MaxTextureDimension { get; init; }

    public long CacheBytes { get; init; } = DefaultCacheBytes;
}

public sealed class DocumentRenderer : IDisposable
{
    internal const string FoldShader = "shape_fold.ps";
    internal const string BoxHalfShader = "box_half.ps";
    internal const string PlaceShader = "layer_place.ps";
    internal const string BlendShader = "layer_blend.ps";

    private readonly RenderService _service;
    private readonly ISourceImages _sources;
    private readonly IEffectCatalog _catalog;

    // 以下は描画の専用スレッドの上だけで触る
    private readonly NodeCache _cache;
    private readonly Dictionary<string, SourceTexture> _sourceTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedSources = new(StringComparer.Ordinal);
    private bool _disposed;

    // エフェクトのノードを計算した回数(id ごと。AC-36 の (8) のテスト用の計数)。専用スレッドが書き、テストが読むので lock で守る
    private readonly Dictionary<string, int> _effectRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// service の専用スレッドの上で文書を描く。sources は取り込んだ画像の画素、catalog は id から EffectDef を引く(null なら service.Effects)。
    /// </summary>
    public DocumentRenderer(RenderService service, ISourceImages sources, DocumentRendererOptions? options = null, IEffectCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(sources);
        _service = service;
        _sources = sources;
        _catalog = catalog ?? service.Effects;
        options ??= new DocumentRendererOptions();
        MaxTextureDimension = options.MaxTextureDimension ?? service.MaxTextureDimension;
        _cache = new NodeCache(options.CacheBytes);
        _service.DeviceReleasing += ReleaseDeviceResources;
    }

    /// <summary>デバイスが失われたか、作り直しを待っている間か(FR-93)。</summary>
    public bool IsDeviceLost => _service.IsDeviceLost;

    // デバイスを放す直前(専用スレッド)。ノードの結果とソースのテクスチャは、次の描画が新しいデバイスの上で作り直す(ソースの画素は ISourceImages に残っている)
    private void ReleaseDeviceResources()
    {
        _cache.Clear();
        ReleaseSources(all: true);
    }

    /// <summary>描画の上限の判定(規則 11)に使うテクスチャの最大寸法。</summary>
    public int MaxTextureDimension { get; }

    private volatile ITextRasterizer? _textRasterizer;

    /// <summary>
    /// テキストのエフェクト(aux=text)のビットマップを描く受け口(実装は Codec。組み立てのあとで入れる)。null の間は、aux=text の行を描かない。
    /// 描画の計画を作るときに読むので、どのスレッドから入れてもよい。
    /// </summary>
    public ITextRasterizer? TextRasterizer
    {
        get => _textRasterizer;
        set => _textRasterizer = value;
    }

    /// <summary>キャッシュの数え(テストと測定用。専用スレッドの上の値を読むので、描画の依頼と重ねて読まない)。</summary>
    internal NodeCache Cache => _cache;

    /// <summary>エフェクト id のノードを(キャッシュから使わずに)計算した回数(AC-36 の (8) のテスト用の計数)。</summary>
    internal int EffectRuns(string effectId)
    {
        lock (_effectRuns) return _effectRuns.TryGetValue(effectId, out var n) ? n : 0;
    }

    /// <summary>エフェクトの実行の数えを 0 に戻す。</summary>
    internal void ResetEffectRuns()
    {
        lock (_effectRuns) _effectRuns.Clear();
    }

    private void CountRun(string effectId)
    {
        lock (_effectRuns) _effectRuns[effectId] = _effectRuns.TryGetValue(effectId, out var n) ? n + 1 : 1;
    }

    // ================================================================ 大きさ(規則 1・11)

    /// <summary>合成のネイティブ寸法(Wc, Hc)。</summary>
    public SizeD CompositeNative(SessionDocument doc) => CompositeGeometry.CompositeNative(doc, SizeOf);

    /// <summary>長辺 lout で描くときの配置(CompositeGeometry.Layout と同じもの)。</summary>
    public CompositeLayout Layout(SessionDocument doc, int lout) =>
        CompositeGeometry.Layout(doc, SizeOf, lout, MaxTextureDimension);

    /// <summary>画面のプロキシの長辺 min(2048, ceil(max(Wc, Hc)))。</summary>
    public int ProxyLongSide(SessionDocument doc) => CompositeGeometry.ProxyLongSide(CompositeNative(doc));

    /// <summary>サムネイルの長辺 min(192, ceil(max(Wc, Hc)))。</summary>
    public int ThumbnailLongSide(SessionDocument doc) => CompositeGeometry.ThumbnailLongSideOf(CompositeNative(doc));

    private SizeD SizeOf(string sha256)
    {
        var im = _sources.Find(sha256) ?? throw new KeyNotFoundException("文書が参照する画像が ISourceImages に無い");
        return new SizeD(im.Width, im.Height);
    }

    // ================================================================ 描画の入口

    /// <summary>
    /// 文書を、合成の長辺 lout(画素)で描く。表示・サムネイル・書き出しの共通の入口で、違いは lout と format だけ(INV-6)。
    /// frameK・frameN はフレーム番号とフレーム数(静止画は 0 と 1。t=frameK÷frameN で、frameK=frameN なら t=1)。seed は文書の seed を使う。
    /// priority は描画の専用スレッドの待ち行列の優先度(§9.9 の 8。表示 0・サムネイル 1・書き出し 2)で、結果は変えない。
    /// </summary>
    public Task<DocumentImage> RenderAsync(SessionDocument doc, int lout, DocumentPixelFormat format, int frameK = 0, int frameN = 1,
        RenderPriority priority = RenderPriority.Display)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentOutOfRangeException.ThrowIfLessThan(lout, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = new FrameContext(unchecked((uint)doc.Seed), frameK, frameN);
        return _service.InvokeAsync(ctx => RenderOnThread(ctx, doc, lout, format, frame), priority);
    }

    /// <summary>画面のプロキシ(長辺 min(2048, ceil(max(Wc, Hc)))、既定は表示用の Pbgra32)。フレームは frameK÷frameN。</summary>
    public Task<DocumentImage> RenderProxyAsync(SessionDocument doc, DocumentPixelFormat format = DocumentPixelFormat.Pbgra32, int frameK = 0, int frameN = 1) =>
        RenderAsync(doc, ProxyLongSide(doc), format, frameK, frameN);

    /// <summary>合成のサムネイル(長辺 min(192, ceil(max(Wc, Hc)))、既定は Pbgra32。優先度はサムネイル)。</summary>
    public Task<DocumentImage> RenderThumbnailAsync(SessionDocument doc, DocumentPixelFormat format = DocumentPixelFormat.Pbgra32, int frameK = 0, int frameN = 1) =>
        RenderAsync(doc, ThumbnailLongSide(doc), format, frameK, frameN, RenderPriority.Thumbnail);

    /// <summary>書き出し(長辺 lout は FR-73 で決めた値。straight の RGBA。優先度は書き出し)。</summary>
    public Task<DocumentImage> RenderExportAsync(SessionDocument doc, int lout, int frameK = 0, int frameN = 1) =>
        RenderAsync(doc, lout, DocumentPixelFormat.StraightRgba8, frameK, frameN, RenderPriority.Export);

    /// <summary>ノードのキャッシュと GPU に置いたソースを放す(結果は変わらない)。</summary>
    public Task ClearCacheAsync() => _service.InvokeCleanupAsync(ReleaseDeviceResources);

    // ================================================================ 計画(GPU を使わない)

    private sealed record EffectStep(EffectInvocation Inv, string Key, TextAuxPlan? Aux = null);

    private sealed record SourceRef(string Id, string? Sha256, string? Fill);

    /// <summary>
    /// レイヤーの計画。Steps は静的な行(形の残り・ルック・仕上げ)、PlacedKey は作業面に置いた面のキー、
    /// MotionSteps はその面に掛けるモーションの行(規則 12。枠は Layout.Frame)。
    /// </summary>
    private sealed record LayerPlan(int Index, Layer Layer, LayerLayout Layout, SourceRef Source, FoldPlan Fold, string FoldKey,
        IReadOnlyList<EffectStep> Steps, string PlacedKey, IReadOnlyList<EffectStep> MotionSteps)
    {
        public string StaticKey => Steps.Count > 0 ? Steps[^1].Key : FoldKey;
    }

    /// <summary>PlanSteps が拾うステージ。</summary>
    private enum StageFilter { Static, Motion, All }

    private sealed record SourceTexture(GpuTexture Texture, bool Uniform);

    private DocumentImage RenderOnThread(RenderContext ctx, SessionDocument doc, int lout, DocumentPixelFormat format, FrameContext frame)
    {
        var layout = CompositeGeometry.Layout(doc, SizeOf, lout, MaxTextureDimension);
        var wo = layout.Output.Wo;
        var ho = layout.Output.Ho;
        var plans = new List<LayerPlan>();
        var shrunk = new List<int>();
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (!layer.Visible) continue; // FR-47: オフのレイヤーは重ねない
            var ll = layout.Layers[i];
            if (ll.Frame.ClipTo(wo, ho).IsEmpty) continue; // E-17: 枠が作業面と重ならないレイヤーは何も描かない(重ねても B は変わらない)
            plans.Add(PlanLayer(i, layer, ll, frame, wo, ho));
            if (ll.Shrink.N >= 1) shrunk.Add(i);
        }

        var compositeKey = CompositeKey(wo, ho, plans);
        // 全体のスタック(規則 4 の 6)。モーションはステージの順で最後になり、枠は作業面の全体(規則 12 の末尾)
        var globalSteps = PlanSteps(doc.All.Stack, compositeKey, doc.All.LookStrength, frame, wo, ho, StageFilter.All, new FrameRect(0, 0, wo, ho));

        _usedSources.Clear();
        var done = false;
        try
        {
            var final = RunChain(ctx, compositeKey, () => BuildComposite(ctx, plans, wo, ho, frame), globalSteps, frame);
            var pixels = format == DocumentPixelFormat.StraightRgba8 ? ctx.ReadStraightRgba8(final) : ctx.ReadPbgra32(final);
            done = true;
            return new DocumentImage(wo, ho, format, pixels, layout, shrunk);
        }
        finally
        {
            _cache.EndRender();
            ReleaseSources(all: false);
            // 4 MP 以上の描画は中間のテクスチャを多く作っては放すので、WARP に溜めさせない(AC-61)
            if (done && (long)wo * ho >= 4_000_000) ctx.Gpu.TrimIfWarp();
        }
    }

    private LayerPlan PlanLayer(int index, Layer layer, LayerLayout ll, FrameContext frame, int wo, int ho)
    {
        SourceRef source;
        SizeD size;
        if (layer.Kind == LayerKind.Empty)
        {
            var es = layer.Size ?? DocumentRules.DefaultEmptySize;
            var fill = ColorText.TryNormalizeRgba(layer.Fill, out var f) ? f : ColorText.Transparent;
            source = new SourceRef("empty:" + fill, null, fill);
            size = new SizeD(es.W, es.H);
        }
        else
        {
            var sha = layer.Asset ?? throw new InvalidOperationException("image のレイヤーに asset が無い");
            source = new SourceRef("img:" + sha, sha, null);
            size = SizeOf(sha);
        }
        // 規則 3 の出力はレイヤーの画像(規則 11 で縮めるときは wr×hr)
        var fold = ShapeFold.Plan(size, layer.Stack, ll.Shrink.Wr, ll.Shrink.Hr);
        var (rotate, crop, pad) = ShapeFold.FixedEntries(layer.Stack);
        var foldKey = Key("fold", source.Id, Num(size.W), Num(size.H), Num(fold.OutW), Num(fold.OutH),
            Describe(rotate), Describe(crop), Describe(pad));
        var steps = PlanSteps(layer.Stack, foldKey, layer.LookStrength, frame, fold.OutW, fold.OutH, StageFilter.Static, null);
        // 規則 11 で置いた作業面のキー(静的な結果のキー・枠・縮めの有無・作業面の大きさで決まり、時間に依存しない)
        var q = ll.Frame;
        var staticKey = steps.Count > 0 ? steps[^1].Key : foldKey;
        var placedKey = Key("placed", staticKey, Num(q.Qx), Num(q.Qy), Num(q.W), Num(q.H), Num(ll.Shrink.N), Num(wo), Num(ho));
        // 規則 12: 置いた作業面の上で、枠 Q を基準にモーションステージを実行する
        var motion = PlanSteps(layer.Stack, placedKey, layer.LookStrength, frame, wo, ho, StageFilter.Motion, q);
        return new LayerPlan(index, layer, ll, source, fold, foldKey, steps, placedKey, motion);
    }

    /// <summary>
    /// スタックのうち、折りたたみのあとに普通のエフェクトとして実行する行(規則 4 の 1)。ステージの順(形の残り・ルック・仕上げ)に並べ、
    /// 各ノードのキーを上流のキーから作る。目がオフの行・カタログに無い id・回転・切り抜き・余白(折りたたみで描く)は除く。
    /// filter はモーションステージを除く(Static)・モーションステージだけ(Motion)・全部(All)。モーションの行の依頼には枠 motionFrame を入れる(規則 12・Q-A)。
    /// 時間に依存する行のキーには t=K/N を入れる(規則 5・9)。補助のビットマップを使うエフェクト(aux=text)は、TextRasterizer が無いときは実行しない。
    /// あるときは w×h のビットマップの計画を付ける。
    /// </summary>
    private List<EffectStep> PlanSteps(IReadOnlyList<StackEntry> stack, string headKey, int lookStrength, FrameContext frame, int w, int h,
        StageFilter filter, FrameRect? motionFrame)
    {
        var text = _textRasterizer;
        var picked = new List<(StackEntry Entry, EffectDef Def, int Row)>();
        for (var row = 0; row < stack.Count; row++)
        {
            var e = stack[row];
            if (!e.Enabled) continue;
            if (EffectSchema.IsFixedShape(e.Id)) continue;
            var def = _catalog.Find(e.Id);
            if (def is null) continue;
            var isMotion = def.Stage == EffectStage.Motion;
            if (filter == StageFilter.Static && isMotion) continue;
            if (filter == StageFilter.Motion && !isMotion) continue;
            if (def.Aux == AuxKind.Text && text is null) continue; // 描く受け口が無い
            picked.Add((e, def, row));
        }
        picked.Sort((a, b) =>
        {
            var c = EffectSchema.OrderKey(a.Def).CompareTo(EffectSchema.OrderKey(b.Def));
            return c != 0 ? c : a.Row.CompareTo(b.Row);
        });

        var steps = new List<EffectStep>(picked.Count);
        var upstream = headKey;
        foreach (var (entry, def, _) in picked)
        {
            var inv = ToInvocation(entry, def, lookStrength);
            if (def.Stage == EffectStage.Motion && motionFrame is { } q) inv = inv with { MotionFrame = q };
            var mf = inv.MotionFrame is { } m ? $"{Num(m.Qx)},{Num(m.Qy)},{Num(m.W)},{Num(m.H)}" : "-";
            var key = Key("fx", upstream, def.Id, DescribeValues(inv.Values), Num(inv.Amount), Num(inv.Salt), Num(frame.Seed),
                def.TimeDependent ? $"t={Num(frame.K)}/{Num(frame.N)};c={Num(inv.Cycles)};p={Num(inv.Phase)};w={inv.Wave}" : "-",
                Num(w), Num(h), mf);
            steps.Add(new EffectStep(inv, key, def.Aux == AuxKind.Text ? TextAux.Plan(text!, inv, w, h) : null));
            upstream = key;
        }
        return steps;
    }

    /// <summary>
    /// スタックの行を描画の依頼にする。実効 amount は規則 4 の 3(origin=look のときだけ対象のルックの強さを掛ける)。
    /// 共通パラメータ cycles・phase・wave は依頼の欄へ移し、宣言の params に無いキーは値に入れない(EffectConstants が拒むため)。
    /// </summary>
    internal static EffectInvocation ToInvocation(StackEntry entry, EffectDef def, int lookStrength)
    {
        var values = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
        var cycles = 1;
        var phase = 0.0;
        WaveKind? wave = null;
        foreach (var kv in entry.Params)
        {
            if (def.TimeDependent)
            {
                switch (kv.Key)
                {
                    case "cycles" when kv.Value is NumberValue n: cycles = Math.Clamp((int)Math.Floor(n.Value + 0.5), 1, 8); break;
                    case "phase" when kv.Value is NumberValue n: phase = n.Value; break;
                    case "wave" when kv.Value is StringValue s: wave = ParseWave(s.Value); break;
                }
            }
            if (def.FindParam(kv.Key) is not null) values[kv.Key] = kv.Value;
        }
        var amount = EffectAmount.Effective(entry.Amount, lookStrength, entry.Origin == EffectOrigin.Look, def.AmountMode);
        return new EffectInvocation(def)
        {
            Values = values,
            Amount = amount,
            Salt = unchecked((uint)entry.Salt),
            Cycles = cycles,
            Phase = phase,
            Wave = wave,
        };
    }

    private static WaveKind? ParseWave(string s) => s switch
    {
        "sine" => WaveKind.Sine,
        "tri" => WaveKind.Tri,
        "ease" => WaveKind.Ease,
        "saw" => WaveKind.Saw,
        _ => null,
    };

    /// <summary>
    /// 重ね終えた合成のキー(規則 5)。オンで描くレイヤーすべての、静的な結果のキー・枠(配置)・縮めの有無・不透明度・ブレンド・順序と、
    /// モーションステージの最後のノードのキー(モーションのパラメータと t を含む)。t はどれかのレイヤーにオンのモーションがあるときだけ入る。
    /// </summary>
    private static string CompositeKey(int wo, int ho, IReadOnlyList<LayerPlan> plans)
    {
        var parts = new List<string> { Num(wo), Num(ho) };
        foreach (var p in plans)
        {
            var f = p.Layout.Frame;
            var motionKey = p.MotionSteps.Count > 0 ? p.MotionSteps[^1].Key : "-";
            parts.Add($"{Num(p.Index)}:{p.StaticKey}:{Num(f.Qx)},{Num(f.Qy)},{Num(f.W)},{Num(f.H)}:{Num(p.Layout.Shrink.N)}:{Num(p.Layer.Opacity)}:{p.Layer.Blend}:{motionKey}");
        }
        return Key("composite", [.. parts]);
    }

    // ================================================================ GPU の上の実行(専用スレッド)

    /// <summary>
    /// head(キー headKey)から steps を順に通した結果。下流からキャッシュを探し、見つかったノードより後ろだけを計算する。
    /// 計算するノードの orig には head を渡す。返すテクスチャはキャッシュのもの。
    /// </summary>
    private GpuTexture RunChain(RenderContext ctx, string headKey, Func<GpuTexture> head, IReadOnlyList<EffectStep> steps, FrameContext frame)
    {
        var start = steps.Count - 1;
        while (start >= 0 && !_cache.Contains(steps[start].Key)) start--;
        GpuTexture? current = start >= 0 ? _cache.Get(steps[start].Key) : null;
        if (start == steps.Count - 1 && current is not null) return current;
        var orig = _cache.Get(headKey) ?? _cache.Add(headKey, head());
        current ??= orig;
        for (var i = start + 1; i < steps.Count; i++)
        {
            var inv = steps[i].Inv;
            // aux=text の補助のビットマップは、文字などの値とキャンバスの大きさのキーでキャッシュに置く(値が変わったときだけ作り直す)
            if (steps[i].Aux is { } aux) inv = inv with { Aux = _cache.Get(aux.Key) ?? _cache.Add(aux.Key, TextAux.Build(ctx, aux)) };
            current = _cache.Add(steps[i].Key, ctx.RunEffect(inv, current, frame, orig));
            CountRun(inv.Def.Id);
        }
        return current;
    }

    /// <summary>
    /// 規則 11〜13: オンのレイヤーを下から、作業面に置いて(モーションがあれば規則 12 を通して)重ねる。最初の B は全画素 (0, 0, 0, 0)。
    /// モーションのあるレイヤーの置いた面とモーションのノードはキャッシュに置き(規則 5)、無いレイヤーの置いた面は使い捨てる。
    /// </summary>
    private GpuTexture BuildComposite(RenderContext ctx, IReadOnlyList<LayerPlan> plans, int wo, int ho, FrameContext frame)
    {
        var b = ctx.CreateWorking(wo, ho);
        try
        {
            ctx.Passes.Fill(b, Vector4.Zero);
            foreach (var plan in plans)
            {
                GpuTexture? owned = null;
                GpuTexture layerTop;
                if (plan.MotionSteps.Count > 0)
                {
                    // 規則 12。置いた面とモーションのノードはキャッシュのもの(Dispose しない)
                    layerTop = RunChain(ctx, plan.PlacedKey, () => Place(ctx, plan, wo, ho, frame), plan.MotionSteps, frame);
                }
                else
                {
                    owned = Place(ctx, plan, wo, ho, frame);
                    layerTop = owned;
                }
                var next = ctx.CreateWorking(wo, ho);
                try
                {
                    ctx.Draw(BlendShader, next, [b, layerTop], BlendConstants(plan.Layer));
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
                finally
                {
                    owned?.Dispose();
                }
                b.Dispose();
                b = next;
            }
            return b;
        }
        catch
        {
            b.Dispose();
            throw;
        }
    }

    /// <summary>規則 11: レイヤーの静的な結果を作業面(wo×ho)の枠に置いた新しいテクスチャ(呼び手のもの)。</summary>
    private GpuTexture Place(RenderContext ctx, LayerPlan plan, int wo, int ho, FrameContext frame)
    {
        var stat = RunChain(ctx, plan.FoldKey, () => Fold(ctx, plan), plan.Steps, frame);
        var placed = ctx.CreateWorking(wo, ho);
        try
        {
            ctx.Draw(PlaceShader, placed, [stat], PlaceConstants(plan.Layout));
        }
        catch
        {
            placed.Dispose();
            throw;
        }
        return placed;
    }

    /// <summary>規則 3 の折りたたみ。ds>16 のときは先にソースを 1/2 の箱形縮小で縮める(中間は RGBA16F。キャッシュに置く)。</summary>
    private GpuTexture Fold(RenderContext ctx, LayerPlan plan)
    {
        var src = Source(ctx, plan.Source);
        var input = src.Texture;
        var premultiplied = false;
        var texScale = 1.0;
        if (!src.Uniform)
        {
            var m = plan.Fold.HalvingCount;
            for (var k = 1; k <= m; k++)
            {
                var key = Key("half", plan.Source.Id, Num(k));
                var level = _cache.Get(key) ?? _cache.Add(key, Half(ctx, input, premultiplied));
                input = level;
                premultiplied = true;
                texScale /= 2;
            }
        }
        var target = ctx.CreateWorking(plan.Fold.OutW, plan.Fold.OutH);
        try
        {
            ctx.Draw(FoldShader, target, [input], ShapeFold.Constants(plan.Fold, texScale, premultiplied, src.Uniform));
        }
        catch
        {
            target.Dispose();
            throw;
        }
        return target;
    }

    private static GpuTexture Half(RenderContext ctx, GpuTexture input, bool premultiplied)
    {
        var target = ctx.CreateWorking((input.Width + 1) / 2, (input.Height + 1) / 2);
        try
        {
            var constants = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(constants, premultiplied ? 1u : 0u);
            ctx.Draw(BoxHalfShader, target, [input], constants);
        }
        catch
        {
            target.Dispose();
            throw;
        }
        return target;
    }

    /// <summary>
    /// GPU に置いたソース(規則 2)。image は SHA-256 ごとに 1 つの R8G8B8A8_UNORM_SRGB を共有し、empty は塗りの色の 1×1 で置く。
    /// この描画で使わなかったソースは、描画のあとで放す。
    /// </summary>
    private SourceTexture Source(RenderContext ctx, SourceRef source)
    {
        _usedSources.Add(source.Id);
        if (_sourceTextures.TryGetValue(source.Id, out var t)) return t;
        if (source.Sha256 is { } sha)
        {
            var im = (_sources.Find(sha) ?? throw new KeyNotFoundException("文書が参照する画像が ISourceImages に無い")).Validate();
            t = new SourceTexture(ctx.UploadSrgb8(im.Rgba, im.Width, im.Height, im.Width * 4), Uniform: false);
        }
        else
        {
            ShapeFold.TryParseRgba(source.Fill, out var r, out var g, out var b, out var a);
            t = new SourceTexture(ctx.UploadSrgb8([r, g, b, a], 1, 1, 4), Uniform: true);
        }
        _sourceTextures[source.Id] = t;
        return t;
    }

    private void ReleaseSources(bool all)
    {
        foreach (var id in _sourceTextures.Keys.ToArray())
        {
            if (!all && _usedSources.Contains(id)) continue;
            _sourceTextures[id].Texture.Dispose();
            _sourceTextures.Remove(id);
        }
    }

    private static byte[] PlaceConstants(LayerLayout ll)
    {
        var bytes = new byte[32];
        var s = bytes.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, ll.Frame.Qx);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], ll.Frame.Qy);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], ll.Frame.W);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], ll.Frame.H);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], ll.Shrink.N >= 1 ? 1u : 0u);
        return bytes;
    }

    private static byte[] BlendConstants(Layer layer)
    {
        var bytes = new byte[32];
        var s = bytes.AsSpan();
        BinaryPrimitives.WriteSingleLittleEndian(s, (float)(Math.Clamp(layer.Opacity, 0, 100) / 100.0));
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], BlendIndex(layer.Blend));
        return bytes;
    }

    /// <summary>layer_blend.ps の Mode.x(normal 0・multiply 1・screen 2・overlay 3・softlight 4・add 5)。</summary>
    internal static uint BlendIndex(BlendMode b) => b switch
    {
        BlendMode.Normal => 0u,
        BlendMode.Multiply => 1u,
        BlendMode.Screen => 2u,
        BlendMode.Overlay => 3u,
        BlendMode.SoftLight => 4u,
        _ => 5u,
    };

    // ================================================================ キー

    private static string Key(string kind, params string[] parts)
    {
        var sb = new StringBuilder(kind);
        foreach (var p in parts)
        {
            sb.Append('|').Append(p.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(p);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Describe(StackEntry? e) =>
        e is null ? "-" : e.Id + "{" + DescribeValues(e.Params.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)) + "}";

    private static string DescribeValues(IReadOnlyDictionary<string, ParamValue> values)
    {
        var sb = new StringBuilder();
        foreach (var key in values.Keys.Order(StringComparer.Ordinal))
        {
            sb.Append(key).Append('=').Append(Describe(values[key])).Append(';');
        }
        return sb.ToString();
    }

    private static string Describe(ParamValue v) => v switch
    {
        NumberValue n => "n" + Num(n.Value),
        BoolValue b => b.Value ? "b1" : "b0",
        StringValue s => "s" + Num(s.Value.Length) + ":" + s.Value,
        PointValue p => "p" + Num(p.X) + "," + Num(p.Y),
        RectValue r => "r" + Num(r.L) + "," + Num(r.T) + "," + Num(r.R) + "," + Num(r.B),
        _ => "?",
    };

    /// <summary>キャッシュと GPU に置いたソースを、専用スレッドの上で放す。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.DeviceReleasing -= ReleaseDeviceResources;
        try
        {
            _service.InvokeCleanupAsync(ReleaseDeviceResources).GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException)
        {
            // RenderService が先に破棄されたときは、デバイスと一緒に放されている
        }
    }
}
