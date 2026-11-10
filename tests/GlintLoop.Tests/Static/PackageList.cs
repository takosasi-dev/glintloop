// dotnet list package --include-transitive --format json --no-restore を子プロセスで実行し、全パッケージと版を返す(AC-9)。
// 復元済みの状態(テストがビルドできた状態)を前提にし、復元をしないのでネットワークに出ない。
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlintLoop.Tests.Static;

public static class PackageList
{
    private static readonly Lazy<IReadOnlyList<(string Project, string Id, string Version)>> Cached = new(Run);

    /// <summary>ソリューションの全プロジェクトの (プロジェクト, パッケージ, 解決された版)。</summary>
    public static IReadOnlyList<(string Project, string Id, string Version)> All => Cached.Value;

    private static IReadOnlyList<(string Project, string Id, string Version)> Run()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var psi = new ProcessStartInfo
        {
            FileName = !string.IsNullOrEmpty(hostPath) && File.Exists(hostPath) ? hostPath : "dotnet",
            WorkingDirectory = RepoFiles.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "list", Path.Combine(RepoFiles.Root, "GlintLoop.slnx"), "package",
                                  "--include-transitive", "--format", "json", "--no-restore" })
        {
            psi.ArgumentList.Add(a);
        }
        // CLI 自身が外へ送るもの(テレメトリ)を止め、親の dotnet run から引き継いだ MSBuild の設定を外す。
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var key in new[] { "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath" }) psi.Environment.Remove(key);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dotnet を起動できない");
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException("dotnet list package が 120 秒で終わらない");
        }
        var stderr = stderrTask.Result;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"dotnet list package が終了コード {p.ExitCode}(復元済みか確かめる): {stderr}{stdout}");
        return Parse(stdout);
    }

    /// <summary>dotnet list package の JSON(version 1)を読む。先頭に JSON 以外の行があれば読み飛ばす。</summary>
    public static IReadOnlyList<(string Project, string Id, string Version)> Parse(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("dotnet list package の出力に JSON が無い: " + output);
        using var doc = JsonDocument.Parse(output[start..]);
        var result = new List<(string, string, string)>();
        if (!doc.RootElement.TryGetProperty("projects", out var projects)) return result;
        foreach (var proj in projects.EnumerateArray())
        {
            var path = proj.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
            if (!proj.TryGetProperty("frameworks", out var fws)) continue;
            foreach (var fw in fws.EnumerateArray())
            {
                foreach (var kind in new[] { "topLevelPackages", "transitivePackages" })
                {
                    if (!fw.TryGetProperty(kind, out var pkgs)) continue;
                    foreach (var pkg in pkgs.EnumerateArray())
                    {
                        var id = pkg.GetProperty("id").GetString() ?? "";
                        var ver = pkg.TryGetProperty("resolvedVersion", out var rv) ? rv.GetString() ?? "" : "";
                        result.Add((path, id, ver));
                    }
                }
            }
        }
        return result;
    }
}
