// INV-16 の検査方法のうち「プロセスの強制終了」: 書き出しの途中でプロセスを強制終了しても、最終的な名前のファイルは残らず、次の起動の片付けで .tmp が消える。
// 製品の exe は起動しない。テストのプログラム自身を子プロセスとして動かし(--inv16-child)、本物の ExportCache に途中まで書かせて止める。
using System.Diagnostics;
using System.IO;
using GlintLoop.Core.Export;
using GlintLoop.Core.Platform;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Core;

public sealed class Inv16KillTests
{
    public const string ChildArgument = "--inv16-child";
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string ReadyMarker = "ready.txt";

    /// <summary>子プロセスの本体: キャッシュの .tmp に途中まで書いて、準備ができた印を置き、強制終了されるまで止まる。</summary>
    public static int RunChild(string root)
    {
        var paths = AppPaths.UnderOneRoot(root);
        new ExportCache(paths.CacheExportsDir, SystemClock.Instance).Write(Key, ExportFormat.Gif, s =>
        {
            s.Write(new byte[4096]);
            s.Flush();
            File.WriteAllText(Path.Combine(root, ReadyMarker), "ready");
            Thread.Sleep(Timeout.Infinite);
            return new ExportWritten(1, 1, 1, 1, null);
        });
        return 1;
    }

    [Test]
    public void INV16_書き出しの途中でプロセスを強制終了しても完成のファイルは残らず次の起動でtmpが消える()
    {
        var root = Path.Combine(Path.GetTempPath(), "glint-inv16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(ChildArgument);
            psi.ArgumentList.Add(root);
            using var child = Process.Start(psi) ?? throw new InvalidOperationException("子プロセスを起動できない");
            try
            {
                var watch = Stopwatch.StartNew();
                var marker = Path.Combine(root, ReadyMarker);
                while (!File.Exists(marker))
                {
                    Check.True(!child.HasExited, "子プロセスが先に終わった");
                    Check.True(watch.Elapsed < TimeSpan.FromSeconds(60), "子プロセスが書き始めない");
                    Thread.Sleep(20);
                }
            }
            finally
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }

            var paths = AppPaths.UnderOneRoot(root);
            var cache = new ExportCache(paths.CacheExportsDir, SystemClock.Instance);
            Check.True(File.Exists(cache.TempPath(Key, ExportFormat.Gif)), "途中の .tmp が残っていない(強制終了の前に書いていない)");
            Check.True(!File.Exists(cache.FilePath(Key, ExportFormat.Gif)), "不完全なファイルが最終的な名前で残った");
            Check.True(!File.Exists(cache.MetaPath(Key)), "完成していないのに json が残った");
            Check.True(cache.TryGet(Key, ExportFormat.Gif) is null, "完成として扱った");

            StartupMaintenance.Run(paths);

            Check.True(!File.Exists(cache.TempPath(Key, ExportFormat.Gif)), "次の起動で .tmp が消えない");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
