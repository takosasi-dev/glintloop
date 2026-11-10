// バッチの画像の一覧(FR-87): 追加した順にパスを持つ。同じパス(絶対パスに直し、大文字と小文字を区別しない)は 1 個として数え、200 個を超える分は足さない。
// 出力の名前(BatchNames)もここに置く: 元の名前-glint.拡張子、ふさがっていれば拡張子の前に -2、-3 を付ける。
using System.IO;
using GlintLoop.Core.Export;

namespace GlintLoop.App.Batch;

public sealed class BatchList
{
    public const int MaxItems = 200;

    private readonly List<string> _paths = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>追加した順の絶対パス。</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>paths を渡された順に足す。足した個数と、200 個を超えて足せなかったものがあったかを返す。</summary>
    public (int Added, bool Full) Add(IEnumerable<string> paths)
    {
        var added = 0;
        var full = false;
        foreach (var p in paths)
        {
            var path = Absolute(p);
            if (_seen.Contains(path)) continue;
            if (_paths.Count >= MaxItems)
            {
                full = true;
                continue;
            }
            _seen.Add(path);
            _paths.Add(path);
            added++;
        }
        return (added, full);
    }

    private static string Absolute(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path; // 実行のときに import.read_error になる
        }
    }
}

public static class BatchNames
{
    /// <summary>input の出力の名前を folder の中に決める。taken が真の名前は使わず、-2、-3 と進める。</summary>
    public static string Pick(string folder, string input, string format, Func<string, bool> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(input) + "-glint";
        var ext = "." + ExportFormat.Extension(format);
        var path = Path.Combine(folder, stem + ext);
        for (var n = 2; taken(path); n++) path = Path.Combine(folder, stem + "-" + n + ext);
        return path;
    }
}
