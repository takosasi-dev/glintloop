// §7 の静的な検査(AC-9: INV-1・3・4・10・13・14・17・18)と AC-7 の参照の検査。
// 検索語はこのファイルの中だけで連結して作る。語の検査はこのファイル 1 つだけを対象から外す。
// grep はコメントの中も数える(仕様の検査方法がソースの grep なので)。
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Static;

public sealed class StaticInvariantTests
{
    /// <summary>語の検査から外す唯一のファイル(このファイル)。</summary>
    private const string SelfRelative = "tests/GlintLoop.Tests/Static/StaticInvariantTests.cs";

    private static readonly string[] Excluded = [SelfRelative];

    /// <summary>INV-1 の語の検査だけから外すファイル(AC-55 の (3) のループバックの待ち受け)。</summary>
    private static readonly string[] NetworkExcluded = [.. Excluded, "tests/GlintLoop.Tests/Edge/EdgeAc55UrlDropTests.cs"];

    private static string J(params string[] parts) => string.Concat(parts);

    // INV-1 の検索語
    private static readonly string[] NetworkTerms =
    [
        J("Sys", "tem.", "Net"), J("Http", "Client"), J("Web", "Client"), J("Web", "Request"), J("Web", "Socket"),
        J("Tcp", "Client"), J("Udp", "Client"), J("Soc", "ket"), J("Dn", "s."), J("local", "host"), J("127", ".0.0.1"),
    ];

    // INV-1 の HTTP クライアントのパッケージ名に含まれる語(大文字と小文字を区別しない)
    private static readonly string[] HttpPackageNameParts =
    [
        J("Ht", "tp"), J("Net", ".Ht", "tp"), J("Rest", "Sharp"), J("Ref", "it"), J("Fl", "url"),
        J("Web", "Soc", "ket"), J("Signal", "R"), J("Gr", "pc"),
    ];

    // INV-3 の検索語
    private static readonly string[] ShaderCompileTerms =
    [
        J("D3D", "Compile"), J("Vortice", ".", "D3D", "Compiler"), J("D3D", "Reflect"),
    ];

    private static readonly string D3DCompilerPackage = J("Vortice", ".", "D3D", "Compiler");
    private static readonly string D3DCompilerNativeDll = J("d3d", "compiler");

    // INV-10 の検索語
    private static readonly string[] SerializerTerms = [J("Binary", "Formatter")];

    // INV-13 の検索語
    private static readonly string[] RegistryTerms = [J("Regis", "try.")];

    // INV-17 の検索語
    private static readonly string[] GlobalHookTerms = [J("SetWindows", "HookEx"), J("Register", "HotKey")];

    // INV-18 の検索語
    private static readonly string[] TestInputTerms =
    [
        J("Send", "Keys"), J("Send", "Input"), J("mouse", "_event"), J("SetCursor", "Pos"), J("SetForeground", "Window"),
        J("Clip", "board.", "Set"), J("Clip", "board.", "Get"), J("Environment.", "GetFolder", "Path"),
        J("IsDropDownOpen", " = true"), J(".IsOpen", " = true"), J("Flyout", ".Open("), J("Picker", ".Open("), J("OpenMenu", "("),
    ];

    /// <summary>
    /// INV-14 の検査(色の直書きと、余白・角丸・時間の数値の直書き)から外すトークン定義のファイル(ルートからの相対パス)。
    /// Phase 3 で Tokens.Dark.xaml・Tokens.Light.xaml(色)と Tokens.Layout.xaml(余白・角丸・文字・時間・寸法)を足した。
    /// </summary>
    private static readonly string[] TokenDefinitionFiles =
    [
        "src/GlintLoop.App/Theme/Tokens.Dark.xaml",
        "src/GlintLoop.App/Theme/Tokens.Light.xaml",
        "src/GlintLoop.App/Theme/Tokens.Layout.xaml",
    ];

    /// <summary>
    /// INV-14 で許す余白・角丸・時間・イージングの値。仕様書 §9.1 のトークン表の数字をそのまま書く。
    /// 0(余白なし・角なし・0 ms)は表に無いが、どの種類でも許す(DesignTokenScan.Problem が扱う)。
    /// </summary>
    private static readonly DesignTokenScan.TokenValues SpecTokens = new(
        // 余白: space-1 4・space-2 8・space-3 12・space-4 16・space-5 24・space-6 32(DIP)
        Space: [4, 8, 12, 16, 24, 32],
        // 角丸: radius-control 4・radius-card 8(DIP)
        Radius: [4, 8],
        // 時間(ms): press-down 120・press-up 160・color 120・tooltip-in 150・tooltip-out 100・flyout-in 180・flyout-out 120・
        // dialog-in 200・dialog-out 140・notice-in 200・notice-out 120・pane 200・thumb-in 150
        TimeMs: [120, 160, 120, 150, 100, 180, 120, 200, 140, 200, 120, 200, 150],
        // イージング(KeySpline): ease-out・ease-in-out・ease-drawer・ease-color
        KeySplines: [[0.23, 1, 0.32, 1], [0.77, 0, 0.175, 1], [0.32, 0.72, 0, 1], [0.25, 0.1, 0.25, 1]]);

    /// <summary>ファイル(相対パスと中身)の組から、除外のファイルを除いて INV-14 の数値の違反を集める。</summary>
    private static List<string> TokenNumberProblems(IEnumerable<(string File, string Text)> files, IReadOnlyCollection<string> excludedRelative)
    {
        var problems = new List<string>();
        foreach (var (file, text) in files)
        {
            if (excludedRelative.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            var uses = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? DesignTokenScan.ExtractXaml(file, text)
                : DesignTokenScan.ExtractCode(file, text);
            problems.AddRange(uses.Select(u => DesignTokenScan.Problem(u, SpecTokens)).OfType<string>());
        }
        return problems;
    }

    /// <summary>INV-14 の色と数値の実ファイルの検査が読む拡張子(src の下)。Phase 3 で入る XAML を必ず含める。</summary>
    private static readonly string[] Inv14Extensions = [".cs", ".xaml"];

    /// <summary>root の src の下で INV-14 の検査が読むファイル(相対パスと中身)。実ファイルの検査と一時フォルダの自己検査が同じ経路を通る。</summary>
    private static List<(string File, string Text)> Inv14Files(string root) =>
        RepoFiles.Under(root, ["src"], Inv14Extensions).Select(f => (RepoFiles.Relative(root, f), RepoFiles.ReadText(f))).ToList();

    /// <summary>root の src の下の、トークン定義のファイルを除いた色の直書き。</summary>
    private static List<SourceScan.Hit> Inv14ColorHits(string root) =>
        Inv14Files(root)
            .Where(f => !TokenDefinitionFiles.Contains(f.File, StringComparer.OrdinalIgnoreCase))
            .SelectMany(f => SourceScan.FindHexColors(f.File, f.Text))
            .ToList();

    /// <summary>root の src の下の、トークン定義のファイルを除いた余白・角丸・時間・イージングの違反。</summary>
    private static List<string> Inv14NumberProblems(string root) => TokenNumberProblems(Inv14Files(root), TokenDefinitionFiles);

    private static readonly string[] AllTops = ["src", "tests", "probes", "tools"];

    /// <summary>リポジトリのすべてのソース。src・tests・probes・tools の下と、ルート直下(Directory.Build.props など)。</summary>
    private static IReadOnlyList<string> AllSources() => AllSources(RepoFiles.Root);

    private static IReadOnlyList<string> AllSources(string root) =>
        RepoFiles.AtRoot(root, RepoFiles.SourceExtensions).Concat(RepoFiles.Under(root, AllTops, RepoFiles.SourceExtensions)).ToList();

    /// <summary>一時フォルダを作って action に渡し、終わったら消す(検査の道具を実ファイルの経路で確かめるとき用)。</summary>
    private static void WithTempRoot(Action<string> action)
    {
        var dir = Directory.CreateTempSubdirectory("glintloop-static-");
        try
        {
            action(dir.FullName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static void WriteFile(string root, string relative, string text)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void NoHits(List<SourceScan.Hit> hits, string what)
    {
        Check.True(hits.Count == 0, $"{what} が {hits.Count} 件: {string.Join(", ", hits.Take(20))}");
    }

    // ---- 検査の道具そのものの確かめ ----

    [Test]
    public void INV1_語の検査がソースを集め自分だけを外す()
    {
        var all = AllSources().Select(RepoFiles.Relative).ToList();
        Check.True(all.Contains(SelfRelative), "この検査のファイルが一覧に無い");
        Check.True(all.Contains("src/GlintLoop.Core/Numerics/Srgb.cs"), "src のソースが一覧に無い");
        Check.True(all.Any(p => p.EndsWith(".csproj", StringComparison.Ordinal)), "csproj が一覧に無い");
        Check.True(all.Contains("Directory.Build.props"), "ルート直下の Directory.Build.props が一覧に無い");
        Check.True(all.Contains("src/GlintLoop.Render/Shaders/fill.ps.hlsl"), "シェーダーの .hlsl が一覧に無い");
        Check.True(!all.Any(p => p.Contains("/obj/", StringComparison.Ordinal) || p.Contains("/bin/", StringComparison.Ordinal)),
            "bin・obj の下が一覧に入っている");
        // このファイルを外さなければ語が見つかる(= 検査が語を見つけられる)ことを確かめる。
        var self = SourceScan.FindTerms(SelfRelative, J("// ", NetworkTerms[1], " ", TestInputTerms[0]), NetworkTerms.Concat(TestInputTerms).ToList());
        Check.Equal(2, self.Count, "植えた語の件数");
        Check.True(SourceScan.FindTerms("x", "var s = \"safe text\";", NetworkTerms).Count == 0, "無関係の文に語が見つかった");
    }

    [Test]
    public void INV1_ソースとみなす拡張子の一覧()
    {
        // 手で書くソース: C#・プロジェクト・XAML・MSBuild の .props と .targets・シェーダーの .hlsl とインクルードの .hlsli
        Check.SequenceEqual([".cs", ".csproj", ".xaml", ".props", ".targets", ".hlsl", ".hlsli"], RepoFiles.SourceExtensions, "SourceExtensions");
    }

    [Test]
    public void INV1_一時フォルダの各拡張子のソースが集まり語が見つかる()
    {
        WithTempRoot(root =>
        {
            string[] expected =
            [
                "Directory.Build.props", "Directory.Build.targets",
                "src/App/A.cs", "src/App/App.csproj", "src/App/Views/Main.xaml",
                "src/Render/Shaders/fill.ps.hlsl", "src/Render/Shaders/common.hlsli",
                "tests/T/T.cs", "probes/p/P.cs", "tools/t/x.targets",
            ];
            foreach (var rel in expected) WriteFile(root, rel, "// ok\n");
            // 集めないもの: bin・obj の下、ソースでない拡張子、4 つの上のフォルダの外
            foreach (var rel in new[] { "src/App/obj/Gen.cs", "src/App/bin/Debug/X.cs", "src/App/readme.txt", "docs/D.cs" })
                WriteFile(root, rel, "// ok\n");
            var got = AllSources(root).Select(f => RepoFiles.Relative(root, f)).OrderBy(p => p, StringComparer.Ordinal).ToList();
            Check.SequenceEqual(expected.OrderBy(p => p, StringComparer.Ordinal).ToList(), got, "一時フォルダから集めたソース");

            // シェーダーのインクルードに植えた語を、実ファイルの検査と同じ経路で見つける。
            WriteFile(root, "src/Render/Shaders/common.hlsli", J("// ", NetworkTerms[9], "\n"));
            var hits = SourceScan.FindTermsInFiles(AllSources(root), NetworkTerms, Excluded);
            Check.Equal(1, hits.Count, "common.hlsli に植えた語の件数");
            Check.True(hits[0].File.EndsWith("common.hlsli", StringComparison.Ordinal), $"語を見つけたファイル: {hits[0].File}");
        });
    }

    // ---- INV-1 ----

    [Test]
    public void INV1_ソースにネットワークの語が無い()
    {
        NoHits(SourceScan.FindTermsInFiles(AllSources(), NetworkTerms, NetworkExcluded), "ネットワークの語");
    }

    [Test]
    public void INV1_HTTPクライアントのパッケージが無い()
    {
        var bad = PackageList.All
            .Where(p => HttpPackageNameParts.Any(part => p.Id.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Id} {p.Version}({p.Project})")
            .Distinct()
            .ToList();
        Check.True(PackageList.All.Count > 0, "dotnet list package の結果が空");
        Check.True(bad.Count == 0, "HTTP クライアントらしいパッケージ: " + string.Join(", ", bad));
    }

    // ---- INV-3 ----

    [Test]
    public void INV3_srcにシェーダーのコンパイルの語が無い()
    {
        var files = RepoFiles.Under(["src"], RepoFiles.SourceExtensions);
        NoHits(SourceScan.FindTermsInFiles(files, ShaderCompileTerms, Excluded), "src のシェーダーのコンパイルの語");
    }

    [Test]
    public void INV3_srcのcsprojがD3DCompilerを参照しない()
    {
        var bad = new List<string>();
        foreach (var csproj in RepoFiles.Under(["src"], [".csproj"]))
        {
            var doc = XDocument.Parse(RepoFiles.ReadText(csproj));
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "Reference" or "ProjectReference"))
            {
                var include = (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "";
                if (include.Contains(D3DCompilerPackage, StringComparison.OrdinalIgnoreCase) ||
                    include.Contains("ShaderCompiler", StringComparison.OrdinalIgnoreCase))
                    bad.Add($"{RepoFiles.Relative(csproj)}: {include}");
            }
        }
        Check.True(bad.Count == 0, "src の csproj がシェーダーのコンパイラを参照: " + string.Join(", ", bad));
    }

    private static string AssemblyNameOf(string csproj)
    {
        var doc = XDocument.Parse(RepoFiles.ReadText(csproj));
        var name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();
        return string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(csproj) : name;
    }

    [Test]
    public void INV3_製品のアセンブリがD3DCompilerを参照しない()
    {
        // 製品のアセンブリは src の下の csproj から集める(プロジェクトを足したときに検査から漏れないように)。
        // csproj が AssemblyName を持てばその名前(GlintLoop.App.csproj は GlintLoop.dll)、無ければ csproj のファイル名。
        var names = RepoFiles.Under(["src"], [".csproj"]).Select(p => AssemblyNameOf(p) + ".dll").ToList();
        foreach (var expected in new[] { "GlintLoop.Core.dll", "GlintLoop.Render.dll", "GlintLoop.Codec.dll", "GlintLoop.dll" })
            Check.True(names.Contains(expected), $"src の csproj から {expected} が集まらない");
        foreach (var name in names)
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            Check.True(File.Exists(path), $"{name} がテストの出力フォルダに無い");
            var bytes = RepoFiles.ReadBytes(path);
            Check.True(AssemblyMetadataScan.AssemblyReferences(bytes).Count > 0, $"{name} の参照アセンブリが読めない");
            var problems = Inv3Problems(bytes);
            Check.True(problems.Count == 0, $"{name} がシェーダーのコンパイラを参照している: {string.Join(", ", problems)}");
        }
    }

    /// <summary>
    /// INV-3 の検査方法(Release ビルドの出力に D3DCompile を呼ぶ参照が無い。アセンブリの参照一覧と grep)。参照の一覧と、
    /// メタデータの名前(AssemblyRef・TypeRef・MemberRef・ModuleRef・P/Invoke の名前)を検索語で調べる。埋め込みリソースの中身は対象にしない(2026-10-28 の作者の判断)。
    /// </summary>
    private static List<string> Inv3Problems(byte[] bytes)
    {
        var problems = AssemblyMetadataScan.AssemblyReferences(bytes)
            .Where(r => r.Contains(D3DCompilerPackage, StringComparison.OrdinalIgnoreCase)).Select(r => "参照 " + r).ToList();
        problems.AddRange(AssemblyMetadataScan.Hits(AssemblyMetadataScan.Names(bytes), ShaderCompileTerms.Append(D3DCompilerNativeDll)).Select(n => n.ToString()));
        return problems;
    }

    /// <summary>P/Invoke を 1 つだけ持つ偽のアセンブリを作る(検査自身の確認用)。</summary>
    private static byte[] FakePInvokeAssembly(string methodName, string dll, string entry)
    {
        var ab = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("GlintLoop.Fake"), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("GlintLoop.Fake");
        var tb = mb.DefineType("Fake.Native", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Sealed);
        tb.DefinePInvokeMethod(methodName, dll, entry,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static | System.Reflection.MethodAttributes.PinvokeImpl,
            System.Reflection.CallingConventions.Standard, typeof(int), [typeof(IntPtr), typeof(IntPtr)],
            System.Runtime.InteropServices.CallingConvention.Winapi, System.Runtime.InteropServices.CharSet.Ansi);
        tb.CreateType();
        using var ms = new MemoryStream();
        ab.Save(ms);
        return ms.ToArray();
    }

    [Test]
    public void INV3_検査はシェーダーのコンパイラをPInvokeで宣言した偽のアセンブリで落ちる()
    {
        var compile = J("D3D", "Compile");
        var dll = D3DCompilerNativeDll + "_47.dll";
        // メソッド名も入口の名前も D3DCompile
        var p1 = Inv3Problems(FakePInvokeAssembly(compile, dll, compile));
        Check.True(p1.Any(p => p.StartsWith("PInvokeEntry ", StringComparison.Ordinal)), "入口の名前で落ちない: " + string.Join(", ", p1));
        Check.True(p1.Any(p => p.StartsWith("ModuleRef ", StringComparison.Ordinal)), "DLL の名前で落ちない: " + string.Join(", ", p1));
        // メソッド名を変え、DLL の名前も別にしても、入口の名前(EntryPoint)で落ちる
        var p2 = Inv3Problems(FakePInvokeAssembly("Build", "other.dll", J("D3D", "Reflect")));
        Check.True(p2.Count == 1 && p2[0].StartsWith("PInvokeEntry ", StringComparison.Ordinal), "EntryPoint だけの宣言で落ちない: " + string.Join(", ", p2));
        // 関係の無い P/Invoke では落ちない
        var p3 = Inv3Problems(FakePInvokeAssembly("GetTickCount64", "kernel32.dll", "GetTickCount64"));
        Check.True(p3.Count == 0, "関係の無い P/Invoke で落ちた: " + string.Join(", ", p3));
    }

    [Test]
    public void INV3_検査は埋め込みリソースの中身を対象にしない()
    {
        // GlintLoop.dll は THIRD-PARTY-NOTICES.md(ビルドだけで使う Vortice.D3DCompiler の行を含む)を埋め込む(FR-103)
        var bytes = RepoFiles.ReadBytes(Path.Combine(AppContext.BaseDirectory, "GlintLoop.dll"));
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var md = pe.GetMetadataReader();
            var resources = md.ManifestResources.Select(h => md.GetString(md.GetManifestResource(h).Name)).ToList();
            Check.True(resources.Contains("GlintLoop.App.THIRD-PARTY-NOTICES.md"), "THIRD-PARTY-NOTICES.md を埋め込んでいない: " + string.Join(", ", resources));
        }
        Check.True(SourceScan.ContainsAsciiIgnoreCase(bytes, D3DCompilerPackage), "埋め込んだ表の文字がバイト列に無い(テストの前提)");
        var problems = Inv3Problems(bytes);
        Check.True(problems.Count == 0, "埋め込みリソースの中身で落ちた: " + string.Join(", ", problems));
    }

    // ---- INV-4 ----

    private static string Notices() => RepoFiles.ReadText(Path.Combine(RepoFiles.Root, "THIRD-PARTY-NOTICES.md"));

    private static (string, string)[] RealPackages() =>
        PackageList.All.Select(p => (p.Id, p.Version)).Distinct().ToArray();

    [Test]
    public void INV4_全パッケージが通知の表にあり許可リストに入る()
    {
        var pkgs = RealPackages();
        Check.True(pkgs.Length > 0, "dotnet list package の結果が空");
        var problems = NoticesCheck.Evaluate(Notices(), pkgs);
        Check.True(problems.Count == 0, "INV-4: " + string.Join(" / ", problems));
    }

    [Test]
    public void INV4_許可リストに無いライセンスを足すと失敗する()
    {
        var notices = Notices();
        var pkgs = RealPackages().ToList();
        Check.True(NoticesCheck.Evaluate(notices, pkgs).Count == 0, "元の表がすでに失敗している");

        // (a) GPL のパッケージの行を足し、そのパッケージを依存に加える
        var gpl = notices.Replace("| Vortice.Direct3D11 | 3.8.3 | MIT |",
            "| Example.Gpl | 1.0.0 | GPL-3.0 | tests |\n| Vortice.Direct3D11 | 3.8.3 | MIT |");
        Check.True(gpl != notices, "行の挿入に失敗");
        var withGpl = pkgs.Append(("Example.Gpl", "1.0.0")).ToList();
        Check.True(NoticesCheck.Evaluate(gpl, withGpl).Any(p => p.Contains("Example.Gpl", StringComparison.Ordinal)), "GPL の行で失敗しない");

        // (b) 表に無いパッケージが依存にある
        var missing = NoticesCheck.Evaluate(notices, pkgs.Append(("Example.Missing", "2.0.0")).ToList());
        Check.True(missing.Any(p => p.Contains("Example.Missing", StringComparison.Ordinal)), "表に無いパッケージで失敗しない");

        // (c) 版が表と違う
        var wrongVer = NoticesCheck.Evaluate(notices, [("Vortice.Direct3D11", "9.9.9")]);
        Check.True(wrongVer.Any(p => p.Contains("9.9.9", StringComparison.Ordinal)), "版の食い違いで失敗しない");

        // (d) 部分一致では通さない(Old MIT・LGPL を MIT と見なさない)
        foreach (var lic in new[] { "Old MIT", "LGPL-2.1", "MPL-2.0", "Proprietary", "" })
        {
            var md = "## NuGet パッケージ\n\n| パッケージ | バージョン | ライセンス | 使う場所 |\n|---|---|---|---|\n" +
                     $"| A | 1.0.0 | {lic} | src |\n";
            Check.True(NoticesCheck.Evaluate(md, [("A", "1.0.0")]).Count > 0, $"ライセンス「{lic}」で失敗しない");
        }

        // (e) OFL はフォントだけに許す
        const string head = "## NuGet パッケージ\n\n| パッケージ | バージョン | ライセンス | 使う場所 |\n|---|---|---|---|\n";
        Check.True(NoticesCheck.Evaluate(head + "| F | 1.0.0 | OFL-1.1 | 同梱フォント |\n", [("F", "1.0.0")]).Count == 0, "フォントの OFL が通らない");
        Check.True(NoticesCheck.Evaluate(head + "| F | 1.0.0 | OFL-1.1 | src |\n", [("F", "1.0.0")]).Count > 0, "フォント以外の OFL が通る");

        // (f) 表が無い
        Check.True(NoticesCheck.Evaluate("# 空\n", pkgs).Count > 0, "表が無くても失敗しない");
    }

    [Test]
    public void INV4_ネイティブ部品の扱いの判定()
    {
        const string nuget = "## NuGet パッケージ\n\n| パッケージ | バージョン | ライセンス | 使う場所 |\n|---|---|---|---|\n| A | 1.0.0 | MIT | src |\n\n";
        const string head = "## SkiaSharp のネイティブ部品\n\n説明の行\n\n| 部品 | ライセンス | 扱い |\n|---|---|---|\n";
        (string Row, bool Ok)[] cases =
        [
            ("| zlib | zlib | 許可リスト |", true),
            ("| HarfBuzz | Old MIT | 例外として許可(2026-10-27) |", true),
            ("| gif | MPL 1.1 / GPL 2.0 / LGPL 2.1 | 通知に載っているが使わない部品として記録(2026-10-27)。理由 |", true),
            ("| HarfBuzz | Old MIT | 許可リスト |", false),
            ("| x | LGPL 2.1 | 許可リスト |", false),
            ("| x | LGPL 2.1 | 使う |", false),
            ("| x | MIT | |", false),
        ];
        foreach (var (row, ok) in cases)
        {
            var problems = NoticesCheck.Evaluate(nuget + head + row + "\n", [("A", "1.0.0")]);
            Check.Equal(ok, problems.Count == 0, $"行「{row}」の判定({string.Join(" / ", problems)})");
        }
    }

    // ---- INV-10 ----

    [Test]
    public void INV10_ソースにBinaryFormatterが無い()
    {
        NoHits(SourceScan.FindTermsInFiles(AllSources(), SerializerTerms, Excluded), "シリアライザーの語");
    }

    [Test]
    public void INV10_srcのSetDataの形式名が4つだけ()
    {
        var problems = new List<string>();
        foreach (var f in RepoFiles.Under(["src"], [".cs", ".xaml"]))
            problems.AddRange(SourceScan.ClipboardFormatProblems(RepoFiles.Relative(f), RepoFiles.ReadText(f)));
        Check.True(problems.Count == 0, string.Join(" / ", problems));
    }

    [Test]
    public void INV10_形式名の判定が許されない形式を見つける()
    {
        const string ok = "data.SetData(DataFormats.FileDrop, files);\ndata.SetData(\"PNG\", png);\n" +
                          "data.SetData(DataFormats.Dib, dib);\ndata.SetData(\"Format17\", MakeStream(a, b));\n";
        Check.True(SourceScan.ClipboardFormatProblems("ok", ok).Count == 0, "許される 4 形式で失敗した: " +
            string.Join(" / ", SourceScan.ClipboardFormatProblems("ok", ok)));
        Check.Equal(4, SourceScan.FindSetDataFormats(ok).Count, "SetData の呼び出しの数");
        foreach (var bad in new[]
                 {
                     "d.SetData(DataFormats.Bitmap, bmp);", "d.SetData(DataFormats.Text, s);", "d.SetData(\"GlintLoop\", obj);",
                     "d.SetData(typeof(Foo), foo);", "d.SetData(bmp);", "d.SetData(DataFormats.Serializable, x);",
                 })
        {
            Check.True(SourceScan.ClipboardFormatProblems("bad", bad).Count == 1, $"「{bad}」を見つけない");
        }
    }

    // ---- INV-13 ----

    [Test]
    public void INV13_srcにレジストリの語が無い()
    {
        NoHits(SourceScan.FindTermsInFiles(RepoFiles.Under(["src"], RepoFiles.SourceExtensions), RegistryTerms, Excluded), "レジストリの語");
    }

    [Test]
    public void INV13_srcのマニフェストはasInvoker()
    {
        foreach (var manifest in RepoFiles.Under(["src"], [".manifest"]))
        {
            var doc = XDocument.Parse(RepoFiles.ReadText(manifest));
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "requestedExecutionLevel"))
            {
                Check.Equal("asInvoker", (string?)e.Attribute("level"), $"{RepoFiles.Relative(manifest)} の level");
                Check.True(!string.Equals((string?)e.Attribute("uiAccess"), "true", StringComparison.OrdinalIgnoreCase),
                    $"{RepoFiles.Relative(manifest)} の uiAccess が true");
            }
        }
    }

    // ---- INV-14 ----

    [Test]
    public void INV14_srcに色の直書きが無い()
    {
        NoHits(Inv14ColorHits(RepoFiles.Root), "色の直書き");
    }

    [Test]
    public void INV14_実ファイルの検査がsrcのXAMLとコードを読む()
    {
        // Phase 1 の src には XAML が無いので、既知の違反を置いた一時フォルダで実ファイルの経路を通す。
        Check.SequenceEqual([".cs", ".xaml"], Inv14Extensions, "INV-14 の検査が読む拡張子");
        WithTempRoot(root =>
        {
            WriteFile(root, "src/App/Views/Bad.xaml", "<Border Margin=\"5\" Background=\"#123456\"/>\n");
            WriteFile(root, "src/App/Bad.cs", "border.Margin = new Thickness(6); // #ABCDEF\n");
            WriteFile(root, "src/App/obj/Gen.xaml", "<Border Margin=\"5\" Background=\"#123456\"/>\n"); // obj の下は読まない
            WriteFile(root, "src/App/Note.txt", "<Border Margin=\"5\" Background=\"#123456\"/>\n"); // ソースでない
            WriteFile(root, "tests/T.xaml", "<Border Margin=\"5\" Background=\"#123456\"/>\n"); // src の外

            var problems = Inv14NumberProblems(root);
            Check.Equal(2, problems.Count, $"数値の違反の数({string.Join(" / ", problems)})");
            Check.True(problems.Any(p => p.StartsWith("src/App/Views/Bad.xaml:1 ", StringComparison.Ordinal)), "XAML の違反を見つけない");
            Check.True(problems.Any(p => p.StartsWith("src/App/Bad.cs:1 ", StringComparison.Ordinal)), "コードの違反を見つけない");

            var colors = Inv14ColorHits(root);
            Check.Equal(2, colors.Count, $"色の違反の数({string.Join(", ", colors)})");
            Check.True(colors.Any(h => h.File == "src/App/Views/Bad.xaml"), "XAML の色を見つけない");
            Check.True(colors.Any(h => h.File == "src/App/Bad.cs"), "コードの色を見つけない");
        });
    }

    [Test]
    public void INV14_色の直書きの判定()
    {
        foreach (var s in new[] { "Background=\"#1E1E1E\"", "Color.FromArgb(\"#80FF8800\")", "// #abcdef の色", "<Color>#FFFFFFFF</Color>" })
            Check.Equal(1, SourceScan.FindHexColors("x", s).Count, $"「{s}」の色の数");
        foreach (var s in new[] { "#region 名前", "#if DEBUG", "#define TRACE", "#endregion", "&#123456;", "#FFF", "#12345", "#1234567", "#123456789", "#pragma warning disable" })
            Check.Equal(0, SourceScan.FindHexColors("x", s).Count, $"「{s}」を色と見なした");
    }

    [Test]
    public void INV14_srcに余白角丸時間の数値の直書きが無い()
    {
        Check.True(Inv14Files(RepoFiles.Root).Count > 0, "src のソースが無い");
        var problems = Inv14NumberProblems(RepoFiles.Root);
        Check.True(problems.Count == 0, $"トークン表に無い数値が {problems.Count} 件: {string.Join(" / ", problems.Take(20))}");
    }

    [Test]
    public void INV14_余白角丸時間の数値の判定()
    {
        // トークン表の値・0・参照・数でない書き方は通す
        string[] okXaml =
        [
            "<Border Margin=\"4,8\" Padding=\"12\" CornerRadius=\"8\"/>",
            "<Border Margin=\"0\" Padding=\"16,24,32,8\" CornerRadius=\"4,4,0,0\"/>",
            "<Rectangle RadiusX=\"4\" RadiusY=\"8\"/>",
            "<DoubleAnimation Duration=\"0:0:0.12\" BeginTime=\"0:0:0\"/>",
            "<DoubleAnimation Duration=\"00:00:00.160\"/><DoubleAnimation Duration=\"0:0:0.1\"/><DoubleAnimation Duration=\"0:0:0.14\"/>",
            "<SplineDoubleKeyFrame KeyTime=\"0:0:0.2\" KeySpline=\"0.23,1,0.32,1\"/>",
            "<SplineDoubleKeyFrame KeyTime=\"0:0:0.15\" KeySpline=\"0.77 0 0.175 1\"/>",
            "<SplineDoubleKeyFrame KeyTime=\"0:0:0.18\" KeySpline=\"0.32,0.72,0,1\"/><SplineColorKeyFrame KeySpline=\"0.25,0.1,0.25,1\"/>",
            "<Setter Property=\"Margin\" Value=\"8\"/>",
            "<Setter Property=\"Border.CornerRadius\" Value=\"4\"/>",
            "<Thickness x:Key=\"Space3\">12</Thickness>",
            "<Border Margin=\"{StaticResource Space2}\" CornerRadius=\"{DynamicResource RadiusCard}\"/>",
            "<DoubleAnimation Duration=\"Automatic\"/><DoubleAnimation Duration=\"Forever\"/><DiscreteDoubleKeyFrame KeyTime=\"Uniform\"/>",
            "<DiscreteDoubleKeyFrame KeyTime=\"50%\"/><DiscreteDoubleKeyFrame KeyTime=\"Paced\"/>",
            "<Border ToolTipService.ShowDuration=\"5000\"/><DoubleAnimation Storyboard.TargetProperty=\"Margin\"/>",
            // 単一引用符の属性・プロパティ要素・属性で値を書く要素
            "<Border Margin='8' Padding='12,4'/><Setter Property='Margin' Value='16'/>",
            "<SplineDoubleKeyFrame KeyTime='0:0:0.2' KeySpline='0.77,0,0.175,1'/>",
            "<Border.Margin>16</Border.Margin><DoubleAnimation.Duration>0:0:0.2</DoubleAnimation.Duration>",
            "<Border.Margin>\n  <Thickness>8</Thickness>\n</Border.Margin>",
            "<Thickness Left=\"8\" Top=\"4\" Right=\"0\" Bottom=\"4\"/><CornerRadius TopLeft=\"8\" TopRight=\"8\"/>",
            "<KeySpline ControlPoint1=\"0.23,1\" ControlPoint2=\"0.32,1\"/>",
            "<DoubleAnimation EasingFunction=\"{x:Null}\"/><EasingDoubleKeyFrame KeyTime=\"0:0:0.2\"/>",
        ];
        foreach (var s in okXaml)
        {
            var problems = TokenNumberProblems([("ok.xaml", s)], []);
            Check.True(problems.Count == 0, $"「{s}」で失敗した: {string.Join(" / ", problems)}");
        }
        Check.Equal(3, DesignTokenScan.ExtractXaml("x", okXaml[0]).Count, "Margin・Padding・CornerRadius を拾った数");
        Check.Equal(2, DesignTokenScan.ExtractXaml("x", okXaml[5]).Count, "KeyTime・KeySpline を拾った数");
        Check.Equal(0, DesignTokenScan.ExtractXaml("x", okXaml[11]).Count, "リソースの参照を拾った");

        string[] okCode =
        [
            "border.Margin = new Thickness(8);",
            "border.Padding = new(12, 4);",
            "border.CornerRadius = new CornerRadius(4);",
            "var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));",
            "a.Duration = new Duration(TimeSpan.FromMilliseconds(200));",
            "a.BeginTime = TimeSpan.FromSeconds(0.15);",
            "k.KeySpline = new KeySpline(0.25, 0.1, 0.25, 1);",
            "rect.RadiusX = 8;",
            "border.Margin = new Thickness(space2);",
            "var delay = TimeSpan.FromMilliseconds(130);", // UI の時間の書き方が無い行(GIF の遅延など)は数えない
            "var same = frame.Duration == TimeSpan.FromMilliseconds(130);",
            // 完全修飾名・初期化子・SetValue・複数行・new TimeSpan・FromTicks・名前付き引数・Point の KeySpline
            "border.Margin = new System.Windows.Thickness(8);",
            "border.Margin = new Thickness { Left = 8, Top = 4 };",
            "border.SetValue(Border.PaddingProperty, new Thickness(12));",
            "border.BorderThickness = new Thickness(1);", // 余白でない Thickness(枠線の stroke 1)は数えない
            "var a = new DoubleAnimation(0, 1,\n    TimeSpan.FromMilliseconds(120));",
            "a.Duration = new Duration(new TimeSpan(0, 0, 0, 0, 200));",
            "a.BeginTime = TimeSpan.FromTicks(1_500_000);",
            "var r = new CornerRadius(topLeft: 8, topRight: 8, bottomRight: 0, bottomLeft: 0);",
            "var s = new KeySpline(x1: 0.32, y1: 0.72, x2: 0, y2: 1);",
            "var s = new KeySpline(y2: 1, x1: 0.23, y1: 1, x2: 0.32);",
            "var s = new KeySpline(new Point(0.77, 0), new Point(0.175, 1));",
            "var s = new KeySpline { ControlPoint1 = new Point(0.25, 0.1), ControlPoint2 = new Point(0.25, 1) };",
            "a.EasingFunction = null;",
            "var delay = new TimeSpan(0, 0, 0, 0, 130);", // UI の時間の書き方が無い文は数えない
            "frames.Add(new GifFrame(image,\n    TimeSpan.FromMilliseconds(130)));",
        ];
        foreach (var s in okCode)
        {
            var problems = TokenNumberProblems([("ok.cs", s)], []);
            Check.True(problems.Count == 0, $"「{s}」で失敗した: {string.Join(" / ", problems)}");
        }
        Check.Equal(1, DesignTokenScan.ExtractCode("x", okCode[0]).Count, "new Thickness(8) を拾った数");
        Check.Equal(1, DesignTokenScan.ExtractCode("x", okCode[3]).Count, "アニメーションの時間を拾った数");
        Check.Equal(0, DesignTokenScan.ExtractCode("x", okCode[9]).Count, "UI でない TimeSpan を拾った");

        // トークン表に無い数値は 1 件ずつ失敗する
        string[] badXaml =
        [
            "<Border Margin=\"5\"/>",
            "<Border Padding=\"10,3\"/>",
            "<Border Margin=\"-4\"/>",
            "<Border Margin=\"auto\"/>",
            "<Border CornerRadius=\"6\"/>",
            "<Rectangle RadiusX=\"3\"/>",
            "<DoubleAnimation Duration=\"0:0:0.13\"/>",
            "<DoubleAnimation Duration=\"0:0:0.25\"/>",
            "<DoubleAnimation BeginTime=\"0:0:0.05\"/>",
            "<DoubleAnimation Duration=\"0:0:0.1205\"/>",
            "<EasingDoubleKeyFrame KeyTime=\"0:0:0.3\"/>",
            "<SplineDoubleKeyFrame KeySpline=\"0.5,0,0.5,1\"/>",
            "<Setter Property=\"Padding\" Value=\"6\"/>",
            "<Setter Value=\"2\" Property=\"Border.CornerRadius\"/>",
            "<Thickness x:Key=\"Gap\">2</Thickness>",
            "<Duration>0:0:0.3</Duration>",
            "<Border ToolTipService.InitialShowDelay=\"500\"/>",
            // 4 つ目だけがトークン表(ease-out の 0.23, 1, 0.32, 1)と違う KeySpline
            "<SplineDoubleKeyFrame KeySpline=\"0.23,1,0.32,0.9\"/>",
            "<KeySpline ControlPoint1=\"0.23,1\" ControlPoint2=\"0.32,0.9\"/>",
            "<KeySpline ControlPoint1=\"0.23,1\"/>",
            // 単一引用符の属性・プロパティ要素・属性で値を書く要素
            "<Border Margin='5'/>",
            "<Setter Property='Padding' Value='6'/>",
            "<Border.Margin>5</Border.Margin>",
            "<DoubleAnimation.Duration>0:0:0.13</DoubleAnimation.Duration>",
            "<ToolTipService.InitialShowDelay>400</ToolTipService.InitialShowDelay>",
            "<Thickness Left=\"5\"/>",
            "<CornerRadius TopLeft=\"6\"/>",
            // KeySpline 以外のイージング(§9.1 は KeySpline の 4 本だけ)
            "<CubicEase EasingMode=\"EaseOut\"/>",
            "<DoubleAnimation EasingFunction=\"{StaticResource EaseOutCubic}\"/>",
            "<DoubleAnimation.EasingFunction><CubicEase/></DoubleAnimation.EasingFunction>",
            "<a:ElasticEase Oscillations='3'/>",
        ];
        foreach (var s in badXaml)
        {
            var problems = TokenNumberProblems([("bad.xaml", s)], []);
            Check.Equal(1, problems.Count, $"「{s}」の違反の数({string.Join(" / ", problems)})");
        }

        string[] badCode =
        [
            "border.Margin = new Thickness(6);",
            "border.Padding = new(10, 3);",
            "var r = new CornerRadius(6);",
            "rect.RadiusY = 2;",
            "a.Duration = new Duration(TimeSpan.FromMilliseconds(130));",
            "var a = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3));",
            "k.KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250));",
            "var s = new KeySpline(0.42, 0, 0.58, 1);",
            "ToolTipService.SetInitialShowDelay(button, 500);",
            // 4 つ目だけがトークン表(ease-out の 0.23, 1, 0.32, 1)と違う KeySpline
            "var s = new KeySpline(0.23, 1, 0.32, 0.9);",
            "var s = new KeySpline(x1: 0.23, y1: 1, x2: 0.32, y2: 0.9);",
            "var s = new KeySpline(new Point(0.23, 1), new Point(0.32, 0.9));",
            "var s = new KeySpline { ControlPoint1 = new Point(0.23, 1), ControlPoint2 = new Point(0.32, 0.9) };",
            "var s = new KeySpline { ControlPoint1 = new Point(0.23, 1) };",
            // 完全修飾名・初期化子・SetValue
            "border.Margin = new System.Windows.Thickness(5);",
            "border.Margin = new Thickness { Left = 5 };",
            "border.Padding = new() { Top = 6 };",
            "var r = new CornerRadius { TopLeft = 6 };",
            "var r = new System.Windows.CornerRadius(6);",
            "border.SetValue(Border.PaddingProperty, new Thickness(5));",
            "rect.SetValue(Rectangle.RadiusXProperty, 3.0);",
            "button.SetValue(ToolTipService.InitialShowDelayProperty, 400);",
            // 複数行に分けた呼び出し・new TimeSpan・FromTicks・名前付き引数・初期化子の中の時間
            "var a = new DoubleAnimation(0, 1,\n    TimeSpan.FromMilliseconds(130));",
            "var a = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130));",
            "a.Duration = new Duration(new TimeSpan(0, 0, 0, 0, 130));",
            "a.BeginTime = new System.TimeSpan(1_300_000);",
            "a.BeginTime = TimeSpan.FromTicks(1_300_000);",
            "a.Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds: 130));",
            "var r = new CornerRadius(topLeft: 6, topRight: 6, bottomRight: 6, bottomLeft: 6);",
            "var a = new DoubleAnimation\n{\n    To = 1,\n    Duration = new Duration(TimeSpan.FromSeconds(0.3)),\n};",
            // KeySpline 以外のイージング(§9.1 は KeySpline の 4 本だけ)
            "var e = new CubicEase { EasingMode = EasingMode.EaseOut };",
            "a.EasingFunction = new System.Windows.Media.Animation.QuadraticEase();",
            "a.SetValue(DoubleAnimation.EasingFunctionProperty, ease);",
            "sealed class SnapEase : IEasingFunction",
        ];
        foreach (var s in badCode)
        {
            var problems = TokenNumberProblems([("bad.cs", s)], []);
            Check.Equal(1, problems.Count, $"「{s}」の違反の数({string.Join(" / ", problems)})");
        }

        // 複数行に分けたイージングのプロパティ要素は、行ごとに 1 件ずつ数える
        var multiLineEasing = TokenNumberProblems([("bad.xaml", "<DoubleAnimation.EasingFunction>\n  <local:SnapEase/>\n</DoubleAnimation.EasingFunction>")], []);
        Check.Equal(2, multiLineEasing.Count, $"複数行のイージングの違反の数({string.Join(" / ", multiLineEasing)})");
    }

    [Test]
    public void INV14_時間の読み取りはFromの複数の引数と3引数と名前付き引数のnew_TimeSpanを換算する()
    {
        // .NET 10 の TimeSpan(Microsoft.NETCore.App.Ref 10.0.9 の System.Runtime.xml で確かめた引数の名前と並び):
        // FromSeconds(seconds, milliseconds, microseconds)・FromMinutes(minutes, seconds, milliseconds, microseconds)・FromMilliseconds(milliseconds, microseconds)、
        // new TimeSpan(hours, minutes, seconds)・new TimeSpan(days, hours, minutes, seconds, milliseconds)。コンストラクターには既定値が無いので、名前付き引数はすべての引数に付ける。
        // Real は同じ書き方を本物の TimeSpan で作ったもの(拾った換算と本物の値が同じことも確かめる)。Problems は違反の数(0 は通る良い例、1 は悪い例)。
        var cases = new (string Code, decimal Ms, TimeSpan Real, int Problems)[]
        {
            // From… に引数を 2 つ以上渡す形
            ("a.Duration = new Duration(TimeSpan.FromSeconds(0, 120));", 120, TimeSpan.FromSeconds(0, 120), 0),
            ("a.BeginTime = TimeSpan.FromMinutes(0, 0, 150);", 150, TimeSpan.FromMinutes(0, 0, 150), 0),
            ("a.BeginTime = TimeSpan.FromMilliseconds(200, 0);", 200, TimeSpan.FromMilliseconds(200, 0), 0),
            ("a.Duration = new Duration(TimeSpan.FromSeconds(seconds: 0, milliseconds: 180));", 180, TimeSpan.FromSeconds(seconds: 0, milliseconds: 180), 0),
            ("a.Duration = new Duration(TimeSpan.FromSeconds(0, 130));", 130, TimeSpan.FromSeconds(0, 130), 1),
            ("a.BeginTime = TimeSpan.FromMinutes(0, 1, 0);", 1000, TimeSpan.FromMinutes(0, 1, 0), 1),
            ("a.BeginTime = TimeSpan.FromMilliseconds(120, 500);", 120.5m, TimeSpan.FromMilliseconds(120, 500), 1), // 2 つ目はマイクロ秒(1 ms 未満の端数)
            ("a.Duration = new Duration(TimeSpan.FromSeconds(seconds: 0, milliseconds: 250));", 250, TimeSpan.FromSeconds(seconds: 0, milliseconds: 250), 1),
            // 引数 3 個の new TimeSpan(時・分・秒)。トークン表の時間は 1 秒未満なので、通るのは 0 だけ
            ("a.BeginTime = new TimeSpan(0, 0, 0);", 0, new TimeSpan(0, 0, 0), 0),
            ("a.BeginTime = new TimeSpan(0, 0, 1);", 1000, new TimeSpan(0, 0, 1), 1),
            ("a.BeginTime = new TimeSpan(0, 1, 0);", 60000, new TimeSpan(0, 1, 0), 1),
            // 名前付き引数の new TimeSpan(並べ替えた形も)
            ("a.Duration = new Duration(new TimeSpan(days: 0, hours: 0, minutes: 0, seconds: 0, milliseconds: 160));", 160,
                new TimeSpan(days: 0, hours: 0, minutes: 0, seconds: 0, milliseconds: 160), 0),
            ("a.Duration = new Duration(new TimeSpan(milliseconds: 100, seconds: 0, minutes: 0, hours: 0, days: 0));", 100,
                new TimeSpan(milliseconds: 100, seconds: 0, minutes: 0, hours: 0, days: 0), 0),
            ("a.Duration = new Duration(new TimeSpan(days: 0, hours: 0, minutes: 0, seconds: 0, milliseconds: 130));", 130,
                new TimeSpan(days: 0, hours: 0, minutes: 0, seconds: 0, milliseconds: 130), 1),
            ("a.BeginTime = new TimeSpan(seconds: 2, minutes: 0, hours: 0);", 2000, new TimeSpan(seconds: 2, minutes: 0, hours: 0), 1),
        };
        foreach (var (code, ms, real, expectedProblems) in cases)
        {
            Check.Equal(ms, (decimal)real.Ticks / TimeSpan.TicksPerMillisecond, $"「{code}」の本物の TimeSpan の ms");
            var uses = DesignTokenScan.ExtractCode("x.cs", code);
            Check.Equal(1, uses.Count, $"「{code}」で拾った数");
            var raw = uses[0].Raw;
            Check.True(raw.EndsWith("ms", StringComparison.Ordinal), $"「{code}」を ms に換算していない: {raw}");
            Check.Equal(ms, decimal.Parse(raw[..^2], System.Globalization.CultureInfo.InvariantCulture), $"「{code}」を換算した ms");
            var problems = TokenNumberProblems([("x.cs", code)], []);
            Check.Equal(expectedProblems, problems.Count, $"「{code}」の違反の数({string.Join(" / ", problems)})");
        }
    }

    [Test]
    public void INV14_トークン定義のファイルは数値の検査から外せる()
    {
        const string tokens = "<Thickness x:Key=\"FocusGap\">2</Thickness>\n<sys:Double x:Key=\"Thumb\">20</sys:Double>\n<CornerRadius x:Key=\"Pill\">16</CornerRadius>";
        const string file = "src/GlintLoop.App/Theme/Tokens.Dark.xaml";
        Check.Equal(2, TokenNumberProblems([(file, tokens)], []).Count, "除外しないときの違反の数");
        Check.Equal(0, TokenNumberProblems([(file, tokens)], [file]).Count, "除外したファイルで違反が出た");
        Check.Equal(2, TokenNumberProblems([("src/GlintLoop.App/Views/Main.xaml", tokens)], [file]).Count, "除外していないファイルの違反の数");
    }

    // ---- INV-17 ----

    [Test]
    public void INV17_ソースにグローバルフックの語が無い()
    {
        NoHits(SourceScan.FindTermsInFiles(AllSources(), GlobalHookTerms, Excluded), "グローバルフックの語");
    }

    // ---- INV-18 ----

    [Test]
    public void INV18_テストのソースに入力とクリップボードとデータフォルダの語が無い()
    {
        var files = RepoFiles.Under(["tests"], RepoFiles.SourceExtensions);
        Check.True(files.Count > 0, "tests のソースが無い");
        NoHits(SourceScan.FindTermsInFiles(files, TestInputTerms, Excluded), "テストで使わない語");
    }

    // ---- AC-7(一部) ----

    // ---- AC-7 の probe-handoff のペイントの扱い ----
    // §8.4 測定 4: §8.1 に無い渡し先の列は「該当なし」と書く。§8.5: §8.1 に無いアプリの確認項目を足さない。
    // ペイントの語が出てよいのは、決まった 1 文の行と、表の見出しの行の 2 つだけ(どちらも行の全体が一致すること)。

    private const string HandoffProgram = "probes/probe-handoff/Program.cs";

    /// <summary>ペイントの列の扱いを書く、決まった 1 文の行(ソースの行を前後の空白を除いて比べる)。</summary>
    private const string PaintColumnLine = "W(\"     ペイントの列は §8.1 に無いので「該当なし」と書く(測らない)。\");";

    /// <summary>測定 4 の表の見出しの行と、その次の区切りの行。</summary>
    private const string HandoffTableHeaderLine = "W(\"| 載せ方 | Discord(デスクトップ) | LINE(PC) | Slack(デスクトップ) | X(ブラウザ) | ペイント |\");";

    private const string HandoffTableSeparatorLine = "W(\"|---|---|---|---|---|---|\");";

    /// <summary>ペイントを指す語。NFKC で半角カナと全角英字をそろえ、大文字と小文字を区別しない(mspaint・MS Paint・Paint 3D・pbrush も含む)。</summary>
    private static readonly string[] PaintTerms = ["ペイント", "ぺいんと", "paint", "pbrush"];

    private static bool HasPaintTerm(string line)
    {
        var n = line.Normalize(System.Text.NormalizationForm.FormKC);
        return PaintTerms.Any(t => n.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>probe-handoff のファイル(相対パスと中身)から、ペイントの語の扱いの問題を集める。</summary>
    private static List<string> PaintProblems(IEnumerable<(string File, string Text)> files)
    {
        var problems = new List<string>();
        int columnLines = 0, headerLines = 0;
        foreach (var (file, text) in files)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!HasPaintTerm(lines[i])) continue;
                var line = lines[i].Trim();
                var inProgram = file == HandoffProgram;
                if (inProgram && line == PaintColumnLine)
                {
                    columnLines++;
                }
                else if (inProgram && line == HandoffTableHeaderLine)
                {
                    headerLines++;
                    if (i + 1 >= lines.Length || lines[i + 1].Trim() != HandoffTableSeparatorLine)
                        problems.Add($"{file}:{i + 1} 表の見出しの次の行が区切りの行でない");
                }
                else
                {
                    problems.Add($"{file}:{i + 1} ペイントの語がある行: {line}");
                }
            }
        }
        if (columnLines != 1) problems.Add($"ペイントの列の扱いの 1 文が {columnLines} 回(1 回のはず)");
        if (headerLines != 1) problems.Add($"測定 4 の表の見出しの行が {headerLines} 回(1 回のはず)");
        return problems;
    }

    /// <summary>ペイントの扱いの検査が読む probe-handoff のソース(ルートからの相対パスと中身)。</summary>
    private static List<(string File, string Text)> HandoffFiles() =>
        RepoFiles.Under(["probes/probe-handoff"], RepoFiles.SourceExtensions)
            .Select(f => (RepoFiles.Relative(f), RepoFiles.ReadText(f)))
            .ToList();

    [Test]
    public void AC7_probe_handoffの手順が8_1に無いペイントを測らせない()
    {
        var files = HandoffFiles();
        Check.True(files.Any(f => f.Item1 == HandoffProgram), "probe-handoff の Program.cs が一覧に無い");
        var problems = PaintProblems(files);
        Check.True(problems.Count == 0, "ペイントの扱い: " + string.Join(" / ", problems));
    }

    [Test]
    public void AC7_ペイントの検査がprobe_handoffのcsをすべて集める()
    {
        // 検査の道具(RepoFiles)を使わずにフォルダを列挙し、bin・obj の下(ビルドが作る .cs)を除いた .cs がすべて集まっていることを確かめる
        var dir = Path.Combine(RepoFiles.Root, "probes", "probe-handoff");
        var expected = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();
        var collected = HandoffFiles().Select(f => f.File).ToHashSet(StringComparer.Ordinal);
        // 今ある 3 つは必ず入る(列挙が空で一致してしまわないように)
        foreach (var name in new[] { "probes/probe-handoff/HandoffData.cs", "probes/probe-handoff/MiniGif.cs", "probes/probe-handoff/Program.cs" })
            Check.True(expected.Contains(name), $"フォルダの列挙に {name} が無い");
        Check.True(expected.Count >= 3, $"probe-handoff の .cs が {expected.Count} 個(3 個以上のはず)");
        var missing = expected.Where(f => !collected.Contains(f)).ToList();
        Check.True(missing.Count == 0, $"ペイントの検査が集めていない .cs が {missing.Count} 個: {string.Join(", ", missing)}");
    }

    [Test]
    public void AC7_ペイントの扱いの判定()
    {
        var good = $"        W(\"## 測定 4 の手順\");\n        {PaintColumnLine}\n        W();\n        {HandoffTableHeaderLine}\n        {HandoffTableSeparatorLine}\n";
        Check.True(PaintProblems([(HandoffProgram, good)]).Count == 0, "決まった 2 行で失敗した: " + string.Join(" / ", PaintProblems([(HandoffProgram, good)])));

        // 手順や表の行にペイントを測らせる指示を足すと 1 件ずつ失敗する
        string[] badLines =
        [
            "W(\"  1. 渡し先のアプリ(Discord デスクトップ版・LINE PC 版・Slack デスクトップ版・ブラウザの X・ペイント)を開いておく(ペイントを入れていなければ該当なし)。\");",
            "W(\"     mspaint のキャンバスにも貼り、貼れたかどうかを表の右端の列に書く。\");",
            "W(\"| ペイントのキャンバスにも貼って右端に書く |  |  |  |  |  |\");",
            "W(\"     ﾍﾟｲﾝﾄ にも貼る。\");",
            "W(\"     MS Paint にも貼る。\");",
            "// pbrush で開いて確かめる",
        ];
        foreach (var bad in badLines)
        {
            var problems = PaintProblems([(HandoffProgram, good + "        " + bad + "\n")]);
            Check.Equal(1, problems.Count, $"「{bad}」の問題の数({string.Join(" / ", problems)})");
        }

        // ほかのファイルに書いても失敗する
        Check.Equal(1, PaintProblems([(HandoffProgram, good), ("probes/probe-handoff/HandoffData.cs", "// ペイントにも貼る\n")]).Count, "別のファイルのペイントの語");

        // 決まった 1 文を変える・消す、見出しの行の次に区切りが無い
        var changed = good.Replace(PaintColumnLine, "W(\"     ペイントの列は §8.1 に無いので「該当なし」と書く(測らない)。ペイントにも貼る。\");");
        Check.Equal(2, PaintProblems([(HandoffProgram, changed)]).Count, "1 文を書き換えたときの問題の数");
        var removed = good.Replace(PaintColumnLine, "W(\"     X はブラウザの投稿欄に貼る。\");");
        Check.Equal(1, PaintProblems([(HandoffProgram, removed)]).Count, "1 文を消したときの問題の数");
        var noSeparator = good.Replace(HandoffTableSeparatorLine, "W(\"| 測る |  |  |  |  |  |\");");
        Check.Equal(1, PaintProblems([(HandoffProgram, noSeparator)]).Count, "見出しの次に区切りが無いときの問題の数");
        // Program.cs でないファイルの 2 行は許さない(2 行の問題と、1 文・見出しが 0 回の問題)
        Check.Equal(4, PaintProblems([("probes/probe-handoff/Other.cs", good)]).Count, "Program.cs でないファイルの 2 行の問題の数");
    }

    [Test]
    public void AC7_srcのプロジェクトがprobesを参照しない()
    {
        var probesDir = Path.GetFullPath(Path.Combine(RepoFiles.Root, "probes")) + Path.DirectorySeparatorChar;
        var csprojs = RepoFiles.Under(["src"], [".csproj"]);
        Check.True(csprojs.Count > 0, "src に csproj が無い");
        var bad = new List<string>();
        foreach (var csproj in csprojs)
        {
            var doc = XDocument.Parse(RepoFiles.ReadText(csproj));
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName is "ProjectReference" or "Reference" or "Compile" or "None" or "Content"))
            {
                var include = (string?)e.Attribute("Include");
                if (string.IsNullOrEmpty(include)) continue;
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (full.StartsWith(probesDir, StringComparison.OrdinalIgnoreCase) || include.Contains("probe-", StringComparison.OrdinalIgnoreCase))
                    bad.Add($"{RepoFiles.Relative(csproj)}: {e.Name.LocalName} {include}");
            }
        }
        Check.True(bad.Count == 0, "src から probes への参照: " + string.Join(", ", bad));
    }

    [Test]
    public void AC7_probesに5つの測定用プロジェクトがある()
    {
        foreach (var name in new[] { "probe-gpu", "probe-skia", "probe-gif", "probe-handoff", "probe-ui" })
        {
            var path = Path.Combine(RepoFiles.Root, "probes", name, name + ".csproj");
            Check.True(File.Exists(path), $"probes/{name}/{name}.csproj が無い");
        }
    }
}
