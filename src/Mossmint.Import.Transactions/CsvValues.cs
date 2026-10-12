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

        // One sign at most: "(-4.50)" or "-4.50-" could mean either, so it isn't read.
        var signs = 0;
        if (s.Length > 1 && s[0] == '(' && s[^1] == ')')
        {
            signs++;
            s = s[1..^1];
        }

        if (s.EndsWith('-'))
        {
            signs++;
            s = s[..^1];
        }

        s = string.Concat(s.Where(c => !char.IsWhiteSpace(c) && c is not ('$' or '€' or '£' or '¥')));
        if (s.StartsWith('-'))
        {
            signs++;
            s = s[1..];
        }
        else if (s.StartsWith('+'))
        {
            s = s[1..];
        }

        if (signs > 1)
        {
            return false;
        }

        if (!(decimalSeparator == ',' ? DecimalComma() : DecimalPoint()).IsMatch(s))
        {
            return false;
        }

        var plain = decimalSeparator == ',' ? s.Replace(".", "", StringComparison.Ordinal).Replace(',', '.') : s.Replace(",", "", StringComparison.Ordinal);

        // Too many digits for a decimal (a long reference number) isn't an amount either.
        if (!decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            return false;
        }

        amount = signs == 1 ? -amount : amount;
        return true;
    }

    // A cell with no digits ("Total", a note) can't be a date, so it doesn't count
    // against a format; the row is left out when it's imported.
    public static bool CouldBeDate(string text) => text.Any(char.IsAsciiDigit);

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
