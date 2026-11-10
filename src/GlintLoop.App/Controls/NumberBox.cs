// 数値入力(FR-27 の規則): Enter を押したとき、またはフォーカスを失ったときに反映する。範囲外は範囲の端に直して反映し、
// 数として読めない入力(空・文字)は反映せず、欄の文字を直前の値に戻す。数字はタブラー(Controls.xaml のスタイル)。
// 反映した値は Value と ValueCommitted で受け取る。小数の桁は Decimals(表示の丸め)で決める。
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GlintLoop.App.Controls;

public class NumberBox : TextBox
{
    private bool _blank;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberBox)d).ShowValue()));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.MinValue));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.MaxValue));

    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(NumberBox), new PropertyMetadata(0, (d, _) => ((NumberBox)d).ShowValue()));

    static NumberBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NumberBox), new FrameworkPropertyMetadata(typeof(NumberBox)));
    }

    public NumberBox()
    {
        ShowValue();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

    /// <summary>値が無い状態か(欄を空で出す)。読める値を反映すると外れる。</summary>
    public bool IsBlank
    {
        get => _blank;
        set
        {
            _blank = value;
            ShowValue();
        }
    }

    /// <summary>入力を反映したとき(値が変わらなくても、端に直したときも出す)。</summary>
    public event Action<NumberBox, double>? ValueCommitted;

    /// <summary>FR-27 の規則で、文字を値に直す。読めないときは null(欄を直前の値に戻す)。</summary>
    public static double? Parse(string text, double min, double max, int decimals)
    {
        var s = text.Trim();
        if (s.Length == 0) return null;
        CultureInfo culture;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) culture = CultureInfo.InvariantCulture;
        else if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) culture = CultureInfo.CurrentCulture;
        else return null;
        if (!double.IsFinite(v)) return null;
        v = RoundHalfUp(s, culture, v, Math.Clamp(decimals, 0, 15));
        return Math.Clamp(v, min, max);
    }

    /// <summary>
    /// 小数 decimals 桁への丸め。仕様の数の約束の round(x)=floor(x+0.5)(0.5 は大きい方へ。負の中間値も同じ)に従う。
    /// 入力の文字を 10 進(decimal)で読めるときはその値で丸め(1.005 を double の 1.00499… にしない)、読めない大きさのときは double で丸める。
    /// </summary>
    private static double RoundHalfUp(string s, CultureInfo culture, double v, int decimals)
    {
        var scale = 1m;
        for (var i = 0; i < decimals; i++) scale *= 10m;
        if (decimal.TryParse(s, NumberStyles.Float, culture, out var m) && Math.Abs(m) < decimal.MaxValue / scale / 2)
            return (double)(Math.Floor(m * scale + 0.5m) / scale);
        var p = Math.Pow(10, decimals);
        var r = Math.Floor(v * p + 0.5) / p;
        return double.IsFinite(r) ? r : v;
    }

    /// <summary>いまの文字を反映する(Enter とフォーカスを失ったとき)。</summary>
    public void Commit()
    {
        var v = Parse(Text, Minimum, Maximum, Decimals);
        if (v is { } value)
        {
            _blank = false;
            Value = value;
            ShowValue();
            ValueCommitted?.Invoke(this, value);
        }
        else
        {
            ShowValue();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Commit();
    }

    private void ShowValue()
    {
        var text = _blank ? "" : Value.ToString("F" + Math.Clamp(Decimals, 0, 15).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (Text != text) Text = text;
    }
}
