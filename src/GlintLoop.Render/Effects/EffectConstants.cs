// EffectDef の bind を、HLSL の定数バッファ b0 のバイト列に詰める(規則は Shaders/common.hlsli の「規則 3」と同じ)。
// 1 つの bind が 16 バイトの 1 枠(float4 か uint4)を占め、bind の順に並べる。値はパラメータの定義の単位のまま。
// パラメータの値の解決(既定値の補い、scale モードの実効値)もここで行う。GPU を触らないので単体で試せる。
using System.Buffers.Binary;
using System.Globalization;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Numerics;
using GlintLoop.Core.Session;

namespace GlintLoop.Render.Effects;

public static class EffectConstants
{
    /// <summary>1 つの bind が占めるバイト数(HLSL の 4 成分の 1 レジスター)。</summary>
    public const int SlotBytes = 16;

    /// <summary>
    /// 描画に使う値を決める。宣言の順に、渡された値(無ければ既定値)を入れ、scale モードでは scaleParams の値を実効値に直す。
    /// 宣言に無いキーの値や、型の合わない値は ArgumentException。
    /// </summary>
    public static IReadOnlyDictionary<string, ParamValue> ResolveValues(EffectInvocation inv)
    {
        var def = inv.Def;
        foreach (var key in inv.Values.Keys)
        {
            if (def.FindParam(key) is null) throw new ArgumentException($"{def.Id}: 宣言に無いパラメータ {key} の値が渡された");
        }
        var result = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
        foreach (var p in def.Params)
        {
            var v = inv.Values.TryGetValue(p.Key, out var given) ? given : p.Default;
            if (!Fits(p.Type, v)) throw new ArgumentException($"{def.Id}: パラメータ {p.Key}({p.Type})に型の合わない値 {v}");
            if (v is NumberValue n && def.AmountMode == AmountMode.Scale)
                v = new NumberValue(EffectAmount.ScaleParam(def, p, n.Value, inv.Amount));
            result[p.Key] = v;
        }
        return result;
    }

    private static bool Fits(ParamType type, ParamValue v) => type switch
    {
        ParamType.Float or ParamType.Int or ParamType.Angle => v is NumberValue,
        ParamType.Bool => v is BoolValue,
        ParamType.Enum or ParamType.Color or ParamType.Text or ParamType.Font => v is StringValue,
        ParamType.Point => v is PointValue,
        ParamType.Rect => v is RectValue,
        _ => false,
    };

    /// <summary>
    /// 1 つのパスの定数バッファを作る。values は ResolveValues の結果。inputWidth・inputHeight はこのパスの最初の入力の大きさ
    /// (入力の無いパスはエフェクトの入力の大きさ)。effectWidth・effectHeight はエフェクトの入力の大きさ(0 なら inputWidth・inputHeight と同じとみなす)。
    /// inv.MotionFrame があるとき(モーションステージ。Q-A)、$frame はその枠、$width・$height・$shortSide は枠の Wl・Hl・min(Wl, Hl)。
    /// scale が 1 でないパスでは、枠をこのパスの入力の大きさ÷エフェクトの入力の大きさの比で縮めて渡す。bind が無ければ空。
    /// </summary>
    public static byte[] Pack(EffectInvocation inv, PassDef pass, IReadOnlyDictionary<string, ParamValue> values,
        int inputWidth, int inputHeight, FrameContext frame, int effectWidth = 0, int effectHeight = 0)
    {
        var box = FrameBox(inv.MotionFrame, inputWidth, inputHeight, effectWidth > 0 ? effectWidth : inputWidth, effectHeight > 0 ? effectHeight : inputHeight);
        var bytes = new byte[pass.Bind.Count * SlotBytes];
        for (var i = 0; i < pass.Bind.Count; i++)
        {
            var slot = bytes.AsSpan(i * SlotBytes, SlotBytes);
            var source = pass.Bind[i].Source;
            if (source.StartsWith('$')) PackBuiltin(slot, source, inv, inputWidth, inputHeight, box, frame);
            else PackParam(slot, inv.Def, source, values);
        }
        return bytes;
    }

    /// <summary>
    /// このパスの $frame(qx, qy, Wl, Hl。このパスの入力の画素)。枠が無ければ (0, 0, 入力の幅, 入力の高さ)。
    /// 枠があるときは、パスの入力とエフェクトの入力の大きさの比を軸ごとに掛ける(scale が 1 のパスでは枠そのもの)。
    /// </summary>
    public static (double X, double Y, double W, double H) FrameBox(FrameRect? motionFrame, int inputWidth, int inputHeight, int effectWidth, int effectHeight)
    {
        if (motionFrame is not { } q) return (0, 0, inputWidth, inputHeight);
        var sx = effectWidth > 0 ? (double)inputWidth / effectWidth : 1.0;
        var sy = effectHeight > 0 ? (double)inputHeight / effectHeight : 1.0;
        return (q.Qx * sx, q.Qy * sy, q.W * sx, q.H * sy);
    }

    private static void PackBuiltin(Span<byte> slot, string name, EffectInvocation inv, int w, int h,
        (double X, double Y, double W, double H) box, FrameContext frame)
    {
        switch (name)
        {
            case "$width": F(slot, box.W); break;
            case "$height": F(slot, box.H); break;
            case "$shortSide": F(slot, Math.Min(box.W, box.H)); break;
            case "$frame": F(slot, box.X, box.Y, box.W, box.H); break;
            case "$texel": F(slot, 1.0 / w, 1.0 / h, w, h); break;
            case "$amount": F(slot, inv.Amount); break;
            case "$t": F(slot, frame.T); break;
            case "$phase": F(slot, inv.Phase); break;
            case "$k": U(slot, (uint)frame.K); break;
            case "$n": U(slot, (uint)frame.N); break;
            case "$cycles": U(slot, (uint)inv.Cycles); break;
            case "$wave": U(slot, (uint)(inv.Wave ?? inv.Def.WaveDefault)); break;
            case "$seed": U(slot, frame.Seed); break;
            case "$salt": U(slot, inv.Salt); break;
            default: throw new ArgumentException($"組み込み変数 {name} は無い");
        }
    }

    private static void PackParam(Span<byte> slot, EffectDef def, string key, IReadOnlyDictionary<string, ParamValue> values)
    {
        var p = def.FindParam(key) ?? throw new ArgumentException($"{def.Id}: bind の右辺 {key} がパラメータに無い");
        var v = values[key];
        switch (p.Type)
        {
            case ParamType.Float or ParamType.Int or ParamType.Angle:
                F(slot, ((NumberValue)v).Value);
                break;
            case ParamType.Bool:
                U(slot, ((BoolValue)v).Value ? 1u : 0u);
                break;
            case ParamType.Enum:
                U(slot, (uint)EnumIndex(def, p, ((StringValue)v).Value));
                break;
            case ParamType.Color:
                var c = ParseColor(((StringValue)v).Value);
                F(slot, c.R, c.G, c.B, c.A);
                break;
            case ParamType.Point:
                var pt = (PointValue)v;
                F(slot, pt.X, pt.Y);
                break;
            case ParamType.Rect:
                var r = (RectValue)v;
                F(slot, r.L, r.T, r.R, r.B);
                break;
            default:
                throw new ArgumentException($"{def.Id}: {p.Type} のパラメータ {key} は定数バッファに渡せない");
        }
    }

    /// <summary>enum の値の、options の中の番号(0 から)。無ければ ArgumentException。</summary>
    public static int EnumIndex(EffectDef def, ParamDef p, string value)
    {
        var options = p.Options ?? [];
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Value == value) return i;
        }
        throw new ArgumentException($"{def.Id}: {p.Key} の値 {value} が options に無い");
    }

    /// <summary>色 #RRGGBBAA(sRGB の straight)を、lin した線形 RGB と不透明度 AA÷255 にする(§9.4 の「色のパラメータ」)。</summary>
    public static (double R, double G, double B, double A) ParseColor(string s)
    {
        if (s.Length != 9 || s[0] != '#' ||
            !uint.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgba))
            throw new ArgumentException($"色の形式が #RRGGBBAA でない: {s}");
        return (
            Srgb.Lin(((rgba >> 24) & 0xFF) / 255.0),
            Srgb.Lin(((rgba >> 16) & 0xFF) / 255.0),
            Srgb.Lin(((rgba >> 8) & 0xFF) / 255.0),
            (rgba & 0xFF) / 255.0);
    }

    private static void F(Span<byte> slot, double x, double y = 0, double z = 0, double w = 0)
    {
        BinaryPrimitives.WriteSingleLittleEndian(slot[0..], (float)x);
        BinaryPrimitives.WriteSingleLittleEndian(slot[4..], (float)y);
        BinaryPrimitives.WriteSingleLittleEndian(slot[8..], (float)z);
        BinaryPrimitives.WriteSingleLittleEndian(slot[12..], (float)w);
    }

    private static void U(Span<byte> slot, uint x)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(slot[0..], x);
        slot[4..].Clear();
    }
}
