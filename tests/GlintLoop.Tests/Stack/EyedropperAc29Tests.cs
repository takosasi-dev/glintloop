// AC-29 の (8)(FR-29): レイヤーが 2 枚の文書で、色抜きの key のスポイトに合成の画素を与え、対象(2 枚目か全体)ごとの色の取り方を WARP で確かめる。
// 2 枚目が対象のときはソースの色(不透明度とブレンドは通さない)、全体のときは重ねた合成の色(線形光で混ぜる)。枠の外は、2 枚目では何も取らず、全体では合成の色。
using System.Windows;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Stack.StackTestKit;

namespace GlintLoop.Tests.Stack;

public sealed class EyedropperAc29Tests
{
    [Test]
    public void AC29_8_スポイトは対象が2枚目ならソースの色で全体なら合成の色()
    {
        using var app = TestApp.Start(gpu: GpuAdapterRequest.Warp);
        var m = Module(app);
        var s = app.Shell.Session;
        Import(app, 400, 300, Img.Solid(400, 300, 0, 0, 0), "base");
        Import(app, 100, 100, Gradient(100, 100, 128), "top");
        app.Ui(() =>
        {
            s.SetLayerPlacement(1, 0, 0, 100);
            s.SetLayerOpacity(1, 50);
            s.AddEffect(Target.OfLayer(1), "chroma");
            s.SetEffectParam(Target.OfLayer(1), 0, "key", new StringValue("#11223340"));
            s.AddEffect(Target.All, "chroma");
            s.SetEffectParam(Target.All, 0, "key", new StringValue("#44556677"));
        });
        string KeyOf(Target t) => app.Ui(() => ((StringValue)s.Document!.StackOf(t)[0].Get("key")!).Value);
        bool Pick(Target t, double x, double y)
        {
            app.Ui(() =>
            {
                s.Select(t);
                m.SelectRow(0);
                m.Eyedropper.Start(t, s.Document!.StackOf(t)[0].Salt, "key");
            });
            var ok = app.Ui(() => m.Eyedropper.PickAtAsync(new Point(x, y))).GetAwaiter().GetResult();
            UiHost.Idle();
            return ok;
        }
        // 2 枚目の枠は x が 150〜249、y が 100〜199
        Check.True(Pick(Target.OfLayer(1), 160, 110), "2 枚目で取れない");
        Check.Equal("#0A0A8040", KeyOf(Target.OfLayer(1)), "2 枚目のソースの (10, 10)");
        // 2 枚目の色抜きは、いま取った色を抜くので、全体の確かめの前に目をオフにする(AC-29 の (8) の文書は色抜きを 1 つだけ持つ)
        app.Ui(() => s.SetEffectEnabled(Target.OfLayer(1), 0, false));
        Check.True(Pick(Target.All, 160, 110), "全体で取れない");
        var all = KeyOf(Target.All);
        int Ch(int i) => Convert.ToInt32(all.Substring(1 + i * 2, 2), 16);
        Check.Near(5, Ch(0), 1, "全体の R(線形光で混ぜた 5)");
        Check.Near(5, Ch(1), 1, "全体の G");
        Check.Near(92, Ch(2), 1, "全体の B");
        Check.Equal("77", all[7..], "全体の AA は変えない");
        // 枠の外の点 (100, 50)
        Check.True(!Pick(Target.OfLayer(1), 100, 50), "2 枚目で枠の外を取った");
        Check.True(m.Eyedropper.IsActive, "枠の外でスポイトが終わった");
        Check.Equal("#0A0A8040", KeyOf(Target.OfLayer(1)), "枠の外で 2 枚目の key が変わった");
        app.Ui(() => m.Eyedropper.Cancel());
        Check.True(Pick(Target.All, 100, 50), "全体で (100, 50) が取れない");
        Check.Equal("#00000077", KeyOf(Target.All), "全体の (100, 50) は土台の黒");
        // 合成の外は全体でも取らない
        Check.True(!Pick(Target.All, 400, 10), "全体で合成の外を取った");
        app.Ui(() => m.Eyedropper.Cancel());
    }
}
