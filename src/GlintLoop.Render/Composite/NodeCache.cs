// 描画のノードのキャッシュ(§9.7 の規則 5)。キーは上流のキーとノードの中身のハッシュで、値は GPU のテクスチャ(RGBA16F)。
// GPU メモリで上限(既定 512 MB)を持ち、超えたら最後に使った順の古いものから解放する。描画の途中で使ったノードは解放しない。
// 描画の専用スレッドだけで使う(INV-5)。キャッシュに入れたテクスチャはキャッシュのもので、呼び手は Dispose しない。
namespace GlintLoop.Render.Composite;

internal sealed class NodeCache : IDisposable
{
    private sealed class Node(string key, GpuTexture texture, long bytes)
    {
        public string Key { get; } = key;
        public GpuTexture Texture { get; } = texture;
        public long Bytes { get; } = bytes;
        public LinkedListNode<Node>? Lru { get; set; }
        public long Epoch { get; set; }
    }

    private readonly Dictionary<string, Node> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Node> _lru = new(); // 先頭が最も古い
    private long _epoch = 1;

    public NodeCache(long limitBytes) => LimitBytes = limitBytes;

    public long LimitBytes { get; }

    public long Bytes { get; private set; }

    public int Count => _map.Count;

    /// <summary>作ったノードの数(テストと測定用)。</summary>
    public int Computed { get; private set; }

    /// <summary>キャッシュから使ったノードの数(テストと測定用)。</summary>
    public int Reused { get; private set; }

    /// <summary>RGBA16F の 1 画素のバイト数。</summary>
    public static long BytesOf(GpuTexture t) => (long)t.Width * t.Height * 8;

    public bool Contains(string key) => _map.ContainsKey(key);

    /// <summary>あれば返し、最も新しく使ったものにする(この描画の間は解放しない)。</summary>
    public GpuTexture? Get(string key)
    {
        if (!_map.TryGetValue(key, out var node)) return null;
        Touch(node);
        Reused++;
        return node.Texture;
    }

    /// <summary>作ったノードを入れる(テクスチャの持ち主はキャッシュになる)。同じキーがあれば古い方を放す。</summary>
    public GpuTexture Add(string key, GpuTexture texture)
    {
        if (_map.Remove(key, out var old))
        {
            _lru.Remove(old.Lru!);
            Bytes -= old.Bytes;
            if (!ReferenceEquals(old.Texture, texture)) old.Texture.Dispose();
        }
        var node = new Node(key, texture, BytesOf(texture));
        node.Lru = _lru.AddLast(node);
        node.Epoch = _epoch;
        _map[key] = node;
        Bytes += node.Bytes;
        Computed++;
        Evict();
        return texture;
    }

    /// <summary>1 回の描画が終わったとき。使用中の印を外し、上限まで古いものから解放する。</summary>
    public void EndRender()
    {
        _epoch++;
        Evict();
    }

    private void Touch(Node node)
    {
        _lru.Remove(node.Lru!);
        node.Lru = _lru.AddLast(node);
        node.Epoch = _epoch;
    }

    private void Evict()
    {
        var n = _lru.First;
        while (Bytes > LimitBytes && n is not null)
        {
            var next = n.Next;
            if (n.Value.Epoch != _epoch)
            {
                _lru.Remove(n);
                _map.Remove(n.Value.Key);
                Bytes -= n.Value.Bytes;
                n.Value.Texture.Dispose();
            }
            n = next;
        }
    }

    public void Clear()
    {
        foreach (var node in _map.Values) node.Texture.Dispose();
        _map.Clear();
        _lru.Clear();
        Bytes = 0;
    }

    public void Dispose() => Clear();
}
