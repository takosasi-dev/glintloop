// ウィンドウの中へ直接発生させる入力(INV-18: 実際のキーボードとマウスを使わない)と、ウィンドウの画素の読み取り(RenderTargetBitmap)。
// Key: UI スレッドで、フォーカスのある要素(キーボードフォーカスが無ければウィンドウの論理フォーカス、それも無ければ target)に PreviewKeyDown → KeyDown → PreviewKeyUp → KeyUp を発生させる。修飾キーは FakeModifierKeys で先に入れておく。
// Snapshot: ウィンドウを、その DPI の画素で描いて、DIP の座標で画素を読む(この PC のように 120% の画面でも、DIP を画素に直して読む)。
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlintLoop.Tests.Ui;

public static class UiInput
{
    /// <summary>キーを 1 回押して離す(UI スレッドで呼ぶ)。押下が処理されたら true。</summary>
    public static bool Key(UIElement target, Key key)
    {
        var down = KeyDown(target, key);
        KeyUp(target, key);
        return down;
    }

    public static bool KeyDown(UIElement target, Key key) => Raise(target, key, Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent);

    public static bool KeyUp(UIElement target, Key key) => Raise(target, key, Keyboard.PreviewKeyUpEvent, Keyboard.KeyUpEvent);

    /// <summary>
    /// キーボードフォーカスが無いとき(画面の外の非アクティブなウィンドウ)に、ウィンドウがアクティブになったらキーを受ける要素: target のウィンドウの論理フォーカス
    /// (DialogHost はアクティブでないウィンドウでは最初のフォーカスを論理フォーカスだけで決める)。表示されていなければ null。
    /// </summary>
    private static UIElement? LogicalFocus(UIElement target)
    {
        if (Window.GetWindow(target) is not { } window) return null;
        return FocusManager.GetFocusedElement(window) is UIElement { IsVisible: true } e && PresentationSource.FromVisual(e) is not null
            && ReferenceEquals(Window.GetWindow(e), window) ? e : null;
    }

    private static bool Raise(UIElement target, Key key, RoutedEvent preview, RoutedEvent bubble)
    {
        var to = Keyboard.FocusedElement as UIElement ?? LogicalFocus(target) ?? target;
        var source = PresentationSource.FromVisual(to) ?? PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("キーを送る要素が表示されていない");
        var p = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = preview };
        to.RaiseEvent(p);
        if (p.Handled) return true;
        var b = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = bubble };
        to.RaiseEvent(b);
        return b.Handled;
    }
}

/// <summary>ウィンドウを描いた画素(Pbgra32)。</summary>
public sealed class Snapshot
{
    private readonly byte[] _px;

    private Snapshot(byte[] px, int width, int height, double scale)
    {
        _px = px;
        Width = width;
        Height = height;
        Scale = scale;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>画素 ÷ DIP。</summary>
    public double Scale { get; }

    /// <summary>visual を、ウィンドウの DPI で描く(UI スレッドで呼ぶ)。</summary>
    public static Snapshot Of(Visual visual, FrameworkElement sizeOf)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var w = (int)Math.Ceiling(sizeOf.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(sizeOf.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return new Snapshot(px, w, h, dpi.DpiScaleX);
    }

    /// <summary>DIP の座標 p を中心に含む画素の色(A・R・G・B)。</summary>
    public (byte A, byte R, byte G, byte B) AtDip(Point p)
    {
        var x = Math.Clamp((int)Math.Floor(p.X * Scale), 0, Width - 1);
        var y = Math.Clamp((int)Math.Floor(p.Y * Scale), 0, Height - 1);
        var i = (y * Width + x) * 4;
        return (_px[i + 3], _px[i + 2], _px[i + 1], _px[i]);
    }

    /// <summary>色の各チャンネルの差が tolerance 以内か(不透明の色と比べる)。</summary>
    public bool Near(Point dip, Color c, int tolerance)
    {
        var (a, r, g, b) = AtDip(dip);
        return Math.Abs(a - c.A) <= tolerance && Math.Abs(r - c.R) <= tolerance && Math.Abs(g - c.G) <= tolerance && Math.Abs(b - c.B) <= tolerance;
    }

    public string Describe(Point dip)
    {
        var (a, r, g, b) = AtDip(dip);
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }
}
