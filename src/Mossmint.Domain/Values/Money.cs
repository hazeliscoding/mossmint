using System.Globalization;
using System.Text.Json.Serialization;

namespace Mossmint.Domain;

// A decimal amount in one currency. Arithmetic across currencies throws: there
// is no FX conversion, so mixing them is always a bug. Amounts keep full decimal
// precision until a model rounds them explicitly with Round().
public readonly record struct Money : IComparable<Money>
{
    [JsonConstructor]
    public Money(decimal amount, Currency currency)
    {
        if (!currency.IsDefined)
        {
            throw new ArgumentException("Money needs a defined currency.", nameof(currency));
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    [JsonIgnore]
    public bool IsZero => Amount == 0m;

    [JsonIgnore]
    public bool IsNegative => Amount < 0m;

    [JsonIgnore]
    public bool IsPositive => Amount > 0m;

    // True when the amount needs no rounding at the currency's precision.
    [JsonIgnore]
    public bool IsWholeMinorUnits => decimal.Round(Amount, Currency.MinorUnits) == Amount;

    public static Money Zero(Currency currency) => new(0m, currency);

    public static Money Usd(decimal amount) => new(amount, Currency.Usd);

    public static Money FromMinorUnits(long minorUnits, Currency currency) =>
        new(minorUnits / Scale(currency), currency);

    public long ToMinorUnits() =>
        IsWholeMinorUnits
            ? decimal.ToInt64(Amount * Scale(Currency))
            : throw new InvalidOperationException(
                $"{this} has more precision than {Currency.Code} minor units; round it first.");

    public Money Round(MidpointRounding mode) => new(decimal.Round(Amount, Currency.MinorUnits, mode), Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    public static Money Min(Money a, Money b) => a <= b ? a : b;

    public static Money Max(Money a, Money b) => a >= b ? a : b;

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount, SameCurrency(a, b));

    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount, SameCurrency(a, b));

    public static Money operator -(Money a) => new(-a.Amount, a.Currency);

    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);

    public static Money operator *(decimal factor, Money a) => a * factor;

    public static bool operator <(Money a, Money b) => a.CompareTo(b) < 0;

    public static bool operator >(Money a, Money b) => a.CompareTo(b) > 0;

    public static bool operator <=(Money a, Money b) => a.CompareTo(b) <= 0;

    public static bool operator >=(Money a, Money b) => a.CompareTo(b) >= 0;

    public int CompareTo(Money other)
    {
        _ = SameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() =>
        $"{Amount.ToString(CultureInfo.InvariantCulture)} {Currency.Code}";

    private static decimal Scale(Currency currency) => currency.MinorUnits switch
    {
        0 => 1m,
        2 => 100m,
        3 => 1000m,
        var n => throw new NotSupportedException($"Minor units {n} are not supported."),
    };

    private static Currency SameCurrency(Money a, Money b) =>
        a.Currency == b.Currency
            ? a.Currency
            : throw new CurrencyMismatchException(a.Currency, b.Currency);
}

public sealed class CurrencyMismatchException(Currency left, Currency right)
    : InvalidOperationException($"Cannot combine {left.Code} with {right.Code}; Mossmint has no currency conversion.");
