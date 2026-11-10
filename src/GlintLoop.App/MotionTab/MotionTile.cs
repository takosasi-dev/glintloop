// モーションのタブの 1 つのタイル(FR-64・§9.3): ルックのタイルと同じ 132 DIP の幅に、絵の正方形と、その下に caption の名前を 1 行で置く(あふれは「…」)。
// 絵は、操作されていないときはプリセットの「タイルの t」の 1 フレームの静止画で、再生中は描けたフレームを差し替えて出す(再生の時刻は MotionTabModule が決める)。
// 作成中は無地の surface-2 で、静止画が完成したら thumb-in で出す。タイルの見た目(塗り・枠・押下・選択・輪)は一覧の ItemContainerStyle(TileItem)に任せる。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlintLoop.App.Input;
using GlintLoop.App.Theme;
using GlintLoop.Core.Motion;
using GlintLoop.Render.Composite;

namespace GlintLoop.App.MotionTab;

public sealed class MotionTile : ListBoxItem
{
    private readonly Image _image;

    /// <summary>preset が null なら「モーションなし」。</summary>
    public MotionTile(MotionPreset? preset, string name)
    {
        Preset = preset;
        DisplayName = name;
        SetResourceReference(WidthProperty, "TileSize");
        Padding = new Thickness(0);
        ShortcutScope.SetKind(this, FocusKind.List);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetAutomationId(this, "MotionTile:" + Key);
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

    /// <summary>当てるプリセット(「モーションなし」は null)。</summary>
    public MotionPreset? Preset { get; }

    public string DisplayName { get; }

    /// <summary>タイルのキー(「モーションなし」は none、ほかは表のキー)。</summary>
    public string Key => Preset?.Key ?? "none";

    /// <summary>当てるエフェクト(「モーションなし」は空)。</summary>
    public IReadOnlyList<MotionPresetEffect> Effects => Preset?.Effects ?? Array.Empty<MotionPresetEffect>();

    /// <summary>静止画の位相 t=StillK÷StillN(「モーションなし」は 0÷1)。</summary>
    public int StillK => Preset?.TileK ?? 0;

    public int StillN => Preset?.TileN ?? 1;

    /// <summary>再生するか(動くエフェクトを 1 つ以上持つ)。「モーションなし」と、エフェクトがまだカタログに無いプリセットは再生しない。</summary>
    public bool CanPlay => Effects.Count > 0;

    /// <summary>絵の正方形(作成中は無地の surface-2)。</summary>
    public Border Thumb { get; }

    /// <summary>静止画の画素(Pbgra32。作成中は null)。テストがエンジンの結果とバイト列で比べる。</summary>
    public DocumentImage? Thumbnail { get; private set; }

    /// <summary>いま出している画素(静止画か、再生中のフレーム)。</summary>
    public DocumentImage? Shown { get; private set; }

    /// <summary>いま出している再生のフレームの番号(静止画を出しているときは null)。</summary>
    public int? ShownFrame { get; private set; }

    public ImageSource? ThumbnailSource => _image.Source;

    /// <summary>静止画を消して作成中(無地の surface-2)に戻す。</summary>
    public void ClearThumbnail()
    {
        Thumbnail = null;
        Shown = null;
        ShownFrame = null;
        _image.BeginAnimation(OpacityProperty, null);
        _image.Opacity = 0;
        _image.Source = null;
    }

    /// <summary>静止画だけを捨てる(再生中のタイルの作り直し。出しているフレームはそのまま)。</summary>
    public void ForgetThumbnail() => Thumbnail = null;

    /// <summary>完成した静止画を持つ。再生中でなければ thumb-in で出す。</summary>
    public void SetThumbnail(DocumentImage image, bool show)
    {
        Thumbnail = image;
        if (!show) return;
        Put(image, null);
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

    /// <summary>再生のフレーム k を出す(差し替えはすぐ。動きの演出は付けない)。</summary>
    public void ShowFrame(DocumentImage image, int k)
    {
        Put(image, k);
        _image.BeginAnimation(OpacityProperty, null);
        _image.Opacity = 1;
    }

    /// <summary>再生をやめて静止画に戻す(静止画がまだ無ければ作成中のまま)。</summary>
    public void ShowStill()
    {
        _image.BeginAnimation(OpacityProperty, null);
        if (Thumbnail is { } still)
        {
            Put(still, null);
            _image.Opacity = 1;
        }
        else
        {
            Shown = null;
            ShownFrame = null;
            _image.Source = null;
            _image.Opacity = 0;
        }
    }

    private void Put(DocumentImage image, int? frame)
    {
        Shown = image;
        ShownFrame = frame;
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Pixels, image.Width * 4);
        bmp.Freeze();
        _image.Source = bmp;
    }
}
