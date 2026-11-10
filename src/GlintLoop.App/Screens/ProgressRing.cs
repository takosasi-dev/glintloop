// ウェルカム画面の進捗リング(§9.3 の「処理中」・§9.1 の動きの規則 10): 円弧を一定の速さで回す。アニメーションの縮小が有効なときは回さず、静止した円弧を出す(表示中に縮小の設定が変わっても追う)。
// 1 周の時間(1.2 秒)はトークンの辞書のキー MotionProgressRingTurn(Tokens.Layout.xaml)から読む(数値を直書きしない。INV-14)。
// 大きさは HitMin、線の太さは StrokeEmphasis、色は accent。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using GlintLoop.App.Theme;

namespace GlintLoop.App.Screens;

public sealed class ProgressRing : Grid
{
    /// <summary>1 周の時間のトークンのキー(§9.1 の規則 10 の 1.2 秒)。</summary>
    public const string TurnDurationKey = "MotionProgressRingTurn";

    private readonly RotateTransform _rotate = new();
    private readonly Path _arc;

    public ProgressRing()
    {
        SetResourceReference(WidthProperty, "HitMin");
        SetResourceReference(HeightProperty, "HitMin");
        AutomationProperties.SetAutomationId(this, "ProgressRing");
        _arc = new Path
        {
            // 単位の正方形の中の 3/4 周の円弧(Stretch で大きさに合わせる)
            Data = Geometry.Parse("M 1,0.5 A 0.5,0.5 0 1 1 0.5,0"),
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotate,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _arc.SetResourceReference(Shape.StrokeThicknessProperty, "StrokeEmphasis");
        Children.Add(_arc);
        IsVisibleChanged += (_, _) => Update();
    }

    /// <summary>今回っているか(テスト用)。</summary>
    public bool IsSpinning { get; private set; }

    /// <summary>
    /// 縮小の設定(ウィンドウから継承する Motion.Reduced。AppShell が設定と Windows の「アニメーション効果」の変化で置き直す)が
    /// 表示中に変わったら、回すか止めるかを決め直す(FR-97・§9.1 の規則 10)。
    /// </summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == Motion.ReducedProperty) Update();
    }

    /// <summary>表示・縮小の設定が変わったときに、回すか止めるかを決め直す。</summary>
    public void Update()
    {
        var turn = TryFindResource(TurnDurationKey) as TimeSpan?;
        var spin = IsVisible && !Motion.GetReduced(this) && turn is { } t && t > TimeSpan.Zero;
        if (spin == IsSpinning) return;
        IsSpinning = spin;
        if (!spin)
        {
            _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }
        // 移行ではなく処理中を示す連続の回転なので、規則 4・7 の対象外(規則 10)。一定の速さ(イージングなし)
        var a = new DoubleAnimation(0, 360, new Duration(turn!.Value)) { RepeatBehavior = RepeatBehavior.Forever };
        _rotate.BeginAnimation(RotateTransform.AngleProperty, a);
    }
}
