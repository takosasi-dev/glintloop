// 取り込みの行き先の口(IImportTarget)を EditorSession につなぐ。1 回の AddImages を AddImportedImages の 1 回にする(Undo の 1 件)。
// デコードした画素は描画が使うので SHA-256 ごとにここで持つ。文書に足す前に登録し(変更の通知の受け手がすぐ描けるように)、足されなかった分と、セッションが手放した(SourceReleased)ものを捨てる。取り込み元のパス(INV-2)も同じ間だけ覚える。
// 上限の判定に使う合計は、セッションの BeginImportBudget(現在の文書が参照する異なる画像)から読む。
using System.Collections.Concurrent;
using GlintLoop.Core.Session;

namespace GlintLoop.Codec.Import;

public sealed class SessionImportTarget : IImportTarget
{
    private readonly EditorSession _session;
    // 取り込みのスレッドが書き、UI と描画のスレッドが読むので、スレッドをまたいで使える辞書にする
    private readonly ConcurrentDictionary<string, ImportedImage> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _paths = new(StringComparer.Ordinal);

    public SessionImportTarget(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _session.SourceReleased += sha =>
        {
            _images.TryRemove(sha, out _);
            _paths.TryRemove(sha, out _);
        };
    }

    /// <summary>path が、セッションが保持するソース(どのレイヤーか、履歴だけが参照するものかを問わない)の取り込み元のファイルか(絶対パスに直し、大文字と小文字を区別しない。INV-2)。</summary>
    public bool IsImportedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return _paths.Values.Any(set => set.ContainsKey(full));
    }

    public EditorSession Session => _session;

    /// <summary>セッションが保持するソースのデコード済みの画素(SHA-256 → 画像)。描画がテクスチャを作るときに読む。</summary>
    public IReadOnlyDictionary<string, ImportedImage> Images => _images;

    public bool HasDocument => _session.HasDocument;

    public int LayerCount => _session.Document?.Layers.Count ?? 0;

    public long SourceBytesTotal => _session.BeginImportBudget().Bytes;

    public long SourcePixelsTotal => _session.BeginImportBudget().Pixels;

    public bool ContainsSource(string sha256)
    {
        var doc = _session.Document;
        if (doc is null) return false;
        foreach (var a in doc.Assets)
        {
            if (a.Sha256 == sha256) return true;
        }
        return false;
    }

    public int AddImages(IReadOnlyList<ImportedLayer> layers, ImportVia via)
    {
        var list = new List<ImportedSource>(layers.Count);
        foreach (var l in layers)
        {
            var im = l.Image;
            list.Add(new ImportedSource(im.Sha256, im.Ext, im.ByteCount, im.Width, im.Height, l.Name));
        }
        // 画素を先に登録する。AddImportedImages の中で上がる変更の通知の受け手が、新しい画像をすぐ読めるようにするため
        var fresh = new List<string>();
        foreach (var l in layers)
        {
            if (_images.TryAdd(l.Image.Sha256, l.Image)) fresh.Add(l.Image.Sha256);
        }
        var result = _session.AddImportedImages(list);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < result.AddedCount; i++)
        {
            var im = layers[i].Image;
            kept.Add(im.Sha256);
            // 新しい文書になったとき、前の文書の同じ画像が SourceReleased で消えていることがあるので、足し直す
            _images.TryAdd(im.Sha256, im);
            if (layers[i].SourcePath is { } path)
                _paths.GetOrAdd(im.Sha256, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[Path.GetFullPath(path)] = 0;
        }
        foreach (var sha in fresh)
        {
            if (!kept.Contains(sha)) _images.TryRemove(sha, out _);
        }
        return LayerCount;
    }

    /// <summary>
    /// 読み込んだ文書(.glint。Phase 5)で置き換える。画素を先に登録してから文書を置き換え、Undo の履歴は空になる。
    /// dialog を渡すと未渡しのときに確認し(FR-13)、キャンセルなら何も変えない。dialog が null なら確認しない。
    /// </summary>
    public ReplaceResult OpenDocument(SessionDocument doc, IReadOnlyList<ImportedImage> images, IReplaceConfirmation? dialog = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(images);
        var fresh = new List<string>();
        foreach (var im in images)
        {
            if (_images.TryAdd(im.Sha256, im)) fresh.Add(im.Sha256);
        }
        var sources = images.Select(im => new SourceInfo(im.Sha256, im.Ext, im.ByteCount, im.Width, im.Height)).ToArray();
        ReplaceResult result;
        try
        {
            if (dialog is not null)
            {
                result = _session.RequestOpen(doc, sources, dialog);
            }
            else
            {
                _session.OpenDocument(doc, sources);
                result = ReplaceResult.Replaced;
            }
        }
        catch
        {
            foreach (var sha in fresh) _images.TryRemove(sha, out _);
            throw;
        }
        if (result != ReplaceResult.Replaced)
        {
            foreach (var sha in fresh) _images.TryRemove(sha, out _);
            return result;
        }
        // 前の文書の同じ画像が SourceReleased で消えていることがあるので足し直し(AddImages と同じ)、文書が使っていない画像は持たない
        foreach (var im in images)
        {
            if (_session.Sources.ContainsKey(im.Sha256)) _images.TryAdd(im.Sha256, im);
            else _images.TryRemove(im.Sha256, out _);
        }
        return result;
    }
}
