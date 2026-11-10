// 描画の優先度(§9.9 の 8・NFR-4)のテスト: AC-39 の (6) の前半(待ち行列の順)と、実行中の仕事を中断しないこと。GPU を使わない(RenderThread だけ)。
// 専用スレッドを門で塞いでいる間に、書き出し → サムネイル → 表示の順に積み、処理の順が 表示 → サムネイル → 書き出し になることを見る。
// あわせて、組み込み変数 $frame と、モーションステージでの $width・$height・$shortSide の読み替え(Q-A)の詰め方を確かめる。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Render;

public sealed class RenderPriorityAc39Tests
{
    [Test]
    public void AC39_6_待ち行列は表示サムネイル書き出しの順で実行中の仕事は中断しない()
    {
        using var thread = new RenderThread("test.render");
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var order = new List<string>();
        var busy = thread.InvokeAsync(() =>
        {
            started.Set();
            gate.Wait();
            lock (order) order.Add("busy");
        }, RenderPriority.Export);
        Check.True(started.Wait(5000), "専用スレッドが仕事を始めない");
        var tasks = new List<Task>();
        void Add(string name, RenderPriority p) => tasks.Add(thread.InvokeAsync(() => { lock (order) order.Add(name); }, p));
        int pendingDisplay, pendingExport;
        try
        {
            Add("export1", RenderPriority.Export);
            Add("thumb1", RenderPriority.Thumbnail);
            Add("display1", RenderPriority.Display);
            Add("export2", RenderPriority.Export);
            Add("thumb2", RenderPriority.Thumbnail);
            Add("display2", RenderPriority.Display);
            pendingDisplay = thread.Pending(RenderPriority.Display);
            pendingExport = thread.Pending(RenderPriority.Export);
        }
        finally
        {
            gate.Set(); // 検査が落ちても専用スレッドを止めたままにしない(破棄が終わらなくなる)
        }
        Check.True(Task.WaitAll([busy, .. tasks], 5000), "仕事が終わらない");
        Check.Equal(2, pendingDisplay, "表示の待ち");
        Check.Equal(2, pendingExport, "書き出しの待ち");
        // 実行中の仕事(busy)は割り込まれずに最後まで走り、そのあと優先度の順(同じ優先度の中は先着順)
        Check.SequenceEqual(new[] { "busy", "display1", "display2", "thumb1", "thumb2", "export1", "export2" }, order, "処理の順");
    }

    [Test]
    public void 破棄は積まれた仕事を優先度の順に終えてから止まる()
    {
        var thread = new RenderThread("test.render2");
        using var gate = new ManualResetEventSlim();
        var order = new List<string>();
        var t0 = thread.InvokeAsync(() => gate.Wait());
        var t1 = thread.InvokeAsync(() => { lock (order) order.Add("export"); }, RenderPriority.Export);
        var t2 = thread.InvokeAsync(() => { lock (order) order.Add("display"); });
        gate.Set();
        thread.Dispose();
        Check.True(t0.IsCompleted && t1.IsCompleted && t2.IsCompleted, "破棄の前に積んだ仕事が終わっていない");
        Check.SequenceEqual(new[] { "display", "export" }, order, "破棄のときの順");
        Check.Throws<ObjectDisposedException>(() => thread.InvokeAsync(() => 1), "破棄のあとの依頼");
    }

    [Test]
    public void QA_frameとモーションステージのwidthheightshortSideの詰め方()
    {
        var def = new EffectDef(1, "m", "m", EffectStage.Motion, "g", [], AmountMode.None, [], true, true, WaveKind.Sine, SizeRule.Same,
            AuxKind.None, [], []);
        var pass = new PassDef("p", "spin", ["src"], 1, PassRepeat.Once, null,
            [new("a", "$frame"), new("b", "$width"), new("c", "$height"), new("d", "$shortSide"), new("e", "$texel")]);
        float F(byte[] bytes, int slot, int c) => BitConverter.ToSingle(bytes, slot * 16 + c * 4);
        float[] Slot(byte[] bytes, int slot) => [F(bytes, slot, 0), F(bytes, slot, 1), F(bytes, slot, 2), F(bytes, slot, 3)];

        // 枠なし(モーションステージの外・単独の実行): $frame は (0, 0, 入力の幅, 入力の高さ)
        var plain = new EffectInvocation(def);
        var a = EffectConstants.Pack(plain, pass, EffectConstants.ResolveValues(plain), 200, 120, FrameContext.Still);
        Check.SequenceEqual(new[] { 0f, 0f, 200f, 120f }, Slot(a, 0), "枠なしの $frame");
        Check.Equal(200f, F(a, 1, 0), "枠なしの $width");
        Check.Equal(120f, F(a, 3, 0), "枠なしの $shortSide");

        // レイヤーのモーション: 枠 Q=(90, 45, 100, 50)。$width などは枠の大きさ、$texel は入力のまま
        var framed = plain with { MotionFrame = new FrameRect(90, 45, 100, 50) };
        var b = EffectConstants.Pack(framed, pass, EffectConstants.ResolveValues(framed), 200, 200, FrameContext.Still);
        Check.SequenceEqual(new[] { 90f, 45f, 100f, 50f }, Slot(b, 0), "枠の $frame");
        Check.Equal(100f, F(b, 1, 0), "枠の $width");
        Check.Equal(50f, F(b, 2, 0), "枠の $height");
        Check.Equal(50f, F(b, 3, 0), "枠の $shortSide");
        Check.SequenceEqual(new[] { 1f / 200, 1f / 200, 200f, 200f }, Slot(b, 4), "枠のときも $texel は入力");

        // scale 0.5 のパス(仮の決め): 枠をパスの入力÷エフェクトの入力の比で縮める
        var c = EffectConstants.Pack(framed, pass, EffectConstants.ResolveValues(framed), 100, 100, FrameContext.Still, 200, 200);
        Check.SequenceEqual(new[] { 45f, 22.5f, 50f, 25f }, Slot(c, 0), "半分のパスの $frame");
        Check.Equal(25f, F(c, 3, 0), "半分のパスの $shortSide");
        Check.True(EffectDefLoader.BuiltinVariables.Contains("$frame"), "読み込みの検査が $frame を許す");
    }
}
