// GifEncoder にフレームを渡す口(仕様書 §9.8 の 1)。1 回目(集計)と 2 回目(符号化)で同じ画素を返すこと。
// 画素は 8 ビット straight RGBA(R,G,B,A の順・行優先・幅×高さ×4 バイト)。GetFrame は符号化を呼んだスレッドから番号の順に呼ばれる。
// 同時に手元に置くフレームは並列度+2 枚まで。使い終えたフレームは ReleaseFrame で返る(作業用のスレッドから呼ばれることがある)。
namespace GlintLoop.Core.Gif;

/// <summary>フレームを供給する回(1 回目は集計、2 回目は符号化)。</summary>
public enum GifPass
{
    Collect,
    Encode,
}

public interface IGifFrameSource
{
    int Width { get; }

    int Height { get; }

    int FrameCount { get; }

    /// <summary>フレーム <paramref name="index"/> の画素(幅×高さ×4 バイト)。返した配列は ReleaseFrame まで書き換えないこと。</summary>
    byte[] GetFrame(GifPass pass, int index);

    /// <summary>エンコーダーがフレームを使い終えた。配列を使い回す供給元のための口で、既定は何もしない。</summary>
    void ReleaseFrame(GifPass pass, int index, byte[] pixels)
    {
    }
}

/// <summary>メモリ上のフレームの列をそのまま渡す供給元(テストと測定用プログラム向け)。</summary>
public sealed class GifFrameList : IGifFrameSource
{
    private readonly IReadOnlyList<byte[]> _frames;

    public GifFrameList(int width, int height, IReadOnlyList<byte[]> frames)
    {
        Width = width;
        Height = height;
        _frames = frames;
    }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount => _frames.Count;

    public byte[] GetFrame(GifPass pass, int index) => _frames[index];
}
