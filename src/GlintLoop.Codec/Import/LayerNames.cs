// 取り込んだ画像の新しいレイヤーの名前の決め方(仕様書 FR-42・FR-41)。
// ファイルはファイル名から拡張子を除いたもの(40 文字を超えるときは先頭の 40 文字、空なら「画像」)、
// クリップボードは「貼り付け」、サンプルは「サンプル」。
namespace GlintLoop.Codec.Import;

public static class LayerNames
{
    public const int MaxLength = 40;
    public const string Fallback = "画像";
    public const string Paste = "貼り付け";
    public const string Sample = "サンプル";

    /// <summary>ファイルのパスからレイヤーの名前を作る。</summary>
    public static string FromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return Truncate(string.IsNullOrEmpty(name) ? Fallback : name);
    }

    /// <summary>
    /// 先頭の 40 文字(string.Length で数える)にする。40 文字目がサロゲートペアの前半なら、ペアを割らないように 39 文字にする。
    /// </summary>
    public static string Truncate(string name)
    {
        if (name.Length <= MaxLength) return name;
        var n = char.IsHighSurrogate(name[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return name[..n];
    }
}
