using System.Globalization;

namespace Mossmint.Desktop.Services;

// Reads amounts the way the user's locale writes them: "1,234.56" in the US,
// "1.234,56" in Germany, with or without the currency symbol. Anything else is
// refused with a message rather than guessed.
public static class MoneyInput
{
    public static bool TryParse(string? text, CultureInfo culture, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().Replace(culture.NumberFormat.CurrencySymbol, "", StringComparison.Ordinal).Replace("−", "-", StringComparison.Ordinal);
        return decimal.TryParse(cleaned, NumberStyles.Number, culture, out amount);
    }

    public static decimal? Optional(string? text, CultureInfo culture, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TryParse(text, culture, out var amount)
            ? amount
            : throw new ArgumentException($"{field}: \"{text}\" isn't an amount.");
    }

    public static decimal Required(string? text, CultureInfo culture, string field) =>
        Optional(text, culture, field) ?? throw new ArgumentException($"{field}: enter an amount.");

    public static string Format(Domain.Money money, CultureInfo culture)
    {
        var number = money.Abs().Amount.ToString("N" + money.Currency.MinorUnits, culture);
        return (money.IsNegative ? "−" : "") + (money.Currency.Code == "USD" ? "$" : money.Currency.Code + " ") + number;
    }
}
