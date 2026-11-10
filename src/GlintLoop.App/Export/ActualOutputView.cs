// 実出力(FR-19)の中身: 書き出しのキャッシュの完成ファイルをデコードしたフレームを出し、GIF と APNG はファイルの遅延のとおりに再生する。
// デコードと BitmapSource の作成は UI スレッドの外(Load。NFR-7)で行い、凍結したものだけを UI スレッドへ渡す。再生のタイマーは ITimerFactory(テストは偽の時計)。
// キャンバスが合成の矩形に引き伸ばして置くので、画素が見えるよう最近傍で拡大する(仮の決め)。再生の回数はファイルの値(0 は無限)に従い、終わったら最後のフレームで止める。
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Theme;
using GlintLoop.Codec.Preview;
using GlintLoop.Core.Platform;

namespace GlintLoop.App.Export;

/// <summary>デコード済みの実出力(凍結したフレームと遅延)。</summary>
public sealed record ActualOutputFrames(string Format, int Width, int Height, IReadOnlyList<BitmapSource> Frames, IReadOnlyList<int> DelaysMs, int Plays)
{
    /// <summary>ファイルを読んでデコードする(UI スレッドの外で呼ぶ)。</summary>
    public static ActualOutputFrames Load(string path, string format, IFileSystem? fileSystem = null)
    {
        var bytes = (fileSystem ?? RealFileSystem.Instance).ReadAllBytes(path);
        var anim = OutputFrameDecoder.Decode(bytes, format);
        var frames = new List<BitmapSource>(anim.Frames.Count);
        var delays = new List<int>(anim.Frames.Count);
        foreach (var f in anim.Frames)
        {
            var bmp = BitmapSource.Create(anim.Width, anim.Height, 96, 96, PixelFormats.Bgra32, null, f.Bgra, anim.Width * 4);
            bmp.Freeze();
            frames.Add(bmp);
            delays.Add(f.DelayMs);
        }
        return new ActualOutputFrames(format, anim.Width, anim.Height, frames, delays, anim.Plays);
    }
}

public sealed class ActualOutputView : Image, IDisposable
{
    private readonly ActualOutputFrames _frames;
    private readonly IOneShotTimer? _timer;
    private int _playsDone;

    public ActualOutputView(ActualOutputFrames frames, ITimerFactory timers)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(timers);
        _frames = frames;
        Stretch = Stretch.Fill;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(this, Icons.ActualOutput.Name);
        Source = frames.Frames[0];
        if (frames.Frames.Count > 1)
        {
            _timer = timers.Create();
            _timer.Elapsed += Next;
            Unloaded += (_, _) => _timer.Stop();
            _timer.Start(DelayOf(0));
        }
    }

    public ActualOutputFrames Frames => _frames;

    /// <summary>いま出しているフレームの番号。</summary>
    public int Index { get; private set; }

    /// <summary>再生が終わって最後のフレームで止まったか(無限のループでは false のまま)。</summary>
    public bool Ended { get; private set; }

    public BitmapSource CurrentFrame => _frames.Frames[Index];

    private TimeSpan DelayOf(int i) => TimeSpan.FromMilliseconds(Math.Max(1, _frames.DelaysMs[i]));

    private void Next()
    {
        var next = Index + 1;
        if (next >= _frames.Frames.Count)
        {
            _playsDone++;
            if (_frames.Plays > 0 && _playsDone >= _frames.Plays)
            {
                Ended = true;
                return;
            }
            next = 0;
        }
        Index = next;
        Source = _frames.Frames[next];
        _timer!.Start(DelayOf(next));
    }

    public void Dispose()
    {
        if (_timer is null) return;
        _timer.Elapsed -= Next;
        _timer.Dispose();
    }
}
