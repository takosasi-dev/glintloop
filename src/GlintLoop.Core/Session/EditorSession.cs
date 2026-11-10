// 文書の編集状態の唯一の持ち主(§2 の EditorSession)。編集の操作・Undo/Redo の履歴(FR-30・D-36)・未渡しの状態(FR-13)を持つ。
// 操作はどれも新しい不変の文書を作って Commit し、JSON が変わったときだけ履歴に 1 件を積む(選択だけの変更は積まない)。
// ソースの情報は SHA-256 ごとに 1 つ持ち、現在の文書か履歴のどれかが参照する間だけ保持する。
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Notices;

namespace GlintLoop.Core.Session;

public sealed partial class EditorSession
{
    /// <summary>Undo できる件数(FR-30)。</summary>
    public const int DefaultMaxUndo = 100;

    /// <summary>現在の文書が使わず履歴だけが参照するソースのバイト数の上限(FR-30・Q-23)。</summary>
    public const long DefaultHistorySourceBytesLimit = 300_000_000;

    private sealed record Entry(SessionDocument Doc, string Json, IReadOnlySet<string> Refs);

    private readonly IEffectCatalog _catalog;
    private readonly ISeedSource _seeds;
    private readonly ILogSink _log;
    private readonly int _maxUndo;
    private readonly long _historySourceLimit;
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, SourceInfo> _sources = new(StringComparer.Ordinal);
    private int _cur = -1;
    private int _batchDepth;
    private bool _batchPushed;

    public EditorSession(
        IEffectCatalog catalog,
        ISeedSource? seeds = null,
        ILogSink? log = null,
        int maxUndo = DefaultMaxUndo,
        long historySourceBytesLimit = DefaultHistorySourceBytesLimit)
    {
        _catalog = catalog;
        _seeds = seeds ?? SystemSeedSource.Instance;
        _log = log ?? NullLogSink.Instance;
        _maxUndo = maxUndo;
        _historySourceLimit = historySourceBytesLimit;
    }

    /// <summary>
    /// 取り込みと空のキャンバスで作る新しい文書の出力設定を返す関数(§9.11 の 2 の lastExport)。null なら既定の出力設定。
    /// 取り込みは UI スレッドの外から呼ぶことがある。.glint を開くときは使わない(文書の値をそのまま使う)。
    /// </summary>
    public Func<OutputSettings>? NewDocumentOutput { get; set; }

    /// <summary>ソースを保持しなくなったとき(履歴からも現在の文書からも参照されなくなった)に、その SHA-256 で呼ぶ。</summary>
    public event Action<string>? SourceReleased;

    public IEffectCatalog Catalog => _catalog;

    /// <summary>文書があるか(false はウェルカム画面)。</summary>
    public bool HasDocument => _cur >= 0;

    public SessionDocument? Document => HasDocument ? _entries[_cur].Doc : null;

    /// <summary>現在の文書の JSON(§9.11 の 3)。</summary>
    public string? Json => HasDocument ? _entries[_cur].Json : null;

    public Target Selected => Document?.Selected ?? Target.All;

    /// <summary>未渡し(FR-13)。変更・Undo・Redo で true、渡した・新しい文書で false。</summary>
    public bool IsUndelivered { get; private set; }

    /// <summary>確認ダイアログの表示中(この間の取り込み・新規・開くは無視する。E-20)。</summary>
    public bool IsConfirming { get; private set; }

    public bool CanUndo => _cur > 0;

    public bool CanRedo => HasDocument && _cur < _entries.Count - 1;

    /// <summary>Undo できる件数。</summary>
    public int UndoCount => Math.Max(0, _cur);

    /// <summary>Redo できる件数。</summary>
    public int RedoCount => HasDocument ? _entries.Count - 1 - _cur : 0;

    /// <summary>保持しているソース(現在の文書か履歴が参照するもの)。</summary>
    public IReadOnlyDictionary<string, SourceInfo> Sources => _sources;

    /// <summary>現在の文書が使わず、履歴だけが参照するソースのバイト数の合計。</summary>
    public long HistoryOnlySourceBytes => HasDocument ? HistoryOnlyBytes() : 0;

    // ================================================================ 大きさの読み出し

    /// <summary>ソースの幅と高さ。</summary>
    public SizeD SourceSize(string sha256)
    {
        var s = _sources[sha256];
        return new SizeD(s.Width, s.Height);
    }

    /// <summary>合成のネイティブ寸法(Wc, Hc)。</summary>
    public SizeD CompositeNative() => CompositeGeometry.CompositeNative(RequireDoc(), SourceSize);

    /// <summary>現在の文書の配置を、長辺 lout で計算する(§9.7 の規則 1 と 11)。</summary>
    public CompositeLayout Layout(int lout, int maxTextureDim) =>
        CompositeGeometry.Layout(RequireDoc(), SourceSize, lout, maxTextureDim);

    /// <summary>画面のプロキシの配置(Lout=min(2048, ceil(max(Wc, Hc))))。</summary>
    public CompositeLayout ProxyLayout(int maxTextureDim) =>
        Layout(CompositeGeometry.ProxyLongSide(CompositeNative()), maxTextureDim);

    // ================================================================ 取り込み(FR-42・FR-57)

    /// <summary>1 回の取り込みの上限の判定を、いまの文書から始める(デコードの前に使う)。</summary>
    public ImportBudget BeginImportBudget()
    {
        var doc = Document;
        if (doc is null) return new ImportBudget(0, 0, 0, Array.Empty<string>());
        long bytes = 0, pixels = 0;
        foreach (var a in doc.Assets)
        {
            bytes += a.Bytes;
            if (_sources.TryGetValue(a.Sha256, out var s)) pixels += s.Pixels;
        }
        return new ImportBudget(doc.Layers.Count, bytes, pixels, doc.Assets.Select(a => a.Sha256));
    }

    /// <summary>
    /// 読めた画像の列を 1 回の操作で足す(FR-42)。文書が無ければ新しい文書を作って先頭を土台にし(履歴を空にする)、
    /// あれば最前面に足して最後に足したものを選ぶ(Undo の 1 件)。32 枚を超える分は足さない。
    /// </summary>
    public ImportAddResult AddImportedImages(IReadOnlyList<ImportedSource> images)
    {
        if (images.Count == 0 || IsConfirming) return new ImportAddResult(0, false, null);
        using var _ = Track();
        foreach (var im in images)
        {
            if (!DocumentJson.IsSha256(im.Sha256)) throw new ArgumentException("sha256 が小文字の 16 進 64 桁でない", nameof(images));
            if (!DocumentJson.AssetExts.Contains(im.Ext)) throw new ArgumentException("ext が列挙に無い", nameof(images));
            if (im.Width < 1 || im.Height < 1 || im.Bytes < 1) throw new ArgumentException("画像の大きさかバイト数が 0", nameof(images));
        }

        if (!HasDocument)
        {
            var take = Math.Min(DocumentRules.MaxLayers, images.Count);
            var sources = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
            var layers = new List<Layer>(take);
            var first = images[0];
            for (var i = 0; i < take; i++)
            {
                var im = images[i];
                sources.TryAdd(im.Sha256, new SourceInfo(im.Sha256, im.Ext, im.Bytes, im.Width, im.Height));
                var scale = i == 0 ? 100 : DocumentRules.ImportScale(first.Width, first.Height, im.Width, im.Height);
                layers.Add(ImageLayer(im, scale));
            }
            var doc = NewDocument(layers);
            StartNewDocument(doc, sources.Values);
            var notice = take > 1 ? Notice.Of(NoticeKeys.LayerAdded, NoticeLevel.Info) : null;
            return new ImportAddResult(take, true, notice);
        }

        var cur = RequireDoc();
        var available = DocumentRules.MaxLayers - cur.Layers.Count;
        if (available <= 0) return new ImportAddResult(0, false, Notice.Of(NoticeKeys.LayerFull, NoticeLevel.Warn));
        var count = Math.Min(available, images.Count);
        var native = CompositeNative();
        var list = cur.Layers.ToList();
        for (var i = 0; i < count; i++)
        {
            var im = images[i];
            _sources.TryAdd(im.Sha256, new SourceInfo(im.Sha256, im.Ext, im.Bytes, im.Width, im.Height));
            list.Add(ImageLayer(im, DocumentRules.ImportScale(native.W, native.H, im.Width, im.Height)));
        }
        Commit(cur with { Layers = list, Selected = Target.OfLayer(list.Count - 1) });
        return new ImportAddResult(count, false, Notice.Of(NoticeKeys.LayerAdded, NoticeLevel.Info));
    }

    private static Layer ImageLayer(ImportedSource im, double scale) => new(
        DocumentRules.ImportName(im.Name), LayerKind.Image, im.Sha256, null, null, true, 100, BlendMode.Normal,
        new Placement(0, 0, scale), 100, Array.Empty<StackEntry>());

    private SessionDocument NewDocument(IReadOnlyList<Layer> layers) => new(
        _seeds.NextSeed(), 0, TimeSettings.Default, Target.OfLayer(layers.Count - 1), Array.Empty<AssetEntry>(), layers,
        GlobalStack.Empty, NewDocumentOutput?.Invoke() ?? OutputSettings.Default);

    /// <summary>空のキャンバス(FR-44)。幅と高さは 16〜4096 に、塗りは不正なら不透明の白にして、新しい文書を始める。</summary>
    public void NewEmptyCanvas(int width, int height, string? fill = null)
    {
        var w = Math.Clamp(width, DocumentRules.MinCanvasSide, DocumentRules.MaxEmptySide);
        var h = Math.Clamp(height, DocumentRules.MinCanvasSide, DocumentRules.MaxEmptySide);
        var f = ColorText.TryNormalizeRgba(fill, out var nf) ? nf : ColorText.OpaqueWhite;
        var layer = new Layer(DocumentRules.CanvasName, LayerKind.Empty, null, new EmptySize(w, h), f, true, 100,
            BlendMode.Normal, Placement.Identity, 100, Array.Empty<StackEntry>());
        StartNewDocument(NewDocument([layer]), Array.Empty<SourceInfo>());
    }

    /// <summary>
    /// 読み込んだ文書(.glint を開く。Phase 5)で始める。sources は文書の image のレイヤーが参照するソースの全部。
    /// </summary>
    public void OpenDocument(SessionDocument doc, IReadOnlyList<SourceInfo> sources)
    {
        var map = sources.ToDictionary(s => s.Sha256, StringComparer.Ordinal);
        foreach (var l in doc.Layers)
        {
            if (l.Kind == LayerKind.Image && (l.Asset is null || !map.ContainsKey(l.Asset)))
                throw new ArgumentException("文書が参照するソースが無い", nameof(sources));
        }
        StartNewDocument(doc, map.Values);
    }

    // ================================================================ 新規・置き換え(FR-13・FR-43)

    /// <summary>新規・開くで置き換える前に確認が要るか(文書があって未渡しのとき)。</summary>
    public bool NeedsReplaceConfirmation => HasDocument && IsUndelivered;

    /// <summary>確認ダイアログのボタン(FR-13)。「保存して置き換え」の保存は画面の側(ProjectCoordinator)が行う。</summary>
    public IReadOnlyList<ReplaceChoice> ReplaceButtons { get; } = [ReplaceChoice.SaveAndReplace, ReplaceChoice.ReplaceWithoutSaving, ReplaceChoice.Cancel];

    /// <summary>新規(Ctrl+N。FR-43)。文書が無ければ何もしない。未渡しなら確認し、置き換えるなら文書を閉じて履歴を空にする。</summary>
    public ReplaceResult RequestNew(IReplaceConfirmation dialog)
    {
        if (!HasDocument || IsConfirming) return ReplaceResult.Ignored;
        if (!Confirm(dialog)) return ReplaceResult.Cancelled;
        CloseDocument();
        return ReplaceResult.Replaced;
    }

    /// <summary>.glint を開く操作の置き換え(FR-13)。文書が無ければ確認せずに開く。</summary>
    public ReplaceResult RequestOpen(SessionDocument doc, IReadOnlyList<SourceInfo> sources, IReplaceConfirmation dialog)
    {
        if (IsConfirming) return ReplaceResult.Ignored;
        if (HasDocument && !Confirm(dialog)) return ReplaceResult.Cancelled;
        OpenDocument(doc, sources);
        return ReplaceResult.Replaced;
    }

    private bool Confirm(IReplaceConfirmation dialog)
    {
        if (!IsUndelivered) return true;
        ReplaceChoice choice;
        IsConfirming = true;
        try
        {
            choice = dialog.Ask(ReplaceButtons, ReplaceChoice.Cancel);
        }
        finally
        {
            IsConfirming = false;
        }
        // セッションは保存できないので、「保存して置き換え」はここでは置き換えない。画面の経路は保存してから CloseDocument・OpenDocument を呼ぶ
        return choice == ReplaceChoice.ReplaceWithoutSaving;
    }

    /// <summary>文書を閉じてウェルカム画面に戻す。履歴とソースを空にする。</summary>
    public void CloseDocument()
    {
        using var _ = Track();
        _docGeneration++;
        var released = _sources.Keys.ToList();
        _entries.Clear();
        _sources.Clear();
        _cur = -1;
        _batchDepth = 0;
        _batchPushed = false;
        IsUndelivered = false;
        foreach (var sha in released) SourceReleased?.Invoke(sha);
    }

    /// <summary>コピー・ドラッグ・保存・プロジェクト保存が成功したことを知らせる(未渡しでなくなる)。</summary>
    public void MarkDelivered()
    {
        using var _ = Track();
        IsUndelivered = false;
    }

    // ================================================================ 履歴(FR-30)

    /// <summary>連続する変更(スライダーのつまみ・レイヤーの枠のドラッグなど)が始まった(true)・終わった(false)とき。入れ子の内側では鳴らない。</summary>
    public event Action<bool>? ContinuousChanged;

    /// <summary>連続する変更(スライダーのドラッグなど)をまとめ始める。EndContinuous までの変更は 1 件になる。</summary>
    public void BeginContinuous()
    {
        if (_batchDepth++ == 0)
        {
            _batchPushed = false;
            _batchNotified = false;
            ContinuousChanged?.Invoke(true);
        }
    }

    /// <summary>連続する変更を終える。変更が元に戻っていたときは、積んだ 1 件を捨てる。</summary>
    public void EndContinuous()
    {
        if (_batchDepth == 0) return;
        using var _ = Track();
        if (--_batchDepth > 0) return;
        if (_batchPushed && _cur > 0 && _entries[_cur].Json == _entries[_cur - 1].Json)
        {
            _entries.RemoveAt(_cur);
            _cur--;
        }
        _batchPushed = false;
        ContinuousChanged?.Invoke(false);
    }

    public bool Undo()
    {
        using var _ = Track();
        EndAllContinuous();
        if (!CanUndo) return false;
        _cur--;
        IsUndelivered = true;
        return true;
    }

    public bool Redo()
    {
        using var _ = Track();
        EndAllContinuous();
        if (!CanRedo) return false;
        _cur++;
        IsUndelivered = true;
        return true;
    }

    private void EndAllContinuous()
    {
        while (_batchDepth > 0) EndContinuous();
    }

    /// <summary>
    /// 選んでいる対象だけを変える(FR-51・FR-46)。履歴に積まず、いまの件の selected を書き換える。未渡しにしない。
    /// </summary>
    public bool Select(Target target)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        if (doc.Selected == target) return false;
        using var _ = Track();
        var next = doc with { Selected = target };
        _entries[_cur] = new Entry(next, DocumentJson.Write(next), _entries[_cur].Refs);
        return true;
    }

    private void StartNewDocument(SessionDocument doc, IEnumerable<SourceInfo> sources)
    {
        using var _ = Track();
        _docGeneration++;
        var old = _sources.Keys.ToList();
        _entries.Clear();
        _sources.Clear();
        foreach (var s in sources) _sources[s.Sha256] = s;
        doc = DocumentRules.Normalize(doc, AssetMap());
        _entries.Add(new Entry(doc, DocumentJson.Write(doc), Refs(doc)));
        _cur = 0;
        _batchDepth = 0;
        _batchPushed = false;
        IsUndelivered = false;
        CollectSources(old);
    }

    private EditOutcome Commit(SessionDocument next, Notice? notice = null, int? row = null)
    {
        next = DocumentRules.Normalize(next, AssetMap());
        var json = DocumentJson.Write(next);
        if (json == _entries[_cur].Json) return new EditOutcome(false, notice, row);
        using var _ = Track();
        var entry = new Entry(next, json, Refs(next));
        if (_batchDepth > 0 && _batchPushed)
        {
            _entries[_cur] = entry;
        }
        else
        {
            if (_cur < _entries.Count - 1) _entries.RemoveRange(_cur + 1, _entries.Count - _cur - 1);
            _entries.Add(entry);
            _cur++;
            if (_batchDepth > 0) _batchPushed = true;
            while (_entries.Count > _maxUndo + 1)
            {
                _entries.RemoveAt(0);
                _cur--;
            }
        }
        IsUndelivered = true;
        TrimHistoryBySources();
        return new EditOutcome(true, notice, row);
    }

    private Dictionary<string, AssetEntry> AssetMap()
    {
        var map = new Dictionary<string, AssetEntry>(_sources.Count, StringComparer.Ordinal);
        foreach (var s in _sources.Values) map[s.Sha256] = new AssetEntry(s.Sha256, s.Ext, s.Bytes);
        return map;
    }

    private static IReadOnlySet<string> Refs(SessionDocument doc)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in doc.Layers)
        {
            if (l.Kind == LayerKind.Image && l.Asset is not null) set.Add(l.Asset);
        }
        return set;
    }

    private long HistoryOnlyBytes()
    {
        var current = _entries[_cur].Refs;
        var only = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < _entries.Count; i++)
        {
            if (i == _cur) continue;
            foreach (var r in _entries[i].Refs)
            {
                if (!current.Contains(r)) only.Add(r);
            }
        }
        long sum = 0;
        foreach (var r in only)
        {
            if (_sources.TryGetValue(r, out var s)) sum += s.Bytes;
        }
        return sum;
    }

    /// <summary>
    /// 履歴だけが参照するソースのバイト数の合計が上限を超える間、最も古い件から捨てる(FR-30)。
    /// 最も古い件が現在の件のとき(Undo で戻りきったとき)は、最も新しい Redo の件から捨てる(仮の決め)。
    /// </summary>
    private void TrimHistoryBySources()
    {
        var before = _sources.Keys.ToList();
        while (_entries.Count > 1 && HistoryOnlyBytes() > _historySourceLimit)
        {
            if (_cur > 0)
            {
                _entries.RemoveAt(0);
                _cur--;
            }
            else
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
        CollectSources(before);
    }

    /// <summary>どの件も参照しないソースを捨て、candidates のうち保持しなくなったものを知らせる。</summary>
    private void CollectSources(IEnumerable<string> candidates)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _entries) used.UnionWith(e.Refs);
        foreach (var sha in _sources.Keys.ToList())
        {
            if (!used.Contains(sha)) _sources.Remove(sha);
        }
        foreach (var sha in candidates)
        {
            if (!_sources.ContainsKey(sha)) SourceReleased?.Invoke(sha);
        }
    }

    private SessionDocument RequireDoc() => Document ?? throw new InvalidOperationException("文書が無い");

    private static void CheckTarget(SessionDocument doc, Target t)
    {
        if (!t.IsAll && (t.LayerIndex < 0 || t.LayerIndex >= doc.Layers.Count))
            throw new ArgumentOutOfRangeException(nameof(t), "レイヤーの番号が範囲外");
    }

    private static void CheckLayer(SessionDocument doc, int index)
    {
        if (index < 0 || index >= doc.Layers.Count) throw new ArgumentOutOfRangeException(nameof(index), "レイヤーの番号が範囲外");
    }

    // ================================================================ レイヤーの操作(FR-45〜FR-54)

    /// <summary>空のレイヤー(FR-45)。合成と同じ大きさ(round(Wc)・round(Hc) を 1〜4096)、塗りは透明で最前面に足して選ぶ。</summary>
    public EditOutcome AddEmptyLayer()
    {
        var doc = RequireDoc();
        if (doc.Layers.Count >= DocumentRules.MaxLayers) return EditOutcome.Rejected(NoticeKeys.LayerFull);
        var native = CompositeNative();
        var w = (int)DocumentRules.Clamp(Numerics.MathX.RoundHalfUp(native.W), DocumentRules.MinEmptySide, DocumentRules.MaxEmptySide);
        var h = (int)DocumentRules.Clamp(Numerics.MathX.RoundHalfUp(native.H), DocumentRules.MinEmptySide, DocumentRules.MaxEmptySide);
        var layer = new Layer(DocumentRules.DefaultEmptyName, LayerKind.Empty, null, new EmptySize(w, h), ColorText.Transparent,
            true, 100, BlendMode.Normal, Placement.Identity, 100, Array.Empty<StackEntry>());
        var list = doc.Layers.Append(layer).ToList();
        return Commit(doc with { Layers = list, Selected = Target.OfLayer(list.Count - 1) });
    }

    /// <summary>
    /// 削除(FR-53)。最後の 1 枚は消せない。土台を消すと上のレイヤーが新しい土台になる(配置とブレンドは Normalize が直す)。
    /// 選択は削除した位置にあったレイヤー(最前面を消したときは新しい最前面)へ移る。
    /// </summary>
    public EditOutcome DeleteLayer(int index)
    {
        var doc = RequireDoc();
        CheckLayer(doc, index);
        if (doc.Layers.Count <= 1) return EditOutcome.Unchanged;
        var list = doc.Layers.ToList();
        list.RemoveAt(index);
        var sel = index < list.Count ? index : list.Count - 1;
        return Commit(doc with { Layers = list, Selected = Target.OfLayer(sel) });
    }

    /// <summary>複製(FR-53)。直後(1 つ手前)に、名前に「のコピー」を付け、スタックの行に新しい salt を与えたコピーを足して選ぶ。</summary>
    public EditOutcome DuplicateLayer(int index)
    {
        var doc = RequireDoc();
        CheckLayer(doc, index);
        if (doc.Layers.Count >= DocumentRules.MaxLayers) return EditOutcome.Rejected(NoticeKeys.LayerFull);
        var src = doc.Layers[index];
        var counter = doc.SaltCounter;
        var stack = src.Stack.Select(e => e with { Salt = ++counter }).ToArray();
        var copy = src with { Name = DocumentRules.CopyName(src.Name), Stack = stack };
        var list = doc.Layers.ToList();
        list.Insert(index + 1, copy);
        return Commit(doc with { Layers = list, SaltCounter = counter, Selected = Target.OfLayer(index + 1) });
    }

    /// <summary>
    /// 並べ替え(FR-52)。from のレイヤーを to の番号へ動かし、動かしたレイヤーを選ぶ。番号 0 へ来たレイヤーは
    /// 配置 (0, 0, 100)・ブレンド normal になる。
    /// </summary>
    public EditOutcome MoveLayer(int from, int to)
    {
        var doc = RequireDoc();
        CheckLayer(doc, from);
        CheckLayer(doc, to);
        if (from == to) return EditOutcome.Unchanged;
        var list = doc.Layers.ToList();
        var l = list[from];
        list.RemoveAt(from);
        list.Insert(to, l);
        return Commit(doc with { Layers = list, Selected = Target.OfLayer(to) });
    }

    public EditOutcome SetLayerVisible(int index, bool visible) => UpdateLayer(index, l => l with { Visible = visible });

    /// <summary>不透明度(0〜100 の整数。round して範囲に直す)。</summary>
    public EditOutcome SetLayerOpacity(int index, double opacity) =>
        double.IsNaN(opacity) ? EditOutcome.Unchanged : UpdateLayer(index, l => l with { Opacity = DocumentRules.RoundPercent(opacity) });

    /// <summary>ブレンド(FR-49)。土台は変えられない。</summary>
    public EditOutcome SetLayerBlend(int index, BlendMode blend) =>
        index == 0 ? EditOutcome.Unchanged : UpdateLayer(index, l => l with { Blend = blend });

    /// <summary>配置(FR-48)。x・y は小数第 2 位、scale は小数第 1 位に round して範囲に直す。土台は変えられない。</summary>
    public EditOutcome SetLayerPlacement(int index, double x, double y, double scale)
    {
        if (index == 0 || double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(scale)) return EditOutcome.Unchanged;
        var p = new Placement(Zero(DocumentRules.RoundPlace(x)), Zero(DocumentRules.RoundPlace(y)), DocumentRules.RoundScale(scale));
        return UpdateLayer(index, l => l with { Place = p });
    }

    /// <summary>名前(FR-54)。空と空白だけは反映しない。41 文字以上は先頭の 40 文字。</summary>
    public EditOutcome SetLayerName(int index, string name) =>
        string.IsNullOrWhiteSpace(name) ? EditOutcome.Unchanged : UpdateLayer(index, l => l with { Name = DocumentRules.Truncate(name) });

    /// <summary>empty のレイヤーの塗り(FR-48)。<c>#RRGGBBAA</c> でない値と image のレイヤーは反映しない。</summary>
    public EditOutcome SetLayerFill(int index, string fill)
    {
        if (!ColorText.TryNormalizeRgba(fill, out var f)) return EditOutcome.Unchanged;
        return UpdateLayer(index, l => l.Kind == LayerKind.Empty ? l with { Fill = f } : null);
    }

    /// <summary>ルックの強さ(FR-35。0〜100 の整数)。対象はレイヤーか全体。</summary>
    public EditOutcome SetLookStrength(Target target, double value)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        if (double.IsNaN(value)) return EditOutcome.Unchanged;
        var v = DocumentRules.RoundPercent(value);
        if (target.IsAll) return Commit(doc with { All = doc.All with { LookStrength = v } });
        return UpdateLayer(target.LayerIndex, l => l with { LookStrength = v });
    }

    private EditOutcome UpdateLayer(int index, Func<Layer, Layer?> update)
    {
        var doc = RequireDoc();
        CheckLayer(doc, index);
        var next = update(doc.Layers[index]);
        if (next is null) return EditOutcome.Unchanged;
        var list = doc.Layers.ToArray();
        list[index] = next;
        return Commit(doc with { Layers = list });
    }

    private static double Zero(double v) => v == 0 ? 0 : v;

    // ================================================================ スタックの操作(FR-22〜FR-32)

    /// <summary>
    /// エフェクトを既定値・origin=user・新しい salt で、そのステージの末尾に足す(FR-26)。回転・切り抜き・余白は形ステージの
    /// 先頭の固定の位置に入る。全体の回転・切り抜き・余白と、同じ固定の行の 2 個目は拒否する。65 個目は stack.full。
    /// Row は足した行の番号。
    /// </summary>
    public EditOutcome AddEffect(Target target, string effectId)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        var def = _catalog.Find(effectId);
        if (def is null) return EditOutcome.Unchanged;
        var stack = doc.StackOf(target);
        if (EffectSchema.IsFixedShape(effectId) && (target.IsAll || stack.Any(e => e.Id == effectId))) return EditOutcome.Unchanged;
        if (stack.Count >= EffectSchema.MaxStackEntries) return EditOutcome.Rejected(NoticeKeys.StackFull);
        var key = EffectSchema.OrderKey(def);
        var pos = 0;
        for (var i = 0; i < stack.Count; i++)
        {
            if (KeyOf(stack[i]) <= key) pos = i + 1;
        }
        var salt = doc.SaltCounter + 1;
        var entry = new StackEntry(def.Id, true, EffectOrigin.User, salt, 100, EffectSchema.DefaultParams(def));
        var list = stack.ToList();
        list.Insert(pos, entry);
        return Commit(WithStack(doc with { SaltCounter = salt }, target, list), null, pos);
    }

    public EditOutcome RemoveEffect(Target target, int row) => UpdateStack(target, row, (list, _) =>
    {
        list.RemoveAt(row);
        return list;
    });

    /// <summary>複製(FR-23)。元の行の直後に、同じパラメータ・origin=user・新しい salt で入れる。固定の 3 つは複製しない(仮の決め)。</summary>
    public EditOutcome DuplicateEffect(Target target, int row)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        var stack = doc.StackOf(target);
        CheckRow(stack, row);
        if (EffectSchema.IsFixedShape(stack[row].Id)) return EditOutcome.Unchanged;
        if (stack.Count >= EffectSchema.MaxStackEntries) return EditOutcome.Rejected(NoticeKeys.StackFull);
        var salt = doc.SaltCounter + 1;
        var list = stack.ToList();
        list.Insert(row + 1, stack[row] with { Origin = EffectOrigin.User, Salt = salt });
        return Commit(WithStack(doc with { SaltCounter = salt }, target, list), null, row + 1);
    }

    public EditOutcome SetEffectEnabled(Target target, int row, bool enabled) =>
        UpdateStack(target, row, (list, e) =>
        {
            list[row] = e with { Enabled = enabled };
            return list;
        });

    /// <summary>
    /// ステージの中の並べ替え(FR-25)。row の行を to の位置へ動かす。別のステージへの移動、固定の 3 つの移動、
    /// 固定の 3 つより上への移動は拒否する。
    /// </summary>
    public EditOutcome MoveEffect(Target target, int row, int to)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        var stack = doc.StackOf(target);
        CheckRow(stack, row);
        CheckRow(stack, to);
        if (row == to || EffectSchema.IsFixedShape(stack[row].Id)) return EditOutcome.Unchanged;
        var list = stack.ToList();
        var e = list[row];
        list.RemoveAt(row);
        list.Insert(to, e);
        for (var i = 1; i < list.Count; i++)
        {
            if (KeyOf(list[i - 1]) > KeyOf(list[i])) return EditOutcome.Unchanged;
        }
        return Commit(WithStack(doc, target, list), null, to);
    }

    /// <summary>
    /// パラメータの変更(FR-27)。範囲外は端に直し、型違いと NaN は反映しない。切り抜きの l+r・t+b は動かした辺を止め、
    /// levels の white−black<5 は動かした側を直す(§9.4)。
    /// </summary>
    public EditOutcome SetEffectParam(Target target, int row, string key, ParamValue value)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        var stack = doc.StackOf(target);
        CheckRow(stack, row);
        var e = stack[row];
        var def = _catalog.Find(e.Id);
        var p = def is null ? null : EffectSchema.FindParam(def, key);
        if (def is null || p is null) return EditOutcome.Unchanged;
        // 型違い・NaN・列挙に無い値・色の形でない値は、数として読めない入力と同じく反映しない(FR-27)。
        if (!SameType(p, value) || IsNaN(value)) return EditOutcome.Unchanged;
        if (p.Type == ParamType.Enum && (p.Options is null || !p.Options.Any(o => o.Value == ((StringValue)value).Value)))
            return EditOutcome.Unchanged;
        if (p.Type == ParamType.Color && !ColorText.TryNormalizeRgba(((StringValue)value).Value, out _)) return EditOutcome.Unchanged;
        var v = ParamRules.Normalize(p, value, out _);
        var values = e.Params.Select(kv => kv.Key == key ? new KeyValuePair<string, ParamValue>(key, v) : kv).ToArray();
        var fixedValues = ParamRules.ApplyConstraints(e.Id, values, key, e.Params);
        var list = stack.ToList();
        list[row] = e with { Params = fixedValues };
        return Commit(WithStack(doc, target, list));
    }

    /// <summary>強さ(FR-28。0〜100)。</summary>
    public EditOutcome SetEffectAmount(Target target, int row, double amount)
    {
        if (double.IsNaN(amount)) return EditOutcome.Unchanged;
        var a = DocumentRules.Clamp(amount, 0, 100);
        return UpdateStack(target, row, (list, e) =>
        {
            list[row] = e with { Amount = a };
            return list;
        });
    }

    /// <summary>エフェクトのリセット(FR-31)。すべてのパラメータを既定値に、amount を 100 に戻す。</summary>
    public EditOutcome ResetEffect(Target target, int row) => UpdateStack(target, row, (list, e) =>
    {
        var def = _catalog.Find(e.Id);
        if (def is null) return null;
        list[row] = e with { Amount = 100, Params = EffectSchema.DefaultParams(def) };
        return list;
    });

    /// <summary>
    /// すべてリセット(FR-32)。すべてのレイヤーと全体のスタックを空に、ルックの強さを 100 に、時間を既定に戻す。
    /// レイヤーの画像・配置・不透明度・ブレンド・表示・名前と、出力設定は変えない。
    /// </summary>
    public EditOutcome ResetAll()
    {
        var doc = RequireDoc();
        var layers = doc.Layers.Select(l => l with { LookStrength = 100, Stack = Array.Empty<StackEntry>() }).ToArray();
        return Commit(doc with { Layers = layers, All = GlobalStack.Empty, Time = TimeSettings.Default });
    }

    private EditOutcome UpdateStack(Target target, int row, Func<List<StackEntry>, StackEntry, List<StackEntry>?> update)
    {
        var doc = RequireDoc();
        CheckTarget(doc, target);
        var stack = doc.StackOf(target);
        CheckRow(stack, row);
        var list = update(stack.ToList(), stack[row]);
        if (list is null) return EditOutcome.Unchanged;
        return Commit(WithStack(doc, target, list));
    }

    private static SessionDocument WithStack(SessionDocument doc, Target target, IReadOnlyList<StackEntry> stack)
    {
        if (target.IsAll) return doc with { All = doc.All with { Stack = stack } };
        var layers = doc.Layers.ToArray();
        layers[target.LayerIndex] = layers[target.LayerIndex] with { Stack = stack };
        return doc with { Layers = layers };
    }

    private int KeyOf(StackEntry e)
    {
        var def = _catalog.Find(e.Id);
        return def is null ? int.MaxValue : EffectSchema.OrderKey(def);
    }

    private static void CheckRow(IReadOnlyList<StackEntry> stack, int row)
    {
        if (row < 0 || row >= stack.Count) throw new ArgumentOutOfRangeException(nameof(row), "スタックの行の番号が範囲外");
    }

    private static bool IsNaN(ParamValue v) => v switch
    {
        NumberValue n => double.IsNaN(n.Value),
        PointValue p => double.IsNaN(p.X) || double.IsNaN(p.Y),
        RectValue r => double.IsNaN(r.L) || double.IsNaN(r.T) || double.IsNaN(r.R) || double.IsNaN(r.B),
        _ => false,
    };

    private static bool SameType(ParamDef p, ParamValue v) => p.Type switch
    {
        ParamType.Float or ParamType.Int or ParamType.Angle => v is NumberValue,
        ParamType.Bool => v is BoolValue,
        ParamType.Enum or ParamType.Color or ParamType.Text or ParamType.Font => v is StringValue,
        ParamType.Point => v is PointValue,
        ParamType.Rect => v is RectValue,
        _ => false,
    };
}
