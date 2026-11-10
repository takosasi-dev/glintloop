// perf-report の入口。測定の記録を読み、NFR の表を標準出力に出す(仕様書 §6.8 の「集計」)。引数: <キー> <パス>…
// 引数の間違いと、読めるファイルが 1 つも無いときは、表を出さず、標準エラーに理由を 1 行書いて終了コード 2 で終わる。
// 出力は BOM なしの UTF-8、改行 LF にする(Console の既定の改行と文字コードを通さない)。
using System.Text;
using GlintLoop.Tools.PerfReport;

if (args.Length < 2) return Fail("引数が足りません。使い方: perf-report <キー> <パス>…");
if (!ReportTable.IsKnownKey(args[0])) return Fail($"未知のキーです: {args[0]}");

var (files, unreadable) = PerfLog.ReadAll(args.Skip(1));
// 読めないパスの行は、表を出すときだけ書く(何も読めないときの標準エラーは 1 行だけ。AC-59 の (11) の (h))。
if (files.Count == 0) return Fail("読めるファイルが 1 つもありません");
foreach (var path in unreadable) WriteError($"読めないパスを読み飛ばします: {path}");

using (var stdout = Console.OpenStandardOutput()) Write(stdout, ReportTable.Render(args[0], files));
return 0;

static int Fail(string reason)
{
    WriteError(reason);
    return 2;
}

static void WriteError(string message)
{
    using var stderr = Console.OpenStandardError();
    Write(stderr, message.ReplaceLineEndings(" ") + "\n");
}

static void Write(Stream stream, string text)
{
    stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
    stream.Flush();
}
