// ログの 1 行(仕様書 §9.11 の 6)を、書き先から切り離して受け渡すための型。
// 値に入れてよいのは、数値・列挙値・拡張子などの §9.11 の表の項目だけ(INV-15)。パスや文字列の中身は入れない。
// ファイルへの書き出しは別の実装が行い、テストはメモリに貯める実装を使う。
namespace GlintLoop.Core.Diagnostics;

public sealed record LogEvent(string Name, IReadOnlyList<KeyValuePair<string, object>> Fields)
{
    public static LogEvent Of(string name, params (string Key, object Value)[] fields)
    {
        var list = new List<KeyValuePair<string, object>>(fields.Length);
        foreach (var (key, value) in fields)
        {
            list.Add(new KeyValuePair<string, object>(key, value));
        }
        return new LogEvent(name, list);
    }

    public object? Get(string key)
    {
        foreach (var kv in Fields)
        {
            if (kv.Key == key)
            {
                return kv.Value;
            }
        }
        return null;
    }
}

public interface ILogSink
{
    void Write(LogEvent e);
}

/// <summary>何も書かない受け口。</summary>
public sealed class NullLogSink : ILogSink
{
    public static NullLogSink Instance { get; } = new();

    public void Write(LogEvent e)
    {
    }
}

/// <summary>メモリに貯める受け口。テストと、起動直後の書き先が決まる前に使う。</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly List<LogEvent> _events = new();
    private readonly object _gate = new();

    public void Write(LogEvent e)
    {
        lock (_gate)
        {
            _events.Add(e);
        }
    }

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }
}
