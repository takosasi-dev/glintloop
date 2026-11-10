// 静的な検査(AC-9)が読むリポジトリのファイルを集める道具。
// ルートはテストの出力フォルダから上へ GlintLoop.slnx を探して決め、bin・obj・.git の下は数えない。
using System.IO;

namespace GlintLoop.Tests.Static;

public static class RepoFiles
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    /// <summary>リポジトリのルート(GlintLoop.slnx があるフォルダ)の絶対パス。</summary>
    public static string Root => RootLazy.Value;

    /// <summary>手で書くソースとみなす拡張子(MSBuild の .props・.targets と、シェーダーの .hlsl・インクルードの .hlsli も含む)。</summary>
    public static readonly string[] SourceExtensions = [".cs", ".csproj", ".xaml", ".props", ".targets", ".hlsl", ".hlsli"];

    private static readonly string[] SkippedDirectoryNames = ["bin", "obj", ".git", ".vs"];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlintLoop.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("GlintLoop.slnx が出力フォルダの上に見つからない");
    }

    /// <summary>ルートからの相対パスを、区切りを / にそろえて返す。</summary>
    public static string Relative(string fullPath) => Relative(Root, fullPath);

    /// <summary>root からの相対パスを、区切りを / にそろえて返す(一時フォルダで検査の道具を確かめるとき用)。</summary>
    public static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    /// <summary>tops(src・tests・probes・tools など)の下にある、指定の拡張子のファイル。bin・obj の下を除く。</summary>
    public static IReadOnlyList<string> Under(IEnumerable<string> tops, IEnumerable<string> extensions) =>
        Under(Root, tops, extensions);

    /// <summary>root の下の tops にある、指定の拡張子のファイル。検査の道具を一時フォルダで確かめるときにも同じ経路を通す。</summary>
    public static IReadOnlyList<string> Under(string root, IEnumerable<string> tops, IEnumerable<string> extensions)
    {
        var exts = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var top in tops)
        {
            var start = Path.Combine(root, top);
            if (Directory.Exists(start)) Walk(new DirectoryInfo(start), exts, result);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>ルート直下(下のフォルダは見ない)にある、指定の拡張子のファイル。Directory.Build.props など。</summary>
    public static IReadOnlyList<string> AtRoot(IEnumerable<string> extensions) => AtRoot(Root, extensions);

    /// <summary>root の直下(下のフォルダは見ない)にある、指定の拡張子のファイル。</summary>
    public static IReadOnlyList<string> AtRoot(string root, IEnumerable<string> extensions)
    {
        var exts = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var result = new DirectoryInfo(root).EnumerateFiles()
            .Where(f => exts.Contains(f.Extension))
            .Select(f => f.FullName)
            .ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void Walk(DirectoryInfo dir, HashSet<string> exts, List<string> result)
    {
        foreach (var file in dir.EnumerateFiles())
        {
            if (exts.Contains(file.Extension)) result.Add(file.FullName);
        }
        foreach (var sub in dir.EnumerateDirectories())
        {
            if (SkippedDirectoryNames.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) continue;
            Walk(sub, exts, result);
        }
    }

    /// <summary>ファイルを読み取り専用・共有可で開いて文字列で返す(ほかのプロセスが開いていても読める)。</summary>
    public static string ReadText(string fullPath)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>ファイルを読み取り専用・共有可で開いてバイト列で返す。</summary>
    public static byte[] ReadBytes(string fullPath)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }
}
