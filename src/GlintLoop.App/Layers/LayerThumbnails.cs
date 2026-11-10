// レイヤーの行のサムネイル(FR-55・§9.7 の規則 14): そのレイヤー 1 枚を土台にした文書(SingleLayerView)を、長辺 64 px(元より大きくしない)で描く。
// 絵に効く項目(形・ルック・仕上げのステージとソース)だけから作ったキーで覚え、配置・不透明度・ブレンド・表示・名前・モーションの変更では作り直さない。
// キーが変わった行は、最後の変更の 300 ms 後に 1 回だけ描き直す(タイマーは ITimerFactory。テストは偽の時計で進める)。できるまでは行が単色の枠を出す。
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Platform;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Layers;

/// <summary>できたサムネイル。Pixels は描画の結果(Pbgra32、Width×Height×4)。Bitmap は画面に出す凍結済みの画像。</summary>
public sealed record LayerThumbnail(string Key, int Width, int Height, byte[] Pixels, BitmapSource Bitmap);

public sealed class LayerThumbnails : IDisposable
{
    /// <summary>変更から作り直しまでの時間(ms。FR-55)。</summary>
    public const int DelayMs = 300;

    private readonly IEffectCatalog _catalog;
    private readonly ITimerFactory _timers;
    private readonly Func<SessionDocument?> _document;
    private readonly Task<DocumentRenderer>? _renderer;
    private readonly Dictionary<string, LayerThumbnail> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly List<string?> _slotKeys = [];
    private readonly Dictionary<int, IOneShotTimer> _slotTimers = [];
    private bool _disposed;

    /// <param name="document">いまの文書を返す(UI スレッドで呼ぶ)。</param>
    /// <param name="renderer">共有の文書の描画(GPU を使わないテストでは null。そのときは単色の枠のまま)。</param>
    public LayerThumbnails(IEffectCatalog catalog, ITimerFactory timers, Func<SessionDocument?> document, Task<DocumentRenderer>? renderer)
    {
        _catalog = catalog;
        _timers = timers;
        _document = document;
        _renderer = renderer;
    }

    /// <summary>サムネイルが 1 つできたとき(UI スレッドで上がる)。</summary>
    public event Action? Updated;

    /// <summary>サムネイルを描けなかったとき(UI スレッドで上がる)。</summary>
    public event Action<Exception>? Failed;

    /// <summary>描き始めた回数(テストが「作り直しが起きない」「1 回だけ」を数える)。</summary>
    public int RenderCount { get; private set; }

    /// <summary>描きかけの数。</summary>
    public int Pending => _inFlight.Count;

    /// <summary>
    /// サムネイルのキー: 規則 14 の単体の描画の文書から、絵に効かない項目(名前・saltCounter・assets・時間・出力設定)と
    /// モーションのステージの行を除いたものの JSON。配置・不透明度・ブレンド・表示は SingleLayerView が既定に直している。
    /// </summary>
    public static string KeyOf(SessionDocument doc, int layerIndex, IEffectCatalog catalog)
    {
        var single = SingleLayerView.Of(doc, layerIndex);
        var l = single.Layers[0];
        var stack = l.Stack.Where(e => catalog.Find(e.Id)?.Stage != EffectStage.Motion).ToArray();
        var keyDoc = single with
        {
            SaltCounter = 0,
            Assets = [],
            Time = TimeSettings.Default,
            Output = OutputSettings.Default,
            Layers = [l with { Name = "", Stack = stack }],
        };
        return DocumentJson.Write(keyDoc);
    }

    /// <summary>番号 layerIndex の行のいまのサムネイル(まだできていなければ null)。</summary>
    public LayerThumbnail? Get(int layerIndex) =>
        layerIndex >= 0 && layerIndex < _slotKeys.Count && _slotKeys[layerIndex] is { } k && _cache.TryGetValue(k, out var t) ? t : null;

    /// <summary>文書が変わったときに呼ぶ(UI スレッド)。キーが変わった行のタイマーを 300 ms で始め直す。</summary>
    public void Update(SessionDocument? doc)
    {
        if (_disposed) return;
        var count = doc?.Layers.Count ?? 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = KeyOf(doc!, i, _catalog);
            used.Add(key);
            if (i >= _slotKeys.Count) _slotKeys.Add(null);
            if (_slotKeys[i] == key) continue;
            _slotKeys[i] = key;
            if (_cache.ContainsKey(key)) StopSlot(i);
            else RestartSlot(i);
        }
        for (var i = _slotKeys.Count - 1; i >= count; i--)
        {
            StopSlot(i);
            _slotKeys.RemoveAt(i);
        }
        foreach (var k in _cache.Keys.ToList())
        {
            if (!used.Contains(k)) _cache.Remove(k);
        }
    }

    private void RestartSlot(int i)
    {
        if (!_slotTimers.TryGetValue(i, out var t))
        {
            t = _timers.Create();
            var slot = i;
            t.Elapsed += () => OnSlotElapsed(slot);
            _slotTimers[i] = t;
        }
        t.Stop();
        t.Start(TimeSpan.FromMilliseconds(DelayMs));
    }

    private void StopSlot(int i)
    {
        if (_slotTimers.TryGetValue(i, out var t)) t.Stop();
    }

    private async void OnSlotElapsed(int slot)
    {
        if (_disposed || _renderer is null) return;
        var doc = _document();
        if (doc is null || slot >= doc.Layers.Count) return;
        var key = KeyOf(doc, slot, _catalog);
        if (_cache.ContainsKey(key) || !_inFlight.Add(key)) return;
        RenderCount++;
        try
        {
            var renderer = await _renderer;
            // 規則 14: 行のサムネイルはモーションステージを通さない(Q-B: 文書の側で外す)
            var single = SingleLayerView.Of(doc, slot, _catalog);
            var lout = CompositeGeometry.CappedLongSide(renderer.CompositeNative(single), CompositeGeometry.RowThumbnailLongSide);
            var image = await renderer.RenderAsync(single, lout, DocumentPixelFormat.Pbgra32, priority: RenderPriority.Thumbnail);
            if (_disposed) return;
            var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Pixels, image.Width * 4);
            bmp.Freeze();
            // いまも使っているキーのときだけ覚える(描いている間に変わったものは、次のタイマーで描き直す)
            if (_slotKeys.Contains(key)) _cache[key] = new LayerThumbnail(key, image.Width, image.Height, image.Pixels, bmp);
        }
        catch (Exception e)
        {
            // 閉じたあとの失敗(破棄で失敗になった依頼)は捨てる。async void の外へ出すと処理されない例外になる(FR-104)
            // 描けなかったときは単色の枠のまま。知らせるのは受け手(E-41)
            if (!_disposed) Failed?.Invoke(e);
        }
        finally
        {
            _inFlight.Remove(key);
        }
        if (!_disposed) Updated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in _slotTimers.Values) t.Dispose();
        _slotTimers.Clear();
    }
}
