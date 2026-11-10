// 製品の入口(Main): 起動の処理(AppStartup)に、製品の実装を渡して呼ぶだけ。多重起動の判定に負けた起動は Application を作らない。
using GlintLoop.App.Startup;

namespace GlintLoop.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppStartup.Run(args, ProductionStartup.Ports());
}
