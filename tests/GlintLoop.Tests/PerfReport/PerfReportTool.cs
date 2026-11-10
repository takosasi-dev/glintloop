// 集計ツール perf-report をプロセスとして起動する道具(AC-59 の (11))。標準出力と標準エラーをバイト列で受ける。
// exe の場所は、テストの csproj がビルドの結果から入れたアセンブリのメタデータで知る。
using System.Diagnostics;
using System.IO;
using System.Reflection;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.PerfReport;

/// <summary>1 回の起動の結果。</summary>
internal sealed record ToolResult(byte[] Stdout, byte[] Stderr, int ExitCode);

internal static class PerfReportTool
{
    private const string MetadataKey = "GlintLoop.PerfReportExe";

    /// <summary>ビルドした exe のパス。</summary>
    public static string ExePath()
    {
        var path = typeof(PerfReportTool).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == MetadataKey)?.Value;
        Check.True(!string.IsNullOrEmpty(path), $"テストのアセンブリにメタデータ {MetadataKey} が無い");
        Check.True(File.Exists(path), $"perf-report の exe が無い: {path}");
        return path!;
    }

    /// <summary>引数を渡して起動し、終わるまで待つ。</summary>
    public static ToolResult Run(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("perf-report を起動できない");
        var stdout = Task.Run(() => ReadAll(process.StandardOutput.BaseStream));
        var stderr = Task.Run(() => ReadAll(process.StandardError.BaseStream));
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new CheckFailedException("perf-report が 60 秒で終わらない");
        }
        return new ToolResult(stdout.Result, stderr.Result, process.ExitCode);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
