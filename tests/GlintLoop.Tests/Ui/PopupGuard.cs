// テストの見張り: ポップアップ(ドロップダウン・フライアウト・メニュー・ツールチップ)が開いたら記録し、そのテストを失敗にする。
// WPF はポップアップを最も近いモニターの中へ寄せるので、画面の外の窓から開いても実際の画面に出る(INV-18)。テストは開かずに中身だけを調べる。
using System.Windows;
using System.Windows.Media;

namespace GlintLoop.Tests.Ui;

public static class PopupGuard
{
    private static readonly List<string> Opened = [];

    /// <summary>UI スレッドで 1 回呼ぶ。ポップアップの中の要素が読み込まれたら、開いたものとして記録する。</summary>
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Visual v || !IsPopupRoot(PresentationSource.FromVisual(v)?.RootVisual)) return;
        lock (Opened) Opened.Add(sender.GetType().Name);
    }

    /// <summary>WPF のポップアップの窓の根(PopupRoot は公開の型でないので名前で見る)か。</summary>
    public static bool IsPopupRoot(object? root) => root?.GetType().Name == "PopupRoot";

    /// <summary>前回からのあいだにポップアップが開いていたら例外にする(テストのランナーが各テストのあとに呼ぶ)。</summary>
    public static void ThrowIfOpened()
    {
        string[] names;
        lock (Opened)
        {
            if (Opened.Count == 0) return;
            names = [.. Opened.Distinct()];
            Opened.Clear();
        }
        throw new InvalidOperationException("テストでポップアップを開いた(実際の画面に出る。INV-18): " + string.Join(", ", names));
    }
}
