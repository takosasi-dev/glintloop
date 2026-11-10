// 時間の設定の入力の直し(FR-58 の L と FR-61 の停止時間)。L は 10 進数の文字列を decimal で読み、0.1 秒刻みへ round(0.05 ちょうどは大きい方へ)して範囲の端に直す。
// 停止時間は 100 ms 刻みへ round(50 ちょうどは大きい方へ)して 0〜5000 ms に直す。どちらも浮動小数を経由しない。
// タイムラインの入力欄・EditorSession の時間の操作・保存した文書の読み込み(DocumentJson.ReadTime)が同じ規則を使う。
using System.Globalization;

namespace GlintLoop.Core.Session;

public static class TimeInput
{
    /// <summary>L(秒)を 0.1 秒刻みへ round した値(範囲の端には直さない)。round(x)=floor(x+0.5)(§9.4)。</summary>
    public static decimal RoundTenths(decimal seconds)
    {
        // ×10 で decimal があふれる大きさは、そのまま返す(どのみち範囲の端に直る)
        if (Math.Abs(seconds) >= decimal.MaxValue / 20m) return seconds;
        return decimal.Floor(seconds * 10m + 0.5m) / 10m;
    }

    /// <summary>L(秒)を L10 に直す: 0.1 秒刻みへ round してから 5〜100(0.5〜10.0 秒)の端に直す(FR-58)。</summary>
    public static int Length10Of(decimal seconds)
    {
        // 範囲の外は先に端へ(round は単調なので結果は同じ。×10 で decimal があふれない)
        if (seconds >= TimeModel.MaxLength10 / 10m) return TimeModel.MaxLength10;
        if (seconds <= TimeModel.MinLength10 / 10m) return TimeModel.MinLength10;
        var tenths = RoundTenths(seconds) * 10m;
        if (tenths <= TimeModel.MinLength10) return TimeModel.MinLength10;
        if (tenths >= TimeModel.MaxLength10) return TimeModel.MaxLength10;
        return (int)tenths;
    }

    /// <summary>L の入力の文字列を読む。数として読めなければ null(欄は直前の値に戻す)。読み方は InvariantCulture、だめなら現在のカルチャ。</summary>
    public static int? ParseLength10(string? text) => TryDecimal(text, out var v) ? Length10Of(v) : null;

    /// <summary>停止時間(ms)を直す: 100 ms 刻みへ round(50 ちょうどは大きい方へ)し、0〜5000 に切る(FR-61)。</summary>
    public static long HoldMsOf(decimal ms)
    {
        // 範囲の外は round の前に端へ寄せる(round は単調なので結果は同じ。long への変換があふれない)
        if (ms <= TimeModel.MinHoldMs) return TimeModel.MinHoldMs;
        if (ms >= TimeModel.MaxHoldMs) return TimeModel.MaxHoldMs;
        var steps = (long)decimal.Floor(ms / TimeModel.HoldStepMs + 0.5m);
        return Math.Clamp(steps * TimeModel.HoldStepMs, TimeModel.MinHoldMs, TimeModel.MaxHoldMs);
    }

    /// <summary>停止時間の入力の文字列を読む。数として読めなければ null。</summary>
    public static long? ParseHoldMs(string? text) => TryDecimal(text, out var v) ? HoldMsOf(v) : null;

    /// <summary>L10 の表示(「2.0」の形。InvariantCulture)。</summary>
    public static string FormatLength10(int length10) =>
        (length10 / 10).ToString(CultureInfo.InvariantCulture) + "." + (length10 % 10).ToString(CultureInfo.InvariantCulture);

    /// <summary>停止時間の表示(整数の ms)。</summary>
    public static string FormatHoldMs(long ms) => ms.ToString(CultureInfo.InvariantCulture);

    private static bool TryDecimal(string? text, out decimal value)
    {
        value = 0;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        const NumberStyles styles = NumberStyles.Float;
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.CurrentCulture })
        {
            if (decimal.TryParse(s, styles, culture, out value)) return true;
            // decimal に入らない大きさ(1e30 など)は数として読めるので、符号の側の端に直す(どのみち範囲の端になる)
            if (double.TryParse(s, styles, culture, out var d) && double.IsFinite(d) && Math.Abs(d) >= (double)decimal.MaxValue)
            {
                value = d > 0 ? decimal.MaxValue : decimal.MinValue;
                return true;
            }
        }
        return false;
    }
}
