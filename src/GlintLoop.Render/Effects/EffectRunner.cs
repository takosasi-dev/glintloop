// EffectDef の passes を解釈して 1 つのエフェクトを実行し、全エフェクト共通の後処理(mix の amount、出力の切り)を掛ける(§9.2・§9.7 の規則 4)。
// inputs(src・orig・aux・pass:id)、scale、repeat、when、bind を扱う。パスの順は参照の向きに従い、同順位は宣言の順。
// GpuDevice の持ち主のスレッドだけで使う(INV-5)。返したテクスチャの破棄は呼び手が行う。
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;

namespace GlintLoop.Render.Effects;

internal sealed class EffectRunner
{
    /// <summary>エフェクトの共通の後処理のシェーダー。</summary>
    internal const string FinishShader = "effect_finish.ps";

    private readonly GpuDevice _gpu;
    private readonly ShaderPassDrawer _drawer;

    public EffectRunner(GpuDevice gpu, ShaderPassDrawer drawer)
    {
        _gpu = gpu;
        _drawer = drawer;
    }

    /// <summary>EffectDef の shader の名前を、埋め込みのシェーダーの名前("exposure" → "exposure.ps")にする。</summary>
    public static string PixelShaderName(string shader) => shader + ".ps";

    /// <summary>
    /// 1 つのエフェクトを input に掛けた結果(新しい RGBA16F のテクスチャ)を返す。orig は inputs の orig(null なら input)。
    /// 実行するパスが 1 つも無いときは、入力をそのまま後処理に通す。
    /// </summary>
    public GpuTexture Run(EffectInvocation inv, GpuTexture input, FrameContext frame, GpuTexture? orig)
    {
        _gpu.EnsureOwnerThread();
        var def = inv.Def;
        var values = EffectConstants.ResolveValues(inv);
        var outputs = new Dictionary<string, GpuTexture>(StringComparer.Ordinal);
        var owned = new List<GpuTexture>();
        try
        {
            var last = -1;
            foreach (var index in ExecutionOrder(def.Passes))
            {
                var pass = def.Passes[index];
                if (!WhenHolds(def, pass.When, values)) continue;
                var inputs = new GpuTexture?[pass.Inputs.Count];
                for (var i = 0; i < inputs.Length; i++)
                    inputs[i] = ResolveInput(def, pass, pass.Inputs[i], input, orig, inv.Aux, outputs);
                var width = ScaledSize(input.Width, pass.Scale);
                var height = ScaledSize(input.Height, pass.Scale);
                var count = RepeatCount(def, pass.Repeat, values);
                GpuTexture? result = inputs.Length > 0 ? inputs[0] : input; // repeat が 0 なら最初の入力をそのまま通す
                for (var r = 0; r < count; r++)
                {
                    if (r > 0 && inputs.Length > 0) inputs[0] = result; // 2 回目からは、前の出力を最初の入力にする
                    var first = inputs.Length > 0 ? inputs[0]! : input;
                    var constants = EffectConstants.Pack(inv, pass, values, first.Width, first.Height, frame, input.Width, input.Height);
                    var target = new GpuTexture(_gpu, width, height, PassRenderer.WorkingFormat);
                    owned.Add(target);
                    _drawer.Draw(PixelShaderName(pass.Shader), target, inputs, constants);
                    result = target;
                }
                outputs[pass.Id] = result!;
                last = index; // エフェクトの出力は、実行の順で最後に実行したパスの出力
            }

            var fx = last >= 0 ? outputs[def.Passes[last].Id] : input;
            return Finish(def.AmountMode, inv.Amount, input, fx);
        }
        finally
        {
            foreach (var t in owned) t.Dispose();
        }
    }

    /// <summary>規則 4 の 3 と 4: mix なら P_in + a×(P_fx − P_in)、そのほかは P_fx。どちらも RGB 0〜64・α 0〜1・NaN→0 に切る。</summary>
    public GpuTexture Finish(AmountMode mode, double amount, GpuTexture input, GpuTexture fx)
    {
        var mix = mode == AmountMode.Mix;
        if (mix && (fx.Width != input.Width || fx.Height != input.Height))
            throw new InvalidOperationException("mix のエフェクトの出力の大きさが入力と違う");
        var constants = new byte[EffectConstants.SlotBytes];
        BitConverter.TryWriteBytes(constants.AsSpan(0, 4), (float)amount);
        BitConverter.TryWriteBytes(constants.AsSpan(4, 4), mix ? 1f : 0f);
        var target = new GpuTexture(_gpu, fx.Width, fx.Height, PassRenderer.WorkingFormat);
        try
        {
            _drawer.Draw(FinishShader, target, [input.Width == fx.Width && input.Height == fx.Height ? input : fx, fx], constants);
        }
        catch
        {
            target.Dispose();
            throw;
        }
        return target;
    }

    /// <summary>出力の辺 = ceil(入力の辺 × scale)(1 以上)。</summary>
    public static int ScaledSize(int side, double scale) => Math.Max(1, (int)Math.Ceiling(side * scale));

    private static GpuTexture ResolveInput(EffectDef def, PassDef pass, string spec, GpuTexture src, GpuTexture? orig, GpuTexture? aux,
        Dictionary<string, GpuTexture> outputs)
    {
        switch (spec)
        {
            case "src": return src;
            case "orig": return orig ?? src;
            case "aux": return aux ?? throw new InvalidOperationException($"{def.Id}: パス {pass.Id} が aux を読むが、補助のビットマップが渡されていない");
        }
        if (spec.StartsWith("pass:", StringComparison.Ordinal))
        {
            var id = spec["pass:".Length..];
            return outputs.TryGetValue(id, out var t)
                ? t
                : throw new InvalidOperationException($"{def.Id}: パス {pass.Id} が読むパス {id} は実行されていない(when で外れた)");
        }
        throw new InvalidOperationException($"{def.Id}: パス {pass.Id} の入力 {spec} は無い形");
    }

    private static bool WhenHolds(EffectDef def, ParamCondition? when, IReadOnlyDictionary<string, ParamValue> values)
    {
        if (when is null) return true;
        var v = values.TryGetValue(when.Key, out var value) ? value : null;
        var s = v switch
        {
            StringValue sv => sv.Value,
            BoolValue bv => bv.Value ? "true" : "false",
            _ => throw new InvalidOperationException($"{def.Id}: when のキー {when.Key} が enum でない"),
        };
        return when.Values.Contains(s, StringComparer.Ordinal);
    }

    /// <summary>repeat の回数。整数ならそのまま、パラメータなら値を round して 0〜64 に切ったもの。</summary>
    private static int RepeatCount(EffectDef def, PassRepeat repeat, IReadOnlyDictionary<string, ParamValue> values)
    {
        if (repeat.ParamKey is null) return repeat.Count;
        if (!values.TryGetValue(repeat.ParamKey, out var v) || v is not NumberValue n)
            throw new InvalidOperationException($"{def.Id}: repeat のキー {repeat.ParamKey} が数値のパラメータでない");
        return Math.Clamp(MathX.RoundHalfUpToInt(n.Value), 0, 64);
    }

    /// <summary>
    /// パスを実行する順(添字)。pass:id の参照先を先に置き、同じ順位なら宣言の順。循環は読み込みの検査(§9.2 の 3)で除かれている。
    /// </summary>
    public static IReadOnlyList<int> ExecutionOrder(IReadOnlyList<PassDef> passes)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < passes.Count; i++) index.TryAdd(passes[i].Id, i);
        var placed = new bool[passes.Count];
        var order = new List<int>(passes.Count);
        while (order.Count < passes.Count)
        {
            var progressed = false;
            for (var i = 0; i < passes.Count; i++)
            {
                if (placed[i]) continue;
                var ready = true;
                foreach (var input in passes[i].Inputs)
                {
                    if (input.StartsWith("pass:", StringComparison.Ordinal) &&
                        index.TryGetValue(input["pass:".Length..], out var dep) && !placed[dep])
                    {
                        ready = false;
                        break;
                    }
                }
                if (!ready) continue;
                placed[i] = true;
                order.Add(i);
                progressed = true;
                break; // 置くたびに先頭から見直す(宣言の順を優先する)
            }
            if (!progressed) throw new InvalidOperationException("パスの参照が循環している");
        }
        return order;
    }
}
