using System.Globalization;
using Mossmint.Domain;

namespace Mossmint.Application.Import;

// How review items and reports show amounts and dates: plain, unambiguous,
// with a true minus sign.
public static class ImportText
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string Amount(Money money)
    {
        var digits = money.Currency.MinorUnits;
        var magnitude = Math.Abs(money.Amount).ToString("N" + digits.ToString(CultureInfo.InvariantCulture), Culture);
        var sign = money.IsNegative ? "−" : "";
        return money.Currency.Code == "USD" ? $"{sign}${magnitude}" : $"{sign}{magnitude} {money.Currency.Code}";
    }

    public static string Date(DateOnly date) => date.ToString("MMMM d, yyyy", Culture);

    public static string ShortDate(DateOnly date) => date.ToString("MMM d, yyyy", Culture);
}
