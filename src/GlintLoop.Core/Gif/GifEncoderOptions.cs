// GifEncoder のオプション(仕様書 §9.8・FR-61・FR-74)。値の範囲の検査は GifEncoder が符号化の前に行う。
// 色数 n・ディザ・マット色・差分の最適化・ループ・fps・最後のフレームの停止時間・並列度を持つ。
// 並列度は速さだけを変え、出力のバイト列は変えない(INV-8・D-29)。
namespace GlintLoop.Core.Gif;

/// <summary>ディザの種類(D-23)。既定は青色雑音。</summary>
public enum GifDither
{
    BlueNoise,
    ErrorDiffusion,
    None,
}

/// <summary>繰り返し(D-26)。無限は NETSCAPE2.0 を書き、1 回は書かない。</summary>
public enum GifLoop
{
    Infinite,
    Once,
}

/// <summary>8 ビット sRGB の色(straight)。</summary>
public readonly record struct GifColor(byte R, byte G, byte B);

public sealed record GifEncoderOptions
{
    /// <summary>色数 n(2〜256)。透明インデックスを置くときは、色に使えるのは n−1 個。</summary>
    public int Colors { get; init; } = 256;

    public GifDither Dither { get; init; } = GifDither.BlueNoise;

    /// <summary>マット色。null なら合成せず、α が 128 未満の画素を透明にする(D-27)。</summary>
    public GifColor? Matte { get; init; }

    /// <summary>差分の最適化(D-24)。不透明で 2 フレーム以上のときだけ効く。</summary>
    public bool DiffOptimization { get; init; } = true;

    public GifLoop Loop { get; init; } = GifLoop.Infinite;

    /// <summary>フレームレート F(1 以上の整数。製品の画面では FR-58 の 11 通りから選ぶ)。</summary>
    public int Fps { get; init; } = 20;

    /// <summary>最後のフレームの停止時間(ms。0 以上)。round(ms÷10) を最後のフレームの遅延に足す(FR-61)。</summary>
    public int FinalHoldMs { get; init; }

    /// <summary>同時に計算するフレームの数(1 以上)。同時に保持するフレームは、これに 2 を足した枚数まで。</summary>
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>計算と受け取りを動かす TaskScheduler(null は TaskScheduler.Default)。書き出しは BelowNormal の専用スレッドを渡す(Q-F)。出力のバイト列は変えない。</summary>
    public TaskScheduler? Scheduler { get; init; }
}
