// 画面の各部分(スタック・レイヤー・ルック・キャンバス・各画面)を AppShell につなぐ口。担当ごとに 1 つ作り、共通のファイルを触らずに足せるようにする。
// AppShell は組み立ての最後に、このアセンブリの IShellModule を Order の昇順(同じなら型の名前の順)で 1 つずつ作り、Attach を呼ぶ。
// 引数の無いコンストラクターが要る。IDisposable なら AppShell.Dispose で逆の順に Dispose する。
namespace GlintLoop.App.Startup;

public interface IShellModule
{
    /// <summary>つなぐ順。小さいものから先に Attach する。</summary>
    int Order { get; }

    /// <summary>領域の ContentControl に部品を入れ、Router と Session.Changed に登録する(UI スレッドで呼ばれる)。</summary>
    void Attach(AppShell shell);
}
