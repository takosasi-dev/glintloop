// 書き出し(FR-68〜FR-79・§9.9)の型の契約だけを置く(P0-7)。中身(フレーム・エンコーダー・はしご・探索・キャッシュ・帯)は担当 export と band が作る。
// フレームは 8 ビット straight RGBA(§9.7 の規則 7)。エンコーダーは上限のバイト数を超えた時点で打ち切る(§9.9 の 4 の 5)。
// 並列の仕事は ExportRun.Scheduler(BelowNormal の専用スレッドの TaskScheduler。Q-F)で動かし、キャッシュのキーの本体は BuildIdentity の 4 つのアセンブリ(Q-H)。
using System.Reflection;
using GlintLoop.Core.Gif;
using GlintLoop.Core.Session;

namespace GlintLoop.Core.Export;

/// <summary>形式の名前(文書の output.format と同じ文字列)と拡張子(FR-85)。</summary>
public static class ExportFormat
{
    public const string Png = "png";
    public const string Jpeg = "jpeg";
    public const string Gif = "gif";
    public const string Webp = "webp";
    public const string Apng = "apng";

    /// <summary>5 つの形式(並びは FR-68〜FR-72 の順)。</summary>
    public static IReadOnlyList<string> All { get; } = [Png, Jpeg, Gif, Webp, Apng];

    /// <summary>ファイルの拡張子(FR-85: PNG と APNG は png、JPEG は jpg)。知らない形式は ArgumentException。</summary>
    public static string Extension(string format) => format switch
    {
        Png => "png",
        Jpeg => "jpg",
        Gif => "gif",
        Webp => "webp",
        Apng => "png",
        _ => throw new ArgumentException("知らない形式: " + format, nameof(format)),
    };

    /// <summary>全フレームを書く形式(GIF・APNG)か。そのほかはスクラブバーの位置の 1 枚(§9.9 の 5)。</summary>
    public static bool IsAnimated(string format) => format is Gif or Apng;
}

/// <summary>
/// はしごの段 r_i 1 つ分の、解決済みの書き出しの設定(§9.9 の 3・4)。Width・Height は出力の画素、LongSide はその長辺 Lout。
/// FrameCount・Fps・Length10 は書き出しのフレーム数と fps(はしごや制約で fps を下げたあとの値)。静止形式は FrameCount=1 で、FrameK に描くフレームの番号を持つ。
/// </summary>
public sealed record ExportSettings
{
    public required string Format { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>出力の長辺 Lout(16〜4096。長辺×0.85 の段の元)。</summary>
    public required int LongSide { get; init; }

    /// <summary>書き出すフレーム数(静止形式とモーションの無い文書は 1)。</summary>
    public int FrameCount { get; init; } = 1;

    /// <summary>描くときのフレーム数 N(t=k÷N の分母)。静止形式でもモーションがあれば TimeModel の N。</summary>
    public int TimelineFrames { get; init; } = 1;

    /// <summary>静止形式で描くフレームの番号 k(スクラブバーの位置。モーションが無ければ 0)。</summary>
    public int FrameK { get; init; }

    public int Fps { get; init; } = TimeModel.DefaultFps;

    public int Length10 { get; init; } = TimeModel.DefaultLength10;

    public LoopMode Loop { get; init; } = LoopMode.Infinite;

    /// <summary>APNG のループ回数(ループが「1 回」のとき 1〜4。無限のときは使わない)。</summary>
    public int ApngLoops { get; init; } = 1;

    /// <summary>最後のフレームの停止時間(ms。0〜5000、100 ms 刻み)。</summary>
    public long HoldMs { get; init; }

    /// <summary>GIF とパレットの APNG の色数(2〜256)。</summary>
    public int Colors { get; init; } = 256;

    public GifDither Dither { get; init; } = GifDither.BlueNoise;

    public GifColor? Matte { get; init; }

    public bool DiffOptimization { get; init; } = true;

    /// <summary>JPEG・WebP の品質(1〜100)。</summary>
    public int Quality { get; init; } = 90;

    /// <summary>WebP のロスレス。</summary>
    public bool Lossless { get; init; }

    /// <summary>APNG をパレット(256 色以下、alpha は二値)で書くか。</summary>
    public bool ApngPalette { get; init; }

    /// <summary>JPEG の背景色(文書の output.options.jpeg.background と同じ形。既定は白。透明な部分を線形光で over する色)。</summary>
    public string JpegBackground { get; init; } = OutputSettings.Default.Jpeg.Background;

    /// <summary>GIF のエンコーダーのオプション(並列度は parallelism、計算と受け取りは scheduler の上で動く)。</summary>
    public GifEncoderOptions ToGifOptions(int parallelism, TaskScheduler? scheduler = null) => new()
    {
        Colors = Colors,
        Dither = Dither,
        Matte = Matte,
        DiffOptimization = DiffOptimization,
        Loop = Loop == LoopMode.Once ? GifLoop.Once : GifLoop.Infinite,
        Fps = Fps,
        FinalHoldMs = (int)HoldMs,
        Parallelism = Math.Max(1, parallelism),
        Scheduler = scheduler,
    };
}

/// <summary>
/// 書き出すフレームの列(出力の大きさの 8 ビット straight RGBA、行優先・幅×高さ×4 バイト)。index は 0〜FrameCount−1 の書き出しの順。
/// 同じ index は何度呼んでも同じ画素を返す(GIF の 2 回の供給。Q-I では描き直す)。どのスレッドから呼ばれてもよい。ct が立ったら OperationCanceledException。
/// </summary>
public interface IExportFrames
{
    int Width { get; }

    int Height { get; }

    int FrameCount { get; }

    byte[] GetFrame(int index, CancellationToken ct);
}

/// <summary>1 回の書き出しの実行の道具。Scheduler は BelowNormal の専用スレッドの TaskScheduler(Q-F)、Parallelism はエンコードの並列度。</summary>
public sealed record ExportRun(TaskScheduler Scheduler, int Parallelism, CancellationToken Cancel, IProgress<ExportProgress>? Progress = null);

/// <summary>符号化の結果。Overflowed は上限のバイト数を超えて打ち切ったこと(そのときの Bytes は書いた分で、結果は使わない)。</summary>
public sealed record EncodeResult(long Bytes, bool Overflowed);

/// <summary>1 つの形式のエンコーダー。output に書き、書いたバイトの合計が limitBytes を超えた時点で打ち切る(§9.9 の 4 の 5、AC-38 の (3))。</summary>
public interface IExportEncoder
{
    /// <summary>ExportFormat の名前。</summary>
    string Format { get; }

    EncodeResult Encode(IExportFrames frames, ExportSettings settings, Stream output, long? limitBytes, ExportRun run);
}

/// <summary>書き出しの進み(FR-79: 完了したフレーム数 / N と、容量調整の段 n / R。調整が無ければ Step・StepCount は 0)。</summary>
public sealed record ExportProgress(int FramesDone, int FrameCount, int Step, int StepCount);

/// <summary>書き出し帯の状態(FR-79: 待機・計算中・完了・失敗)。</summary>
public enum ExportState { Idle, Running, Done, Failed }

/// <summary>
/// 完成した書き出し(§9.9 の 7 の &lt;key&gt;.json の中身と、置いたファイル)。FpsEff は実際に使った fps、Steps は調整の内容の文字(無ければ null)、
/// SizeFail は上限に収まらなかった(FR-76)、Condition は制約の警告の {内容}(無ければ null)。
/// </summary>
public sealed record ExportOutcome(
    string Path,
    string Format,
    long Bytes,
    int Width,
    int Height,
    int Frames,
    double FpsEff,
    string? Steps,
    bool SizeFail,
    string? Condition)
{
    /// <summary>書いたファイルの描画の上限で縮めたレイヤーの番号(layer.render_shrunk の候補。E-19)。</summary>
    public IReadOnlyList<int> ShrunkLayers { get; init; } = Array.Empty<int>();
}

/// <summary>キャッシュのキーの (4)(§9.9 の 7・Q-H): アプリの版と、本体アセンブリ(App・Core・Codec・Render)の名前と ModuleVersionId の並び。</summary>
public sealed record BuildIdentity(string AppVersion, IReadOnlyList<KeyValuePair<string, Guid>> Modules)
{
    /// <summary>アセンブリの名前と ModuleVersionId を、渡した順に並べる(App は typeof(AppShell).Assembly などで呼ぶ側が渡す)。</summary>
    public static BuildIdentity FromAssemblies(string appVersion, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentNullException.ThrowIfNull(assemblies);
        var list = assemblies.Select(a => new KeyValuePair<string, Guid>(a.GetName().Name ?? "", a.ManifestModule.ModuleVersionId)).ToArray();
        return new BuildIdentity(appVersion, list);
    }
}
