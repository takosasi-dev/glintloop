// コンソールのテストランナー(仕様書 D-39。`dotnet run --project tests/GlintLoop.Tests`)。
// [Test] の付いたメソッドを STA スレッドで順に実行し、失敗が 1 つでもあれば終了コード 1 を返す。
// 引数: --filter <部分文字列>(名前で絞る)、--all(Slow の検査も走らせる)、--list(一覧だけ出す)、--soak(Soak の検査だけを走らせる。付けなければ --all でも対象にしない)、--soak-minutes <分>(Soak の長さ。60 分未満は判定せず終了コード 2)。
using System.Diagnostics;
using System.Reflection;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args is [GlintLoop.Tests.Core.Inv16KillTests.ChildArgument, var childRoot]) return GlintLoop.Tests.Core.Inv16KillTests.RunChild(childRoot);
        string? filter = null;
        var all = false;
        var listOnly = false;
        var soak = false;
        int? soakMinutes = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
                case "--all": all = true; break;
                case "--list": listOnly = true; break;
                case "--soak": soak = true; break;
                case "--soak-minutes":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var minutes) || minutes < 1)
                    {
                        Console.WriteLine("--soak-minutes には 1 以上の整数を渡す");
                        return 1;
                    }
                    soakMinutes = minutes;
                    break;
            }
        }
        if (soakMinutes is not null && !soak)
        {
            Console.WriteLine("--soak-minutes は --soak と一緒に使う");
            return 1;
        }
        if (soakMinutes is { } m) GlintLoop.Tests.Soak.SoakOptions.Minutes = m;

        var tests = typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => (Type: t, Method: m, Attr: m.GetCustomAttribute<TestAttribute>())))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Type, x.Method, Attr: x.Attr!, Name: $"{x.Type.Name}.{x.Method.Name}"))
            .Where(x => filter is null || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(x => soak ? x.Attr.Soak : !x.Attr.Soak && (all || !x.Attr.Slow))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        if (listOnly)
        {
            foreach (var t in tests) Console.WriteLine(t.Name);
            return 0;
        }

        var failed = 0;
        var notJudged = 0;
        var total = Stopwatch.StartNew();
        foreach (var (type, method, _, name) in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var instance = method.IsStatic ? null : Activator.CreateInstance(type);
                method.Invoke(instance, null);
                GlintLoop.Tests.Ui.PopupGuard.ThrowIfOpened();
                Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (TargetInvocationException e) when (e.InnerException is NotJudgedException)
            {
                notJudged++;
                Console.WriteLine($"NOT JUDGED {name} ({sw.ElapsedMilliseconds} ms): {e.InnerException.Message}");
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                failed++;
                Console.WriteLine($"FAIL {name} ({sw.ElapsedMilliseconds} ms): {Describe(e.InnerException)}");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"FAIL {name} ({sw.ElapsedMilliseconds} ms): {Describe(e)}");
            }
        }

        var notJudgedText = notJudged == 0 ? "" : $"、未判定 {notJudged}";
        Console.WriteLine($"{tests.Count - failed - notJudged}/{tests.Count} 通過、失敗 {failed}{notJudgedText}({total.Elapsed.TotalSeconds:F1} 秒)");
        return failed > 0 ? 1 : notJudged > 0 ? 2 : 0;
    }

    private static string Describe(Exception e) =>
        e is CheckFailedException ? e.Message : $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}";
}
