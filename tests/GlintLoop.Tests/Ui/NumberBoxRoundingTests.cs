// 数値入力(NumberBox.Parse)の小数の桁の丸めが、仕様の数の約束の round(x)=floor(x+0.5)(0.5 は大きい方へ)に従うことのテスト(FR-27)。
// 負の中間値は 0 に近い方へ(−1.235 は −1.23、−2.5 は −2)。入力の文字の 10 進の値で丸める(1.005 は 1.01)。
using GlintLoop.App.Controls;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Ui;

public sealed class NumberBoxRoundingTests
{
    private static double? P(string text, int decimals) => NumberBox.Parse(text, -1e6, 1e6, decimals);

    [Test]
    public void FR27_数値入力の丸めはfloor_x_plus_half_で負の中間値は大きい方へ()
    {
        Check.Equal(-1.23, P("-1.235", 2), "−1.235(2 桁)");
        Check.Equal(1.24, P("1.235", 2), "1.235(2 桁)");
        Check.Equal(-2.0, P("-2.5", 0), "−2.5(0 桁)");
        Check.Equal(3.0, P("2.5", 0), "2.5(0 桁)");
        Check.True(P("-0.5", 0) == 0, "−0.5(0 桁)は 0");
        Check.Equal(-1.0, P("-1.5", 0), "−1.5(0 桁)");
        Check.Equal(-1.2, P("-1.25", 1), "−1.25(1 桁)");
        Check.Equal(1.01, P("1.005", 2), "1.005(2 桁。double では 1.00499… になる値も入力の 10 進で丸める)");
        Check.Equal(-1.0, P("-1.005", 2), "−1.005(2 桁)");
        Check.Equal(-1.235, P("-1.2355", 3), "−1.2355(3 桁。floor(−1235.5+0.5)=−1235)");
        Check.Equal(12.0, P("1.15e1", 0), "指数の表記");
    }

    [Test]
    public void FR27_数値入力は丸めてから範囲の端に直す()
    {
        Check.Equal(-1.0, NumberBox.Parse("-1.04", -1, 1, 1), "−1.04 は −1.0(範囲の中)");
        Check.Equal(-1.0, NumberBox.Parse("-1.06", -1, 1, 1), "−1.06 は −1.1 に丸めてから −1 に直す");
        Check.Equal(1e6, NumberBox.Parse("1e300", -1e6, 1e6, 2), "とても大きい値は最大");
        Check.Equal(-1e6, NumberBox.Parse("-1e300", -1e6, 1e6, 2), "とても小さい値は最小");
    }
}
