// AC-62(配布物とリポジトリの最終確認)の道具: 子プロセスの実行、exe に埋め込まれたマニフェストの読み出し、各項目の判定。
// 判定は文字列やバイト列を受け取る関数にしてあり、壊した入力を与えて落ちることを確かめられる。
// publish した exe は読むだけで起動しない(INV-18)。
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GlintLoop.Tests.Dist;

public static class Ac62Checks
{
    // ================================================================ 子プロセス

    /// <summary>テストを dotnet run で動かしたときは親の dotnet、そうでなければ PATH の dotnet。</summary>
    public static string DotnetPath =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    /// <summary>子プロセスを窓なしで動かし、終了コードと出力(標準出力と標準エラー)を返す。時間内に終わらなければ木ごと止める。</summary>
    public static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> args, string workingDirectory, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // CLI 自身が外へ送るもの(テレメトリ)を止め、ビルドのノードを残さず、親の dotnet run から引き継いだ MSBuild の設定を外す(PackageList と同じ)
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var key in new[] { "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath" }) psi.Environment.Remove(key);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(fileName + " を起動できない");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{fileName} {string.Join(' ', psi.ArgumentList)} が {timeout.TotalMinutes} 分で終わらない");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>
    /// AC-62 の (1) の publish(Release・win-x64・self-contained・単一ファイル・圧縮あり・デバッグ記号なし)を root で行い、outDir に出す。
    /// 復元は emptySource(空のフォルダ)だけを見て、この PC のパッケージのキャッシュで済ませる(外へ出ない)。コンパイラのサーバーを残さない。
    /// </summary>
    public static (int ExitCode, string Output) Publish(string root, string outDir, string emptySource) => Run(DotnetPath,
        [
            "publish", Path.Combine(root, "src", "GlintLoop.App", "GlintLoop.App.csproj"),
            "-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false",
            "-p:NuGetAudit=false", "-p:RestoreSources=" + emptySource, "-p:UseSharedCompilation=false", "--disable-build-servers",
            "-o", outDir, "-nologo",
        ], root, TimeSpan.FromMinutes(15));

    /// <summary>
    /// 追跡しているファイルを今の作業ツリーの中身で dest へ写す(publish をリポジトリの外で行うため)。
    /// リポジトリで publish すると、RID 付きの復元が src の obj/project.assets.json を書き換え、次のビルドまで INV-4 の dotnet list package に
    /// publish だけで使うパッケージ(Microsoft.NET.ILLink.Tasks)が出る。
    /// </summary>
    public static void CopyTracked(string root, string dest)
    {
        foreach (var file in TrackedFiles(root))
        {
            var to = Path.Combine(dest, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(root, file), to);
        }
    }

    /// <summary>git ls-files の結果(ルートからの相対パス、区切りは /)。</summary>
    public static IReadOnlyList<string> TrackedFiles(string root)
    {
        var (code, output) = Run("git", ["-c", "core.quotepath=off", "ls-files", "-z"], root, TimeSpan.FromMinutes(2));
        if (code != 0) throw new InvalidOperationException($"git ls-files が終了コード {code}: {output}");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // ================================================================ (3) マニフェスト

    private const int RtManifest = 24;

    /// <summary>PE のリソース(.rsrc)から RT_MANIFEST の最初のものを読む。無ければ null。ファイルは読むだけ。</summary>
    public static string? ReadEmbeddedManifest(string pePath)
    {
        using var fs = new FileStream(pePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(fs);
        var dir = pe.PEHeaders.PEHeader?.ResourceTableDirectory ?? default;
        if (dir.Size == 0) return null;
        var rsrc = pe.GetSectionData(dir.RelativeVirtualAddress).GetContent();
        int U16(int at) => rsrc[at] | rsrc[at + 1] << 8;
        uint U32(int at) => (uint)(rsrc[at] | rsrc[at + 1] << 8 | rsrc[at + 2] << 16 | rsrc[at + 3] << 24);

        // 資源の木は 種類 → 名前(番号) → 言語 の 3 段。種類は番号 24 を探し、下の 2 段は最初の項目をたどる
        int? Child(int dirAt, int? id)
        {
            var named = U16(dirAt + 12);
            var count = named + U16(dirAt + 14);
            for (var i = 0; i < count; i++)
            {
                var entry = dirAt + 16 + i * 8;
                if (id is not null && (i < named || U32(entry) != id)) continue;
                return (int)(U32(entry + 4) & 0x7FFF_FFFF);
            }
            return null;
        }
        if (Child(0, RtManifest) is not { } typeDir || Child(typeDir, null) is not { } nameDir || Child(nameDir, null) is not { } dataEntry) return null;
        var rva = (int)U32(dataEntry);
        var size = (int)U32(dataEntry + 4);
        var bytes = pe.GetSectionData(rva).GetContent(0, size);
        return Encoding.UTF8.GetString(bytes.AsSpan()).TrimStart('\uFEFF');
    }

    /// <summary>マニフェストの XML が AC-62 の (3) を満たさない点(level・uiAccess・dpiAwareness・longPathAware)。名前空間は見ず、要素の名前で探す。</summary>
    public static List<string> ManifestProblems(string xml)
    {
        var problems = new List<string>();
        var doc = XDocument.Parse(xml);
        XElement? One(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
        void Expect(string what, string? actual, string expected)
        {
            if (!string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase)) problems.Add($"{what} が {actual ?? "(無い)"}({expected} のはず)");
        }
        var level = One("requestedExecutionLevel");
        Expect("requestedExecutionLevel の level", (string?)level?.Attribute("level"), "asInvoker");
        Expect("requestedExecutionLevel の uiAccess", (string?)level?.Attribute("uiAccess"), "false");
        // dpiAwareness は「PerMonitorV2, PerMonitor」のような並びも書けるので、最初の値を見る
        Expect("dpiAwareness", One("dpiAwareness")?.Value.Split(',')[0], "PerMonitorV2");
        Expect("longPathAware", One("longPathAware")?.Value, "true");
        return problems;
    }

    // ================================================================ (4) 版

    /// <summary>0.y.z の形(SemVer の 3 つ組。先頭の 0 を付けない)か。</summary>
    public static bool IsZeroMajorSemVer(string version) => Regex.IsMatch(version, @"^0\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$");

    /// <summary>CHANGELOG.md の最初の「## [版] - YYYY-MM-DD」の見出しの版。無ければ null。</summary>
    public static string? FirstChangelogVersion(string markdown)
    {
        var m = Regex.Match(markdown.Replace("\r\n", "\n"), @"^## \[([^\]]+)\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Directory.Build.props の Version。</summary>
    public static string PropsVersion(string root) =>
        XDocument.Parse(File.ReadAllText(Path.Combine(root, "Directory.Build.props"))).Descendants().First(e => e.Name.LocalName == "Version").Value.Trim();

    // ================================================================ (5) ライセンス

    /// <summary>Q-3 の回答の著作権の行。</summary>
    public const string CopyrightLine = "Copyright (c) 2026 takosasi-dev";

    /// <summary>MIT ライセンスの全文(著作権の行は CopyrightLine)。</summary>
    public const string MitText = "MIT License\n\n" + CopyrightLine + "\n\n" +
        "Permission is hereby granted, free of charge, to any person obtaining a copy\n" +
        "of this software and associated documentation files (the \"Software\"), to deal\n" +
        "in the Software without restriction, including without limitation the rights\n" +
        "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
        "copies of the Software, and to permit persons to whom the Software is\n" +
        "furnished to do so, subject to the following conditions:\n\n" +
        "The above copyright notice and this permission notice shall be included in all\n" +
        "copies or substantial portions of the Software.\n\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n" +
        "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n" +
        "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n" +
        "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n" +
        "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\n" +
        "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\n" +
        "SOFTWARE.\n";

    /// <summary>LICENSE の中身が MIT の全文と違う点。折り返しの位置の違いは見ず、語の並びで比べる。著作権の行は 1 行まるごと比べる。</summary>
    public static List<string> LicenseProblems(string text)
    {
        static string Words(string s) => Regex.Replace(s, @"\s+", " ").Trim();
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (!lines.Contains(CopyrightLine, StringComparer.Ordinal))
            problems.Add($"「{CopyrightLine}」の行が無い(あるのは {string.Join(" / ", lines.Where(l => l.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)))})");
        if (Words(text) != Words(MitText)) problems.Add("MIT ライセンスの全文と違う");
        return problems;
    }

    /// <summary>フォントとみなす拡張子(同梱フォントの検査)。</summary>
    public static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".woff", ".woff2"];

    // ================================================================ (6) リポジトリ

    /// <summary>BOM と CR を許さない拡張子(と、名前で決める .gitattributes・.gitignore)。</summary>
    public static readonly string[] TextExtensions = [".cs", ".xaml", ".csproj", ".props", ".json", ".md", ".txt", ".hlsl", ".yml"];

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg"];

    public const string GitAttributesLine = "* text=auto eol=lf";

    /// <summary>ひらがな・カタカナ・漢字を 1 文字以上含むか。</summary>
    public static bool HasJapanese(string s) =>
        s.Any(c => c is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or '\u3005');

    private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>tests/assets/ の下か、README の画像(docs/images/ の下。合成した絵から作ったもの。2026-11-10 の作者の判断)か、アプリのアイコン(src/GlintLoop.App/ の下で .ico か名前に icon を含むもの)か。</summary>
    public static bool IsAllowedImagePlace(string path) =>
        path.StartsWith("tests/assets/", StringComparison.Ordinal) || path.StartsWith("docs/images/", StringComparison.Ordinal) || IsAppIcon(path);

    private static bool IsAppIcon(string path) =>
        path.StartsWith("src/GlintLoop.App/", StringComparison.Ordinal)
        && (Ext(path) == ".ico" || Name(path).Contains("icon", StringComparison.OrdinalIgnoreCase));

    /// <summary>追跡しているファイル(相対パスと中身)が AC-62 の (6) を満たさない点。</summary>
    public static List<string> RepoProblems(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        var problems = new List<string>();
        var attributes = files.FirstOrDefault(f => f.Path == ".gitattributes");
        if (attributes.Path is null) problems.Add(".gitattributes が追跡されていない");
        else if (Encoding.UTF8.GetString(attributes.Bytes) is var a && a != GitAttributesLine && a != GitAttributesLine + "\n")
            problems.Add($".gitattributes が「{GitAttributesLine}」の 1 行でない");

        foreach (var (path, bytes) in files)
        {
            var name = Name(path);
            var ext = Ext(path);
            if (TextExtensions.Contains(ext) || name is ".gitattributes" or ".gitignore")
            {
                if (bytes is [0xEF, 0xBB, 0xBF, ..]) problems.Add($"{path}: 先頭に BOM がある");
                if (bytes.Contains((byte)0x0D)) problems.Add($"{path}: 0x0D(CR)を含む");
            }
            if (path.Split('/')[..^1].Any(d => d is "bin" or "obj" or ".vs") || ext == ".user")
                problems.Add($"{path}: bin・obj・.vs の下か *.user のファイルが追跡されている");
            if (ext is ".cs" or ".hlsl" && HeaderProblem(Text(bytes)) is { } cs) problems.Add($"{path}: {cs}");
            if (ext == ".xaml" && XamlHeaderProblem(Text(bytes)) is { } xaml) problems.Add($"{path}: {xaml}");
            if (ImageExtensions.Contains(ext) && !IsAllowedImagePlace(path)) problems.Add($"{path}: tests/assets/ の下でも docs/images/ の下でもアプリのアイコンでもない画像");
            if (path.StartsWith("src/", StringComparison.Ordinal) && ext == ".csproj"
                && Encoding.UTF8.GetString(bytes).Contains("probes", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{path}: probes を参照している");
        }
        var icons = files.Where(f => ImageExtensions.Contains(Ext(f.Path)) && IsAppIcon(f.Path)).ToList();
        if (icons.Count > 1) problems.Add($"アプリのアイコンが {icons.Count} 個(1 個まで): {string.Join(", ", icons.Select(i => i.Path))}");
        return problems;
    }

    /// <summary>UTF-8 として読み、先頭の BOM は除く(BOM は別に数える)。</summary>
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

    /// <summary>.cs・.hlsl の先頭: 1〜3 行の // の日本語のコメントで、次の行が // で始まらない。合っていれば null。</summary>
    public static string? HeaderProblem(string text)
    {
        var lines = text.Split('\n');
        var n = lines.TakeWhile(l => l.StartsWith("//", StringComparison.Ordinal)).Count();
        if (n is < 1 or > 3) return $"先頭の // のコメントが {n} 行(1〜3 行のはず)";
        var plain = Enumerable.Range(0, n).FirstOrDefault(i => !HasJapanese(lines[i]), -1);
        return plain >= 0 ? $"{plain + 1} 行目のコメントに日本語が無い" : null;
    }

    /// <summary>.xaml の先頭(&lt;?xml の宣言があればその次の行から): 1〜3 行の &lt;!-- … --&gt; の日本語のコメント。合っていれば null。</summary>
    public static string? XamlHeaderProblem(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("<?xml", StringComparison.Ordinal)) lines.RemoveAt(0);
        static bool IsComment(string l) => l.Trim() is var t && t.StartsWith("<!--", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal);
        var n = lines.TakeWhile(IsComment).Count();
        if (n is < 1 or > 3) return $"先頭の <!-- --> のコメントが {n} 行(1〜3 行のはず)";
        var plain = Enumerable.Range(0, n).FirstOrDefault(i => !HasJapanese(lines[i]), -1);
        return plain >= 0 ? $"先頭から {plain + 1} 行目のコメントに日本語が無い" : null;
    }

    // ================================================================ (7) 逸脱不可要件

    /// <summary>AC-9(静的な検査)と AC-18(実行して確かめる検査)の INV の番号。</summary>
    public static readonly int[] Ac9Invs = [1, 3, 4, 10, 13, 14, 17, 18];

    public static readonly int[] Ac18Invs = [2, 5, 7, 8, 9, 11, 12, 15, 16];

    /// <summary>invs のうち、名前に INV の番号(前後が _ か端)を含むテストが 1 つも無いもの。</summary>
    public static List<int> InvsWithoutTest(IEnumerable<string> testMethodNames, IEnumerable<int> invs)
    {
        var names = testMethodNames.ToList();
        return invs.Where(n => !names.Any(m => Regex.IsMatch(m, $"(^|_)INV{n}(_|$)"))).ToList();
    }
}
