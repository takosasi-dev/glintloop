// AC-62 の (1)〜(7): 配布物(publish の出力)とリポジトリ(git ls-files)を調べる。publish を伴う検査は Slow(--all のときだけ)。
// publish した GlintLoop.exe は起動しない(INV-18)。版の画面とログは、テストの中で一時フォルダ・偽の時計・WARP の製品の窓を組み立てて読む。
// 判定の道具は Ac62Checks にあり、壊した入力で落ちることを各項目で確かめる。
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Xml.Linq;
using GlintLoop.App.Screens;
using GlintLoop.App.Startup;
using GlintLoop.Core.Platform;
using GlintLoop.Render;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Static;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Dist;

public sealed class DistAc62Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static string Root => RepoFiles.Root;

    private static string ReadRepo(string relative) => RepoFiles.ReadText(Path.Combine(Root, relative));

    // ================================================================ (1)〜(4) publish の出力

    [Test(Slow = true)]
    public void AC62_1_2_3_4_publishの出力はexe1つで200MB以下でマニフェストと版が合う()
    {
        var temp = Directory.CreateTempSubdirectory("glintloop-dist-");
        try
        {
            // 追跡しているファイルの写しで publish する(リポジトリの obj を書き換えない。Ac62Checks.CopyTracked)
            var copy = Path.Combine(temp.FullName, "src");
            Ac62Checks.CopyTracked(Root, copy);
            var outDir = Path.Combine(temp.FullName, "out");
            var emptySource = Directory.CreateDirectory(Path.Combine(temp.FullName, "empty-source")).FullName;
            var (code, output) = Ac62Checks.Publish(copy, outDir, emptySource);
            Check.Equal(0, code, "publish の終了コード(出力の末尾: " + output[Math.Max(0, output.Length - 2000)..] + ")");

            // (1) 出力は GlintLoop.exe の 1 つだけ(ほかが出たら Q-22 を質問する)
            var files = Directory.EnumerateFileSystemEntries(outDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(outDir, f)).Order(StringComparer.Ordinal).ToList();
            Check.SequenceEqual(["GlintLoop.exe"], files, "publish の出力");
            var exe = Path.Combine(outDir, "GlintLoop.exe");

            // (2) 200,000,000 バイト以下(NFR-1)
            var size = new FileInfo(exe).Length;
            Console.WriteLine($"AC62: GlintLoop.exe は {size:N0} バイト");
            Check.True(size <= 200_000_000, $"GlintLoop.exe が {size:N0} バイト(200,000,000 バイト以下のはず)");

            // (3) 埋め込まれたマニフェスト
            var manifest = Ac62Checks.ReadEmbeddedManifest(exe);
            Check.True(manifest is not null, "GlintLoop.exe に RT_MANIFEST が無い");
            var problems = Ac62Checks.ManifestProblems(manifest!);
            Check.True(problems.Count == 0, "マニフェスト: " + string.Join(" / ", problems));

            // (4) 製品の版(ファイルの版の情報を読むだけ)
            Check.Equal(Ac62Checks.PropsVersion(Root), FileVersionInfo.GetVersionInfo(exe).ProductVersion, "GlintLoop.exe の ProductVersion");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // ================================================================ (3) マニフェストの読み出しと判定

    [Test]
    public void AC62_3_ビルドしたexeのマニフェストを読めてマニフェストの無いdllはnull()
    {
        // テストの出力にある製品の exe(ビルドの apphost。publish の exe と同じ作り方で資源を入れる)。起動せずに読むだけ
        var built = Path.Combine(AppContext.BaseDirectory, "GlintLoop.exe");
        Check.True(File.Exists(built), "テストの出力に GlintLoop.exe が無い");
        var manifest = Ac62Checks.ReadEmbeddedManifest(built);
        Check.True(manifest is not null, "ビルドした GlintLoop.exe に RT_MANIFEST が無い");
        Check.True(Ac62Checks.ManifestProblems(manifest!).Count == 0, "ビルドした exe のマニフェスト: " + string.Join(" / ", Ac62Checks.ManifestProblems(manifest!)));

        // ライブラリの dll にはマニフェストが無い(読み出しが何でも返すわけではない)
        Check.True(Ac62Checks.ReadEmbeddedManifest(typeof(AppPaths).Assembly.Location) is null, "GlintLoop.Core.dll からマニフェストが読めた");
    }

    [Test]
    public void AC62_3_マニフェストの判定は壊した値で落ちる()
    {
        var good = ReadRepo("src/GlintLoop.App/app.manifest");
        Check.True(Ac62Checks.ManifestProblems(good).Count == 0, "app.manifest で失敗した: " + string.Join(" / ", Ac62Checks.ManifestProblems(good)));
        Check.True(Ac62Checks.ManifestProblems(good.Replace(">PerMonitorV2<", ">PerMonitorV2, PerMonitor<")).Count == 0, "並びの最初が PerMonitorV2 なのに失敗した");
        string[] broken =
        [
            good.Replace("level=\"asInvoker\"", "level=\"requireAdministrator\""),
            good.Replace("uiAccess=\"false\"", "uiAccess=\"true\""),
            good.Replace("uiAccess=\"false\"", ""),
            good.Replace(">PerMonitorV2<", ">System<"),
            good.Replace(">PerMonitorV2<", ">PerMonitor, PerMonitorV2<"),
            good.Replace(">true</longPathAware>", ">false</longPathAware>"),
            Without(good, "<dpiAwareness[^>]*>[^<]*</dpiAwareness>"),
            Without(good, "<longPathAware[^>]*>[^<]*</longPathAware>"),
        ];
        foreach (var xml in broken)
        {
            Check.True(xml != good, "壊したつもりの入力が元と同じ");
            Check.Equal(1, Ac62Checks.ManifestProblems(xml).Count, "壊したマニフェストの問題の数");
        }
        Check.Equal(2, Ac62Checks.ManifestProblems(Without(good, "<requestedExecutionLevel[^>]*/>")).Count, "requestedExecutionLevel が無いときの問題の数");
    }

    private static string Without(string text, string pattern) => System.Text.RegularExpressions.Regex.Replace(text, pattern, "");

    // ================================================================ (4) 版

    [Test]
    public void AC62_4_版は0yzの形で情報の版にコミットを付けずCHANGELOGの最初の見出しと同じ()
    {
        var version = Ac62Checks.PropsVersion(Root);
        Check.True(Ac62Checks.IsZeroMajorSemVer(version), $"Directory.Build.props の Version「{version}」が 0.y.z の形でない");
        var app = XDocument.Parse(ReadRepo("src/GlintLoop.App/GlintLoop.App.csproj"));
        var flag = app.Descendants().Where(e => e.Name.LocalName == "IncludeSourceRevisionInInformationalVersion").Select(e => e.Value.Trim()).ToList();
        Check.SequenceEqual(["false"], flag, "GlintLoop.App.csproj の IncludeSourceRevisionInInformationalVersion");
        var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Check.Equal(version, informational, "GlintLoop.dll の InformationalVersion(+ 以降が付かない)");
        Check.Equal(version, AppVersion.Current, "AppVersion.Current");
        Check.Equal(version, Ac62Checks.FirstChangelogVersion(ReadRepo("CHANGELOG.md")), "CHANGELOG.md の最初の見出しの版");
    }

    [Test]
    public void AC62_4_版の判定は形の違う版と日付の無い見出しを受け付けない()
    {
        foreach (var ok in new[] { "0.5.0", "0.0.0", "0.10.12" }) Check.True(Ac62Checks.IsZeroMajorSemVer(ok), ok + " を受け付けない");
        foreach (var bad in new[] { "1.0.0", "0.5", "0.05.0", "0.5.0-beta", "0.5.0+abc", "v0.5.0", " 0.5.0", "0.5.0.0" })
            Check.True(!Ac62Checks.IsZeroMajorSemVer(bad), bad + " を受け付けた");
        Check.Equal("0.4.0", Ac62Checks.FirstChangelogVersion("# CHANGELOG\n\n## [Unreleased]\n\n## [0.4.0] - 2026-11-01\n\n## [0.3.0] - 2026-10-01\n"), "日付の無い見出しを飛ばした最初の版");
        Check.Equal(null, Ac62Checks.FirstChangelogVersion("# CHANGELOG\n\n## 0.4.0 - 2026-11-01\n### [0.3.0] - 2026-10-01\n"), "決まった形の見出しが無いとき");
    }

    [Test]
    public void AC62_4_設定から開いたこのソフトについての版とapp_startのvがVersionと同じ()
    {
        var version = Ac62Checks.PropsVersion(Root);
        var root = Directory.CreateTempSubdirectory("glintloop-dist-ui-").FullName;
        var paths = AppPaths.UnderOneRoot(root);
        var clock = new FakeClock();
        // TestApp と同じ組み立てで、版だけを製品と同じ AppVersion.Current にする(製品の Production は本物のデータフォルダを使うので使わない)
        var services = new ShellServices(paths, clock, clock, new FakeOsInfo(), new FakeModifierKeys(), new FakeFileDialogs(), new FakeClipboard(), GpuAdapterRequest.Warp, AppVersion.Current)
        {
            Offscreen = true,
            FileSystem = new FakeFileSystem(),
            DragRunner = new FakeDragRunner(),
            CrashDialog = new FakeCrashDialog(),
            ProcessExit = new FakeProcessExit(),
        };
        var shell = UiHost.Invoke(() =>
        {
            var s = AppShell.Create(Application.Current, services);
            s.Show();
            return s;
        });
        try
        {
            UiHost.Idle();
            var hwnd = UiHost.Invoke(() => new WindowInteropHelper(shell.Window).Handle);
            shell.RenderReady!.GetAwaiter().GetResult();

            var screens = UiHost.Invoke(() => shell.Modules.OfType<ScreensModule>().Single());
            UiHost.Invoke(screens.ShowSettings);
            Check.True(UiHost.WaitUntil(() => screens.Settings is not null, Wait), "設定の画面が出ない");
            UiHost.Idle();
            Uia.Invoke(Uia.Find(Uia.Root(hwnd), "このソフトについて"));
            Check.True(UiHost.WaitUntil(() => shell.Dialogs.Top is AboutView, Wait), "このソフトについてが出ない");
            UiHost.Idle();

            // 画面の版の表示を UIA で読む(TextBlock の名前は文字列そのもの)
            var expected = ScreenTexts.Get("about.version") + " " + version;
            var shown = UiHost.Invoke(() => ((AboutView)shell.Dialogs.Top!).VersionText.Text);
            Check.True(Uia.TryFind(Uia.Root(hwnd), expected) is not null, $"UIA で「{expected}」が見つからない(画面は「{shown}」)");
            // 画面の MIT の本文が LICENSE と同じ(依存の表は FR103 の検査が THIRD-PARTY-NOTICES.md と欄ごとに比べている)
            var license = ReadRepo("LICENSE").Replace("\r\n", "\n");
            Check.Equal(license.TrimEnd('\n'), UiHost.Invoke(() => ((AboutView)shell.Dialogs.Top!).LicenseText.Text), "画面の LICENSE の本文");

            // ログの app_start の v
            string? AppStartVersion()
            {
                if (!Directory.Exists(paths.LogsDir)) return null;
                foreach (var file in Directory.GetFiles(paths.LogsDir, "*.jsonl"))
                {
                    foreach (var line in RepoFiles.ReadText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.GetProperty("event").GetString() == "app_start") return doc.RootElement.GetProperty("v").GetString();
                    }
                }
                return null;
            }
            Check.True(UiHost.WaitUntil(() => AppStartVersion() is not null, Wait), "ログに app_start が無い");
            Check.Equal(version, AppStartVersion(), "ログの app_start の v");
        }
        finally
        {
            UiHost.Invoke(() =>
            {
                shell.Window.Close();
                shell.Dispose();
            });
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ================================================================ (5) ライセンス

    [Test]
    public void AC62_5_LICENSEはMITの全文で名義がQ3の回答と同じ()
    {
        var problems = Ac62Checks.LicenseProblems(ReadRepo("LICENSE"));
        Check.True(problems.Count == 0, "LICENSE: " + string.Join(" / ", problems));
    }

    [Test]
    public void AC62_5_LICENSEの判定は名義や本文を変えると落ちる()
    {
        var good = Ac62Checks.MitText;
        Check.True(Ac62Checks.LicenseProblems(good.Replace("\n", "\r\n")).Count == 0, "CRLF にしただけで失敗した");
        Check.True(Ac62Checks.LicenseProblems(good.Replace("copy\nof this", "copy of\nthis")).Count == 0, "折り返しの位置を変えただけで失敗した");
        Check.Equal(2, Ac62Checks.LicenseProblems(good.Replace("takosasi-dev", "someone-else")).Count, "名義を変えたときの問題の数");
        Check.Equal(2, Ac62Checks.LicenseProblems(good.Replace("(c) 2026", "(c) 2025")).Count, "年を変えたときの問題の数");
        Check.Equal(1, Ac62Checks.LicenseProblems(good.Replace("free of charge, ", "")).Count, "本文の語を消したときの問題の数");
        Check.Equal(1, Ac62Checks.LicenseProblems(good.Replace("MIT License", "BSD License")).Count, "表題を変えたときの問題の数");
        Check.Equal(1, Ac62Checks.LicenseProblems(good + "\nExtra clause.\n").Count, "条項を足したときの問題の数");
    }

    [Test]
    public void AC62_5_通知の表に全パッケージと同梱フォントがある()
    {
        var notices = ReadRepo("THIRD-PARTY-NOTICES.md");
        var problems = NoticesCheck.Evaluate(notices, PackageList.All.Select(p => (p.Id, p.Version)).ToList());
        Check.True(problems.Count == 0, "THIRD-PARTY-NOTICES.md: " + string.Join(" / ", problems));
        // 同梱フォントは、ファイル名が通知に書かれていること(今は追跡しているフォントが 0 個)
        var fonts = Ac62Checks.TrackedFiles(Root).Where(f => Ac62Checks.FontExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        Console.WriteLine($"AC62: 追跡している同梱フォント {fonts.Count} 個");
        var missing = fonts.Where(f => !notices.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)).ToList();
        Check.True(missing.Count == 0, "THIRD-PARTY-NOTICES.md に無いフォント: " + string.Join(", ", missing));
    }

    // ================================================================ (6) リポジトリ

    [Test]
    public void AC62_6_追跡しているファイルが改行と先頭のコメントと画像と参照の基準に合う()
    {
        var tracked = Ac62Checks.TrackedFiles(Root);
        Check.True(tracked.Contains("GlintLoop.slnx") && tracked.Contains(".gitattributes"), "git ls-files の結果にルートのファイルが無い");
        var files = tracked.Select(f => (f, RepoFiles.ReadBytes(Path.Combine(Root, f)))).ToList();
        var images = tracked.Where(f => Ac62Checks.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        Console.WriteLine($"AC62: 追跡しているファイル {tracked.Count} 個、画像 {images.Count} 枚{(images.Count > 0 ? ": " + string.Join(", ", images) : "")}");
        var problems = Ac62Checks.RepoProblems(files);
        Check.True(problems.Count == 0, $"リポジトリの問題 {problems.Count} 件: " + string.Join(" / ", problems.Take(30)));
    }

    [Test]
    public void AC62_6_リポジトリの判定は壊したファイルで1件ずつ落ちる()
    {
        static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        const string Cs = "// 日本語の説明\nnamespace A;\n";
        (string, byte[])[] good =
        [
            (".gitattributes", B("* text=auto eol=lf\n")),
            (".gitignore", B("bin/\n")),
            ("src/A/A.cs", B(Cs)),
            ("src/A/B.cs", B("// 一行目\n// 二行目\n// 三行目\nnamespace A;\n")),
            ("src/A/Shaders/a.hlsl", B("// シェーダー\nfloat4 main() : SV_Target { return 0; }\n")),
            ("src/A/View.xaml", B("<?xml version=\"1.0\"?>\n<!-- 画面の説明 -->\n<Grid />\n")),
            ("src/A/Plain.xaml", B("<!-- 説明 -->\n<!-- 続き -->\n<Grid />\n")),
            ("src/A/A.csproj", B("<Project />\n")),
            ("tests/assets/made.png", [0x89, 0x50]),
            ("docs/images/screen.png", [0x89, 0x50]),
            ("src/GlintLoop.App/app.ico", [0, 0, 1, 0]),
        ];
        Check.True(Ac62Checks.RepoProblems(good).Count == 0, "正しい組で失敗した: " + string.Join(" / ", Ac62Checks.RepoProblems(good)));

        (string What, (string, byte[])[] Files)[] broken =
        [
            ("gitattributes が 2 行", Replace(good, ".gitattributes", B("* text=auto eol=lf\n*.png binary\n"))),
            ("gitattributes が別の設定", Replace(good, ".gitattributes", B("* text=auto\n"))),
            ("gitattributes が無い", good.Where(f => f.Item1 != ".gitattributes").ToArray()),
            ("BOM", Replace(good, "src/A/A.cs", [0xEF, 0xBB, 0xBF, .. B(Cs)])),
            ("CR", Replace(good, "src/A/A.cs", B(Cs.Replace("\n", "\r\n")))),
            ("gitignore の CR", Replace(good, ".gitignore", B("bin/\r\n"))),
            ("md の BOM", [.. good, ("README.md", [0xEF, 0xBB, 0xBF, 0x23])]),
            ("json の CR", [.. good, ("src/A/a.json", B("{}\r\n"))]),
            ("yml の CR", [.. good, ("x.yml", B("a: 1\r\n"))]),
            ("bin の下", [.. good, ("src/A/bin/Release/A.dll", [0])]),
            ("obj の下", [.. good, ("src/A/obj/project.assets.json", B("{}"))]),
            (".vs の下", [.. good, (".vs/x/v.suo", [0])]),
            ("*.user", [.. good, ("src/A/A.csproj.user", [0])]),
            ("cs の先頭がコメントでない", Replace(good, "src/A/A.cs", B("namespace A;\n"))),
            ("cs のコメントが 4 行", Replace(good, "src/A/B.cs", B("// 一\n// 二\n// 三\n// 四\nnamespace A;\n"))),
            ("cs のコメントが英語だけ", Replace(good, "src/A/A.cs", B("// English only\nnamespace A;\n"))),
            ("cs の 2 行目が英語だけ", Replace(good, "src/A/B.cs", B("// 一行目\n// second\nnamespace A;\n"))),
            ("cs の先頭が空行", Replace(good, "src/A/A.cs", B("\n" + Cs))),
            ("hlsl のコメントが無い", Replace(good, "src/A/Shaders/a.hlsl", B("float4 main() : SV_Target { return 0; }\n"))),
            ("xaml のコメントが無い", Replace(good, "src/A/View.xaml", B("<?xml version=\"1.0\"?>\n<Grid />\n"))),
            ("xaml のコメントが英語だけ", Replace(good, "src/A/Plain.xaml", B("<!-- view -->\n<Grid />\n"))),
            ("xaml のコメントが 4 行", Replace(good, "src/A/Plain.xaml", B("<!-- 一 -->\n<!-- 二 -->\n<!-- 三 -->\n<!-- 四 -->\n<Grid />\n"))),
            ("tests/assets の外の画像", [.. good, ("probes/probe-gif/sample.jpg", [0xFF, 0xD8])]),
            ("docs/images の外の docs の画像", [.. good, ("docs/sample.png", [0x89, 0x50])]),
            ("tests の直下の画像", [.. good, ("tests/photo.webp", [0])]),
            ("2 個目のアイコン", [.. good, ("src/GlintLoop.App/Assets/icon.png", [0x89])]),
            ("src の csproj が probes を参照", Replace(good, "src/A/A.csproj", B("<Project><ItemGroup><ProjectReference Include=\"..\\..\\probes\\probe-gpu\\probe-gpu.csproj\" /></ItemGroup></Project>\n"))),
        ];
        foreach (var (what, files) in broken)
        {
            var problems = Ac62Checks.RepoProblems(files);
            Check.Equal(1, problems.Count, $"{what} の問題の数({string.Join(" / ", problems)})");
        }
    }

    private static (string, byte[])[] Replace((string, byte[])[] files, string path, byte[] bytes) =>
        files.Select(f => f.Item1 == path ? (path, bytes) : f).ToArray();

    // ================================================================ (7) 逸脱不可要件

    /// <summary>このアセンブリの [Test] の付いたメソッドの名前。</summary>
    private static List<string> TestMethodNames() =>
        typeof(DistAc62Tests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

    [Test]
    public void AC62_7_AC9とAC18のINVのそれぞれに名前で対応するテストがある()
    {
        // 検査の中身は各テストが持つ。ここではどの INV にもテストがあることを確かめ、同じコミットでの全体の結果は --all の実行で取る
        var names = TestMethodNames();
        var missing = Ac62Checks.InvsWithoutTest(names, Ac62Checks.Ac9Invs.Concat(Ac62Checks.Ac18Invs));
        Check.True(missing.Count == 0, "テストの無い INV: " + string.Join("・", missing.Select(n => "INV-" + n)));
    }

    [Test]
    public void AC62_7_INVの対応の判定は欠けた番号を見つけ番号の途中で一致させない()
    {
        string[] names = ["INV1_a", "AC17_8_INV8_INV6_b", "INV18_c", "AC43_1_INV9_d", "INV110_e"];
        Check.SequenceEqual([2, 11], Ac62Checks.InvsWithoutTest(names, [1, 2, 6, 8, 9, 11, 18]), "テストの無い INV");
        Check.SequenceEqual([1], Ac62Checks.InvsWithoutTest(["INV18_x", "INV10_y", "XINV1_z"], [1]), "INV18・INV10・XINV1 を INV1 と数えない");
    }
}
