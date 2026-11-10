// ギャラリーの 1 つのタイル(§9.3・§9.1 のタイル): 132 DIP の幅に、サムネイルの正方形と、その下に caption の名前を 1 行で置く(あふれは「…」)。
// サムネイルの画像は長辺 192 px で作り(D-18)、正方形の中に縦横比を保って全体を収め、余白は surface-2。作成中は無地の surface-2 で、
// 完成したら thumb-in(150 ms・ease-out の不透明度 0 → 1)で出す。タイルの見た目(塗り・枠・押下・選択・輪)は一覧の ItemContainerStyle(TileItem)に任せる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Input;
using GlintLoop.App.Theme;
using GlintLoop.Core.Looks;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.Looks;

public sealed class LookTile : ListBoxItem
{
    private readonly Image _image;

    public LookTile(LookRecipe? recipe, string name, UserLook? user)
    {
        Recipe = recipe;
        User = user;
        DisplayName = name;
        SetResourceReference(WidthProperty, "TileSize");
        Padding = new Thickness(0);
        ShortcutScope.SetKind(this, FocusKind.LookTile);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetAutomationId(this, "LookTile:" + Key);
        ToolTip = name;

        var side = (double)FindResource("TileSize") - 2 * (double)FindResource("Stroke");
        _image = new Image { Stretch = Stretch.Uniform, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Thumb = new Border { Width = side, Height = side, Child = _image };
        Thumb.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        Thumb.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        label.SetResourceReference(StyleProperty, "TextCaption");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        label.SetResourceReference(MarginProperty, "Space1Thickness");
        var panel = new StackPanel();
        panel.Children.Add(Thumb);
        panel.Children.Add(label);
        Content = panel;
    }

    /// <summary>当てるルック(「ルックなし」は null)。</summary>
    public LookRecipe? Recipe { get; }

    /// <summary>ユーザーのルックのとき、その保存の情報(組み込みと「ルックなし」は null)。</summary>
    public UserLook? User { get; }

    public string DisplayName { get; }

    /// <summary>タイルのキー(「ルックなし」は none、組み込みは表のキー、ユーザーのルックはファイルの名前)。</summary>
    public string Key => Recipe?.Key ?? "none";

    /// <summary>当てるエフェクト(「ルックなし」は空)。</summary>
    public IReadOnlyList<LookEffect> Effects => Recipe?.Effects ?? Array.Empty<LookEffect>();

    /// <summary>サムネイルの正方形(作成中は無地の surface-2)。</summary>
    public Border Thumb { get; }

    /// <summary>いまのサムネイルの画素(Pbgra32。作成中は null)。テストがエンジンの結果とバイト列で比べる。</summary>
    public DocumentImage? Thumbnail { get; private set; }

    public ImageSource? ThumbnailSource => _image.Source;

    /// <summary>サムネイルを消して作成中(無地の surface-2)に戻す。</summary>
    public void ClearThumbnail()
    {
        Thumbnail = null;
        _image.BeginAnimation(OpacityProperty, null);
        _image.Opacity = 0;
        _image.Source = null;
    }

    /// <summary>完成したサムネイルを出す(thumb-in)。</summary>
    public void SetThumbnail(DocumentImage image)
    {
        Thumbnail = image;
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Pixels, image.Width * 4);
        bmp.Freeze();
        _image.Source = bmp;
        var d = Motion.Duration(this, MotionToken.ThumbIn, MotionCause.Programmatic);
        if (Motion.DoubleTo(1, d, Motion.Easing(this, MotionToken.ThumbIn), from: 0) is { } fade)
        {
            _image.Opacity = 1;
            _image.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            _image.BeginAnimation(OpacityProperty, null);
            _image.Opacity = 1;
        }
    }
}
