// AC-35 の (9) のエフェクトの部分(FR-66): 乱数を使うエフェクト 7 個は、同じ seed で 40 フレームが完全に一致し、seed を変えると 10 フレーム以上で違い、
// 複製した 2 行は salt が違って結果が違う。振り直しと文書の seed の部分は TimelineUiAc35Tests。描画は WARP。
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Render;

namespace GlintLoop.Tests.Motions;

public sealed class SeedAc35Tests
{
    private const int N = 40;

    /// <summary>AC-35 の (9) の 7 個の id。</summary>
    private static readonly string[] Ids = ["shake", "leaks", "glitch", "particles", "sparkle", "grainmove", "grain"];

    private static byte[][] Frames(RenderService rs, string id, uint seed, uint salt = 1)
    {
        var inv = MotionTestKit.Invocation(rs.Effects, id, salt: salt);
        var render = MotionTestKit.Render(rs, MotionTestKit.Ac35Image(), inv, seed);
        var frames = new byte[N][];
        for (var k = 0; k < N; k++) frames[k] = render(k, N);
        return frames;
    }

    private static int DifferentFrames(byte[][] a, byte[][] b)
    {
        var count = 0;
        for (var k = 0; k < N; k++)
            if (MotionTestKit.MaxDiff(a[k], b[k]) > 0) count++;
        return count;
    }

    [Test(Slow = true)]
    public void AC35_9_同じseedの2回は40フレームすべて一致しseedを変えると10フレーム以上で違う()
    {
        using var kit = new CompositeKit();
        using var other = RenderService.Create(GpuAdapterRequest.Warp);
        foreach (var id in Ids)
        {
            var first = Frames(kit.Rs, id, 12345);
            // 2 回目は別のデバイスで描く(同じデバイスの使い回しで一致したのではないことを確かめる)
            var second = Frames(other, id, 12345);
            for (var k = 0; k < N; k++)
                Check.Equal(0, MotionTestKit.MaxDiff(first[k], second[k]), $"{id}: seed=12345 の 2 回のフレーム {k} の差");
            var changed = DifferentFrames(first, Frames(kit.Rs, id, 54321));
            Check.True(changed >= 10, $"{id}: seed=54321 で違うフレームが {changed} 個(10 個以上であること)");
        }
    }

    [Test(Slow = true)]
    public void AC35_9_複製した2行はsaltが違い結果が違う()
    {
        foreach (var id in Ids)
        {
            using var kit = new CompositeKit();
            kit.Import(MotionTestKit.Ac35Image());
            var target = Target.OfLayer(0);
            var row = kit.AddEffect(target, id);
            Check.True(kit.Session.DuplicateEffect(target, row).Changed, $"{id} を複製できない");
            var stack = kit.Doc.StackOf(target);
            var a = stack[row].Salt;
            var b = stack[row + 1].Salt;
            Check.Equal(id, stack[row + 1].Id, $"{id} の複製の id");
            Check.True(a != b, $"{id}: 複製した 2 行の salt が同じ({a})");
            var changed = DifferentFrames(Frames(kit.Rs, id, 12345, (uint)a), Frames(kit.Rs, id, 12345, (uint)b));
            Check.True(changed > 0, $"{id}: salt {a} と {b} の結果が 40 フレームすべてで同じ");
        }
    }
}
