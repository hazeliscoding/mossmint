using System.Globalization;
using System.Text.RegularExpressions;

namespace Mossmint.Import.Transactions;

// Amounts and dates the way bank files write them. Anything that isn't
// clearly a number or a date is refused rather than guessed.
public static partial class CsvValues
{
    public static bool TryAmount(string text, char decimalSeparator, out decimal amount)
    {
        amount = 0;
        var s = text.Trim();
        var negative = false;
        if (s.Length > 1 && s[0] == '(' && s[^1] == ')')
        {
            negative = true;
            s = s[1..^1];
        }

        if (s.EndsWith('-'))
        {
            negative = !negative;
            s = s[..^1];
        }

        s = string.Concat(s.Where(c => !char.IsWhiteSpace(c) && c is not ('$' or '€' or '£' or '¥')));
        if (s.StartsWith('-'))
        {
            negative = !negative;
            s = s[1..];
        }
        else if (s.StartsWith('+'))
        {
            s = s[1..];
        }

        if (!(decimalSeparator == ',' ? DecimalComma() : DecimalPoint()).IsMatch(s))
        {
            return false;
        }

        var plain = decimalSeparator == ',' ? s.Replace(".", "", StringComparison.Ordinal).Replace(',', '.') : s.Replace(",", "", StringComparison.Ordinal);
        amount = decimal.Parse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (negative)
        {
            amount = -amount;
        }

        return true;
    }

    public static bool TryDate(string text, string format, out DateOnly date)
    {
        var s = text.Trim();
        if (DateOnly.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        // A time after a numeric date is dropped: transactions are dated by day.
        var cut = s.IndexOfAny([' ', 'T']);
        return cut > 0 && char.IsDigit(s[cut - 1])
            && DateOnly.TryParseExact(s[..cut], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    [GeneratedRegex(@"^(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$")]
    private static partial Regex DecimalPoint();

    [GeneratedRegex(@"^(\d{1,3}(\.\d{3})+|\d+)(,\d+)?$")]
    private static partial Regex DecimalComma();
}
